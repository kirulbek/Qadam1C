using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Qadam1C;

// Read-only diagnostic for reserving a gutter without a profiling file.
// Stores geometry and class names only, never window captions or module text.
internal static class NativeEditorLayoutProbe
{
    internal static int Run(int pid)
    {
        using var process = Process.GetProcessById(pid);
        if (process.ProcessName != "1cv8" || !process.MainWindowTitle.Contains("Конфигуратор")) return 1;
        var rows = new List<object>();
        var topLevel = new List<object>();
        var root = process.MainWindowHandle;
        var rootClass = new StringBuilder(256);
        GetClassName(root, rootClass, rootClass.Capacity);
        GetWindowThreadProcessId(root, out var rootPid);
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var ownerPid);
            if (ownerPid != pid) return true;
            var className = new StringBuilder(256);
            var caption = new StringBuilder(1024);
            GetClassName(window, className, className.Capacity);
            GetWindowText(window, caption, caption.Capacity);
            GetWindowRect(window, out var rect);
            topLevel.Add(new { Handle = window.ToInt64(), Owner = GetWindow(window, 4).ToInt64(),
                Class = className.ToString(), Visible = IsWindowVisible(window),
                EditorCaption = EditorReader.IsEditorCaption(caption.ToString()),
                Bounds = new[] { rect.Left, rect.Top, rect.Right, rect.Bottom } });
            return true;
        }, IntPtr.Zero);
        EnumChildWindows(root, (window, _) =>
        {
            if (rows.Count >= 1000) return false;
            var className = new StringBuilder(256);
            var caption = new StringBuilder(1024);
            GetClassName(window, className, className.Capacity);
            GetWindowText(window, caption, caption.Capacity);
            GetWindowRect(window, out var rect);
            GetClientRect(window, out var client);
            rows.Add(new { Handle = window.ToInt64(), Parent = GetParent(window).ToInt64(),
                Class = className.ToString(), Visible = IsWindowVisible(window),
                EditorCaption = EditorReader.IsEditorCaption(caption.ToString()),
                Bounds = new[] { rect.Left, rect.Top, rect.Right, rect.Bottom },
                Client = new[] { client.Left, client.Top, client.Right, client.Bottom } });
            return true;
        }, IntPtr.Zero);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "editor-native-layout.json"),
            JsonSerializer.Serialize(new { Pid = pid, Root = root.ToInt64(), RootValid = IsWindow(root),
                RootPid = rootPid, RootMinimized = IsIconic(root), RootClass = rootClass.ToString(), TopLevel = topLevel, Windows = rows },
                new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
}
