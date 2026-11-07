using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;
using Minicloud.Telemetry.Data;
using Minicloud.Telemetry.Services;
using Minicloud.Telemetry.Otlp;
using Minicloud.Common.Contracts;
using TelemetryQuotaService = Minicloud.Telemetry.Services.TelemetryQuotaService;
using OtlpNormalizationService = Minicloud.Telemetry.Services.OtlpNormalizationService;
using TelemetryLimits = Minicloud.Common.Services.TelemetryLimits;

namespace Minicloud.Tests;

public sealed class OtlpIngestionTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private OtlpApiFactory? _factory;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new OtlpApiFactory(_connection);
        _factory.CreateClient().Dispose();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    [Fact]
    public async Task ProtobufFixtureIngest_ForwardsSanitizedBatchUnderServerTenant()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_a", "app_otlp_a", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var request = new OtlpExportRequest();
        var resource = new OtlpResourceLogs
        {
            Resource = new OtlpResource
            {
                Attributes =
                [
                    StringAttr("service.name", "forged-service"),
                    StringAttr("service.instance.id", "i-123"),
                    StringAttr(OtlpNormalizationService.AppIdKey, "app_forged"),
                    StringAttr(OtlpNormalizationService.OrgIdKey, "org_forged"),
                    StringAttr("custom.label", "keep-me"),
                ],
            },
        };
        var scope = new OtlpScopeLogs { Scope = new OtlpScope { Name = "test-scope", Version = "1.0" } };
        scope.LogRecords.Add(new OtlpLogRecord
        {
            TimeUnixNano = ToNanos(now),
            SeverityNumber = 9,
            SeverityText = "INFO",
            Body = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = "hello Authorization: Bearer mcl_ingest_forged_token_value" },
            Attributes =
            [
                StringAttr("password", "hunter2"),
                StringAttr("user.id", "u-1"),
                StringAttr(OtlpNormalizationService.ServiceNameKey, "forged"),
            ],
            TraceId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
            SpanId = Enumerable.Range(1, 8).Select(i => (byte)i).ToArray(),
        });
        resource.ScopeLogs.Add(scope);
        request.ResourceLogs.Add(resource);

        using var content = new ByteArrayContent(request.ToByteArray());
        content.Headers.ContentType = new("application/x-protobuf");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/otlp/v1/logs") { Content = content };
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {seed.RawToken}");
        message.Headers.TryAddWithoutValidation("X-Scope-OrgID", "evil_tenant");
        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);

        var captured = Assert.Single(stub.Requests);
        Assert.Equal(seed.TenantId, captured.Tenant);
        Assert.DoesNotContain("evil_tenant", captured.Tenant);
        Assert.False(captured.HasAuthorization, "Gateway must never forward caller credentials to Loki.");

        var forwarded = OtlpProtobuf.ParseExportRequest(captured.Body);
        var forwardedResource = Assert.Single(forwarded.ResourceLogs);
        Assert.Equal(3, forwardedResource.Resource!.Attributes.Count);
        Assert.Contains(forwardedResource.Resource.Attributes, a => a.Key == "service.name" && a.Value.StringValue == "api");
        Assert.Contains(forwardedResource.Resource.Attributes, a => a.Key == "minicloud.source" && a.Value.StringValue == "otlp");
        Assert.Contains(forwardedResource.Resource.Attributes, a => a.Key == "minicloud.platform.environment" && a.Value.StringValue == "test");

        var record = Assert.Single(Assert.Single(forwardedResource.ScopeLogs).LogRecords);
        string? Attr(string key) => record.Attributes.SingleOrDefault(a => a.Key == key)?.Value.StringValue;
        Assert.Equal(seed.AppId, Attr(OtlpNormalizationService.AppIdKey));
        Assert.Equal(seed.OrganizationId, Attr(OtlpNormalizationService.OrgIdKey));
        Assert.Equal("[REDACTED]", Attr("password"));
        Assert.Equal("u-1", Attr("user.id"));
        Assert.Equal("i-123", Attr("service.instance.id"));
        Assert.Equal("keep-me", Attr("custom.label"));
        Assert.DoesNotContain(record.Attributes, a => a.Key == "service.name");
        Assert.Equal(16, record.TraceId.Length);
        Assert.Equal(8, record.SpanId.Length);
        Assert.DoesNotContain("mcl_ingest_forged_token_value", record.Body!.StringValue);
        Assert.DoesNotContain("app_forged", string.Join(",", record.Attributes.Select(a => a.Value.StringValue)));
    }

    [Fact]
    public async Task CrossAppToken_CannotWriteOtherTenant()
    {
        var now = DateTimeOffset.UtcNow;
        var seedA = await SeedAsync("org_otlp_xa", "app_otlp_xa", "mcl_" + RandomHex(32), now);
        var seedB = await SeedAsync("org_otlp_xb", "app_otlp_xb", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var request = SingleRecordRequest(now, [StringAttr(OtlpNormalizationService.AppIdKey, seedB.AppId)]);
        using var content = new ByteArrayContent(request.ToByteArray());
        content.Headers.ContentType = new("application/x-protobuf");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/otlp/v1/logs") { Content = content };
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {seedA.RawToken}");
        message.Headers.TryAddWithoutValidation("X-Scope-OrgID", seedB.TenantId);
        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = Assert.Single(stub.Requests);
        Assert.Equal(seedA.TenantId, captured.Tenant);
        var forwarded = OtlpProtobuf.ParseExportRequest(captured.Body);
        var record = forwarded.ResourceLogs.Single().ScopeLogs.Single().LogRecords.Single();
        Assert.Equal(seedA.AppId, record.Attributes.Single(a => a.Key == OtlpNormalizationService.AppIdKey).Value.StringValue);
    }

    [Fact]
    public async Task UnknownCredential_IsDeniedWithProtobufBody()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("org_otlp_unk", "app_otlp_unk", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        using var content = new ByteArrayContent(SingleRecordRequest(now, []).ToByteArray());
        content.Headers.ContentType = new("application/x-protobuf");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/otlp/v1/logs") { Content = content };
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer mcl_ingest_deadbeef_deadbeef_deadbeef_deadbeef_deadbeef_deadbeef_deadbeef_deadbeef");
        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task DisabledTelemetry_DeniesWrite()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_dis", "app_otlp_dis", "mcl_" + RandomHex(32), now, enabled: false);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var status = await PostProtobufAsync(client, seed.RawToken, SingleRecordRequest(now, []).ToByteArray());

        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task JsonAndGrpc_AreRejectedExplicitly()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_proto", "app_otlp_proto", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        foreach (var mediaType in new[] { "application/json", "application/grpc", "application/grpc+proto", "text/plain" })
        {
            using var content = new ByteArrayContent("{\"resourceLogs\":[]}"u8.ToArray());
            content.Headers.ContentType = new(mediaType);
            using var message = new HttpRequestMessage(HttpMethod.Post, "/otlp/v1/logs") { Content = content };
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {seed.RawToken}");
            var response = await client.SendAsync(message);

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
            var (rejected, error) = OtlpProtobuf.ParseExportResponse(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(0, rejected);
            Assert.NotEmpty(error);
        }

        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task DuplicateAuthorizationHeader_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_dup", "app_otlp_dup", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        using var content = new ByteArrayContent(SingleRecordRequest(now, []).ToByteArray());
        content.Headers.ContentType = new("application/x-protobuf");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/otlp/v1/logs") { Content = content };
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {seed.RawToken}");
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {seed.RawToken}");
        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task GzipBomb_IsBounded()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_bomb", "app_otlp_bomb", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var bomb = Compress(new byte[5 * 1024 * 1024]);
        Assert.True(bomb.Length < 64 * 1024, "Fixture must actually compress.");

        using var content = new ByteArrayContent(bomb);
        content.Headers.ContentType = new("application/x-protobuf");
        content.Headers.ContentEncoding.Add("gzip");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/otlp/v1/logs") { Content = content };
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {seed.RawToken}");
        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task GzipValidIngest_Succeeds()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_gzip", "app_otlp_gzip", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var body = Compress(SingleRecordRequest(now, []).ToByteArray());
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/x-protobuf");
        content.Headers.ContentEncoding.Add("gzip");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/otlp/v1/logs") { Content = content };
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {seed.RawToken}");
        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task MalformedGzip_IsRejectedWithoutForward()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_mgzip", "app_otlp_mgzip", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var notGzip = "this is not a gzip stream, just hostile bytes"u8.ToArray();
        using var content = new ByteArrayContent(notGzip);
        content.Headers.ContentType = new("application/x-protobuf");
        content.Headers.ContentEncoding.Add("gzip");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/otlp/v1/logs") { Content = content };
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {seed.RawToken}");
        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task OversizedEncodedBody_RejectedBeforeParse()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_big", "app_otlp_big", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var status = await PostProtobufAsync(client, seed.RawToken, new byte[TelemetryLimits.MaxEncodedBytes + 1]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status.StatusCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task TooManyRecords_RejectedWithoutForward()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_many", "app_otlp_many", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var request = new OtlpExportRequest();
        var resource = new OtlpResourceLogs();
        var scope = new OtlpScopeLogs();
        for (var i = 0; i < TelemetryLimits.MaxRecordsPerRequest + 1; i++)
        {
            scope.LogRecords.Add(new OtlpLogRecord
            {
                TimeUnixNano = ToNanos(now),
                Body = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = $"line {i}" },
            });
        }

        resource.ScopeLogs.Add(scope);
        request.ResourceLogs.Add(resource);

        var status = await PostProtobufAsync(client, seed.RawToken, request.ToByteArray());

        Assert.Equal(HttpStatusCode.BadRequest, status.StatusCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task OldAndFutureTimestamps_ArePartiallyRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_time", "app_otlp_time", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var request = new OtlpExportRequest();
        var resource = new OtlpResourceLogs();
        var scope = new OtlpScopeLogs();
        scope.LogRecords.Add(new OtlpLogRecord
        {
            TimeUnixNano = ToNanos(now.AddDays(-30)),
            Body = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = "too old" },
        });
        scope.LogRecords.Add(new OtlpLogRecord
        {
            TimeUnixNano = ToNanos(now.AddMinutes(10)),
            Body = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = "too far ahead" },
        });
        scope.LogRecords.Add(new OtlpLogRecord
        {
            TimeUnixNano = ToNanos(now),
            Body = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = "just right" },
        });
        resource.ScopeLogs.Add(scope);
        request.ResourceLogs.Add(resource);

        var response = await PostProtobufRawAsync(client, seed.RawToken, request.ToByteArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (rejected, error) = OtlpProtobuf.ParseExportResponse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, rejected);
        Assert.NotEmpty(error);
        var captured = Assert.Single(stub.Requests);
        var forwarded = OtlpProtobuf.ParseExportRequest(captured.Body);
        var body = Assert.Single(forwarded.ResourceLogs.Single().ScopeLogs.Single().LogRecords).Body;
        Assert.Equal("just right", body!.StringValue);
    }

    [Fact]
    public async Task LokiPartialSuccess_IsTranslated()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_part", "app_otlp_part", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        stub.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(OtlpProtobuf.EncodeExportResponse(2, "loki kept the rest")),
        };
        using var client = _factory.CreateClient();

        var response = await PostProtobufRawAsync(client, seed.RawToken, SingleRecordRequest(now, []).ToByteArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        var (rejected, error) = OtlpProtobuf.ParseExportResponse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, rejected);
        Assert.Contains("loki kept the rest", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LokiRateLimit_IsRetryableProtobuf()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_429", "app_otlp_429", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        stub.Responder = _ =>
        {
            var message = new HttpResponseMessage((HttpStatusCode)429);
            message.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return message;
        };
        using var client = _factory.CreateClient();

        var response = await PostProtobufRawAsync(client, seed.RawToken, SingleRecordRequest(now, []).ToByteArray());

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Headers.RetryAfter);
        var (_, error) = OtlpProtobuf.ParseExportResponse(await response.Content.ReadAsByteArrayAsync());
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task LokiTransportFailure_MapsToRetryable503()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_down", "app_otlp_down", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        stub.Responder = _ => throw new HttpRequestException("connection refused");
        using var client = _factory.CreateClient();

        var response = await PostProtobufRawAsync(client, seed.RawToken, SingleRecordRequest(now, []).ToByteArray());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        var (_, error) = OtlpProtobuf.ParseExportResponse(await response.Content.ReadAsByteArrayAsync());
        Assert.NotEmpty(error);
        Assert.DoesNotContain("connection refused", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictingNormalizedNames_RejectRecord()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_conf", "app_otlp_conf", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var request = SingleRecordRequest(now, [StringAttr("a.b", "one"), StringAttr("a_b", "two")]);
        var response = await PostProtobufRawAsync(client, seed.RawToken, request.ToByteArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var (rejected, _) = OtlpProtobuf.ParseExportResponse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, rejected);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task DeploymentId_ValidatedAgainstSameApp()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_dep", "app_otlp_dep", "mcl_" + RandomHex(32), now);
        await SeedDeploymentAsync(seed.OrganizationId, seed.AppId, "dep_valid_1", now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var request = new OtlpExportRequest();
        var resource = new OtlpResourceLogs();
        var scope = new OtlpScopeLogs();
        scope.LogRecords.Add(new OtlpLogRecord
        {
            TimeUnixNano = ToNanos(now),
            Body = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = "with valid deployment" },
            Attributes = [StringAttr(OtlpNormalizationService.DeploymentIdKey, "dep_valid_1")],
        });
        scope.LogRecords.Add(new OtlpLogRecord
        {
            TimeUnixNano = ToNanos(now),
            Body = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = "with bogus deployment" },
            Attributes = [StringAttr(OtlpNormalizationService.DeploymentIdKey, "dep_bogus")],
        });
        resource.ScopeLogs.Add(scope);
        request.ResourceLogs.Add(resource);

        var response = await PostProtobufRawAsync(client, seed.RawToken, request.ToByteArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = OtlpProtobuf.ParseExportRequest(Assert.Single(stub.Requests).Body);
        var records = forwarded.ResourceLogs.Single().ScopeLogs.Single().LogRecords;
        Assert.Equal(2, records.Count);
        Assert.Equal("dep_valid_1", records[0].Attributes.Single(a => a.Key == OtlpNormalizationService.DeploymentIdKey).Value.StringValue);
        Assert.DoesNotContain(records[1].Attributes, a => a.Key == OtlpNormalizationService.DeploymentIdKey);
    }

    [Fact]
    public async Task InvalidTraceAndSpanIds_AreStripped()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_otlp_ids", "app_otlp_ids", "mcl_" + RandomHex(32), now);
        var stub = _factory!.LokiStub;
        stub.Reset();
        using var client = _factory.CreateClient();

        var request = new OtlpExportRequest();
        var resource = new OtlpResourceLogs();
        var scope = new OtlpScopeLogs();
        scope.LogRecords.Add(new OtlpLogRecord
        {
            TimeUnixNano = ToNanos(now),
            Body = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = "bad ids" },
            TraceId = [1, 2, 3],
            SpanId = new byte[8],
        });
        resource.ScopeLogs.Add(scope);
        request.ResourceLogs.Add(resource);

        var response = await PostProtobufRawAsync(client, seed.RawToken, request.ToByteArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = OtlpProtobuf.ParseExportRequest(Assert.Single(stub.Requests).Body);
        var record = forwarded.ResourceLogs.Single().ScopeLogs.Single().LogRecords.Single();
        Assert.Empty(record.TraceId);
        Assert.Empty(record.SpanId);
    }

    [Fact]
    public void ProtobufCodec_RoundTrips()
    {
        var request = new OtlpExportRequest();
        var resource = new OtlpResourceLogs
        {
            Resource = new OtlpResource { Attributes = [StringAttr("service.name", "api")] },
            SchemaUrl = "https://example.test/schema/1",
        };
        var scope = new OtlpScopeLogs { Scope = new OtlpScope { Name = "s", Version = "v" } };
        scope.LogRecords.Add(new OtlpLogRecord
        {
            TimeUnixNano = 123456789,
            ObservedTimeUnixNano = 123456790,
            SeverityNumber = 17,
            SeverityText = "ERROR",
            Body = new OtlpAnyValue
            {
                Kind = OtlpValueKind.KvList,
                KvValues =
                [
                    new OtlpKeyValue { Key = "items", Value = new OtlpAnyValue { Kind = OtlpValueKind.Array, ArrayValues = [new OtlpAnyValue { Kind = OtlpValueKind.Int, IntValue = 7 }] } },
                    new OtlpKeyValue { Key = "ok", Value = new OtlpAnyValue { Kind = OtlpValueKind.Bool, BoolValue = true } },
                    new OtlpKeyValue { Key = "ratio", Value = new OtlpAnyValue { Kind = OtlpValueKind.Double, DoubleValue = 1.5 } },
                    new OtlpKeyValue { Key = "raw", Value = new OtlpAnyValue { Kind = OtlpValueKind.Bytes, BytesValue = [9, 8] } },
                ],
            },
            Attributes = [StringAttr("k", "v")],
            DroppedAttributesCount = 1,
            Flags = 1,
            TraceId = Enumerable.Repeat((byte)7, 16).ToArray(),
            SpanId = Enumerable.Repeat((byte)8, 8).ToArray(),
            EventName = "app.event",
        });
        resource.ScopeLogs.Add(scope);
        request.ResourceLogs.Add(resource);

        var parsed = OtlpProtobuf.ParseExportRequest(request.ToByteArray());

        var record = parsed.ResourceLogs.Single().ScopeLogs.Single().LogRecords.Single();
        Assert.Equal(123456789UL, record.TimeUnixNano);
        Assert.Equal(17, record.SeverityNumber);
        Assert.Equal("ERROR", record.SeverityText);
        Assert.Equal("app.event", record.EventName);
        Assert.Equal(4, record.Body!.KvValues.Count);
        Assert.Equal(7L, record.Body.KvValues[0].Value.ArrayValues.Single().IntValue);
        Assert.Equal([7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7], record.TraceId);
        Assert.Equal("https://example.test/schema/1", parsed.ResourceLogs.Single().SchemaUrl);
    }

    [Fact]
    public void Redactor_CoversIngestTokensAndSecretFields()
    {
        Assert.Contains("[REDACTED]", RuntimeLogRedactor.Redact("token mcl_ingest_abc123_xyz here"), StringComparison.Ordinal);
        Assert.DoesNotContain("mcl_ingest_abc123_xyz", RuntimeLogRedactor.Redact("token mcl_ingest_abc123_xyz here"), StringComparison.Ordinal);
        Assert.True(RuntimeLogRedactor.IsSecretFieldName("db.password"));
        Assert.True(RuntimeLogRedactor.IsSecretFieldName("client_secret"));
        Assert.False(RuntimeLogRedactor.IsSecretFieldName("user.id"));

        var value = new OtlpAnyValue
        {
            Kind = OtlpValueKind.KvList,
            KvValues =
            [
                new OtlpKeyValue { Key = "password", Value = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = "hunter2" } },
                new OtlpKeyValue { Key = "note", Value = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = "hello" } },
            ],
        };
        RuntimeLogRedactor.RedactOtlpValue(value, "attributes");
        Assert.Equal("[REDACTED]", value.KvValues[0].Value.StringValue);
        Assert.Equal("hello", value.KvValues[1].Value.StringValue);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }

    private async Task<OtlpSeed> SeedAsync(string organizationId, string appId, string tenantId, DateTimeOffset now, bool enabled = true)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();

        var salt = RandomNumberGenerator.GetBytes(16);
        var rawToken = $"mcl_ingest_{RandomHex(8)}_{RandomHex(32)}";
        var prefix = rawToken[..19];
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(rawToken),
            salt,
            210_000,
            HashAlgorithmName.SHA256,
            32);
        var keyHash = $"pbkdf2_sha256$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

        // Seed authority checkpoint so freshness passes (< 60m)
        var checkpoint = await db.AuthorityCheckpoints.FindAsync(1);
        if (checkpoint is null)
        {
            db.AuthorityCheckpoints.Add(new TelemetryAuthorityCheckpoint
            {
                Id = 1,
                Sequence = 1,
                CheckpointTimestamp = now,
                ConfirmedAt = now
            });
        }
        else
        {
            checkpoint.CheckpointTimestamp = now;
            checkpoint.ConfirmedAt = now;
        }

        db.TenantBindings.Add(new TelemetryTenantBinding
        {
            AppId = appId,
            OrganizationId = organizationId,
            TenantId = tenantId,
            Enabled = enabled,
            RetentionPolicy = "7d",
            UpdatedAt = now
        });

        db.CredentialReplicas.Add(new TelemetryCredentialReplica
        {
            Id = $"tcred_{Guid.NewGuid():N}",
            OrganizationId = organizationId,
            AppId = appId,
            ServiceName = "api",
            TenantId = tenantId,
            KeyPrefix = prefix,
            KeyHash = keyHash,
            CredentialVersion = 0,
            Status = "active",
            ExpiresAt = now.AddDays(90),
            UpdatedAt = now
        });

        await db.SaveChangesAsync();

        return new OtlpSeed(organizationId, appId, tenantId, rawToken);
    }

    private async Task SeedDeploymentAsync(string organizationId, string appId, string deploymentId, DateTimeOffset now)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        db.DeploymentReplicas.Add(new TelemetryDeploymentReplica
        {
            Id = deploymentId,
            AppId = appId,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> PostProtobufRawAsync(HttpClient client, string token, byte[] body)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/x-protobuf");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/otlp/v1/logs") { Content = content };
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return await client.SendAsync(message);
    }

    private static async Task<HttpResponseMessage> PostProtobufAsync(HttpClient client, string token, byte[] body) =>
        await PostProtobufRawAsync(client, token, body);

    private static OtlpExportRequest SingleRecordRequest(DateTimeOffset now, OtlpKeyValue[] attributes)
    {
        var request = new OtlpExportRequest();
        var resource = new OtlpResourceLogs();
        var scope = new OtlpScopeLogs();
        scope.LogRecords.Add(new OtlpLogRecord
        {
            TimeUnixNano = ToNanos(now),
            SeverityNumber = 9,
            SeverityText = "INFO",
            Body = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = "hello" },
            Attributes = [.. attributes],
        });
        resource.ScopeLogs.Add(scope);
        request.ResourceLogs.Add(resource);
        return request;
    }

    private static OtlpKeyValue StringAttr(string key, string value) =>
        new() { Key = key, Value = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = value } };

    private static ulong ToNanos(DateTimeOffset value) => (ulong)value.ToUnixTimeMilliseconds() * 1_000_000;

    private static byte[] Compress(byte[] raw)
    {
        using var destination = new MemoryStream();
        using (var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(raw);
        }

        return destination.ToArray();
    }

    private static string RandomHex(int chars)
    {
        var bytes = RandomNumberGenerator.GetBytes((chars + 1) / 2);
        return Convert.ToHexString(bytes).ToLowerInvariant()[..chars];
    }

    private sealed record OtlpSeed(string OrganizationId, string AppId, string TenantId, string RawToken);

    private sealed class OtlpApiFactory(SqliteConnection connection) : WebApplicationFactory<Minicloud.Telemetry.Program>
    {
        public readonly OtlpLokiStub LokiStub = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["ConnectionStrings:Telemetry"] = "Data Source=:memory:",
                    ["Telemetry:Environment"] = "test",
                    ["Telemetry:LokiBaseAddress"] = "http://loki:3100",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<TelemetryDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<TelemetryDbContext>>();
                services.AddDbContext<TelemetryDbContext>(options => options.UseSqlite(connection));
                services.RemoveAll<IHttpClientFactory>();
                var stub = LokiStub;
                services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(stub));
            });
        }
    }

    private sealed class OtlpLokiStub : HttpMessageHandler
    {
        private readonly object _sync = new();
        public List<CapturedRequest> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(OtlpProtobuf.EncodeExportResponse(0, string.Empty)),
            };

        public void Reset()
        {
            lock (_sync)
            {
                Requests.Clear();
                Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(OtlpProtobuf.EncodeExportResponse(0, string.Empty)),
                };
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var hasAuth = request.Headers.Contains("Authorization");
            string? tenant = null;
            if (request.Headers.TryGetValues("X-Scope-OrgID", out var tenants))
            {
                tenant = string.Join(",", tenants);
            }

            Func<HttpRequestMessage, HttpResponseMessage> responder;
            lock (_sync)
            {
                Requests.Add(new CapturedRequest(tenant ?? string.Empty, hasAuth, body));
                responder = Responder;
            }

            return responder(request);
        }
    }

    private sealed record CapturedRequest(string Tenant, bool HasAuthorization, byte[] Body);

    private sealed class StubHttpClientFactory(OtlpLokiStub stub) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(stub, disposeHandler: false);
    }
}

public sealed class TelemetryQuotaServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private OtlpQuotaFactory? _factory;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new OtlpQuotaFactory(_connection);
        _factory.CreateClient().Dispose();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    [Fact]
    public async Task DailyBudget_IsEnforcedAndSurvivesRestart()
    {
        var quotas = CreateQuotas(appDaily: 1024, orgDaily: 10 * 1024 * 1024, globalDaily: 100 * 1024 * 1024);

        var first = await quotas.ReserveAsync("app_q1", "org_q1", 600, CancellationToken.None);
        Assert.True(first.Admitted);
        var second = await quotas.ReserveAsync("app_q1", "org_q1", 500, CancellationToken.None);
        Assert.False(second.Admitted);
        Assert.NotNull(second.RetryAfter);

        // A fresh instance (process restart) sees the same persisted budget:
        // 600 + 424 == 1024 exactly, then 1 more byte is denied.
        var restarted = CreateQuotas(appDaily: 1024, orgDaily: 10 * 1024 * 1024, globalDaily: 100 * 1024 * 1024);
        Assert.True((await restarted.ReserveAsync("app_q1", "org_q1", 424, CancellationToken.None)).Admitted);
        var third = await restarted.ReserveAsync("app_q1", "org_q1", 1, CancellationToken.None);
        Assert.False(third.Admitted);

        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        var row = await db.DailyUsages.SingleAsync(x => x.AppId == "app_q1");
        Assert.Equal(1024, row.AdmittedBytes);
    }

    [Fact]
    public async Task OrganizationBudget_PreventsBypassAcrossApps()
    {
        var quotas = CreateQuotas(appDaily: 10 * 1024 * 1024, orgDaily: 1500, globalDaily: 100 * 1024 * 1024);

        Assert.True((await quotas.ReserveAsync("app_qo1", "org_qo", 1000, CancellationToken.None)).Admitted);
        var denied = await quotas.ReserveAsync("app_qo2", "org_qo", 1000, CancellationToken.None);
        Assert.False(denied.Admitted);
    }

    [Fact]
    public async Task ConcurrentReservations_CannotOverspend()
    {
        var quotas = CreateQuotas(appDaily: 10_000, orgDaily: 1_000_000, globalDaily: 10_000_000);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => quotas.ReserveAsync("app_qc", "org_qc", 1000, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        var admitted = results.Count(r => r.Admitted);
        Assert.Equal(10, admitted);

        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        var row = await db.DailyUsages.SingleAsync(x => x.AppId == "app_qc");
        Assert.Equal(10_000, row.AdmittedBytes);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }

    private TelemetryQuotaService CreateQuotas(long appDaily, long orgDaily, long globalDaily)
    {
        var scopes = _factory!.Services.GetRequiredService<IServiceScopeFactory>();
        var opts = Options.Create(new TelemetryOptions
        {
            PerAppDailyBytes = appDaily,
            PerOrganizationDailyBytes = orgDaily,
            WholeStackDailyBytes = globalDaily,
            PerAppBurstBytes = 10 * 1024 * 1024,
            PerAppSustainedBytesPerSecond = 10 * 1024 * 1024,
        });
        return new TelemetryQuotaService(scopes, TimeProvider.System, opts);
    }

    private sealed class OtlpQuotaFactory(SqliteConnection connection) : WebApplicationFactory<Minicloud.Telemetry.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["ConnectionStrings:Telemetry"] = "Data Source=:memory:",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<TelemetryDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<TelemetryDbContext>>();
                services.AddDbContext<TelemetryDbContext>(options => options.UseSqlite(connection));
            });
        }
    }
}
