using System.Runtime.InteropServices;

namespace Qadam1C;

internal sealed class SearchTextBox : TextBox
{
    private readonly ClearButton clear = new();
    private readonly ToolTip tip = new();

    internal SearchTextBox()
    {
        Controls.Add(clear);
        tip.SetToolTip(clear, "Очистить поиск");
        clear.Click += (_, _) => { Clear(); Focus(); };
        TextChanged += (_, _) => LayoutClearButton();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutClearButton();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutClearButton();
    }

    private void LayoutClearButton()
    {
        if (clear is null) return;
        var size = Math.Max(16, ClientSize.Height);
        clear.SetBounds(Math.Max(0, ClientSize.Width - size), 0, size, ClientSize.Height);
        clear.BackColor = BackColor;
        clear.Visible = TextLength > 0;
        if (IsHandleCreated)
            SendMessage(Handle, 0x00D3, new IntPtr(2), new IntPtr((TextLength > 0 ? size + 2 : 2) << 16)); // EM_SETMARGINS, right margin only
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tip.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private sealed class ClearButton : Button
    {
        internal ClearButton()
        {
            TabStop = false;
            AccessibleName = "Очистить поиск";
            Cursor = Cursors.Arrow;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.FromArgb(233, 239, 247);
            FlatAppearance.MouseDownBackColor = Color.FromArgb(218, 234, 252);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var radius = Math.Max(3f, 3.5f * DeviceDpi / 96f);
            var x = ClientSize.Width / 2f;
            var y = ClientSize.Height / 2f;
            using var pen = new Pen(Color.FromArgb(91, 105, 122), Math.Max(1.3f, DeviceDpi / 96f));
            e.Graphics.DrawLine(pen, x - radius, y - radius, x + radius, y + radius);
            e.Graphics.DrawLine(pen, x - radius, y + radius, x + radius, y - radius);
        }
    }
}
