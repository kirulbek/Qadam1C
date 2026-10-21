using System.Runtime.InteropServices;

namespace Qadam1C;

// An owned external window that follows 1C, not a child control injected into the Configurator.
internal sealed class FavoritesAttachment : IDisposable
{
    private readonly Form form;
    private readonly Func<bool> busy;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    private IntPtr target, previousOwner;
    private Rectangle floatingBounds;
    private bool hidden;
    public bool Enabled { get; private set; }
    public FavoritesAttachment(Form form, Func<bool> busy)
    {
        this.form = form; this.busy = busy;
        timer.Tick += (_, _) => Follow();
    }
    public void SetTarget(IntPtr value)
    {
        target = value;
        // Floating favorites also belong to 1C, so activating the owner never covers the tool.
        // Attachment changes positioning only; ownership keeps the tool visible in both modes.
        SetWindowLongPtr(form.Handle, -8, target);
        Follow();
    }
    public void SetEnabled(bool value)
    {
        if (value == Enabled) return;
        if (value)
        {
            if (target == IntPtr.Zero || !IsWindow(target)) throw new InvalidOperationException("Сначала выберите открытый Конфигуратор.");
            floatingBounds = form.Bounds;
            previousOwner = GetWindowLongPtr(form.Handle, -8);
            SetWindowLongPtr(form.Handle, -8, target);
            Enabled = true; timer.Start(); Follow();
        }
        else
        {
            Enabled = false; timer.Stop();
            SetWindowLongPtr(form.Handle, -8, previousOwner);
            form.Bounds = floatingBounds;
            if (hidden) { ShowWindow(form.Handle, 4); hidden = false; }
        }
    }
    private void Follow()
    {
        if (!Enabled || form.IsDisposed || busy()) return;
        var foreground = CompletionNative.GetForegroundWindow();
        CompletionNative.GetWindowThreadProcessId(foreground, out var pid);
        if (!IsWindow(target) || IsIconic(target) || (foreground != target && pid != Environment.ProcessId))
        {
            if (!hidden) { ShowWindow(form.Handle, 0); hidden = true; }
            return;
        }
        if (!GetClientRect(target, out var client)) return;
        var origin = new NativePoint();
        if (!ClientToScreen(target, ref origin)) return;
        if (!GetWindowRect(form.Handle, out var current)) return;
        var width = Math.Min(Math.Max(300, current.Right - current.Left), Math.Max(300, client.Right - 30));
        var height = Math.Max(280, client.Bottom - 125);
        var x = origin.X + client.Right - width - 8;
        var y = origin.Y + 100;
        if (current.Left != x || current.Top != y || current.Right - current.Left != width || current.Bottom - current.Top != height)
            SetWindowPos(form.Handle, IntPtr.Zero, x, y, width, height, 0x14);
        if (hidden) { ShowWindow(form.Handle, 4); hidden = false; }
    }
    public void Dispose() { timer.Stop(); timer.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
