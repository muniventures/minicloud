using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Minicloud.Common.Contracts;

namespace Minicloud.Api.Auth;

public sealed class AccountFlowException(string code, int statusCode, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record KratosFlowResult(
    AccountBrowserFlow? Flow,
    string? IdentityId,
    string? SessionId,
    IReadOnlyDictionary<string, string> Cookies);

public sealed class KratosBrowserFlowClient(HttpClient client, IOptions<KratosOptions> options)
{
    public const int BodyLimit = 64 * 1024;
    private readonly KratosOptions _options = options.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "csrf_token", "identifier", "password", "method", "email", "code", "provider",
        "traits.email", "traits.name", "traits.name.first"
    };

    public static bool IsFlowCookie(string name) =>
        name == "ory_kratos_session" || Regex.IsMatch(name, @"^csrf_token_[A-Za-z0-9_-]{1,128}$");

    public string ValidateReturnTo(string? path)
    {
        path ??= "/apps";
        var decoded = Uri.UnescapeDataString(path);
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) ||
            decoded.StartsWith("//", StringComparison.Ordinal) ||
            decoded.Any(c => c == '\\' || char.IsControl(c) || char.IsWhiteSpace(c)) ||
            !Uri.TryCreate(_options.DashboardOrigin, UriKind.Absolute, out var dashboard) ||
            dashboard.Scheme != Uri.UriSchemeHttps || dashboard.GetLeftPart(UriPartial.Authority) != _options.DashboardOrigin ||
            !Uri.TryCreate(dashboard, path, out var target) ||
            target.GetLeftPart(UriPartial.Authority) != _options.DashboardOrigin)
        {
            throw InvalidFlow();
        }
        return target.AbsoluteUri;
    }

    public async Task<KratosFlowResult> SendAsync(
        string kind,
        string operation,
        Guid? id,
        string? returnTo,
        JsonElement? fields,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken cancellationToken)
    {
        if (kind is not ("login" or "settings" or "recovery") ||
            kind == "recovery" && !_options.EmailRecoveryEnabled ||
            operation is not ("start" or "read" or "submit") ||
            operation != "start" && (id is null || id == Guid.Empty))
        {
            throw InvalidFlow();
        }
        var baseUri = PublicOrigin();
        var path = operation switch
        {
            "start" => $"/self-service/{kind}/browser?return_to={Uri.EscapeDataString(ValidateReturnTo(returnTo))}" +
                (kind == "login" ? "&refresh=true" : ""),
            "read" => $"/self-service/{kind}/flows?id={id:D}",
            _ => $"/self-service/{kind}?flow={id:D}"
        };
        using var request = new HttpRequestMessage(operation == "submit" ? HttpMethod.Post : HttpMethod.Get, new Uri(baseUri, path));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("Origin", _options.DashboardOrigin);
        var cookieJar = cookies.Where(cookie => IsFlowCookie(cookie.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        AddCookies(request, cookieJar);
        if (operation == "submit")
        {
            request.Content = new StringContent(SerializeFields(fields), Encoding.UTF8, "application/json");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var upstreamToken = timeout.Token;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upstreamToken);
            var updatedCookies = ReadCookies(response);
            foreach (var cookie in updatedCookies)
            {
                if (cookie.Value.Length == 0) cookieJar.Remove(cookie.Key);
                else cookieJar[cookie.Key] = cookie.Value;
            }
            if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound) throw ExpiredFlow();
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new AccountFlowException("unauthenticated", 401, "Authentication was rejected.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new AccountFlowException("csrf_invalid", 403, "The account form is invalid.");
            if ((int)response.StatusCode >= 500) throw new KratosUnavailableException();
            if (!response.IsSuccessStatusCode)
            {
                // Browser redirects are never followed or exposed as upstream URLs.
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                    throw InvalidFlow();
                throw new KratosUnavailableException();
            }
            var payload = await ReadJsonAsync(response, upstreamToken);
            var verified = operation == "submit" &&
                (payload.TryGetProperty("session", out _) ||
                 payload.TryGetProperty("state", out var state) && state.GetString() is "success" or "passed_challenge");
            if (verified)
            {
                using var whoami = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/sessions/whoami"));
                whoami.Headers.Accept.ParseAdd("application/json");
                AddCookies(whoami, cookieJar);
                using var sessionResponse = await client.SendAsync(whoami, HttpCompletionOption.ResponseHeadersRead, upstreamToken);
                if (sessionResponse.StatusCode == HttpStatusCode.Unauthorized)
                    throw new AccountFlowException("unauthenticated", 401, "Authentication was rejected.");
                if (!sessionResponse.IsSuccessStatusCode) throw new KratosUnavailableException();
                var session = await ReadJsonAsync(sessionResponse, upstreamToken);
                if (!session.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True)
                    throw new AccountFlowException("unauthenticated", 401, "Authentication was rejected.");
                var sessionId = session.GetProperty("id").GetString();
                var identityId = session.GetProperty("identity").GetProperty("id").GetString();
                if (!Guid.TryParse(sessionId, out _) || !Guid.TryParse(identityId, out _))
                    throw new KratosUnavailableException();
                return new KratosFlowResult(null, identityId, sessionId, updatedCookies);
            }
            var flow = payload.Deserialize<UpstreamFlow>(JsonOptions) ?? throw new KratosUnavailableException();
            if (!Guid.TryParse(flow.Id, out var returnedId) || id is not null && returnedId != id ||
                flow.ExpiresAt <= DateTimeOffset.UtcNow)
                throw ExpiredFlow();
            if (flow.Type != "browser" || flow.Ui is null || flow.Ui.Nodes is null ||
                !Uri.TryCreate(flow.Ui.Action, UriKind.Absolute, out var action) ||
                action.GetLeftPart(UriPartial.Authority) != baseUri.GetLeftPart(UriPartial.Authority) ||
                action.AbsolutePath != $"/self-service/{kind}" || action.Query != $"?flow={flow.Id}" ||
                !string.Equals(flow.Ui.Method, "POST", StringComparison.OrdinalIgnoreCase))
                throw new KratosUnavailableException();
            var nodes = flow.Ui.Nodes
                .Where(node => node is not null && node.Attributes is not null && Fields.Contains(node.Attributes.Name) &&
                    node.Attributes.Type is "hidden" or "text" or "email" or "password" or "submit" &&
                    (node.Group != "oidc" || _options.GoogleEnabled && node.Attributes.Value.ToString() == "google"))
                .Select(node => new AccountFlowNode(
                    node.Group, node.Attributes.Type, node.Attributes.Name,
                    node.Attributes.Type == "password" || node.Attributes.Name is "password" or "code" ? null : node.Attributes.Value.ToString(),
                    node.Attributes.Required, SanitizeMessages(node.Messages)))
                .ToArray();
            return new KratosFlowResult(
                new AccountBrowserFlow(flow.Id, kind, flow.ExpiresAt,
                    new AccountFlowUi($"/api/account/flows/{kind}?flow={flow.Id}", "POST", nodes, SanitizeMessages(flow.Ui.Messages))),
                null, null, updatedCookies);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or
                                           InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new KratosUnavailableException();
        }
    }

    private Uri PublicOrigin()
    {
        if (!Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new KratosUnavailableException();
        return uri;
    }

    private string SerializeFields(JsonElement? fields)
    {
        if (fields is not { ValueKind: JsonValueKind.Object } body) throw InvalidFlow();
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var traits = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in body.EnumerateObject())
        {
            if (!Fields.Contains(field.Name) || field.Value.ValueKind != JsonValueKind.String ||
                field.Value.GetString() is not { } value || value.Length > BodyLimit ||
                field.Name == "provider" && (!_options.GoogleEnabled || value != "google"))
                throw InvalidFlow();
            if (field.Name.StartsWith("traits.", StringComparison.Ordinal))
            {
                if (field.Name == "traits.name.first")
                {
                    if (!traits.TryAdd("name", new Dictionary<string, string> { ["first"] = value }))
                        throw InvalidFlow();
                }
                else if (!traits.TryAdd(field.Name[7..], value)) throw InvalidFlow();
            }
            else if (!values.TryAdd(field.Name, value)) throw InvalidFlow();
        }
        if (traits.Count > 0) values["traits"] = traits;
        if (!values.TryGetValue("csrf_token", out var csrf) || string.IsNullOrWhiteSpace(csrf.ToString()))
            throw new AccountFlowException("csrf_invalid", 403, "The account form is invalid.");
        return JsonSerializer.Serialize(values);
    }

    private static void AddCookies(HttpRequestMessage request, IReadOnlyDictionary<string, string> cookies)
    {
        if (cookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(x => $"{x.Key}={x.Value}")));
    }

    private static Dictionary<string, string> ReadCookies(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return result;
        if (!SetCookieHeaderValue.TryParseList(values.ToArray(), out var parsed))
            throw new KratosUnavailableException();
        foreach (var cookie in parsed)
        {
            if (!IsFlowCookie(cookie.Name.ToString())) continue;
            result[cookie.Name.ToString()] = cookie.MaxAge <= TimeSpan.Zero || cookie.Expires <= DateTimeOffset.UtcNow
                ? "" : cookie.Value.ToString();
        }
        return result;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > BodyLimit) throw new KratosUnavailableException();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > BodyLimit) throw new KratosUnavailableException();
            buffer.Write(chunk, 0, read);
        }
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        return document.RootElement.Clone();
    }

    private static AccountFlowMessage[] SanitizeMessages(UpstreamMessage[] messages) =>
        messages.Where(message => message is not null).Select(message => new AccountFlowMessage(message.Id,
            message.Type == "error" ? "Check your account details." : "Continue with the account form.",
            message.Type == "error" ? "error" : "info")).ToArray();

    private static AccountFlowException InvalidFlow() => new("auth_flow_invalid", 400, "The account form is invalid.");
    private static AccountFlowException ExpiredFlow() => new("auth_flow_expired", 410, "The account form expired. Start a new form.");

    private sealed record UpstreamFlow(string Id, string Type, [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt, UpstreamUi Ui);
    private sealed record UpstreamUi(string Action, string Method, UpstreamNode[] Nodes, UpstreamMessage[] Messages)
    {
        public UpstreamMessage[] Messages { get; init; } = Messages ?? [];
    }
    private sealed record UpstreamNode(string Group, UpstreamAttributes Attributes, UpstreamMessage[] Messages)
    {
        public UpstreamMessage[] Messages { get; init; } = Messages ?? [];
    }
    private sealed record UpstreamAttributes(string Name, string Type, JsonElement Value, bool Required);
    private sealed record UpstreamMessage(int Id, string Type);
}
