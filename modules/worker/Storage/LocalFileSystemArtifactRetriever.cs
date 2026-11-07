using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;

namespace Minicloud.Worker.Storage;

public sealed class LocalFileSystemArtifactRetriever : IArtifactRetriever
{
    private readonly WorkerOptions _options;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<LocalFileSystemArtifactRetriever>? _logger;

    public LocalFileSystemArtifactRetriever(
        IOptions<WorkerOptions> options,
        IServiceScopeFactory? scopeFactory = null,
        ILogger<LocalFileSystemArtifactRetriever>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<Stream> OpenArtifactStreamAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);

        var root = Path.GetFullPath(_options.ArtifactRootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Artifact root directory does not exist: {root}");
        }

        // 1. Check if artifactId is directly a path relative to the root
        var directPath = Path.GetFullPath(Path.Combine(root, artifactId));
        if (directPath.StartsWith(root, StringComparison.Ordinal) && File.Exists(directPath))
        {
            _logger?.LogDebug("Resolved artifact {ArtifactId} directly at {Path}", artifactId, directPath);
            return new FileStream(directPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        }

        // 2. Try looking up the storage path in the database if scope factory is available
        if (_scopeFactory is not null)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
                var artifact = await db.DeploymentArtifacts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken);

                if (artifact is not null && !string.IsNullOrWhiteSpace(artifact.StoragePath))
                {
                    var dbPath = Path.GetFullPath(Path.Combine(root, artifact.StoragePath));
                    if (dbPath.StartsWith(root, StringComparison.Ordinal) && File.Exists(dbPath))
                    {
                        _logger?.LogDebug("Resolved artifact {ArtifactId} via database storage path at {Path}", artifactId, dbPath);
                        return new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to resolve artifact {ArtifactId} via database lookup, falling back to filesystem scan", artifactId);
            }
        }

        // 3. Fallback: Search directory for artifact file or folder
        var cleanId = artifactId.Replace("art_", "");
        var candidates = Directory.EnumerateFiles(root, "*service.zip", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories));

        foreach (var candidate in candidates)
        {
            if (candidate.Contains(artifactId, StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains(cleanId, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug("Resolved artifact {ArtifactId} via search at {Path}", artifactId, candidate);
                return new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            }
        }

        throw new FileNotFoundException($"Artifact '{artifactId}' could not be located under '{root}'.", directPath);
    }
}
