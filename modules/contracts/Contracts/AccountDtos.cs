using System.Text.Json.Serialization;

namespace Minicloud.Common.Contracts;

public sealed record AccountCapabilitiesResponse(
    bool Password,
    bool Google,
    bool EmailRecovery,
    string BootstrapState);

public sealed record AccountSessionResponse(
    string UserId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset IdleExpiresAt,
    string CsrfToken);

public sealed record AccountFlowResponse(
    AccountBrowserFlow? Flow,
    AccountSessionResponse? Session);

public sealed record AccountBrowserFlow(
    string Id,
    string Kind,
    DateTimeOffset ExpiresAt,
    AccountFlowUi Ui);

public sealed record AccountFlowUi(
    string Action,
    string Method,
    IReadOnlyList<AccountFlowNode> Nodes,
    IReadOnlyList<AccountFlowMessage> Messages,
    // Omitted unless an enabled optional provider is offered by this flow.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AccountFlowProvider>? Providers = null);

public sealed record AccountFlowProvider(
    string Id,
    string Action,
    string Method);

public sealed record AccountProviderRedirectResponse(
    string Provider,
    string RedirectUrl);

public sealed record AccountFlowNode(
    string Group,
    string Type,
    string Name,
    string? Value,
    bool Required,
    IReadOnlyList<AccountFlowMessage> Messages);

public sealed record AccountFlowMessage(int Id, string Text, string Type);
