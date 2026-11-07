namespace Minicloud.RuntimeAgent;

public static class AgentOperations
{
    public static async Task TryAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"minicloud-agent {operation} failed: {RuntimeLogRedactor.Redact(ex.Message)}");
        }
    }
}
