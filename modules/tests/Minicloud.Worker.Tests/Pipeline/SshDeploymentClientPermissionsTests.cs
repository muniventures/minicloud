using System.Runtime.CompilerServices;
using Minicloud.Worker.Ssh;
using Renci.SshNet.Sftp;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class SshDeploymentClientPermissionsTests
{
    [Fact]
    public void PrivateModesAreAcceptedBySshNetAndRestrictAccess()
    {
        var attributes = (SftpFileAttributes)RuntimeHelpers.GetUninitializedObject(typeof(SftpFileAttributes));

        attributes.SetPermissions(SshDeploymentClient.PrivateDeploymentDirectoryMode);
        Assert.True(attributes.OwnerCanRead);
        Assert.True(attributes.OwnerCanWrite);
        Assert.True(attributes.OwnerCanExecute);
        Assert.False(attributes.GroupCanRead);
        Assert.False(attributes.OthersCanRead);

        attributes.SetPermissions(SshDeploymentClient.PrivatePayloadFileMode);
        Assert.True(attributes.OwnerCanRead);
        Assert.True(attributes.OwnerCanWrite);
        Assert.False(attributes.OwnerCanExecute);
        Assert.False(attributes.GroupCanRead);
        Assert.False(attributes.OthersCanRead);
    }
}
