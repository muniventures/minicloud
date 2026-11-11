using System.Buffers.Binary;

namespace Minicloud.Worker.Pipeline.Steps;

internal static class RuntimeAgentBundle
{
    // The SSH probe returns exit status only, so reserve distinct architecture results.
    internal const string ArchitectureCommand = """
machine=$(uname -m) || exit 23
case "$machine" in
    x86_64|amd64) exit 20 ;;
    aarch64|arm64) exit 21 ;;
    *) exit 22 ;;
esac
""";

    internal static async Task<string> SelectBinaryAsync(string bundlePath, Func<string, Task<int>> probe)
    {
        var result = await probe(ArchitectureCommand);
        var rid = result switch
        {
            20 => "linux-x64",
            21 => "linux-arm64",
            22 => throw new PipelineStepException("runtime_agent_install_failed",
                "Unsupported runtime agent target architecture. Supported targets are Linux x64 and arm64."),
            _ => throw new PipelineStepException("runtime_agent_install_failed",
                "Could not determine runtime agent target architecture using remote uname -m.")
        };
        var path = Path.Combine(bundlePath, rid, "minicloud-agent");
        // Source/development bundles may contain one flat executable; never assume its architecture.
        if (!File.Exists(path))
        {
            path = Path.Combine(bundlePath, "minicloud-agent");
        }
        if (!File.Exists(path))
        {
            throw new PipelineStepException("runtime_agent_install_failed",
                $"Runtime agent bundle is missing the executable for target {rid}.");
        }
        try
        {
            using var input = File.OpenRead(path);
            var header = new byte[20];
            input.ReadExactly(header);
            var machine = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2));
            if (header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F' ||
                header[4] != 2 || header[5] != 1 || machine != (rid == "linux-x64" ? 62 : 183))
            {
                throw new PipelineStepException("runtime_agent_install_failed",
                    $"Runtime agent executable is not a {rid} ELF binary.");
            }
        }
        catch (IOException)
        {
            throw new PipelineStepException("runtime_agent_install_failed",
                $"Runtime agent executable for target {rid} could not be read as an ELF binary.");
        }
        return path;
    }
}
