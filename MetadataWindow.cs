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
    private readonly ComboBox target = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox configurations = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly Label details = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label binding = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly SearchTextBox search = new() { Dock = DockStyle.Fill, PlaceholderText = "Найти объект или поле (первые 1000 совпадений)" };
    private readonly ListBox objects = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly List<Button> buttons = [];
    private CancellationTokenSource? cancellation;
    internal bool Running => cancellation is not null;
    private MetadataIndex Selected => configurations.SelectedItem as MetadataIndex
        ?? throw new InvalidOperationException("Сначала добавьте конфигурацию и выберите её в списке.");
    private ConfiguratorTarget Target => target.SelectedItem as ConfiguratorTarget
        ?? throw new InvalidOperationException("Откройте нужную базу в Конфигураторе, нажмите «Обновить окна» и выберите её.");

    public MetadataWindow(MetadataStore store)
    {
        this.store = store;
        Icon = AppIcon.Value; Text = "Qadam1C — Мои конфигурации";
        Size = new Size(1060, 800); MinimumSize = new Size(900, 720);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 12, Padding = new Padding(16) };
        foreach (var height in new[] { 68, 28, 34, 44, 110, 28, 34, 44, 58, 32 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text =
            "Добавьте папку ручной выгрузки из 1С. Затем выберите открытую базу, где использовать эти данные.\nQadam читает файлы; базу и папку выгрузки не изменяет." }, 0, 0);
        layout.Controls.Add(new Label { Text = "1. Мои конфигурации", Dock = DockStyle.Fill }, 0, 1);
        layout.Controls.Add(configurations, 0, 2);
        var files = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        Add(files, "Добавить папку…", AddFolder);
        Add(files, "Перечитать файлы", RefreshFiles);
        Add(files, "Переименовать…", Rename);
        Add(files, "Убрать из списка…", Remove);
        Add(files, "Как выгрузить?", () => { ShowHelp(); return Task.CompletedTask; });
        Add(files, "Импорт MXL…", ImportReport);
        layout.Controls.Add(files, 0, 3); layout.Controls.Add(details, 0, 4);
        layout.Controls.Add(new Label { Text = "2. Где использовать выбранную конфигурацию", Dock = DockStyle.Fill }, 0, 5);
        layout.Controls.Add(target, 0, 6);
        var connections = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        Add(connections, "Использовать в этой базе", Bind);
        Add(connections, "Отключить для этой базы", () => { store.Unbind(Target.Key); RefreshBinding(); return Task.CompletedTask; });
        Add(connections, "Обновить окна", () => { ReloadTargets(); return Task.CompletedTask; });
        var cancel = new Button { Text = "Отменить чтение", AutoSize = true };
        cancel.Click += (_, _) => cancellation?.Cancel(); connections.Controls.Add(cancel);
        layout.Controls.Add(connections, 0, 7); layout.Controls.Add(binding, 0, 8);
        layout.Controls.Add(search, 0, 9); layout.Controls.Add(objects, 0, 10); layout.Controls.Add(status, 0, 11);
        Controls.Add(layout);
        configurations.SelectedIndexChanged += (_, _) => { ShowDetails(); RefreshBinding(); };
        target.SelectedIndexChanged += (_, _) => RefreshBinding();
        search.TextChanged += (_, _) => ShowObjects();
        FormClosing += (_, e) => { if (Running) { e.Cancel = true; cancellation?.Cancel(); } };
        Reload(); ReloadTargets();
    }
    private void Add(FlowLayoutPanel panel, string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32 };
        buttons.Add(button); panel.Controls.Add(button);
        button.Click += async (_, _) =>
        {
            if (Running) return;
            try { await action(); }
            catch (OperationCanceledException) { status.Text = "Чтение отменено. Ранее сохранённые данные не изменены."; }
            catch (Exception ex) { status.Text = ex.Message; MessageBox.Show(this, ex.Message, "Не удалось выполнить действие", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
    }
    private async Task Read(Func<CancellationToken, MetadataIndex> read)
    {
        cancellation = new();
        foreach (var b in buttons) b.Enabled = false;
        configurations.Enabled = target.Enabled = false;
        status.Text = "Читаю файлы. Дождитесь завершения…";
        try
        {
            var token = cancellation.Token;
            var index = await Task.Run(() => read(token), token);
            token.ThrowIfCancellationRequested();
            store.SaveConfiguration(index); Reload(index.Id);
            status.Text = "Файлы прочитаны. Для использования в 1С выберите базу во втором шаге.";
        }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            foreach (var b in buttons) b.Enabled = true;
            configurations.Enabled = target.Enabled = true;
        }
    }
    private async Task AddFolder()
    {
        using var picker = new FolderBrowserDialog { Description = "Выберите папку с Configuration.xml и ConfigDumpInfo.xml", UseDescriptionForTitle = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var existing = store.Caches.FirstOrDefault(c => c.SourceKind == "xml-folder" &&
            string.Equals(Path.GetFullPath(c.Source).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(picker.SelectedPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        await Read(t => ConfigurationFolder.Read(picker.SelectedPath, existing, t));
    }
    private async Task RefreshFiles()
    {
        var selected = Selected;
        await Read(t => selected.SourceKind == "xml-folder" ? ConfigurationFolder.Read(selected.Source, selected, t, allowExtension: selected.ExtensionName is not null)
            : MetadataReport.Read(selected.Source, selected.Id, t) with { Name = selected.Name });
    }
    private async Task ImportReport()
    {
        using var picker = new OpenFileDialog { Filter = "Отчёт по конфигурации (*.mxl)|*.mxl", Title = "Дополнительный способ: импорт отчёта MXL" };
        if (picker.ShowDialog(this) == DialogResult.OK) await Read(t => MetadataReport.Read(picker.FileName, cancellation: t));
    }
    private Task Rename()
    {
        var selected = Selected;
        using var dialog = new Form { Text = "Название в Qadam1C", Size = new Size(450, 180), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var name = new TextBox { Text = selected.Name, Dock = DockStyle.Top, MaxLength = 100 };
        var hint = new Label { Text = "Например: УНФ — рабочая или УНФ — тестовая. Имя в 1С не изменится.", Dock = DockStyle.Top, Height = 44 };
        var ok = new Button { Text = "Сохранить", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
        dialog.Controls.Add(name); dialog.Controls.Add(hint); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidOperationException("Введите название конфигурации.");
            store.SaveConfiguration(selected with { Name = name.Text.Trim() }); Reload(selected.Id);
        }
        return Task.CompletedTask;
    }
    private Task Remove()
    {
        var selected = Selected;
        if (MessageBox.Show(this, $"Убрать «{selected.Name}» из Qadam1C и отключить её от баз?\nФайлы выгрузки и база 1С останутся на месте.",
            "Убрать конфигурацию", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        { store.Remove(selected.Id); Reload(); status.Text = "Конфигурация убрана из списка. Исходные файлы сохранены."; }
        return Task.CompletedTask;
    }
    private Task Bind()
    {
        var selected = Selected; var destination = Target;
        if (selected.ExtensionName is not null) throw new InvalidOperationException("Расширения подключаются автоматически мастером «Подключить базы». Не подключайте расширение вместо основной конфигурации.");
        if (!ConfiguratorTarget.List().Any(t => t.Pid == destination.Pid && t.Started == destination.Started))
            throw new InvalidOperationException("Это окно 1С уже закрыто. Обновите список окон.");
        if (MessageBox.Show(this, $"Использовать данные «{selected.Name}» в окне:\n{destination.Title}\n\nПроверьте, что выгрузка сделана именно из этой базы. По папке это нельзя установить автоматически.",
            "Подключить конфигурацию", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return Task.CompletedTask;
        store.Bind(destination.Key, selected.Id); RefreshBinding();
        status.Text = "Подключено. После изменений в 1С повторите ручную выгрузку и перечитайте файлы.";
        return Task.CompletedTask;
    }
    private void Reload(string? id = null)
    {
        configurations.Items.Clear(); configurations.Items.AddRange(store.Caches.Cast<object>().ToArray());
        configurations.SelectedItem = store.Caches.FirstOrDefault(c => c.Id == id);
        if (configurations.SelectedIndex < 0 && configurations.Items.Count > 0) configurations.SelectedIndex = 0;
        ShowDetails(); RefreshBinding();
    }
    private void ReloadTargets()
    {
        var previous = target.SelectedItem as ConfiguratorTarget;
        target.Items.Clear(); target.Items.AddRange(ConfiguratorTarget.List());
        target.SelectedItem = target.Items.Cast<ConfiguratorTarget>().FirstOrDefault(t => t.Pid == previous?.Pid && t.Started == previous.Started);
        if (target.SelectedIndex < 0 && target.Items.Count == 1) target.SelectedIndex = 0;
        RefreshBinding();
    }
    private void RefreshBinding()
    {
        if (target.SelectedItem is not ConfiguratorTarget t)
        { binding.Text = "Выберите нужное окно. Если списка нет — откройте Конфигуратор и нажмите «Обновить окна». Папки можно добавлять без открытой 1С."; return; }
        var current = store.For(t.Key);
        binding.Text = (current is null ? "Для этой базы ничего не подключено." : $"Сейчас используется: {current.Name}.") +
            (t.Key.StartsWith("base:") ? " Подключение сохраняется после перезапуска 1С." : " Адрес базы не определён: подключение действует только до закрытия этого сеанса 1С.");
    }
    private void ShowDetails()
    {
        details.Text = configurations.SelectedItem is MetadataIndex i
            ? $"Источник: {i.Source}\nПрочитано: {i.Updated:g}. Объектов и полей: {i.Entries.Count}.\n" +
                (i.SourceKind == "xml-folder" ? $"Найдено файлов модулей: {i.ModuleCount}. " + (i.TypeSchemaVersion > 0 ? "Прочитаны типы реквизитов для подсказок после точки.\n" : "Для типов реквизитов нажмите «Перечитать файлы».\n") : "Отчёт MXL: структура и типы полей, без текстов модулей.\n") +
                "После изменений в 1С повторите выгрузку, затем нажмите «Перечитать файлы»."
            : "Пока нет конфигураций. Нажмите «Как выгрузить?», затем «Добавить папку…». Открывать базу в Qadam не требуется.";
        ShowObjects();
    }
    private void ShowObjects()
    {
        objects.BeginUpdate();
        try
        {
            objects.Items.Clear();
            if (configurations.SelectedItem is MetadataIndex i)
                objects.Items.AddRange(i.Entries.Where(e => (e.Path + " " + e.Synonym).Contains(search.Text, StringComparison.OrdinalIgnoreCase))
                    .Take(1000).Select(e => (object)e.Path).ToArray());
        }
        finally { objects.EndUpdate(); }
    }
    private void ShowHelp() => MessageBox.Show(this,
        "1. В Конфигураторе выберите «Конфигурация → Выгрузить конфигурацию в файлы…».\n" +
        "2. Укажите отдельную папку для этой базы и иерархический формат. Дождитесь завершения.\n" +
        "3. Здесь нажмите «Добавить папку…» и выберите папку с Configuration.xml и ConfigDumpInfo.xml.\n" +
        "4. При необходимости переименуйте запись, например «УНФ — тестовая».\n" +
        "5. Выберите нужное окно 1С и нажмите «Использовать в этой базе».\n\n" +
        "Для обновления повторите выгрузку в ту же папку и нажмите «Перечитать файлы». Эта кнопка не запускает выгрузку.\n" +
        "Для выгрузки и подключения расширений используйте «Подключить базы / обновить выгрузку» в меню Qadam возле часов.",
        "Как подготовить конфигурацию", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
