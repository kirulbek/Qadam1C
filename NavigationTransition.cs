using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Qadam1C;

// Hold the previous frame while the native tree changes. Input passes through to 1C.
// The bitmap exists only in memory; no controls or redraw settings in the 1C process are changed.
internal sealed class NavigationTransition : Form
{
    private readonly Bitmap snapshot;
    private readonly System.Windows.Forms.Timer lifetime = new() { Interval = 8000 };
    private NavigationTransition(Bitmap image, Rectangle bounds)
    {
        snapshot = image;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual;
        Bounds = bounds; DoubleBuffered = true; Opacity = 0.999;
        lifetime.Tick += (_, _) => Dispose();
        lifetime.Start();
    }
    private sealed record OwnerWindow(IntPtr Handle) : IWin32Window;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x80000 | 0x20 | 0x80; return value; }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; } // HTTRANSPARENT
        if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
        base.WndProc(ref m);
    }
    protected override void OnPaint(PaintEventArgs e) => e.Graphics.DrawImageUnscaled(snapshot, 0, 0);
    protected override void Dispose(bool disposing)
    {
        if (disposing) { lifetime.Dispose(); }
        base.Dispose(disposing);
        if (disposing) snapshot.Dispose();
    }
    internal static NavigationTransition? TryShow(IntPtr window)
    {
        Bitmap? bitmap = null;
        try
        {
            if (CompletionNative.GetForegroundWindow() != window || !GetClientRect(window, out var rect)) return null;
            var origin = new NativePoint();
            if (!ClientToScreen(window, ref origin)) return null;
            var bounds = Rectangle.Intersect(new Rectangle(origin.X, origin.Y, rect.Right, rect.Bottom), SystemInformation.VirtualScreen);
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            var form = new NavigationTransition(bitmap, bounds);
            bitmap = null;
            try { form.Show(new OwnerWindow(window)); form.Update(); return form; }
            catch { form.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is ExternalException or Win32Exception or InvalidOperationException or ArgumentException)
        { bitmap?.Dispose(); return null; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
}
