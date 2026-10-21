using System.IO;

namespace Qadam1C;

// Record state transitions only: never module text, snippets or clipboard data.
internal static class InteractionDiagnostics
{
    internal static void Write(string state)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "interaction-status.txt");
            File.WriteAllText(path, DateTimeOffset.Now.ToString("O") + " " + state);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
