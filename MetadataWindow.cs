using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Qadam1C;

internal static class MetadataScanner
{
    private static AutomationElement? Named(AutomationElement root, string name) => root.FindAll(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.NameProperty, name)).Cast<AutomationElement>()
        .FirstOrDefault(e => !e.Current.IsOffscreen && e.Current.IsEnabled);
    // 1C providers may advertise Invoke on custom controls with unsafe behaviour.
    // Use only a verified physical click, never the provider's default action.
    private static void Invoke(AutomationElement element)
    {
        if (element.Current.IsEnabled && !element.Current.IsOffscreen && !element.Current.BoundingRectangle.IsEmpty)
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
            if (foregroundPid != element.Current.ProcessId)
                throw new InvalidOperationException("Фокус переключён из 1С. Вернитесь в Конфигуратор и повторите сканирование.");
            var bounds = element.Current.BoundingRectangle;
            var point = new System.Windows.Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
            if (!SetCursorPos((int)point.X, (int)point.Y)) throw new InvalidOperationException("Не удалось нажать элемент 1С.");
            // A dialog may have disappeared or another window may cover the target.
            var hit = AutomationElement.FromPoint(point);
            var matches = false;
            for (var parent = hit; parent is not null; parent = TreeWalker.RawViewWalker.GetParent(parent))
            {
                if (Automation.Compare(parent, element)) { matches = true; break; }
                if (parent.Current.ControlType == ControlType.Window) break;
            }
            if (!matches || hit.Current.ProcessId != foregroundPid)
                throw new InvalidOperationException("Элемент 1С переместился или перекрыт. Сканирование остановлено без нажатия.");
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        }
        else throw new InvalidOperationException("Элемент 1С не поддерживает автоматическое нажатие: " + element.Current.Name);
    }
    private static async Task<AutomationElement> Wait(Func<AutomationElement?> read, CancellationToken token)
    {
        for (var i = 0; i < 60; i++)
        {
            token.ThrowIfCancellationRequested();
            var element = await Task.Run(read, token);
            if (element is not null) return element;
            await Task.Delay(150, token);
        }
        throw new TimeoutException("Не удалось дождаться окна 1С. Можно импортировать отчёт вручную.");
    }
    public static async Task<string> Scan(ConfiguratorTarget target, string directory, IProgress<string> progress, CancellationToken token)
    {
        void Stage(string message)
        {
            progress.Report(message);
            try
            {
                var log = AppDataPaths.FilePath("metadata-scan.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log, $"{DateTimeOffset.Now:O} pid={target.Pid} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Stage("Начало сканирования (проверенные нажатия, без Invoke).");
        if (!ConfiguratorTarget.List().Any(t => t.Pid == target.Pid && t.Started == target.Started)) throw new InvalidOperationException("Конфигуратор уже закрыт.");
        var hwnd = new IntPtr(target.Window);
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        if (!SetForegroundWindow(hwnd)) throw new InvalidOperationException("Переключитесь в выбранный Конфигуратор и повторите сканирование.");
        var root = AutomationElement.FromHandle(hwnd);
        AutomationElement? ReportDialog() => AutomationElement.RootElement.FindAll(TreeScope.Children,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, target.Pid),
                    new PropertyCondition(AutomationElement.NameProperty, "Отчет по конфигурации"),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)))
                .Cast<AutomationElement>().FirstOrDefault(w => !w.Current.IsOffscreen && w.Current.IsEnabled);
        Stage("Открываю отчёт по конфигурации…");
        var dialog = await Task.Run(ReportDialog, token);
        if (dialog is null)
        {
            var button = await Task.Run(() => root.FindAll(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.NameProperty, "Конфигурация"),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                .Cast<AutomationElement>().FirstOrDefault(e => !e.Current.IsOffscreen), token)
                ?? throw new InvalidOperationException("Не найдено меню «Конфигурация».");
            await Task.Run(() => Invoke(button), token);
            var item = await Wait(() => Named(root, "Отчет по конфигурации..."), token);
            var opening = Task.Run(() => Invoke(item), token);
            dialog = await Wait(ReportDialog, token);
            if (opening.IsFaulted) await opening;
        }
        Stage("Найден отдельный диалог отчёта.");
        var all = dialog.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
        var scope = all.Where(e => e.Current.ControlType == ControlType.Edit)
            .Select(e => e.TryGetCurrentPattern(ValuePattern.Pattern, out var v) ? ((ValuePattern)v).Current.Value : "")
            .FirstOrDefault(v => v.Contains("Вся конфигурация"));
        if (scope is null) throw new InvalidOperationException("В отчёте установлен отбор. Выберите «Вся конфигурация» и повторите сканирование.");
        var radio = Named(dialog, "Табличный документ") ?? throw new InvalidOperationException("Не найден формат табличного документа.");
        await Task.Run(() => Invoke(radio), token);
        var fields = all.Where(e => e.Current.ControlType == ControlType.Edit && e.TryGetCurrentPattern(ValuePattern.Pattern, out var v) &&
            !((ValuePattern)v).Current.IsReadOnly && !((ValuePattern)v).Current.Value.Contains("Вся конфигурация")).ToArray();
        if (fields.Length != 1) throw new InvalidOperationException("Не удалось однозначно найти поле имени файла.");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "report-" + Guid.NewGuid().ToString("N") + ".mxl");
        var value = (ValuePattern)fields[0].GetCurrentPattern(ValuePattern.Pattern);
        value.SetValue(file);
        if (value.Current.Value != file) throw new InvalidOperationException("1С не подтвердила путь отчёта.");
        token.ThrowIfCancellationRequested();
        var ok = dialog.FindAll(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "ОК")))
            .Cast<AutomationElement>().SingleOrDefault(e => !e.Current.IsOffscreen && e.Current.IsEnabled)
            ?? throw new InvalidOperationException("Не найдена единственная кнопка ОК в диалоге отчёта.");
        Stage("1С формирует отчёт. Ожидание до 5 минут…");
        var generating = Task.Run(() => Invoke(ok), token);
        // New unique path prevents stale report reuse; exclusive read waits for writer completion.
        for (var i = 0; i < 1200; i++)
        {
            await Task.Delay(250, token);
            if (generating.IsFaulted) await generating;
            if (!IsWindow(hwnd))
            {
                Stage("Окно Конфигуратора закрылось во время формирования отчёта.");
                throw new InvalidOperationException("1С закрылась во время формирования отчёта. Кэш не изменён. Журнал: " + AppDataPaths.FilePath("metadata-scan.log"));
            }
            if (!File.Exists(file)) continue;
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                    if (stream.Length == 0) continue;
                Stage("Отчёт сохранён. Конфигуратор остаётся открытым.");
                return file;
            }
            catch (IOException) { }
            catch (System.Text.DecoderFallbackException) { }
        }
        throw new TimeoutException("1С не завершила отчёт за 5 минут. Кэш не изменён.");
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}

internal sealed class MetadataWindow : Form
{
    private readonly MetadataStore store;
    private readonly ComboBox target = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox cache = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly SearchTextBox search = new() { Dock = DockStyle.Top, PlaceholderText = "Поиск объектов и полей (показаны первые 1000 совпадений)" };
    private readonly ListBox list = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 65 };
    private readonly FlowLayoutPanel actions = new() { Dock = DockStyle.Top, Height = 70, AutoScroll = true };
    private CancellationTokenSource? cancellation;
    internal bool Running => cancellation is not null;
    private ConfiguratorTarget Target => target.SelectedItem as ConfiguratorTarget ?? throw new InvalidOperationException("Выберите открытый Конфигуратор.");
    public MetadataWindow(MetadataStore store)
    {
        Icon = AppIcon.Value;
        this.store = store;
        Text = "Qadam1C — метаданные"; Size = new Size(1050, 650); MinimumSize = new Size(800, 450);
        StartPosition = FormStartPosition.CenterScreen;
        void Button(string name, Func<Task> action)
        {
            var b = new Button { Text = name, AutoSize = true };
            b.Click += async (_, _) =>
            {
                if (Running) return;
                try { await action(); }
                catch (OperationCanceledException) { status.Text = "Ожидание отменено. Уже запущенный отчёт 1С может продолжать формироваться. Кэш не изменён."; }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                    try
                    {
                        var log = AppDataPaths.FilePath("metadata-error.log");
                        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                        File.WriteAllText(log, ex.ToString());
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            };
            actions.Controls.Add(b);
        }
        Button("Сканировать в 1С", async () =>
        {
            var selected = Target;
            var existing = store.For(selected.Key)?.Id;
            cancellation = new();
            try
            {
                target.Enabled = cache.Enabled = false;
                var path = await MetadataScanner.Scan(selected, Path.Combine(AppContext.BaseDirectory, "data", "reports"),
                    new Progress<string>(s => status.Text = s), cancellation.Token);
                status.Text = "Читаю отчёт и строю индекс метаданных…";
                var index = await Task.Run(() => MetadataReport.Read(path, existing, cancellation.Token));
                cancellation.Token.ThrowIfCancellationRequested();
                store.Put(index, selected.Key); Reload(index.Id);
                status.Text = $"Готово: {index.Entries.Count} объектов и полей. " + BindingStatus();
                Show(); Activate();
            }
            finally { cancellation.Dispose(); cancellation = null; target.Enabled = cache.Enabled = true; }
        });
        Button("Импортировать .mxl", async () =>
        {
            var selected = Target;
            using var dialog = new OpenFileDialog { Filter = "Отчёт по конфигурации (*.mxl)|*.mxl" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            cancellation = new();
            try
            {
                target.Enabled = cache.Enabled = false;
                status.Text = "Читаю отчёт и строю индекс метаданных…";
                var index = await Task.Run(() => MetadataReport.Read(dialog.FileName, cancellation: cancellation.Token));
                cancellation.Token.ThrowIfCancellationRequested();
                store.Put(index, selected.Key); Reload(index.Id);
                status.Text = $"Импортировано: {index.Entries.Count}. " + BindingStatus();
            }
            finally { cancellation.Dispose(); cancellation = null; target.Enabled = cache.Enabled = true; }
        });
        Button("Подключить кэш", () =>
        {
            var selected = cache.SelectedItem as MetadataIndex ?? throw new InvalidOperationException("Выберите кэш.");
            store.Bind(Target.Key, selected.Id);
            status.Text = BindingStatus(); return Task.CompletedTask;
        });
        Button("Отключить", () => { store.Unbind(Target.Key); status.Text = "Подсказки метаданных отключены для этого сеанса."; return Task.CompletedTask; });
        Button("Обновить список окон", () => { ReloadTargets(); return Task.CompletedTask; });
        var cancel = new Button { Text = "Отменить ожидание", AutoSize = true };
        cancel.Click += (_, _) => cancellation?.Cancel(); actions.Controls.Add(cancel);
        Controls.Add(list); Controls.Add(search); Controls.Add(actions); Controls.Add(cache);
        Controls.Add(new Label { Text = "Сохранённый кэш — подключается автоматически для этой базы:", Dock = DockStyle.Top, Height = 25 });
        Controls.Add(target); Controls.Add(new Label { Text = "Конфигуратор, к которому подключить метаданные:", Dock = DockStyle.Top, Height = 25 });
        Controls.Add(status);
        cache.SelectedIndexChanged += (_, _) => ShowEntries();
        target.SelectedIndexChanged += (_, _) =>
        {
            if (target.SelectedItem is ConfiguratorTarget selected)
            {
                var bound = store.For(selected.Key);
                if (bound is not null) cache.SelectedItem = bound;
                status.Text = BindingStatus();
            }
        };
        search.TextChanged += (_, _) => ShowEntries();
        FormClosing += (_, e) => { if (Running) { e.Cancel = true; cancellation?.Cancel(); } };
        ReloadTargets(); Reload();
        status.Text = BindingStatus();
    }
    private string BindingStatus()
    {
        if (target.SelectedItem is not ConfiguratorTarget selected) return "Откройте Конфигуратор и обновите список окон.";
        if (!selected.Key.StartsWith("base:")) return "Не удалось определить адрес базы. Подключение кэша доступно для текущего сеанса.";
        return store.For(selected.Key) is { } bound
            ? $"Кэш «{bound.Name}» подключён. После перезапуска 1С подключится автоматически."
            : "Для этой базы кэша ещё нет. Сканируйте конфигурацию, импортируйте отчёт или один раз подключите подходящий кэш.";
    }
    private void ReloadTargets()
    {
        target.Items.Clear(); target.Items.AddRange(ConfiguratorTarget.List());
        if (target.Items.Count > 0) target.SelectedIndex = 0;
    }
    private void Reload(string? id = null)
    {
        id ??= target.SelectedItem is ConfiguratorTarget selected ? store.For(selected.Key)?.Id : null;
        cache.Items.Clear(); cache.Items.AddRange(store.Caches.Cast<object>().ToArray());
        if (id is not null) cache.SelectedItem = store.Caches.FirstOrDefault(c => c.Id == id);
        else if (cache.Items.Count > 0) cache.SelectedIndex = 0;
    }
    private void ShowEntries()
    {
        list.BeginUpdate();
        try
        {
        list.Items.Clear();
        if (cache.SelectedItem is MetadataIndex index)
            list.Items.AddRange(index.Entries.Where(e => (e.Path + " " + e.Synonym).Contains(search.Text, StringComparison.OrdinalIgnoreCase))
                .Take(1000)
                .Select(e => (object)(e.Path + (e.Types.Length > 0 ? " : " + string.Join(", ", e.Types) : ""))).ToArray());
        }
        finally { list.EndUpdate(); }
    }
}
