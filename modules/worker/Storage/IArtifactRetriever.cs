namespace Minicloud.Worker.Storage;

public interface IArtifactRetriever
{
    /// <summary>
    /// Opens a readable stream to the deployment artifact archive (e.g. service.zip).
    /// </summary>
    /// <param name="artifactId">The unique identifier of the artifact or its relative storage path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A readable stream of the artifact package.</returns>
    Task<Stream> OpenArtifactStreamAsync(string artifactId, CancellationToken cancellationToken = default);
}
