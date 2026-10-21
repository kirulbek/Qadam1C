using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qadam1C;

internal sealed record ModuleMethod(string Name, string Kind, string Parameters, bool Exported, int Offset, int Line)
{
    public override string ToString() => $"{(Kind.Equals("Функция", StringComparison.OrdinalIgnoreCase) || Kind.Equals("Function", StringComparison.OrdinalIgnoreCase) ? "ƒ" : "▪")} {Name}({Parameters}){(Exported ? " · Экспорт" : "")}  · {Line}";
}

internal static class ModuleOutline
{
    internal static ModuleMethod[] Parse(string text)
    {
        var code = MetadataCompletion.Code(text);
        var matches = Regex.Matches(code,
            @"(?im)^[\t ]*(?<kind>Процедура|Функция|Procedure|Function)[\t ]+(?<name>[\p{L}_][\p{L}\p{N}_]*)\s*\((?<parameters>[^()]*)\)[\t ]*(?<export>Экспорт\b|Export\b)?",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        var result = new List<ModuleMethod>();
        int cursor = 0, line = 1;
        foreach (Match match in matches)
        {
            var offset = match.Groups["kind"].Index;
            while (cursor < offset) { if (text[cursor++] == '\n') line++; }
            var parameters = match.Groups["parameters"];
            result.Add(new(match.Groups["name"].Value, match.Groups["kind"].Value,
                Regex.Replace(text.Substring(parameters.Index, parameters.Length).Trim(), @"\s+", " "),
                match.Groups["export"].Success, offset, line));
        }
        return result.ToArray();
    }

    internal static bool SameModule(EditorSnapshot expected, EditorSnapshot current) =>
        expected.Window == current.Window && expected.Session == current.Session &&
        expected.WindowTitle == current.WindowTitle && expected.Text == current.Text;

    internal static async Task Navigate(EditorSnapshot expected, ModuleMethod method)
    {
        var current = await FocusCurrent(expected);
        EditorReader.SelectRange(current, method.Offset, method.Offset);
        EditorReader.ScrollSelectionIntoView(expected.Window);
    }
    internal static async Task<EditorSnapshot> FocusCurrent(EditorSnapshot expected)
    {
        var foreground = CompletionNative.GetForegroundWindow();
        CompletionNative.GetWindowThreadProcessId(foreground, out var pid);
        if (foreground != expected.Window && pid != Environment.ProcessId)
            throw new InvalidOperationException("Выберите панель Qadam1C или нужный модуль 1С и повторите переход.");
        for (var i = 0; i < 40 && (CompletionNative.KeyDown(1) || CompletionNative.KeyDown(13)); i++)
            await Task.Delay(25);
        if (CompletionNative.GetForegroundWindow() != foreground)
            throw new InvalidOperationException("Активное окно изменилось. Переход отменён.");
        if (CompletionNative.KeyDown(1) || CompletionNative.KeyDown(13))
            throw new InvalidOperationException("Отпустите кнопку мыши и Enter, затем повторите переход.");
        if (!SetForegroundWindow(expected.Window) && CompletionNative.GetForegroundWindow() != expected.Window)
            throw new InvalidOperationException("Windows не разрешила перейти в 1С.");
        await Task.Delay(100);
        var current = await Task.Run(EditorReader.ReadForActions);
        if (current is null || !SameModule(expected, current))
            throw new InvalidOperationException("Модуль изменился. Дождитесь обновления списка и повторите переход.");
        return current;
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}

internal sealed record WorkspacePanelSettings(bool Methods = true, bool Favorites = true)
{
    internal static WorkspacePanelSettings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<WorkspacePanelSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, true);
    }
}

internal sealed class ModuleOutlinePane : UserControl
{
    private readonly Label caption = new() { Dock = DockStyle.Top, Height = 30, Padding = new Padding(6, 4, 6, 2), AutoEllipsis = true,
        Font = PanelTypography.Secondary, ForeColor = PanelTypography.Muted, Text = "", Visible = false };
    private readonly SearchTextBox search = new() { Dock = DockStyle.Top, PlaceholderText = "Найти метод или область…" };
    private readonly DataGridView methods = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false, BorderStyle = BorderStyle.None, BackgroundColor = Color.White,
        AutoGenerateColumns = false, EnableHeadersVisualStyles = false,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        GridColor = Color.FromArgb(237, 241, 246), AccessibleName = "Процедуры и функции текущего модуля"
    };
    private readonly Label hint = new() { Dock = DockStyle.Bottom, Height = 23, Text = "Двойной щелчок или Enter — перейти", Padding = new Padding(6, 3, 6, 2),
        Font = PanelTypography.Caption, ForeColor = PanelTypography.Muted };
    private readonly Label signature = new() { Dock = DockStyle.Bottom, Height = 34, AutoEllipsis = true, Padding = new Padding(6, 5, 6, 2),
        Font = PanelTypography.Signature, ForeColor = PanelTypography.Muted };
    private readonly ToolTip details = new();
    private readonly ContextMenuStrip methodMenu = new();
    private EditorSnapshot? snapshot;
    private ModuleMethod[] entries = [];
    private ModuleRegion[] regionEntries = [];
    private readonly Dictionary<string, HashSet<string>> foldedModules = new();
    private HashSet<string> folded = [];
    private sealed record MethodDrag(EditorSnapshot Snapshot, ModuleMethod Method, string Token);
    private MethodDrag? pendingDrag, currentDrag;
    private Point dragOrigin;
    private int dropRow = -1;
    private bool dropAfter;
    private readonly int foldColumn;
    private readonly System.Windows.Forms.Timer dragScroll = new() { Interval = 140 };
    internal Func<EditorSnapshot, ModuleMethod, Task>? NavigateRequested { get; set; }
    internal Func<EditorSnapshot, MethodMovePlan, Task<bool>>? ApplyRequested { get; set; }
    private ModuleArrangement? arrangement;
    private EditorSnapshot? observed;
    private readonly FlowLayoutPanel arrangementBar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.FromArgb(255, 246, 223), Visible = false, Padding = new Padding(3) };
    private readonly Button applyOrder = new() { Text = "Применить порядок", AutoSize = true };
    private readonly Button cancelOrder = new() { Text = "Отменить", AutoSize = true };
    private readonly Label pendingInfo = new() { AutoSize = true, Padding = new Padding(3, 7, 3, 3), Font = PanelTypography.Secondary };
    internal bool HasPendingChanges => arrangement is not null;
    internal Action<string>? ReportStatus { get; set; }
    internal int MethodCount => entries.Length;
    internal ModuleOutlinePane()
    {
        methods.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(233, 239, 247), ForeColor = PanelTypography.Muted, Font = PanelTypography.Secondary,
            Padding = new Padding(5), SelectionBackColor = Color.FromArgb(233, 239, 247)
        };
        methods.DefaultCellStyle = new DataGridViewCellStyle
        {
            ForeColor = PanelTypography.Primary, BackColor = Color.White, Font = PanelTypography.Secondary,
            SelectionBackColor = Color.FromArgb(218, 234, 252), SelectionForeColor = Color.FromArgb(20, 55, 96),
            Padding = new Padding(5, 2, 5, 2)
        };
        methods.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 252);
        methods.Columns.Add(new DataGridViewImageColumn { Name = "Kind", HeaderText = "", Width = 34, MinimumWidth = 30,
            Resizable = DataGridViewTriState.False, ImageLayout = DataGridViewImageCellLayout.Normal,
            DefaultCellStyle = new DataGridViewCellStyle { NullValue = null, Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = new Padding(0) } });
        methods.Columns.Add(new DataGridViewTextBoxColumn { Name = "Method", HeaderText = "Имя", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 100, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Font = PanelTypography.Identifier, ForeColor = PanelTypography.Primary } });
        methods.Columns.Add(new DataGridViewTextBoxColumn { Name = "Export", HeaderText = "Экспорт", Width = 92, SortMode = DataGridViewColumnSortMode.NotSortable });
        methods.Columns.Add(new DataGridViewTextBoxColumn { Name = "Line", HeaderText = "Строка", Width = 88, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight, ForeColor = Color.FromArgb(99, 115, 137) } });
        foldColumn = methods.Columns.Add(new DataGridViewTextBoxColumn { Name = "Fold", HeaderText = "", Width = 28, MinimumWidth = 28,
            Resizable = DataGridViewTriState.False, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = new Padding(0) } });
        methods.Columns[foldColumn].DisplayIndex = 0;
        methods.CellPainting += (_, e) =>
        {
            if (e.ColumnIndex != foldColumn || e.RowIndex < 0) return;
            e.PaintBackground(e.ClipBounds, true);
            if (methods.Rows[e.RowIndex].Tag is ModuleRegion region && e.Graphics is { } graphics)
            {
                var size = Math.Max(12, (int)(14 * methods.DeviceDpi / 96f));
                var x = e.CellBounds.Left + (e.CellBounds.Width - size) / 2;
                var y = e.CellBounds.Top + (e.CellBounds.Height - size) / 2;
                using var pen = new Pen(Color.FromArgb(79, 104, 133), Math.Max(1, methods.DeviceDpi / 96f));
                graphics.DrawRectangle(pen, x, y, size, size);
                graphics.DrawLine(pen, x + 3, y + size / 2, x + size - 3, y + size / 2);
                if (folded.Contains(RegionKey(region)) && search.Text.Trim().Length == 0)
                    graphics.DrawLine(pen, x + size / 2, y + 3, x + size / 2, y + size - 3);
            }
            e.Handled = true;
        };
        methods.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        methods.RowTemplate.Height = 28;
        methods.SelectionChanged += (_, _) => UpdateSignature();
        methodMenu.Items.Add("Перейти к методу", null, async (_, _) => await Jump());
        methodMenu.Items.Add("Переместить в область…", null, (_, _) => RequestMove());
        var up = methodMenu.Items.Add("Переместить метод выше", null, async (_, _) => await RequestStep(-1));
        var down = methodMenu.Items.Add("Переместить метод ниже", null, async (_, _) => await RequestStep(1));
        methodMenu.Items.Add(new ToolStripSeparator());
        var toggle = methodMenu.Items.Add("Свернуть / развернуть область", null, (_, _) => ToggleSelectedRegion());
        methodMenu.Items.Add("Свернуть все области", null, (_, _) => { foreach (var region in regionEntries) folded.Add(RegionKey(region)); Reload(); });
        methodMenu.Items.Add("Развернуть все области", null, (_, _) => { folded.Clear(); Reload(); });
        methodMenu.Opening += (_, e) =>
        {
            if (snapshot is null || methods.CurrentRow?.Tag is null) { e.Cancel = true; return; }
            methodMenu.Items[0].Text = methods.CurrentRow.Tag is ModuleRegion ? "Перейти к области" : "Перейти к методу";
            methodMenu.Items[1].Enabled = methods.CurrentRow.Tag is ModuleMethod && !snapshot.WindowTitle.Contains("Только для чтения", StringComparison.OrdinalIgnoreCase) &&
                !snapshot.WindowTitle.Contains("Read-only", StringComparison.OrdinalIgnoreCase);
            up.Enabled = down.Enabled = methodMenu.Items[1].Enabled;
            toggle.Enabled = methods.CurrentRow.Tag is ModuleRegion;
        };
        methods.ContextMenuStrip = methodMenu;
        methods.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0) methods.CurrentCell = methods.Rows[e.RowIndex].Cells[1];
            pendingDrag = null;
            if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && methods.Rows[e.RowIndex].Tag is ModuleMethod method && snapshot is { } expected &&
                !expected.WindowTitle.Contains("Только для чтения", StringComparison.OrdinalIgnoreCase) && !expected.WindowTitle.Contains("Read-only", StringComparison.OrdinalIgnoreCase))
            {
                pendingDrag = new(expected, method, Guid.NewGuid().ToString("N"));
                var cell = methods.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
                dragOrigin = new Point(cell.Left + e.X, cell.Top + e.Y);
            }
        };
        ConfigureDragging();
        methods.CellMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && (e.ColumnIndex == 0 || e.ColumnIndex == foldColumn) && e.RowIndex >= 0 && e.Clicks == 1 && methods.Rows[e.RowIndex].Tag is ModuleRegion)
                ToggleSelectedRegion();
        };
        arrangementBar.Controls.AddRange([applyOrder, cancelOrder, pendingInfo]);
        applyOrder.Click += async (_, _) => await ApplyOrder();
        cancelOrder.Click += (_, _) => CancelOrder();
        Controls.Add(methods); Controls.Add(signature); Controls.Add(search); Controls.Add(caption); Controls.Add(arrangementBar); Controls.Add(hint);
        search.TextChanged += (_, _) => Reload();
        methods.CellMouseDoubleClick += async (_, e) => { if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.ColumnIndex != 0 && e.ColumnIndex != foldColumn) await Jump(); };
        methods.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; await Jump(); }
            else if (!e.Control && !e.Alt && !e.Shift && e.KeyCode is Keys.Left or Keys.Right && methods.CurrentRow?.Tag is ModuleRegion region)
            {
                if (e.KeyCode == Keys.Left) folded.Add(RegionKey(region)); else folded.Remove(RegionKey(region));
                e.Handled = e.SuppressKeyPress = true; Reload();
            }
        };
        search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; await Jump(); }
            else if (e.KeyCode == Keys.Down) { e.Handled = e.SuppressKeyPress = true; methods.Focus(); }
        };
    }
    internal void UpdateEditor(EditorSnapshot value)
    {
        observed = value;
        if (arrangement is not null) { UpdateArrangementBar(); return; }
        var changed = snapshot is null || !ModuleOutline.SameModule(snapshot, value);
        snapshot = value;
        var moduleKey = value.Session + "|" + value.Window + "|" + value.WindowTitle;
        if (!foldedModules.TryGetValue(moduleKey, out var moduleFolded)) foldedModules[moduleKey] = moduleFolded = [];
        folded = moduleFolded;
        if (!changed) return;
        try { entries = ModuleOutline.Parse(value.Text); regionEntries = MethodRegions.Parse(value.Text, strict: false); }
        catch (RegexMatchTimeoutException) { entries = []; regionEntries = []; caption.Text = "Слишком сложный модуль для построения списка"; caption.Visible = true; Reload(); return; }
        var separator = value.WindowTitle.IndexOf(" - Конфигуратор", StringComparison.OrdinalIgnoreCase);
        caption.Text = separator > 0 ? value.WindowTitle[..separator] : value.WindowTitle;
        caption.Visible = caption.Text.Length > 0;
        details.SetToolTip(caption, value.WindowTitle);
        Reload();
    }
    internal void ClearEditor()
    {
        if (arrangement is not null || snapshot is null) return;
        snapshot = null; entries = []; regionEntries = []; caption.Text = ""; caption.Visible = false; Reload();
    }
    private void Reload()
    {
        var selected = methods.CurrentRow?.Tag;
        methods.SuspendLayout(); methods.Rows.Clear();
        var query = search.Text.Trim();
        var matchingRegions = regionEntries.Where(r => r.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var matchingMethods = entries.Where(m => (m.Name + " " + m.Kind + " " + m.Parameters).Contains(query, StringComparison.OrdinalIgnoreCase) ||
            matchingRegions.Any(r => r.BodyStart <= m.Offset && r.End >= m.Offset)).ToArray();
        var visibleRegions = regionEntries.Where(r => query.Length == 0 || matchingRegions.Contains(r) ||
            matchingMethods.Any(m => r.BodyStart <= m.Offset && r.End >= m.Offset) ||
            matchingRegions.Any(child => r.Start < child.Start && r.End >= child.End)).ToArray();
        var rows = matchingMethods.Select(m => (Offset: m.Offset, Item: (object)m))
            .Concat(visibleRegions.Select(r => (Offset: r.Start, Item: (object)r))).OrderBy(r => r.Offset);
        foreach (var entry in rows)
        {
            if (query.Length == 0 && regionEntries.Any(r => r.BodyStart <= entry.Offset && r.End > entry.Offset && folded.Contains(RegionKey(r)))) continue;
            if (entry.Item is ModuleRegion region)
            {
                var regionRow = methods.Rows[methods.Rows.Add(MethodKindIcons.Region, region.Name, "", region.Line)];
                regionRow.Tag = region;
                regionRow.Cells[foldColumn].Value = folded.Contains(RegionKey(region)) && query.Length == 0 ? "+" : "−";
                regionRow.DefaultCellStyle.BackColor = Color.FromArgb(235, 240, 248);
                regionRow.Cells[1].Style.Font = PanelTypography.RegionHeading;
                var depth = regionEntries.Count(r => r.Start < region.Start && r.End >= region.End);
                regionRow.Cells[1].Style.Padding = new Padding(5 + Math.Min(depth, 10) * 14, 2, 5, 2);
                foreach (DataGridViewCell cell in regionRow.Cells) cell.ToolTipText = "Область: " + region.Path + "\nСтрока " + region.Line;
                continue;
            }
            var method = (ModuleMethod)entry.Item;
            var function = method.Kind.Equals("Функция", StringComparison.OrdinalIgnoreCase) || method.Kind.Equals("Function", StringComparison.OrdinalIgnoreCase);
            var row = methods.Rows[methods.Rows.Add(function ? MethodKindIcons.Function : MethodKindIcons.Procedure, method.Name, method.Exported ? "Да" : "", method.Line)];
            row.Tag = method;
            var methodDepth = regionEntries.Count(r => r.BodyStart <= method.Offset && r.End >= method.Offset);
            row.Cells[1].Style.Padding = new Padding(5 + Math.Min(methodDepth, 10) * 14, 2, 5, 2);
            row.Cells[0].Style.ForeColor = function ? Color.FromArgb(111, 70, 163) : Color.FromArgb(24, 116, 133);
            foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = $"{method.Kind} {method.Name}({method.Parameters}){(method.Exported ? " Экспорт" : "")}\nСтрока {method.Line}";
        }
        var selectedRow = methods.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => Equals(r.Tag, selected));
        if (selectedRow is not null) methods.CurrentCell = selectedRow.Cells[1];
        else if (methods.Rows.Count > 0) methods.CurrentCell = methods.Rows[0].Cells[1];
        methods.ResumeLayout(); UpdateSignature();
        hint.Text = snapshot is null ? "Модуль не выбран" : $"Методы: {matchingMethods.Length}/{entries.Length} · Области: {visibleRegions.Length}/{regionEntries.Length} · Двойной щелчок / Enter";
    }
    private void UpdateSignature()
    {
        if (IsDisposed || Disposing) return;
        signature.Text = methods.CurrentRow?.Tag switch
        {
            ModuleMethod m => $"{m.Name}({m.Parameters}){(m.Exported ? " Экспорт" : "")}",
            ModuleRegion r => "Область: " + r.Path,
            _ => ""
        };
        details.SetToolTip(signature, signature.Text);
    }
    private async Task Jump()
    {
        if (HasPendingChanges) { ReportStatus?.Invoke("Сначала примените или отмените порядок: номера строк показаны для будущего кода."); return; }
        if (snapshot is not null && methods.CurrentRow?.Tag is ModuleMethod method && NavigateRequested is not null)
            await NavigateRequested(snapshot, method);
        else if (snapshot is not null && methods.CurrentRow?.Tag is ModuleRegion region && NavigateRequested is not null)
            await NavigateRequested(snapshot, new ModuleMethod(region.Name, "Область", "", false, region.Start, region.Line));
    }
    private void RequestMove()
    {
        if (snapshot is not { } expected || methods.CurrentRow?.Tag is not ModuleMethod method) return;
        try
        {
            var choices = MethodRegions.Parse(expected.Text);
            if (choices.Length == 0) { ReportStatus?.Invoke("В текущем модуле нет областей. Сначала добавьте #Область и #КонецОбласти."); return; }
            using var picker = new MethodRegionPicker(method.Name, choices);
            if (picker.ShowDialog(FindForm()) == DialogResult.OK && picker.Selected is { } selected)
                Stage(MethodRegions.Plan(expected.Text, method, selected));
        }
        catch (Exception ex) { ReportStatus?.Invoke(ex.Message); }
    }
    private string RegionKey(ModuleRegion region) => region.Path + "|" + regionEntries.Count(r => r.Path == region.Path && r.Start < region.Start);
    private void ConfigureDragging()
    {
        methods.AllowDrop = true;
        dragScroll.Tick += (_, _) =>
        {
            if (currentDrag is null || methods.Rows.Count == 0) return;
            var point = methods.PointToClient(Control.MousePosition);
            if (!methods.ClientRectangle.Contains(point)) return;
            var first = methods.FirstDisplayedScrollingRowIndex;
            if (first < 0) return;
            var direction = point.Y < methods.ColumnHeadersHeight + 24 ? -1 : point.Y > methods.ClientSize.Height - 24 ? 1 : 0;
            var next = Math.Clamp(first + direction, 0, methods.Rows.Count - 1);
            if (next != first)
            {
                methods.FirstDisplayedScrollingRowIndex = next;
                UpdateDropTarget(point);
            }
        };
        methods.DragEnter += (_, e) => e.Effect = currentDrag is not null && e.Data?.GetData("Qadam1C.MethodMove") as string == currentDrag.Token
            ? DragDropEffects.Move : DragDropEffects.None;
        methods.MouseUp += (_, _) => pendingDrag = null;
        methods.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || pendingDrag is null) return;
            var size = SystemInformation.DragSize;
            if (new Rectangle(dragOrigin.X - size.Width / 2, dragOrigin.Y - size.Height / 2, size.Width, size.Height).Contains(e.Location)) return;
            currentDrag = pendingDrag; pendingDrag = null;
            UpdateDropTarget(e.Location);
            dragScroll.Start();
            try { methods.DoDragDrop(new DataObject("Qadam1C.MethodMove", currentDrag.Token), DragDropEffects.Move); }
            finally { dragScroll.Stop(); currentDrag = null; dropRow = -1; methods.Invalidate(); RestoreHint(); }
        };
        methods.DragOver += (_, e) =>
        {
            e.Effect = DragDropEffects.None;
            if (currentDrag is null || e.Data?.GetData("Qadam1C.MethodMove") as string != currentDrag.Token) { methods.Invalidate(); return; }
            if (UpdateDropTarget(methods.PointToClient(new Point(e.X, e.Y)))) e.Effect = DragDropEffects.Move;
        };
        methods.DragLeave += (_, _) => { dropRow = -1; methods.Invalidate(); RestoreHint(); };
        methods.DragDrop += (_, e) =>
        {
            UpdateDropTarget(methods.PointToClient(new Point(e.X, e.Y)));
            var source = currentDrag; var target = dropRow >= 0 && dropRow < methods.Rows.Count ? methods.Rows[dropRow].Tag : null; var after = dropAfter;
            dropRow = -1; methods.Invalidate();
            if (source is null || e.Data?.GetData("Qadam1C.MethodMove") as string != source.Token) return;
            try
            {
                if (target is ModuleRegion region)
                {
                    folded.Remove(RegionKey(region));
                    Stage(MethodRegions.Plan(source.Snapshot.Text, source.Method, region));
                }
                else if (target is ModuleMethod method)
                    Stage(MethodRegions.PlanRelative(source.Snapshot.Text, source.Method, method, after));
            }
            catch (Exception ex) { ReportStatus?.Invoke(ex.Message); }
        };
        methods.RowPostPaint += (_, e) =>
        {
            if (e.RowIndex != dropRow) return;
            using var pen = new Pen(Color.FromArgb(25, 115, 210), 3);
            var row = methods.Rows[e.RowIndex];
            if (row.Tag is ModuleRegion) e.Graphics.DrawRectangle(pen, e.RowBounds.Left + 2, e.RowBounds.Top + 1, e.RowBounds.Width - 5, e.RowBounds.Height - 3);
            else
            {
                var y = dropAfter ? e.RowBounds.Bottom - 2 : e.RowBounds.Top + 1;
                e.Graphics.DrawLine(pen, e.RowBounds.Left + 3, y, e.RowBounds.Right - 4, y);
            }
        };
    }
    private bool UpdateDropTarget(Point point)
    {
        dropRow = -1;
        var hit = methods.HitTest(point.X, point.Y);
        if (currentDrag is null || hit.RowIndex < 0 || Equals(methods.Rows[hit.RowIndex].Tag, currentDrag.Method))
        { methods.Invalidate(); return false; }
        var row = methods.Rows[hit.RowIndex];
        var rectangle = methods.GetRowDisplayRectangle(hit.RowIndex, false);
        dropAfter = point.Y >= rectangle.Top + rectangle.Height / 2;
        if (row.Tag is ModuleRegion region) hint.Text = "Перенести в конец области «" + region.Name + "»";
        else if (row.Tag is ModuleMethod method) hint.Text = (dropAfter ? "После «" : "Перед «") + method.Name + "»";
        else { methods.Invalidate(); return false; }
        dropRow = hit.RowIndex; methods.Invalidate(); return true;
    }
    private void RestoreHint() => hint.Text = snapshot is null ? "Модуль не выбран" : $"Методы: {entries.Length} · Области: {regionEntries.Length} · Методы можно перетаскивать";
    private void ToggleSelectedRegion()
    {
        if (methods.CurrentRow?.Tag is not ModuleRegion region) return;
        var key = RegionKey(region); if (!folded.Add(key)) folded.Remove(key); Reload();
    }
    private Task RequestStep(int direction)
    {
        try
        {
            if (snapshot is { } expected && methods.CurrentRow?.Tag is ModuleMethod method)
                Stage(MethodRegions.PlanStep(expected.Text, method, direction));
        }
        catch (Exception ex) { ReportStatus?.Invoke(ex.Message); }
        return Task.CompletedTask;
    }
    internal void Stage(MethodMovePlan plan)
    {
        if (snapshot is null) return;
        arrangement ??= new ModuleArrangement(snapshot);
        arrangement.Stage(plan);
        var preview = snapshot with { Text = arrangement.Text };
        // Rebuild only the local preview. Polling must keep the original snapshot for validation.
        entries = ModuleOutline.Parse(preview.Text);
        regionEntries = MethodRegions.Parse(preview.Text, strict: false);
        foreach (var region in regionEntries.Where(r => r.BodyStart <= plan.Start + plan.Cursor && r.End > plan.Start + plan.Cursor))
            folded.Remove(RegionKey(region));
        snapshot = preview;
        Reload();
        var moved = methods.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is ModuleMethod m && m.Offset == plan.Start + plan.Cursor);
        if (moved is not null) methods.CurrentCell = moved.Cells[1];
        UpdateArrangementBar();
        ReportStatus?.Invoke("Порядок изменён только в панели. Разложите методы и нажмите «Применить порядок».");
    }
    private void UpdateArrangementBar()
    {
        arrangementBar.Visible = arrangement is not null;
        if (arrangement is null) return;
        var matches = observed is not null && ModuleOutline.SameModule(arrangement.Original, observed);
        applyOrder.Enabled = matches;
        pendingInfo.Text = matches ? $"Не применено: {arrangement.Changes}" : "Модуль изменился или переключён";
        details.SetToolTip(pendingInfo, "Показан будущий порядок. Строки пересчитаны для него. Вернитесь в исходный неизменённый модуль либо отмените план.");
    }
    internal void CancelOrder()
    {
        var current = observed ?? arrangement?.Original;
        arrangement = null; snapshot = null; arrangementBar.Visible = false;
        if (current is not null) UpdateEditor(current); else ClearEditor();
        ReportStatus?.Invoke("Перестановки отменены. Код в 1С не изменялся.");
    }
    private async Task ApplyOrder()
    {
        if (arrangement is not { } pending || ApplyRequested is null || !applyOrder.Enabled) return;
        applyOrder.Enabled = cancelOrder.Enabled = methods.Enabled = false;
        try
        {
            if (await ApplyRequested(pending.Original, pending.BuildPlan()))
            {
                var applied = pending.Original with { Text = pending.Text };
                arrangement = null; snapshot = null; observed = applied;
                arrangementBar.Visible = false;
                UpdateEditor(applied);
            }
        }
        finally { cancelOrder.Enabled = methods.Enabled = true; UpdateArrangementBar(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { dragScroll.Dispose(); details.Dispose(); methodMenu.Dispose(); }
        base.Dispose(disposing);
    }
}
