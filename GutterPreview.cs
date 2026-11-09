using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Qadam1C;

// Deliberately separate from the normal completion session until live acceptance.
// Never opens/closes measurements, moves the caret, expands folds or changes text.
internal sealed class GutterPreview : ApplicationContext
{
    private readonly NumberStrip strip = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly NotifyIcon tray;
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    private readonly int duration;
    private bool busy;
    private bool stopped;
    private string lastStatus = "";
    private readonly Queue<string> recentStatus = new();
    private readonly DocumentBinding<AutomationElement> documentBinding = new();

    private GutterPreview(int seconds)
    {
        duration = seconds;
        _ = strip.Handle; // Establish UI-thread dispatch before the first await.
        var menu = new ContextMenuStrip();
        menu.Items.Add("Завершить тест полосы", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Icon = SystemIcons.Information, Text = "Qadam1C — тест номеров строк", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ExitThread();
        timer.Tick += async (_, _) => await Tick();
        timer.Start();
    }

    internal static int Run(int seconds)
    {
        using var instance = new Mutex(true, "Local\\Qadam1C.GutterPreview", out var owns);
        if (!owns) return 2;
        using var context = new GutterPreview(Math.Clamp(seconds, 5, 900));
        Application.Run(context);
        return 0;
    }

    private async Task Tick()
    {
        if (lifetime.Elapsed.TotalSeconds >= duration) { ExitThread(); return; }
        if (busy) return;
        strip.CheckForeground();
        busy = true;
        try
        {
            var result = await Task.Run(Read);
            if (stopped) return;
            if (result.Window != IntPtr.Zero && result.Window == CompletionNative.GetForegroundWindow() &&
                result.OwnMeasurement && result.Rows.Length > 0)
                strip.Present(result);
            else strip.Dismiss();
            Report(result.Status);
        }
        catch (Exception ex) { if (!stopped) { strip.Dismiss(); Report("hidden: " + ex); } }
        finally { busy = false; }
    }

    private void Report(string status)
    {
        if (lastStatus == status) return;
        lastStatus = status;
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "gutter-preview-status.txt"), status);
            recentStatus.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + status);
            while (recentStatus.Count > 32) recentStatus.Dequeue();
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "gutter-preview-history.txt"), recentStatus);
            if (status != "hidden: outside-configurator")
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "gutter-preview-last-editor.txt"), status);
            if (status.EndsWith("; ready", StringComparison.Ordinal))
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "gutter-preview-last-ready.txt"), status);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal sealed record Row(int Number, int Top, int Height);
    private sealed record View(IntPtr Window, Rectangle Bounds, Row[] Rows, bool OwnMeasurement, string Status);

    private static View Hidden(string status) => new(IntPtr.Zero, Rectangle.Empty, [], false, status);

    private View Read()
    {
        var window = CompletionNative.GetForegroundWindow();
        CompletionNative.GetWindowThreadProcessId(window, out var pid);
        using var process = Process.GetProcessById((int)pid);
        if (process.ProcessName != "1cv8" || !process.MainWindowTitle.Contains("Конфигуратор", StringComparison.OrdinalIgnoreCase))
            return Hidden("hidden: outside-configurator");
        // Unlike completion, the gutter must not require a caret rectangle:
        // wheel scrolling can leave the caret entirely outside the viewport.
        // Bind to the editor itself. Re-discovering from the accessibility tree on
        // every tick can lose the document when focus moves to metadata controls.
        var document = documentBinding.Resolve(window,
            item => IsExposedDocument(item, (int)pid),
            () => EditorReader.FocusedDocument(window) ?? EditorReader.VisibleDocument(window));
        if (document is null) return Hidden("hidden: no-exposed-bound-document");
        var pattern = (TextPattern)document.GetCurrentPattern(TextPattern.Pattern);
        var entire = pattern.DocumentRange;
        var text = entire.GetText(1_000_001);
        if (text.Length > 1_000_000) return Hidden("hidden: module-too-large");
        var bounds = document.Current.BoundingRectangle;
        var captions = MeasurementCaptions(window);
        var owned = OnlyOwnMeasurement(captions);
        if (captions.Count == 0) return Hidden("hidden: placeholder .pff is not open");
        if (!owned) return Hidden("hidden: measurement identity: " + string.Join(",", captions.Select(n =>
            n.StartsWith("unknown:") ? n : n.Contains("Qadam1C-Lines") ? "placeholder-name" : "other-pff")));
        if (!File.Exists(PlaceholderPath) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PlaceholderPath))) != PlaceholderHash)
            return Hidden("hidden: placeholder file missing or changed");
        var budget = Stopwatch.StartNew();
        LocatedLine? Locate(double sampleY)
        {
            if (budget.ElapsedMilliseconds > 1000) throw new TimeoutException("Visible row mapping exceeded 1000ms.");
            // Ask the editor for the source position at each displayed row. Counting
            // screen rows would silently produce wrong numbers after collapsed code.
            var point = pattern.RangeFromPoint(new System.Windows.Point(bounds.Right - 30, sampleY));
            point.ExpandToEnclosingUnit(TextUnit.Line);
            var prefix = entire.Clone();
            prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, point, TextPatternRangeEndpoint.Start);
            var before = prefix.GetText(1_000_001);
            var character = point.Clone();
            character.MoveEndpointByRange(TextPatternRangeEndpoint.End, character, TextPatternRangeEndpoint.Start);
            character.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
            var charRects = character.GetBoundingRectangles();
            if (charRects.Length != 1) return null;
            var rectangle = charRects[0];
            return new LocatedLine(before, rectangle.Left, rectangle.Top, rectangle.Height);
        }
        var mapped = MapVisibleRows(text, bounds.Top, bounds.Bottom, Locate);
        var rows = mapped.Rows;
        var textLeft = mapped.TextLeft;
        var identity = string.Join(",", captions.Select(n => n.StartsWith("unknown:") ? n :
            n.Contains("Qadam1C-Lines", StringComparison.OrdinalIgnoreCase) ? "placeholder-name" : "other-pff"));
        var status = $"measurements={captions.Count} ({identity}); own={owned}; mapped={rows.Length}; " +
            $"viewportOnly=True; editorFocused={document.Current.HasKeyboardFocus}; first={rows.FirstOrDefault()?.Number}; last={rows.LastOrDefault()?.Number}; " +
            $"reserved={(textLeft == double.MaxValue ? -1 : (int)(textLeft - bounds.Left))}; ms={budget.ElapsedMilliseconds}";
        // Leave generous space for the native breakpoint/outline area. A loaded
        // filename alone never proves that the editor reserved a usable column.
        if (textLeft == double.MaxValue || textLeft - bounds.Left < 160) return Hidden(status + "; hidden: insufficient space");
        if (entire.GetText(1_000_001) != text) return Hidden("hidden: source changed during mapping");
        if (!IsExposedDocument(document, (int)pid) || document.Current.BoundingRectangle != bounds)
            return Hidden("hidden: visible document changed during mapping");
        // A results window may have opened while the provider was mapping rows.
        if (!OnlyOwnMeasurement(MeasurementCaptions(window))) return Hidden("hidden: measurement changed during mapping");
        return new View(window,
            new Rectangle((int)bounds.Left + 32, (int)bounds.Top, 84, (int)bounds.Height),
            rows, owned, status + (owned ? "; ready" : "; hidden: real or missing measurement"));
    }

    // No focus or caret state is involved in retaining the binding. Read-only
    // accessibility hit tests prove that this document is still exposed; a
    // closed/covered/replaced editor must not leave a topmost stale overlay.
    private static bool IsExposedDocument(AutomationElement document, int pid)
    {
        try
        {
            var state = document.Current;
            if (state.ProcessId != pid || state.ControlType != ControlType.Document) return false;
            var bounds = state.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width < 40 || bounds.Height < 20) return false;
            foreach (var fraction in new[] { 0.2, 0.5, 0.8 })
            {
                var hit = AutomationElement.FromPoint(new System.Windows.Point(bounds.Right - 30,
                    bounds.Top + bounds.Height * fraction));
                var matches = false;
                for (var depth = 0; hit is not null && depth < 12; depth++)
                {
                    if (Automation.Compare(hit, document)) { matches = true; break; }
                    if (hit.Current.ProcessId != pid) break;
                    hit = TreeWalker.RawViewWalker.GetParent(hit);
                }
                if (!matches) return false;
            }
            return true;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    internal sealed class DocumentBinding<T> where T : class
    {
        private IntPtr owner;
        private T? document;

        internal T? Resolve(IntPtr window, Func<T, bool> isExposed, Func<T?> discover)
        {
            if (owner == window && document is not null && isExposed(document)) return document;
            document = null;
            owner = window;
            var candidate = discover();
            if (candidate is not null && isExposed(candidate)) document = candidate;
            return document;
        }
    }

    internal sealed record LocatedLine(string Prefix, double Left, double Top, double Height);
    internal sealed record VisibleRows(Row[] Rows, double TextLeft);

    internal static VisibleRows MapVisibleRows(string text, double top, double bottom, Func<double, LocatedLine?> locate)
    {
        var rows = new List<Row>();
        var seen = new HashSet<int>();
        var textLeft = double.MaxValue;
        var samples = 0;
        // Discover row height from visible text, never from the selection. After a
        // hit, continue halfway into the following row so fractional scrolling,
        // folded blocks and small gaps between character rectangles remain safe.
        var step = 8d;
        for (var y = top + 1; y < bottom && samples++ < 300;)
        {
            var line = locate(y);
            if (line is not null && line.Height >= 1 && double.IsFinite(line.Height) &&
                y >= line.Top && y < line.Top + line.Height)
            {
                if (!text.StartsWith(line.Prefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("Inconsistent source range.");
                if (line.Prefix.Length == line.Prefix.LastIndexOf('\n') + 1 &&
                    line.Top >= top && line.Top + line.Height <= bottom)
                {
                    var number = SourceLine(line.Prefix);
                    if (seen.Add(number)) rows.Add(new Row(number, (int)line.Top, (int)line.Height));
                    textLeft = Math.Min(textLeft, line.Left);
                }
                step = Math.Clamp(line.Height / 2, 1, 24);
                y = Math.Max(y + 1, line.Top + line.Height + step);
            }
            else y += step;
        }
        return new VisibleRows(rows.ToArray(), textLeft);
    }

    internal static int SourceLine(string prefix) => 1 + prefix.Count(c => c == '\n');
    internal static string PlaceholderPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "gutter-prototype", "Qadam1C-Lines.pff"));
    private const string PlaceholderHash = "37AD29B5055EF008625E32A5CBA2E6FB48B44409791532A7CCD650E893852B63";
    internal static bool OnlyOwnMeasurement(IReadOnlyCollection<string> names, string? path = null)
    {
        path ??= PlaceholderPath;
        if (names.Count != 1) return false;
        var name = names.Single();
        return name.StartsWith(path, StringComparison.OrdinalIgnoreCase) &&
            (name.Length == path.Length || name[path.Length] is ' ' or '(');
    }

    private static void ClassifyCaption(HashSet<string> found, string name)
    {
        if (name.Contains(".pff", StringComparison.OrdinalIgnoreCase)) found.Add(name);
        // Includes unsaved measurements ("Новый1"), not only saved .pff files.
        else if (!EditorReader.IsEditorCaption(name)) found.Add("unknown: other document");
    }

    private static HashSet<string> MeasurementCaptions(IntPtr window)
    {
        // 1C's virtual tab elements can expire between UIA calls. Request the
        // toolbar and its children together, then read only the cached names.
        var request = new CacheRequest
        {
            TreeScope = TreeScope.Subtree,
            TreeFilter = Automation.RawViewCondition
        };
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        using (request.Activate())
        {
            var root = AutomationElement.FromHandle(window);
            var toolbar = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar),
                new PropertyCondition(AutomationElement.NameProperty, "Панель окон")));
            if (toolbar is null) return ["unknown: window toolbar unavailable"];
            var buttons = toolbar.CachedChildren;
            if (buttons.Count is < 1 or > 200) return ["unknown: empty or excessive window tabs"];
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AutomationElement button in buttons)
                ClassifyCaption(found, button.Cached.Name ?? "");
            return found;
        }
    }

    internal static int Inspect(int pid)
    {
        using var process = Process.GetProcessById(pid);
        if (process.ProcessName != "1cv8" || !process.MainWindowTitle.Contains("Конфигуратор")) return 1;
        var names = MeasurementCaptions(process.MainWindowHandle);
        var result = "own=" + OnlyOwnMeasurement(names) + "; " + string.Join(",", names.Select(n =>
            n.StartsWith("unknown:") ? n : n.Contains("Qadam1C-Lines") ? "placeholder-name" : "other-pff"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "gutter-inspect.txt"), result);
        return 0;
    }

    internal static int Check()
    {
        var binding = new DocumentBinding<string>();
        var owner = new IntPtr(1);
        if (binding.Resolve(owner, _ => true, () => "module-A") != "module-A")
            throw new InvalidOperationException("Initial document binding failed.");
        // Simulate focus moving to the metadata tree: fresh discovery is no
        // longer possible, but the same visible module must keep its binding.
        for (var i = 0; i < 3; i++)
            if (binding.Resolve(owner, _ => true, () => throw new InvalidOperationException("Unexpected focus-dependent rediscovery.")) != "module-A")
                throw new InvalidOperationException("Metadata-tree focus drops the document binding.");
        if (binding.Resolve(owner, item => item == "module-B", () => "module-B") != "module-B" ||
            binding.Resolve(owner, _ => false, () => null) is not null ||
            binding.Resolve(new IntPtr(2), _ => true, () => null) is not null)
            throw new InvalidOperationException("Closed/replaced editors retain a stale binding.");
        // Focus may leave the single visible module for the metadata tree. With
        // multiple visible modules choose only the one that owns keyboard focus.
        if (EditorReader.ChooseVisibleDocument([true]) != 0 ||
            EditorReader.ChooseVisibleDocument([false]) != 0 ||
            EditorReader.ChooseVisibleDocument([]) != -1 ||
            EditorReader.ChooseVisibleDocument([false, true]) != 1 ||
            EditorReader.ChooseVisibleDocument([false, false]) != -1 ||
            EditorReader.ChooseVisibleDocument([true, true]) != -1)
            throw new InvalidOperationException("Metadata-tree focus or ambiguous editor selection is handled incorrectly.");
        if (SourceLine("") != 1 || SourceLine("one\r\ntwo\r\n") != 3 || SourceLine("one\ntwo") != 2)
            throw new InvalidOperationException("Incorrect source line mapping.");
        var placeholder = @"C:\test\Qadam1C-Lines.pff";
        if (!OnlyOwnMeasurement([placeholder + " (Qadam)"], placeholder) || OnlyOwnMeasurement([]) ||
            OnlyOwnMeasurement([@"C:\test\Новый2.pff"]) ||
            OnlyOwnMeasurement([placeholder, @"C:\test\Новый2.pff"], placeholder) ||
            OnlyOwnMeasurement([placeholder + ".backup"], placeholder) ||
            OnlyOwnMeasurement([@"D:\other\Qadam1C-Lines.pff"], placeholder))
            throw new InvalidOperationException("Real measurements are not protected.");
        var documents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ClassifyCaption(documents, "Общий модуль Тест: Модуль");
        ClassifyCaption(documents, placeholder);
        if (!OnlyOwnMeasurement(documents, placeholder)) throw new InvalidOperationException("Module incorrectly blocks the preview.");
        ClassifyCaption(documents, "Новый1 (Замер производительности)");
        if (OnlyOwnMeasurement(documents, placeholder)) throw new InvalidOperationException("Unsaved measurement is not protected.");
        // Regression: scrolling a folded module while the caret remains on line 1.
        // The row mapper deliberately receives no caret position or rectangle.
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var text = string.Join(newline, Enumerable.Range(1, 1500).Select(n => "line" + n)) + newline;
            string Prefix(int number) => string.Join(newline, Enumerable.Range(1, number - 1).Select(n => "line" + n)) +
                (number > 1 ? newline : "");
            foreach (var height in new[] { 9d, 13.5, 18, 27, 36 })
            {
                var firstViewport = new[] { 646, 647, 944 };
                var secondViewport = new[] { 941, 1148, 1431 };
                foreach (var numbers in new[] { firstViewport, secondViewport })
                {
                    var visible = numbers.Select((n, i) => new LocatedLine(Prefix(n), 260, 100 + i * height, height)).ToArray();
                    LocatedLine? Locate(double y) => visible.FirstOrDefault(r => y >= r.Top && y < r.Top + r.Height);
                    var mapped = MapVisibleRows(text, 100, 100 + 3 * height, Locate);
                    if (!mapped.Rows.Select(r => r.Number).SequenceEqual(numbers) || mapped.TextLeft != 260)
                        throw new InvalidOperationException("Scrolling/folding depends on the caret or skips visible rows.");
                    var clipped = MapVisibleRows(text, 100 + height / 2, 100 + 2.5 * height, Locate);
                    if (!clipped.Rows.Select(r => r.Number).SequenceEqual(numbers.Skip(1).Take(1)))
                        throw new InvalidOperationException("Partly clipped rows are incorrectly rendered.");
                }
            }
        }
        if (MapVisibleRows("text", 0, 100, _ => null).Rows.Length != 0)
            throw new InvalidOperationException("Empty viewport produces invented rows.");
        try
        {
            MapVisibleRows("text", 0, 100, _ => new LocatedLine("changed\n", 260, 0, 18));
            throw new InvalidOperationException("Source mismatch was accepted.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "Inconsistent source range.") { }
        return 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { stopped = true; timer.Stop(); timer.Dispose(); tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); strip.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class NumberStrip : Form
    {
        private Row[] rows = [];
        private IntPtr owner;
        internal NumberStrip()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(245, 246, 238);
            ForeColor = Color.FromArgb(92, 111, 125);
            Opacity = 0.98;
            Font = new Font("Consolas", 10);
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x20; return p; }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
            base.WndProc(ref m);
        }
        internal void Present(View view)
        {
            owner = view.Window;
            if (Bounds == view.Bounds && rows.SequenceEqual(view.Rows) && IsHandleCreated && CompletionNative.IsWindowVisible(Handle)) return;
            rows = view.Rows;
            Bounds = view.Bounds;
            CompletionNative.ShowWindow(Handle, 4);
            CompletionNative.SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height, 0x0010 | 0x0200);
            Invalidate();
        }
        internal void Dismiss() { if (IsHandleCreated) CompletionNative.ShowWindow(Handle, 0); }
        internal void CheckForeground() { if (owner != CompletionNative.GetForegroundWindow()) Dismiss(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            foreach (var row in rows)
                TextRenderer.DrawText(e.Graphics, row.Number.ToString(), Font,
                    new Rectangle(0, row.Top - Top, Width - 8, row.Height), ForeColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
