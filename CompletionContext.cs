using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using DrawingPoint = System.Drawing.Point;

namespace Qadam1C;

internal sealed record EditorSnapshot(IntPtr Window, string Text, int Offset, string Prefix, int X, int Y,
    string Session = "", string WindowTitle = "", int EditorLeft = 0, int LineTop = 0,
    int LineHeight = 20, bool CanComplete = true, bool HasSelection = false, int SelectionLength = 0);

internal static class EditorReader
{
    internal static bool IsCodeWindow(string title)
    {
        var separator = title.IndexOf(" - Конфигуратор", StringComparison.OrdinalIgnoreCase);
        if (separator <= 0) return false;
        return IsEditorCaption(title[..separator]);
    }
    internal static bool IsEditorCaption(string caption)
    {
        if (caption.Contains(".mxl", StringComparison.OrdinalIgnoreCase) ||
            caption.Contains("Табличный документ", StringComparison.OrdinalIgnoreCase) ||
            caption.Contains("Отчет по конфигурации", StringComparison.OrdinalIgnoreCase)) return false;
        return Regex.IsMatch(caption, @"(?:^|:\s*)Модуль(?:\s|$)", RegexOptions.IgnoreCase) ||
            caption.Contains("Текстовый документ", StringComparison.OrdinalIgnoreCase) ||
            caption.Contains(".txt", StringComparison.OrdinalIgnoreCase) ||
            caption.Contains(".bsl", StringComparison.OrdinalIgnoreCase);
    }
    internal static string LastStatus = "not-read";
    private static EditorSnapshot? Missing(string reason) { LastStatus = reason; return null; }
    public static EditorSnapshot? Read() => ReadCore(false);
    internal static EditorSnapshot? ReadForActions() => ReadCore(true);
    internal static string CurrentEditorCaption(IntPtr window)
    {
        _ = EditorScope(window, out var title);
        return title;
    }
    private static EditorSnapshot? ReadCore(bool forActions)
    {
        LastStatus = "reading";
        var window = CompletionNative.GetForegroundWindow();
        CompletionNative.GetWindowThreadProcessId(window, out var pid);
        using var process = Process.GetProcessById((int)pid);
        if (process.ProcessName != "1cv8" ||
            !process.MainWindowTitle.Contains("Конфигуратор", StringComparison.OrdinalIgnoreCase)) return Missing("outside-configurator");
        // Do not enter the spreadsheet UIA provider, including while its report is opening.
        _ = EditorScope(window, out var editorTitle);
        var focus = FocusedDocument(window);
        if (focus is null || focus.Current.ProcessId != pid || focus.Current.ControlType != ControlType.Document ||
            !focus.TryGetCurrentPattern(TextPattern.Pattern, out var raw)) return Missing("no-focused-document");
        var pattern = (TextPattern)raw;
        var range = pattern.DocumentRange;
        var text = range.GetText(1_000_001);
        if (text.Length > 1_000_000) return Missing("module-too-large");
        var selections = pattern.GetSelection();
        if (selections.Length != 1) return Missing("multiple-selections");
        var selected = selections[0].GetText(1).Length != 0;
        if (selected && !forActions) return Missing("text-selected");
        var selection = selections[0];
        var prefixRange = range.Clone();
        prefixRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
        var before = prefixRange.GetText(1_000_001);
        var offset = before.Length;
        if (offset > text.Length) return Missing("invalid-offset");
        var insideWord = offset < text.Length && IsWord(text[offset]);
        if (insideWord && !forActions) return Missing("inside-word");
        var start = offset;
        while (start > 0 && IsWord(text[start - 1])) start--;
        var prefix = text[start..offset];
        var adjacent = selection.Clone();
        adjacent.MoveEndpointByRange(TextPatternRangeEndpoint.End, adjacent, TextPatternRangeEndpoint.Start);
        var following = adjacent.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1) == 1;
        if (!following && adjacent.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) != -1)
            return Missing("no-adjacent-character");
        var rectangles = adjacent.GetBoundingRectangles();
        if (rectangles.Length == 0) return Missing("no-character-bounds");
        var rectangle = rectangles[0];
        if (CompletionNative.GetForegroundWindow() != window) return Missing("foreground-changed");
        LastStatus = "ready";
        return new EditorSnapshot(window, text, offset, prefix,
            (int)(following ? rectangle.Left : rectangle.Right), (int)rectangle.Bottom + 2,
            BaseIdentity.Get(process.Id, process.StartTime.ToUniversalTime().Ticks),
            editorTitle.Length > 0 ? editorTitle : process.MainWindowTitle,
            (int)focus.Current.BoundingRectangle.Left, (int)rectangle.Top, Math.Max(16, (int)rectangle.Height),
            !selected && !insideWord, selected, selected ? selection.GetText(-1).Length : 0);
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    internal static EditorSnapshot SelectWholeLines(EditorSnapshot value)
    {
        var start = value.Offset == 0 ? 0 : value.Text.LastIndexOf('\n', value.Offset - 1) + 1;
        var end = value.Offset + value.SelectionLength;
        if (end > 0 && value.Text[end - 1] == '\n')
        {
            end--;
            if (end > 0 && value.Text[end - 1] == '\r') end--;
        }
        else
        {
            var newline = value.Text.IndexOf('\n', end);
            end = newline < 0 ? value.Text.Length : newline;
            if (end > start && value.Text[end - 1] == '\r') end--;
        }
        return SelectRange(value, start, end);
    }
    internal static EditorSnapshot SelectRange(EditorSnapshot value, int start, int end)
    {
        var document = FocusedDocument(value.Window) ?? throw new InvalidOperationException("Редактор недоступен.");
        var pattern = (TextPattern)document.GetCurrentPattern(TextPattern.Pattern);
        var range = pattern.DocumentRange.Clone();
        if (range.GetText(-1) != value.Text) throw new InvalidOperationException("Текст изменился.");
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        static int Characters(string s) => s.Replace("\r\n", "\n").Length;
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, Characters(value.Text[..end]));
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, Characters(value.Text[..start]));
        if (range.GetText(-1) != value.Text[start..end]) throw new InvalidOperationException("Не удалось выделить строки.");
        range.Select();
        return ReadForActions() ?? throw new InvalidOperationException("Не удалось подтвердить выделение.");
    }
    internal static void ScrollSelectionIntoView(IntPtr window)
    {
        var document = FocusedDocument(window) ?? throw new InvalidOperationException("Редактор недоступен.");
        var pattern = (TextPattern)document.GetCurrentPattern(TextPattern.Pattern);
        foreach (var selection in pattern.GetSelection())
        {
            try { selection.ScrollIntoView(true); }
            // Some 1C editor providers support selecting a range but do not
            // implement UIA scrolling. The validated caret move is still valid.
            catch (NotImplementedException) { }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80004001)) { }
        }
    }
    internal static AutomationElement? FocusedDocument(IntPtr window)
    {
        if (CompletionNative.GetForegroundWindow() != window) return null;
        CompletionNative.GetWindowThreadProcessId(window, out var pid);
        using var process = Process.GetProcessById((int)pid);
        if (process.ProcessName != "1cv8" || !process.MainWindowTitle.Contains("Конфигуратор")) return null;
        var scope = EditorScope(window, out _);
        var root = AutomationElement.FromHandle(scope == IntPtr.Zero ? window : scope);
        if (root.Current.ProcessId != pid || !root.Current.IsEnabled) return null;
        // 1C can implement every editor as a virtual control under the main HWND.
        // A bounded walk treats all documents and tables as leaves: never expand
        // a spreadsheet provider, unlike FindFirst(TreeScope.Descendants).
        var pending = new Stack<(AutomationElement Element, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 1500)
        {
            var (element, depth) = pending.Pop();
            var state = element.Current;
            if (state.ProcessId != pid || state.IsOffscreen) continue;
            if (state.ControlType == ControlType.Document)
            {
                if (state.HasKeyboardFocus &&
                    !(bool)element.GetCurrentPropertyValue(AutomationElement.IsGridPatternAvailableProperty) &&
                    !(bool)element.GetCurrentPropertyValue(AutomationElement.IsTablePatternAvailableProperty) &&
                    (bool)element.GetCurrentPropertyValue(AutomationElement.IsTextPatternAvailableProperty))
                    return element;
                continue;
            }
            if (depth >= 18 || state.ControlType == ControlType.Table || state.ControlType == ControlType.DataGrid ||
                state.ControlType == ControlType.Tree || state.ControlType == ControlType.List ||
                state.ControlType == ControlType.ToolBar) continue;
            var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            var siblings = 0;
            while (child is not null && siblings++ < 200)
            {
                pending.Push((child, depth + 1));
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
        }
        return null;
    }
    private static IntPtr EditorScope(IntPtr window, out string title)
    {
        title = "";
        var thread = CompletionNative.GetWindowThreadProcessId(window, out var pid);
        using var process = Process.GetProcessById((int)pid);
        if (process.ProcessName != "1cv8" || !process.MainWindowTitle.Contains("Конфигуратор")) return IntPtr.Zero;
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var obtained = GetGUIThreadInfo(thread, ref info);
        if (!obtained || info.Focus == IntPtr.Zero || info.Focus == window) return IntPtr.Zero;
        var current = info.Focus;
        for (var i = 0; i < 20 && current != IntPtr.Zero && current != window; i++)
        {
            CompletionNative.GetWindowThreadProcessId(current, out var owner);
            if (owner != pid) return IntPtr.Zero;
            var caption = new StringBuilder(1024);
            GetWindowText(current, caption, caption.Capacity);
            if (IsEditorCaption(caption.ToString()))
            {
                title = caption.ToString();
                return current;
            }
            current = GetParent(current);
        }
        if (!IsCodeWindow(process.MainWindowTitle)) return IntPtr.Zero;
        title = process.MainWindowTitle;
        return info.Focus;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
}

internal static partial class CompletionWords
{
    private static readonly string[] Builtin =
    [
        "Если", "Тогда", "Иначе", "ИначеЕсли", "КонецЕсли", "Для", "Каждого", "Из", "По", "Цикл", "КонецЦикла",
        "Пока", "Процедура", "КонецПроцедуры", "Функция", "КонецФункции", "Возврат", "Экспорт", "Перем",
        "Попытка", "Исключение", "КонецПопытки", "ВызватьИсключение", "Продолжить", "Прервать", "Новый",
        "Истина", "Ложь", "Неопределено", "Сообщить", "ЗначениеЗаполнено", "ТипЗнч", "Тип", "Строка",
        "Число", "Дата", "СтрДлина", "СтрНайти", "СтрЗаменить", "СтрШаблон", "СокрЛП", "Лев", "Прав",
        "Сред", "ТекущаяДата", "ЗаполнитьЗначенияСвойств", "Массив", "Структура", "Соответствие",
        "ТаблицаЗначений", "Запрос", "Документы", "Справочники", "РегистрыСведений", "РегистрыНакопления"
    ];

    public static string[] Find(EditorSnapshot snapshot, bool manual)
    {
        if (!manual && snapshot.Prefix.Length < 2) return [];
        // Word completion only; semantic type/member resolution is a later stage.
        return Identifier().Matches(snapshot.Text).Select(m => m.Value).Concat(Builtin)
            .Where(w => SmartCompletion.MatchScore(w, snapshot.Prefix) >= 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(w => SmartCompletion.MatchScore(w, snapshot.Prefix))
            .ThenBy(w => w.Length).ThenBy(w => w, StringComparer.OrdinalIgnoreCase).Take(80).ToArray();
    }

    [GeneratedRegex(@"[\p{L}_][\p{L}\p{N}_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}

internal sealed class CompletionPopup : Form
{
    private IntPtr ownerWindow;
    public string[] Items { get; private set; } = [];
    public int Selected { get; set; }
    internal int PaintCount { get; private set; }
    internal int PositionUpdates { get; private set; }
    internal int VisibilityChanges { get; private set; }
    internal bool IsPresented => IsHandleCreated && CompletionNative.IsWindowVisible(Handle);
    internal void Dismiss()
    {
        if (!IsPresented) return;
        CompletionNative.ShowWindow(Handle, 0);
        VisibilityChanges++;
    }
    public CompletionPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        Text = "Qadam1C — подсказка";
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(28, 35, 47);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 11);
        DoubleBuffered = true;
        // Selection is handled by the keyboard hook in 1C. This overlay must never
        // become an activation target, including when another Qadam1C form is open.
        Enabled = false;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000080; return value; }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
        base.WndProc(ref m);
    }
    public void Present(EditorSnapshot snapshot, string[] items)
    {
        var contentChanged = !Items.SequenceEqual(items);
        if (contentChanged) Selected = 0;
        Items = items;
        var desiredSize = new Size(390, items.Length * 30 + 36);
        var area = Screen.FromPoint(new DrawingPoint(snapshot.X, snapshot.Y)).WorkingArea;
        var desiredLocation = new DrawingPoint(Math.Clamp(snapshot.X, area.Left, Math.Max(area.Left, area.Right - desiredSize.Width)),
            Math.Clamp(snapshot.Y, area.Top, Math.Max(area.Top, area.Bottom - desiredSize.Height)));
        var geometryChanged = Size != desiredSize || Location != desiredLocation;
        if (IsPresented && ownerWindow != snapshot.Window) Dismiss();
        var newlyShown = !IsPresented;
        if (!contentChanged && !geometryChanged && !newlyShown) return;
        if (Size != desiredSize) Size = desiredSize;
        if (Location != desiredLocation) Location = desiredLocation;
        if (newlyShown)
        {
            ownerWindow = snapshot.Window;
            // Bypass Form.Show's managed activation path entirely. Windows displays
            // the HWND without making it the foreground or active window.
            CompletionNative.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            VisibilityChanges++;
        }
        // UI Automation returns physical coordinates. PerMonitorV2 avoids DPI
        // virtualization, and NOACTIVATE keeps keyboard focus in the 1C editor.
        if (newlyShown)
        {
            if (!CompletionNative.SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height, 0x0010 | 0x0200))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        if (newlyShown || geometryChanged) PositionUpdates++;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        PaintCount++;
        base.OnPaint(e);
        for (var i = 0; i < Items.Length; i++)
        {
            if (i == Selected)
            {
                using var brush = new SolidBrush(Color.FromArgb(42, 90, 128));
                e.Graphics.FillRectangle(brush, 1, i * 30 + 1, Width - 2, 30);
            }
            TextRenderer.DrawText(e.Graphics, Items[i], Font, new Rectangle(10, i * 30 + 4, Width - 20, 27), ForeColor,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
        using var footerFont = new Font("Segoe UI", 9);
        TextRenderer.DrawText(e.Graphics, "Qadam1C · Tab принять · ↑↓ выбрать · Esc закрыть", footerFont,
            new Rectangle(8, Items.Length * 30 + 7, Width - 16, 22), Color.LightGray,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        e.Graphics.DrawRectangle(Pens.SlateGray, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class CompletionContext : ApplicationContext
{
    private readonly CompletionPopup popup = new();
    private readonly EditorActionButton actionButton = new();
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    private readonly CompletionNative.HookProc callback;
    private readonly IntPtr hook;
    private EditorSnapshot? shown;
    private EditorSnapshot? visualSnapshot;
    private EditorSnapshot? latest;
    private readonly SnippetLibrary snippets = new(AppDataPaths.FilePath("snippets.json"));
    private CompletionChoice[] choices = [];
    private SnippetManager? manager;
    private readonly MetadataStore metadata = new(AppDataPaths.FilePath("metadata.json"));
    private readonly CompletionHistory history = new(AppDataPaths.FilePath("completion-history.json"));
    private MetadataWindow? metadataWindow;
    private readonly FavoritesStore favorites = new(AppDataPaths.FilePath("favorites.json"));
    private FavoritesWindow? favoritesWindow;
    private readonly EventWaitHandle favoritesRequest = new(false, EventResetMode.AutoReset, "Local\\Qadam1C.Favorites");
    private readonly EventWaitHandle metadataRequest = new(false, EventResetMode.AutoReset, "Local\\Qadam1C.Metadata");
    private readonly EventWaitHandle templateRequest = new(false, EventResetMode.AutoReset, "Local\\Qadam1C.Templates");
    private long generation;
    private bool busy, paused, manual, accepting, suppressedTab, suppressedEscape;
    private bool suppressedDuplicate;
    private bool suppressedFavorite;
    private int transientMisses;
    private string? dismissed;
    private string? lastDiagnostic;

    private void Diagnose(string state)
    {
        if (lastDiagnostic == state) return;
        lastDiagnostic = state;
        try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "completion-status.txt"), state); }
        catch (System.IO.IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public CompletionContext()
    {
        var seed = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "default-snippets.json");
        // Seed only once. Rebuilding the application never replaces user edits.
        snippets.InitializeDefaults(seed);
        // Create the control handle on the UI thread for marshalled hook actions.
        _ = popup.Handle;
        actionButton.OpenTemplates = OpenTemplates;
        actionButton.OpenMetadata = OpenMetadata;
        actionButton.OpenFavorites = OpenFavorites;
        actionButton.AddCurrentFavorite = () => _ = AddCurrentFavoriteAsync();
        actionButton.EditRequested = RunEditorActionAsync;
        actionButton.MenuOpening += (_, _) => InvalidateSuggestion();
        callback = OnKeyboard;
        hook = CompletionNative.SetWindowsHookEx(13, callback, CompletionNative.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var menu = new ContextMenuStrip();
        var pause = new ToolStripMenuItem("Приостановить подсказки") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => { paused = pause.Checked; InvalidateSuggestion(); actionButton.Dismiss(); };
        menu.Items.Add(pause);
        menu.Items.Add("Шаблоны…", null, (_, _) => OpenTemplates());
        menu.Items.Add("Метаданные…", null, (_, _) => OpenMetadata());
        menu.Items.Add("Панель разработчика…", null, (_, _) => OpenFavorites());
        var startup = new ToolStripMenuItem("Запускать вместе с Windows") { Checked = WindowsStartup.Enabled };
        startup.Click += (_, _) =>
        {
            try { WindowsStartup.Enabled = !WindowsStartup.Enabled; startup.Checked = WindowsStartup.Enabled; }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Автозапуск Qadam1C", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        menu.Opening += (_, _) => startup.Checked = WindowsStartup.Enabled;
        menu.Items.Add(startup);
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        tray = new NotifyIcon
        {
            Text = "Qadam1C — автодополнение в 1С", Icon = AppIcon.Value,
            Visible = true, ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => OpenTemplates();
        timer.Tick += async (_, _) => { if (templateRequest.WaitOne(0)) OpenTemplates(); if (metadataRequest.WaitOne(0)) OpenMetadata(); if (favoritesRequest.WaitOne(0)) OpenFavorites(); await RefreshAsync(); };
        timer.Start();
    }

    internal void OpenTemplates()
    {
        InvalidateSuggestion();
        if (manager is null || manager.IsDisposed) manager = new SnippetManager(snippets);
        ShowEditorTool(manager);
    }
    internal void OpenMetadata()
    {
        InvalidateSuggestion();
        if (metadataWindow is null || metadataWindow.IsDisposed) metadataWindow = new MetadataWindow(metadata);
        ShowEditorTool(metadataWindow);
    }
    private sealed record EditorOwner(IntPtr Handle) : IWin32Window;
    internal void OpenFavorites()
    {
        InvalidateSuggestion();
        if (favoritesWindow is null || favoritesWindow.IsDisposed) favoritesWindow = new FavoritesWindow(metadata, favorites);
        else favoritesWindow.RefreshTargets();
        if (latest is not null) favoritesWindow.UpdateEditor(latest);
        ShowEditorTool(favoritesWindow);
    }
    private async Task AddCurrentFavoriteAsync()
    {
        if (accepting || favoritesWindow?.Navigating == true) return;
        accepting = true;
        InvalidateSuggestion();
        try
        {
            var window = CompletionNative.GetForegroundWindow();
            var target = ConfiguratorTarget.List().SingleOrDefault(t => t.Window == window.ToInt64())
                ?? throw new InvalidOperationException("Выберите объект в дереве 1С или откройте его модуль, затем нажмите Ctrl+Alt+B.");
            var index = metadata.For(target.Key) ?? metadata.Caches.FirstOrDefault(c => c.Id == favorites.SelectedIndex(target.Key))
                ?? throw new InvalidOperationException("Сначала выберите индекс текущей конфигурации в окне избранного.");
            string path;
            using (var transition = NavigationTransition.TryShow(window))
                path = await Task.Run(() => CurrentMetadata.Read(target, index));
            favorites.Add(index.Id, new MetadataFavorite(path, ""));
            if (favoritesWindow is null || favoritesWindow.IsDisposed) favoritesWindow = new FavoritesWindow(metadata, favorites);
            favoritesWindow.ShowAdded(target, index, path);
            if (!favoritesWindow.Visible) ShowEditorTool(favoritesWindow);
            TrayNotifications.Show(tray, 2000, "Qadam1C", "В избранном: " + MetadataFavorite.Display(path), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            try { System.IO.File.WriteAllText(AppDataPaths.FilePath("favorites-current-error.log"), ex + "\n" + CurrentMetadata.Diagnostic); }
            catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
            var message = TrayNotifications.ErrorText(ex, "Не удалось прочитать текущий объект 1С. Подробности: favorites-current-error.log.");
            if (favoritesWindow is { IsDisposed: false }) favoritesWindow.ReportError(message);
            TrayNotifications.Show(tray, 3500, "Qadam1C — избранное", message, ToolTipIcon.Warning);
        }
        finally { accepting = false; generation++; }
    }
    private static void ShowEditorTool(Form form)
    {
        var foreground = CompletionNative.GetForegroundWindow();
        CompletionNative.GetWindowThreadProcessId(foreground, out var pid);
        using var process = Process.GetProcessById((int)pid);
        if (!form.Visible && process.ProcessName == "1cv8")
            form.Show(new EditorOwner(foreground));
        else form.Show();
        form.WindowState = FormWindowState.Normal;
        form.BringToFront();
        form.Activate();
    }

    private async Task RefreshAsync()
    {
        actionButton.CheckForeground();
        if (busy || accepting || (paused && favoritesWindow is not { IsDisposed: false, Visible: true }) ||
            metadataWindow?.Running == true || favoritesWindow?.Navigating == true || actionButton.MenuVisible) return;
        busy = true;
        var version = generation;
        try
        {
            var snapshot = await Task.Run(EditorReader.ReadForActions);
            if (version != generation || accepting) return;
            latest = snapshot;
            if (snapshot is null)
            {
                Diagnose("reader=" + EditorReader.LastStatus);
                actionButton.Dismiss();
                HandleMissingSnapshot();
                if (transientMisses > 1) favoritesWindow?.ClearEditorWhenInConfigurator();
                return;
            }
            transientMisses = 0;
            favoritesWindow?.UpdateEditor(snapshot);
            if (paused) { HideSuggestion(); actionButton.Dismiss(); return; }
            actionButton.Present(snapshot);
            if (!snapshot.CanComplete) { HideSuggestion(); return; }
            var key = $"{snapshot.Window}:{snapshot.Offset}:{snapshot.Prefix}";
            if (dismissed == key && !manual) { HideSuggestion(); return; }
            choices = SmartCompletion.Find(snapshot, snippets.Items, metadata.For(snapshot.Session), history, manual);
            var items = choices.Select(c => c.Label).ToArray();
            manual = false;
            if (items.Length == 0) { Diagnose($"reader=ready; prefixLength={snapshot.Prefix.Length}; candidates=0"); HideSuggestion(); return; }
            shown = snapshot;
            visualSnapshot = snapshot;
            popup.Present(snapshot, items);
            var foreground = CompletionNative.GetForegroundWindow();
            CompletionNative.GetWindowThreadProcessId(foreground, out var foregroundPid);
            Diagnose($"reader=ready; prefixLength={snapshot.Prefix.Length}; candidates={items.Length}; visible={popup.IsPresented}; location={popup.Left},{popup.Top}; paints={popup.PaintCount}; positionUpdates={popup.PositionUpdates}; visibilityChanges={popup.VisibilityChanges}; foregroundMatches={foreground == snapshot.Window}; foregroundPid={foregroundPid}; foregroundHandle={foreground.ToInt64():X}; popupHandle={popup.Handle.ToInt64():X}");
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or
            System.ComponentModel.Win32Exception or COMException or ArgumentException)
        { Diagnose("reader-error=" + ex.GetType().Name + ": " + ex.Message); actionButton.Dismiss(); HandleMissingSnapshot(); }
        finally { busy = false; }
    }

    private void HandleMissingSnapshot()
    {
        // UIA may briefly fail during layout/caret changes. Preserve the last
        // visual for one poll only, and never retain it after leaving 1C.
        transientMisses++;
        if (CompletionRefreshPolicy.KeepVisualOnMiss(visualSnapshot?.Window, CompletionNative.GetForegroundWindow(), transientMisses))
            return;
        HideSuggestion();
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CompletionNative.CallNextHookEx(hook, code, message, data);
        var input = Marshal.PtrToStructure<CompletionNative.KeyboardHook>(data);
        if (input.Extra == CompletionNative.InputTag) return CompletionNative.CallNextHookEx(hook, code, message, data);
        var down = message.ToInt32() is 0x100 or 0x104;
        var up = message.ToInt32() is 0x101 or 0x105;
        // Swallow the Tab press, but let Windows observe its release. Blocking
        // key-up can leave GetAsyncKeyState reporting Tab held and cancel paste.
        if (up && input.Key == 9 && suppressedTab)
        {
            suppressedTab = false;
            return CompletionNative.CallNextHookEx(hook, code, message, data);
        }
        if (up && input.Key == 27 && suppressedEscape) { suppressedEscape = false; return (IntPtr)1; }
        if (up && input.Key == 0x44 && suppressedDuplicate) { suppressedDuplicate = false; return (IntPtr)1; }
        if (up && input.Key == 0x42 && suppressedFavorite) { suppressedFavorite = false; return (IntPtr)1; }
        if (down && input.Key == 0x42 && suppressedFavorite) return (IntPtr)1;
        if (down && input.Key == 0x44 && suppressedDuplicate) return (IntPtr)1;
        if (down && input.Key == 0x42 && CompletionNative.IsDown(0x11) && CompletionNative.IsDown(0x12) && !CompletionNative.IsDown(0x10))
        {
            var window = CompletionNative.GetForegroundWindow();
            CompletionNative.GetWindowThreadProcessId(window, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.ProcessName == "1cv8" && process.MainWindowTitle.Contains("Конфигуратор"))
                {
                    suppressedFavorite = true;
                    popup.BeginInvoke(async () => await AddCurrentFavoriteAsync());
                    return (IntPtr)1;
                }
            }
            catch (ArgumentException) { }
        }
        if (!down || paused) return CompletionNative.CallNextHookEx(hook, code, message, data);
        if (!accepting && input.Key == 0x44 && CompletionNative.IsDown(0x11) &&
            !CompletionNative.IsDown(0x12) && !CompletionNative.IsDown(0x10) &&
            actionButton.IsPresented && latest is { HasSelection: false } &&
            CompletionNative.GetForegroundWindow() == latest.Window)
        {
            suppressedDuplicate = true;
            accepting = true;
            InvalidateSuggestion();
            popup.BeginInvoke(async () =>
            {
                try
                {
                    var current = await Task.Run(EditorReader.ReadForActions);
                    if (current is not null && !current.HasSelection)
                        await RunEditorActionAsync(current, "duplicate");
                }
                finally { accepting = false; }
            });
            return (IntPtr)1;
        }
        if (actionButton.MenuVisible)
        {
            if (actionButton.OwnsMenuKey((int)input.Key))
            {
                if (input.Key == 27) suppressedEscape = true;
                popup.BeginInvoke(() => actionButton.MenuKey((int)input.Key));
                return (IntPtr)1;
            }
            if (input.Key is not (16 or 17 or 18 or 160 or 161 or 162 or 163 or 164 or 165))
                popup.BeginInvoke(() => actionButton.CloseMenu());
            return CompletionNative.CallNextHookEx(hook, code, message, data);
        }
        if (input.Key == 13 && CompletionNative.IsDown(0x11) && CompletionNative.IsDown(0x12) &&
            latest is not null && CompletionNative.GetForegroundWindow() == latest.Window)
        {
            popup.BeginInvoke(() => actionButton.ShowMenu());
            return (IntPtr)1;
        }
        var active = shown is not null && popup.IsPresented && CompletionNative.GetForegroundWindow() == shown.Window;
        var modified = CompletionNative.IsDown(0x11) || CompletionNative.IsDown(0x12) || CompletionNative.IsDown(0x10);
        if (input.Key == 32 && CompletionNative.IsDown(0x11) && CompletionNative.IsDown(0x12) &&
            latest is not null && CompletionNative.GetForegroundWindow() == latest.Window)
        {
            // A read-only manual popup can be requested without editing the module.
            manual = true;
            dismissed = null;
            return CompletionNative.CallNextHookEx(hook, code, message, data);
        }
        if (active && !modified)
        {
            if (input.Key == 9)
            {
                suppressedTab = true;
                if (!accepting)
                {
                    accepting = true;
                    var snapshot = shown!;
                    var word = choices[popup.Selected];
                    popup.Dismiss();
                    popup.BeginInvoke(async () => await AcceptAsync(snapshot, word));
                }
                return (IntPtr)1;
            }
            if (input.Key == 27)
            {
                suppressedEscape = true;
                dismissed = $"{shown!.Window}:{shown.Offset}:{shown.Prefix}";
                InvalidateSuggestion();
                return (IntPtr)1;
            }
            if (input.Key is 38 or 40)
            {
                popup.Selected = (popup.Selected + (input.Key == 40 ? 1 : popup.Items.Length - 1)) % popup.Items.Length;
                popup.Invalidate();
                return (IntPtr)1;
            }
        }
        // Shift changes the next character's case, not the current editor contents.
        // Do not hide or invalidate a valid list just because a modifier went down.
        if (input.Key is 16 or 160 or 161) return CompletionNative.CallNextHookEx(hook, code, message, data);
        generation++;
        // Keep the visual while ordinary typing updates the candidates in place.
        // Clear its actionable snapshot immediately so Tab cannot accept stale data.
        if (CompletionRefreshPolicy.HideForKey((int)input.Key, CompletionNative.IsDown(0x11), CompletionNative.IsDown(0x12)))
            HideSuggestion();
        shown = null;
        dismissed = null;
        return CompletionNative.CallNextHookEx(hook, code, message, data);
    }

    private async Task AcceptAsync(EditorSnapshot expected, CompletionChoice word)
    {
        var version = generation;
        InteractionDiagnostics.Write("tab-start");
        try
        {
            var current = await Task.Run(EditorReader.Read);
            if (current is null || version != generation || current.Window != expected.Window ||
                current.Offset != expected.Offset || current.Text != expected.Text || current.Prefix != expected.Prefix ||
                CompletionNative.GetForegroundWindow() != expected.Window)
            {
                InteractionDiagnostics.Write($"tab-cancel: reader={EditorReader.LastStatus}; generationChanged={version != generation}; windowChanged={current?.Window != expected.Window}; offsetChanged={current?.Offset != expected.Offset}; textChanged={current?.Text != expected.Text}");
                return;
            }
            if (word.Template is null)
            {
                var value = word.InsertText ?? word.Label;
                // Abbreviations and substring matches replace the whole prefix.
                if (word.Cursor is not null)
                {
                    if (!await SnippetInsertion.Insert(expected, word.ReplaceLength, new ExpandedSnippet(value, word.Cursor.Value))) return;
                }
                else if (value.StartsWith(expected.Prefix, StringComparison.OrdinalIgnoreCase))
                    CompletionNative.TypeUnicode(value[expected.Prefix.Length..]);
                else if (!await SnippetInsertion.Insert(expected, word.ReplaceLength, new ExpandedSnippet(value, value.Length))) return;
                history.Remember(expected.Session, word.SearchText ?? value);
            }
            else
            {
                var lineStart = expected.Text.LastIndexOf('\n', Math.Max(0, expected.Offset - 1)) + 1;
                var indent = new string(expected.Text[lineStart..expected.Offset].TakeWhile(c => c is ' ' or '\t').ToArray());
                var expansion = SnippetExpansion.Indent(SnippetExpansion.Expand(word.Template.Body), indent);
                if (await SnippetInsertion.Insert(expected, word.ReplaceLength, expansion))
                {
                    history.Remember(expected.Session, word.Template.MatchText);
                    InteractionDiagnostics.Write("tab-template-inserted");
                }
                dismissed = null;
            }
        }
        catch (Exception ex)
        {
            TrayNotifications.Show(tray, 3000, "Qadam1C", "Дополнение отменено: " + ex.Message, ToolTipIcon.Warning);
        }
        finally { accepting = false; generation++; shown = null; }
    }

    private void HideSuggestion() { popup.Dismiss(); shown = null; visualSnapshot = null; }
    private void InvalidateSuggestion() { generation++; HideSuggestion(); }
    private async Task RunEditorActionAsync(EditorSnapshot expected, string command)
    {
        accepting = true;
        InvalidateSuggestion();
        try
        {
            var current = await Task.Run(EditorReader.ReadForActions);
            if (current is null || current.Window != expected.Window || current.Text != expected.Text ||
                current.Offset != expected.Offset || current.SelectionLength != expected.SelectionLength ||
                (current.HasSelection && command is not ("region" or "format" or "format-all" or "align" or
                    "comment-add" or "comment-remove" or "pipe-add" or "pipe-remove")))
            {
                TrayNotifications.Show(tray, 2500, "Qadam1C", "Команда отменена: позиция курсора или выделение изменились. Повторите вызов в редакторе.", ToolTipIcon.Info);
                return;
            }
            var originalCaret = current.Offset;
            if ((command is "format" or "align" || LinePrefixes.Supports(command)) && !current.HasSelection) return;
            if (command == "format-all")
                current = EditorReader.SelectRange(current, 0, current.Text.Length);
            else if (current.HasSelection && (command is "region" or "format" or "align" || LinePrefixes.Supports(command)))
                current = EditorReader.SelectWholeLines(current);
            var expansion = command is "format" or "format-all" or "align"
                ? CodeFormatting.Transform(current, command, originalCaret)
                : LinePrefixes.Supports(command) ? LinePrefixes.Transform(current, command)
                : EditorActionButton.Expansion(current, command);
            if (expansion is not null && (expansion.Text.Length == 0 ||
                expansion.Text == current.Text.Substring(current.Offset, current.SelectionLength)))
            {
                EditorReader.SelectRange(current, expected.Offset, expected.Offset + expected.SelectionLength);
                return;
            }
            if (expansion is not null && !await SnippetInsertion.Insert(current, 0, expansion, forActions: true))
                TrayNotifications.Show(tray, 2500, "Qadam1C", "Команда отменена: отпустите клавиши и повторите вызов в редакторе.", ToolTipIcon.Info);
        }
        catch (Exception ex) { TrayNotifications.Show(tray, 3000, "Qadam1C", ex.Message, ToolTipIcon.Warning); }
        finally { accepting = false; generation++; }
    }
    protected override void ExitThreadCore()
    {
        timer.Stop();
        timer.Dispose();
        CompletionNative.UnhookWindowsHookEx(hook);
        tray.Visible = false;
        tray.Dispose();
        popup.Dispose();
        actionButton.Dispose();
        manager?.Dispose();
        templateRequest.Dispose();
        metadataRequest.Dispose();
        metadataWindow?.Dispose();
        favoritesRequest.Dispose();
        favoritesWindow?.Dispose();
        base.ExitThreadCore();
    }
}

internal static class CompletionChecks
{
    internal static int Run()
    {
        CodeFormatting.Check();
        LinePrefixes.Check();
        if (CompletionRefreshPolicy.HideForKey(0x41, false, false) ||
            CompletionRefreshPolicy.HideForKey(0x10, false, false) ||
            !CompletionRefreshPolicy.HideForKey(0x41, true, false) ||
            !CompletionRefreshPolicy.HideForKey(0x25, false, false))
            throw new InvalidOperationException("Ввод букв и Shift скрывает подсказку либо команда не закрывает её.");
        var editorOwner = new IntPtr(123);
        if (!CompletionRefreshPolicy.KeepVisualOnMiss(editorOwner, editorOwner, 1) ||
            CompletionRefreshPolicy.KeepVisualOnMiss(editorOwner, editorOwner, 2) ||
            CompletionRefreshPolicy.KeepVisualOnMiss(editorOwner, new IntPtr(456), 1) ||
            CompletionRefreshPolicy.KeepVisualOnMiss(null, editorOwner, 1))
            throw new InvalidOperationException("Сбой чтения скрывает последний визуальный список сразу или оставляет его после смены окна.");
        var snapshot = new EditorSnapshot(IntPtr.Zero, "Сообщить Сообщить Сооб", 21, "Сооб", 10, 10);
        if (EditorReader.IsCodeWindow("ОтчетПоКонфигурации.mxl - Конфигуратор - База") ||
            EditorReader.IsCodeWindow("Табличный документ - Конфигуратор") ||
            EditorReader.IsCodeWindow("Конфигуратор - МодульТест") ||
            !EditorReader.IsCodeWindow("Документ Тест: Модуль объекта - Конфигуратор - База"))
            throw new InvalidOperationException("Опрос редактора допускает табличный документ или исключает модуль.");
        if (!EditorReader.IsEditorCaption("Внешняя обработка: Модуль объекта") ||
            EditorReader.IsEditorCaption("МодульОтчета.mxl") ||
            EditorReader.IsEditorCaption("Конфигуратор - МодульТест"))
            throw new InvalidOperationException("Ошибка распознавания внутреннего окна модуля.");
        var words = CompletionWords.Find(snapshot, false);
        if (words.Count(w => w == "Сообщить") != 1 || words.Any(w => w == "Сооб"))
            throw new InvalidOperationException("Фильтрация или устранение повторов не работает.");
        if (CompletionWords.Find(snapshot with { Prefix = "С" }, false).Length != 0 ||
            CompletionWords.Find(snapshot with { Prefix = "" }, true).Length == 0)
            throw new InvalidOperationException("Ошибка порога автодополнения или ручного вызова.");
        var foreground = CompletionNative.GetForegroundWindow();
        snapshot = snapshot with { Window = foreground };
        using var popup = new CompletionPopup();
        popup.Present(snapshot, words);
        Application.DoEvents();
        if (foreground != CompletionNative.GetForegroundWindow())
            throw new InvalidOperationException("Подсказка перехватывает фокус.");
        var paints = popup.PaintCount;
        var positions = popup.PositionUpdates;
        var visibility = popup.VisibilityChanges;
        for (var i = 0; i < 20; i++)
        {
            popup.Present(snapshot, words.ToArray());
            Application.DoEvents();
        }
        if (popup.PaintCount != paints || popup.PositionUpdates != positions || popup.VisibilityChanges != visibility)
            throw new InvalidOperationException("Повторное чтение того же контекста перерисовывает или переключает окно.");
        popup.Present(snapshot with { X = 40 }, words);
        Application.DoEvents();
        if (popup.PositionUpdates != positions + 1 || popup.VisibilityChanges != visibility)
            throw new InvalidOperationException("Перемещение курсора скрывает и повторно открывает подсказку.");
        popup.Dismiss();
        using var button = new EditorActionButton();
        button.Present(snapshot with { EditorLeft = 80, LineTop = 80 });
        Application.DoEvents();
        if (foreground != CompletionNative.GetForegroundWindow() || !button.IsPresented)
            throw new InvalidOperationException("Кнопка действий не показана или перехватывает фокус.");
        if ((CompletionNative.GetWindowLong(button.Handle, -20) & 0x8) == 0)
            throw new InvalidOperationException("Кнопка действий может оказаться под окном 1С.");
        button.ShowMenu();
        Application.DoEvents();
        if (!button.MenuVisible)
            throw new InvalidOperationException("Меню нативно показанной кнопки не открывается.");
        button.CloseMenu();
        button.Dismiss();
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var original = "\tСообщить(1);" + newline + "КонецПроцедуры";
            foreach (var offset in new[] { 0, 5, 13 })
            {
                var source = snapshot with { Text = original, Offset = offset };
                var edit = EditorActionButton.Expansion(source, "duplicate")!;
                var actual = original[..offset] + edit.Text + original[offset..];
                if (actual != "\tСообщить(1);" + newline + original)
                    throw new InvalidOperationException("Дублирование строки повреждает текст.");
            }
        }
        if (EditorActionButton.Expansion(snapshot with { Text = "Код", Offset = 3 }, "region") is not null)
            throw new InvalidOperationException("Область вставляется внутри строки кода.");
        var block = "\tСообщить(1);\r\n\tСообщить(2);";
        var wrapped = EditorActionButton.Expansion(snapshot with
            { Text = block, Offset = 0, HasSelection = true, SelectionLength = block.Length }, "region")!;
        if (wrapped.Text != "\t#Область ИмяОбласти\r\n" + block + "\r\n\t#КонецОбласти" ||
            wrapped.Cursor != "\t#Область ".Length)
            throw new InvalidOperationException("Обрамление области изменяет выделенный код или позицию курсора.");
        return 0;
    }
}

internal static class CompletionNative
{
    internal static readonly IntPtr InputTag = new(0x51414441);
    internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardHook { public uint Key, Scan, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    internal static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static bool KeyDown(int key) => IsDown(key);
    private static Input Key(ushort key, bool up = false) => new()
    {
        Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0, Extra = InputTag } }
    };
    private static void SendKeys(List<Input> inputs)
    {
        if (inputs.Count > 0 && SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>()) != inputs.Count)
            throw new InvalidOperationException("Windows не подтвердил ввод. Проверьте результат.");
    }
    internal static void Paste()
    {
        var keys = new List<Input>();
        keys.Add(Key(0x11)); keys.Add(Key(0x56)); keys.Add(Key(0x56, true)); keys.Add(Key(0x11, true));
        SendKeys(keys);
    }
    internal static void Chord(params ushort[] keys)
    {
        var inputs = keys.Select(k => Key(k)).Concat(keys.Reverse().Select(k => Key(k, true))).ToList();
        SendKeys(inputs);
    }
    internal static void TypeUnicode(string text)
    {
        var inputs = text.SelectMany(c => new[]
        {
            new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = c, Flags = 4, Extra = InputTag } } },
            new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = c, Flags = 6, Extra = InputTag } } }
        }).ToArray();
        if (inputs.Length != 0 && SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException("Windows не подтвердил ввод всего дополнения. Проверьте текст; повтор автоматически не выполняется.");
    }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
}
