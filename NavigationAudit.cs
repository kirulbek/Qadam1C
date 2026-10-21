using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Qadam1C;

internal sealed class NavigationAudit(IntPtr window, string path) : IDisposable
{
    private readonly Frame before = Read(window);
    private readonly Stopwatch watch = Stopwatch.StartNew();
    public bool Completed { get; set; }
    public bool TransitionShown { get; set; }
    private sealed record Frame(int Left, int Top, int Right, int Bottom, bool Maximized, bool Minimized);
    private static Frame Read(IntPtr window)
    {
        GetWindowRect(window, out var rect);
        return new(rect.Left, rect.Top, rect.Right, rect.Bottom, IsZoomed(window), IsIconic(window));
    }
    public void Dispose()
    {
        var after = Read(window);
        try
        {
            File.WriteAllText(AppDataPaths.FilePath("favorites-navigation-last.json"), JsonSerializer.Serialize(new
            {
                Path = path, Completed, TransitionShown, Milliseconds = watch.ElapsedMilliseconds,
                Before = before, After = after, WindowUnchanged = before == after
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
}
