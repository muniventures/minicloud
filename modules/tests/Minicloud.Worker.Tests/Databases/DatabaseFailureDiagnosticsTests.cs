using Minicloud.Worker.Databases;

namespace Minicloud.Worker.Tests.Databases;

public sealed class DatabaseFailureDiagnosticsTests
{
    [Fact]
    public void RemoteSummaryKeepsOnlyKnownStageAndTypeClassifications()
    {
        var error = new DatabaseRemoteOperationException("database_access_change_rolled_back", "dispatch.py", "access-readiness", "CalledProcessError");
        Assert.Equal("access-readiness", error.Stage);
        Assert.Equal("CalledProcessError", error.RuntimeErrorType);
        Assert.Equal("database_access_change_rolled_back", error.Code);
    }

    [Fact]
    public void UntrustedRemoteMetadataCannotBecomeAnOperatorSummary()
    {
        const string secret = "SENTINEL_PASSWORD_TOKEN_PROVIDER_SECRET";
        var error = new DatabaseRemoteOperationException("database_runtime_failed", "dispatch.py", secret, secret);
        Assert.Equal("dispatch.py", error.Stage);
        Assert.Equal("unknown", error.RuntimeErrorType);
        var untrustedScript = new DatabaseRemoteOperationException("database_runtime_failed", secret, secret, secret);
        Assert.Equal("remote", untrustedScript.Stage);
    }
}
