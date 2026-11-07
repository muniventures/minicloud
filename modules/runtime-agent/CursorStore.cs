using System.Text.Json;

namespace Minicloud.RuntimeAgent;

public sealed class CursorStore(string path)
{
    private readonly Dictionary<string, string> _cursors = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Cursors => _cursors;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<AgentState>(stream, JsonDefaults.Options, cancellationToken);
        _cursors.Clear();
        foreach (var pair in state?.Cursors ?? new Dictionary<string, string>())
        {
            _cursors[pair.Key] = pair.Value;
        }
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, string> acceptedCursors, CancellationToken cancellationToken)
    {
        foreach (var pair in acceptedCursors)
        {
            _cursors[pair.Key] = pair.Value;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, new AgentState(_cursors), JsonDefaults.Options, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}

public sealed record AgentState(IReadOnlyDictionary<string, string> Cursors);
