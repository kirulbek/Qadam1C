using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Qadam1C;

// A small non-activating overlay. Never inserts a control into the 1C process.
internal sealed class EditorActionButton : Form
{
    private readonly ContextMenuStrip menu = new();
    private readonly ToolTip tip = new();
    private EditorSnapshot? snapshot;
    private bool hovered;
    public Action? OpenTemplates { get; set; }
    public Action? OpenMetadata { get; set; }
    public Action? OpenFavorites { get; set; }
    public Action? AddCurrentFavorite { get; set; }
    public Func<EditorSnapshot, string, Task>? EditRequested { get; set; }
    public event EventHandler? MenuOpening;
    public bool MenuVisible => menu.Visible;
    internal bool IsPresented => IsHandleCreated && CompletionNative.IsWindowVisible(Handle);
    internal void Dismiss()
    {
        if (IsPresented) CompletionNative.ShowWindow(Handle, 0);
    }
    internal bool OwnsMenuKey(int key) => key is 27 or 13 or 38 or 40;
    internal void MenuKey(int key)
    {
        if (!menu.Visible) return;
        if (key == 27) { menu.Close(); return; }
        var items = menu.Items.OfType<ToolStripMenuItem>().Where(i => i.Enabled).ToArray();
        var index = Array.FindIndex(items, i => i.Selected);
        if (key == 13) { if (index >= 0) items[index].PerformClick(); return; }
        if (key is 38 or 40 && items.Length > 0)
        {
            var next = index < 0 ? (key == 40 ? 0 : items.Length - 1) :
                (index + (key == 40 ? 1 : items.Length - 1)) % items.Length;
            items[next].Select();
        }
    }
    internal void CloseMenu() => menu.Close();
    internal void CheckForeground()
    {
        var foreground = CompletionNative.GetForegroundWindow();
        if (snapshot is not null && foreground != snapshot.Window && foreground != Handle &&
            (!menu.Visible || foreground != menu.Handle))
        { menu.Close(); Dismiss(); }
    }

    public EditorActionButton()
    {
        Text = "Qadam1C — действия с кодом";
        AccessibleName = Text;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Cursor = Cursors.Hand;
        tip.SetToolTip(this, "Qadam1C · Действия с кодом\nCtrl+Alt+Enter");
        menu.Font = new Font("Segoe UI", 10);
        menu.BackColor = Color.FromArgb(248, 250, 253);
        menu.ForeColor = Color.FromArgb(28, 42, 61);
        menu.ShowImageMargin = false;
        menu.Padding = new Padding(6);
        menu.Items.Add(new ToolStripLabel("Qadam1C  ·  Действия") { ForeColor = Color.FromArgb(35, 112, 155) });
        menu.Items.Add(new ToolStripSeparator());
        AddWindow("Шаблоны…", () => OpenTemplates?.Invoke());
        AddWindow("Метаданные конфигурации…", () => OpenMetadata?.Invoke());
        AddWindow("Панель: процедуры и избранное…", () => OpenFavorites?.Invoke());
        AddWindow("Добавить текущий объект в избранное · Ctrl+Alt+B", () => AddCurrentFavorite?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        AddEdit("Дублировать текущую строку", "duplicate");
        AddEdit("Вставить область", "region");
        menu.Items.Add(new ToolStripSeparator());
        AddEdit("Форматировать выделение", "format");
        AddEdit("Форматировать весь модуль", "format-all");
        AddEdit("Выровнять выделение по =", "align");
        menu.Items.Add(new ToolStripSeparator());
        AddEdit("Добавить // к строкам", "comment-add");
        AddEdit("Снять // со строк", "comment-remove");
        AddEdit("Добавить | к строкам", "pipe-add");
        AddEdit("Снять | со строк", "pipe-remove");
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Закрыть меню", null, (_, _) => menu.Close());
    }

    private void AddWindow(string text, Action open)
    {
        menu.Items.Add(text, null, async (_, _) =>
        {
            var expected = snapshot;
            var focusWasOurs = IsMenuForeground();
            menu.Close();
            // Finish native menu focus restoration before activating a new form.
            await Task.Delay(100);
            if (expected is not null && await RestoreEditorAsync(expected, focusWasOurs))
                open();
        });
    }

    private void AddEdit(string text, string command)
    {
        var item = menu.Items.Add(text);
        item.Tag = command;
        if (command == "duplicate" && item is ToolStripMenuItem duplicate)
            duplicate.ShortcutKeyDisplayString = "Ctrl+D";
        item.Click += async (_, _) =>
        {
            var expected = snapshot;
            var focusWasOurs = IsMenuForeground();
            menu.Close();
            if (expected is null || EditRequested is null) return;
            // Menu dismissal restores the editor. Never force focus to an old window.
            await Task.Delay(100);
            if (await RestoreEditorAsync(expected, focusWasOurs))
                await EditRequested(expected, command);
        };
    }
    private bool IsMenuForeground()
    {
        var window = CompletionNative.GetForegroundWindow();
        return window == Handle || window == menu.Handle;
    }
    private async Task<bool> RestoreEditorAsync(EditorSnapshot expected, bool focusWasOurs)
    {
        var foreground = CompletionNative.GetForegroundWindow();
        if (foreground == expected.Window) return true;
        // A mouse click can activate our menu. Restore only our own focus;
        // never steal it after the user has switched to another application.
        if (!focusWasOurs || !IsMenuForeground()) return false;
        SetForegroundWindow(expected.Window);
        for (var i = 0; i < 10; i++)
        {
            if (CompletionNative.GetForegroundWindow() == expected.Window) return true;
            if (!IsMenuForeground()) return false;
            await Task.Delay(20);
        }
        return false;
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x80; return value; }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE, deliver click.
        base.WndProc(ref m);
    }
    public void Present(EditorSnapshot value)
    {
        if (MenuVisible) return;
        snapshot = value;
        var side = Math.Clamp(value.LineHeight, 18, 28);
        // The gutter is part of the editor rectangle; reserve its leftmost strip.
        var point = new Point(value.EditorLeft + 1, value.LineTop + (value.LineHeight - side) / 2);
        var area = Screen.FromPoint(point).WorkingArea;
        if (!area.Contains(new Rectangle(point, new Size(side, side)))) { Dismiss(); return; }
        if (Size != new Size(side, side)) Size = new Size(side, side);
        if (Location != point) Location = point;
        if (!IsPresented)
        {
            CompletionNative.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            // Native ShowWindow bypasses the managed Form.Show path which
            // normally applies TopMost. Keep the button above its editor too.
            CompletionNative.SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height, 0x0010 | 0x0200);
        }
    }
    public void ShowMenu()
    {
        if (snapshot is null || !IsPresented || MenuVisible ||
            CompletionNative.GetForegroundWindow() != snapshot.Window) return;
        foreach (ToolStripItem item in menu.Items)
            if (item.Tag is string command)
            {
                item.Enabled = command switch
                {
                    "region" => snapshot.HasSelection || IsBlankLine(snapshot),
                    "format" or "align" or "comment-add" or "comment-remove" or "pipe-add" or "pipe-remove" => snapshot.HasSelection,
                    "format-all" => snapshot.Text.Length > 0,
                    _ => !snapshot.HasSelection
                };
                if (command == "region") item.Text = snapshot.HasSelection ? "Окружить областью" : "Вставить область";
            }
        MenuOpening?.Invoke(this, EventArgs.Empty);
        // The overlay is shown natively without Form.Show. Anchor the menu in
        // screen coordinates rather than requiring a managed-visible owner form.
        menu.Show(PointToScreen(new Point(Width + 4, 0)));
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) ShowMenu();
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(Width / 24f, Height / 24f);
        using var background = new LinearGradientBrush(new Rectangle(1, 1, 22, 22),
            hovered ? Color.FromArgb(35, 199, 199) : Color.FromArgb(31, 159, 181),
            Color.FromArgb(49, 92, 199), 55f);
        g.FillEllipse(background, 1, 1, 22, 22);
        using var edge = new Pen(Color.FromArgb(195, 235, 246), 0.8f);
        g.DrawEllipse(edge, 1, 1, 22, 22);
        using var pen = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        // Own vector mark: code chevrons with a forward slash.
        g.DrawLines(pen, [new PointF(8, 8), new PointF(4.5f, 12), new PointF(8, 16)]);
        g.DrawLines(pen, [new PointF(16, 8), new PointF(19.5f, 12), new PointF(16, 16)]);
        g.DrawLine(pen, 13.5f, 7, 10.5f, 17);
    }
    internal static ExpandedSnippet? Expansion(EditorSnapshot value, string command)
    {
        var start = value.Offset == 0 ? 0 : value.Text.LastIndexOf('\n', value.Offset - 1) + 1;
        var end = value.Text.IndexOf('\n', value.Offset);
        if (end < 0) end = value.Text.Length;
        if (end > start && value.Text[end - 1] == '\r') end--;
        var newline = value.Text.Contains("\r\n") ? "\r\n" : "\n";
        if (command == "duplicate")
        {
            var prefix = value.Text[start..value.Offset];
            var suffix = value.Text[value.Offset..Math.Max(value.Offset, end)];
            var insertion = suffix + newline + prefix;
            return new ExpandedSnippet(insertion, insertion.Length);
        }
        if (command == "region")
        {
            if (value.HasSelection)
            {
                var selectedText = value.Text.Substring(value.Offset, value.SelectionLength);
                var outerIndent = new string(selectedText.TakeWhile(c => c is ' ' or '\t').ToArray());
                var wrapped = outerIndent + "#Область ИмяОбласти" + newline + selectedText +
                    newline + outerIndent + "#КонецОбласти";
                return new ExpandedSnippet(wrapped, outerIndent.Length + "#Область ".Length);
            }
            if (!IsBlankLine(value)) return null;
            var indent = new string(value.Text[start..value.Offset].TakeWhile(c => c is ' ' or '\t').ToArray());
            var text = "#Область ИмяОбласти" + newline + indent + newline + indent + "#КонецОбласти";
            return new ExpandedSnippet(text, "#Область ".Length);
        }
        return null;
    }
    private static bool IsBlankLine(EditorSnapshot value)
    {
        var start = value.Offset == 0 ? 0 : value.Text.LastIndexOf('\n', value.Offset - 1) + 1;
        var end = value.Text.IndexOf('\n', value.Offset);
        if (end < 0) end = value.Text.Length;
        return string.IsNullOrWhiteSpace(value.Text[start..end]);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { menu.Dispose(); tip.Dispose(); }
        base.Dispose(disposing);
    }
}
