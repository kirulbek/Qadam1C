using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;

namespace Qadam1C;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Contains("--completion-test")) return CompletionChecks.Run();
            if (args.Contains("--smart-completion-test")) return SmartCompletionChecks.Run();
            if (args.Contains("--snippet-test")) return SnippetChecks.Run();
            if (args.Contains("--favorites-test")) return FavoritesChecks.Run();
            if (args.Contains("--outline-test")) return ModuleOutlineChecks.Run();
            if (args.Contains("--metadata-test")) return MetadataChecks.Run(args.Last());
            if (args.Contains("--metadata-report-check"))
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var index = MetadataReport.Read(args.Last());
                var modules = index.Entries.Where(e => e.Path.StartsWith("ОбщиеМодули.") && e.Path.Split('.').Length == 2).ToArray();
                var suggestions = SmartCompletion.Find(new EditorSnapshot(IntPtr.Zero, "Общ", 3, "Общ", 0, 0),
                    [], index, new CompletionHistory(Path.Combine(AppContext.BaseDirectory, "data", "completion-history.json")), false);
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "metadata-report-check.json"),
                    JsonSerializer.Serialize(new { index.Name, Entries = index.Entries.Count, CommonModules = modules.Length,
                        Examples = modules.Take(5).Select(e => e.Name), Seconds = timer.Elapsed.TotalSeconds,
                        Suggestions = suggestions.Select(c => c.Label),
                        PeakMemoryMB = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024 }));
                return 0;
            }
            if (!args.Contains("--templates") && !args.Contains("--smoke-test"))
            {
                using var instance = new Mutex(true, "Local\\Qadam1C.Completion", out var ownsInstance);
                if (!ownsInstance)
                {
                    if (args.Contains("--snippet-manager"))
                    {
                        using var request = EventWaitHandle.OpenExisting("Local\\Qadam1C.Templates");
                        request.Set();
                    }
                    if (args.Contains("--metadata"))
                    {
                        using var request = EventWaitHandle.OpenExisting("Local\\Qadam1C.Metadata");
                        request.Set();
                    }
                    if (args.Contains("--favorites"))
                    {
                        if (EventWaitHandle.TryOpenExisting("Local\\Qadam1C.Favorites", out var request))
                        { using (request) request.Set(); }
                        else MessageBox.Show("Сейчас работает предыдущая версия Qadam1C. Закройте её через значок возле часов → Выход, затем снова откройте избранное.",
                            "Qadam1C", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return 0;
                }
                if (args.Contains("--import-metadata"))
                {
                    var report = args[Array.IndexOf(args, "--import-metadata") + 1];
                    var pid = int.Parse(args[Array.IndexOf(args, "--pid") + 1]);
                    var target = ConfiguratorTarget.List().Single(t => t.Pid == pid);
                    var index = MetadataReport.Read(report);
                    new MetadataStore(AppDataPaths.FilePath("metadata.json")).Put(index, target.Key);
                }
                using var completion = new CompletionContext();
                if (args.Contains("--snippet-manager")) completion.OpenTemplates();
                if (args.Contains("--metadata")) completion.OpenMetadata();
                if (args.Contains("--favorites")) completion.OpenFavorites();
                Application.Run(completion);
                return 0;
            }
            var store = new TemplateStore(AppDataPaths.FilePath("templates.json"));
            using var form = new MainWindow(store);
            if (args.Contains("--smoke-test"))
            {
                form.Show();
                Application.DoEvents();
                if (form.TemplateCount < 2) throw new InvalidOperationException("Нет начальных шаблонов.");
                form.CheckSearch();
                var testPath = Path.Combine(AppContext.BaseDirectory, "smoke-" + Guid.NewGuid() + ".json");
                try
                {
                    var testStore = new TemplateStore(testPath);
                    var expected = new List<CodeTemplate> { new("Тест Юникода", "Код\r\n\tСтрока") };
                    testStore.Save(expected);
                    if (!testStore.Load().SequenceEqual(expected)) throw new InvalidOperationException("Ошибка сохранения шаблонов.");
                }
                finally { if (File.Exists(testPath)) File.Delete(testPath); }
                form.Close();
                return 0;
            }
            Application.Run(form);
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Contains("--completion-test") || args.Contains("--snippet-test") || args.Contains("--metadata-test") || args.Contains("--smart-completion-test") || args.Contains("--metadata-report-check") || args.Contains("--import-metadata") || args.Contains("--favorites-test") || args.Contains("--outline-test"))
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "completion-error.log"), ex.ToString());
                return 1;
            }
            if (args.Contains("--smoke-test"))
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.log"), ex.ToString());
            if (!args.Contains("--smoke-test"))
                MessageBox.Show(ex.Message, "Qadam1C — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed record CodeTemplate(string Name, string Code);

internal sealed class TemplateStore(string path)
{
    public List<CodeTemplate> Load()
    {
        if (File.Exists(path))
            return JsonSerializer.Deserialize<List<CodeTemplate>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Файл шаблонов пуст или повреждён: " + path);
        return
        [
            new("Процедура", "Процедура ИмяПроцедуры()\r\n\t// Ваш код\r\nКонецПроцедуры"),
            new("Функция", "Функция ИмяФункции()\r\n\tВозврат Неопределено;\r\nКонецФункции")
        ];
    }

    public void Save(List<CodeTemplate> templates)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(templates,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}

internal sealed class MainWindow : Form
{
    private const int HotkeyId = 0x5143;
    private readonly TemplateStore store;
    private readonly List<CodeTemplate> templates;
    private readonly SearchTextBox search = new() { PlaceholderText = "Поиск шаблона…", Dock = DockStyle.Fill };
    private readonly ListBox list = new() { Dock = DockStyle.Fill, DisplayMember = "Name" };
    private readonly TextBox name = new() { PlaceholderText = "Название шаблона", Dock = DockStyle.Fill };
    private readonly TextBox editor = new()
    {
        Multiline = true, AcceptsTab = true, AcceptsReturn = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 11), MaxLength = 0
    };
    private readonly Label status = new() { AutoSize = true, Padding = new Padding(8) };
    private CodeTemplate? selected;
    private bool hotkeyRegistered;
    public int TemplateCount => templates.Count;

    public MainWindow(TemplateStore store)
    {
        Icon = AppIcon.Value;
        this.store = store;
        templates = store.Load();
        Text = "Qadam1C — помощник разработчика 1С";
        Size = new Size(1000, 700);
        MinimumSize = new Size(800, 540);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.Controls.Add(new Label
        {
            Text = "Qadam1C   /   Шаблоны BSL", AutoSize = true,
            Font = new Font("Segoe UI", 18, FontStyle.Bold)
        }, 0, 0);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 260 };
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(search, 0, 0);
        left.Controls.Add(list, 0, 1);
        split.Panel1.Controls.Add(left);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(name, 0, 0);
        right.Controls.Add(editor, 0, 1);
        split.Panel2.Controls.Add(right);
        root.Controls.Add(split, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        AddButton(actions, "Новый", () => { list.ClearSelected(); selected = null; name.Clear(); editor.Clear(); name.Focus(); });
        AddButton(actions, "Сохранить шаблон", SaveTemplate);
        AddButton(actions, "Из буфера", ReadClipboard);
        AddButton(actions, "Копировать код", CopyCode);
        root.Controls.Add(actions, 0, 2);
        root.Controls.Add(status, 0, 3);
        Controls.Add(root);
        search.TextChanged += (_, _) => RefreshList();
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedItem is not CodeTemplate template) return;
            selected = template;
            name.Text = template.Name;
            editor.Text = template.Code;
        };
        RefreshList();
        if (list.Items.Count > 0) list.SelectedIndex = 0;
    }

    private static void AddButton(Control parent, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 34 };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Qadam1C", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        parent.Controls.Add(button);
    }

    private void RefreshList()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var template in templates.Where(t =>
            t.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) ||
            t.Code.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            list.Items.Add(template);
        list.EndUpdate();
    }

    internal void CheckSearch()
    {
        search.Text = "НетТакогоШаблона_327491";
        if (list.Items.Count != 0) throw new InvalidOperationException("Ошибка фильтрации шаблонов.");
        search.Clear();
        if (list.Items.Count != templates.Count) throw new InvalidOperationException("Ошибка сброса поиска.");
    }

    private void SaveTemplate()
    {
        var title = name.Text.Trim();
        if (title.Length == 0 || string.IsNullOrWhiteSpace(editor.Text))
            throw new InvalidOperationException("Введите название и код шаблона.");
        if (templates.Any(t => t != selected && string.Equals(t.Name, title, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Шаблон с таким названием уже существует.");
        var updated = new CodeTemplate(title, editor.Text);
        var next = new List<CodeTemplate>(templates);
        if (selected is not null && next.Contains(selected)) next[next.IndexOf(selected)] = updated;
        else next.Add(updated);
        store.Save(next);
        templates.Clear();
        templates.AddRange(next);
        selected = updated;
        search.Clear();
        RefreshList();
        list.SelectedItem = updated;
        status.Text = "Шаблон сохранён в папке data рядом с программой.";
    }

    private void ReadClipboard()
    {
        if (!Clipboard.ContainsText()) throw new InvalidOperationException("В буфере обмена нет текста. Скопируйте код в Конфигураторе через Ctrl+C.");
        var text = Clipboard.GetText();
        list.ClearSelected();
        selected = null;
        name.Clear();
        editor.Text = text;
        status.Text = "Код загружен из буфера. Для сохранения задайте название.";
    }

    private void CopyCode()
    {
        if (editor.Text.Length == 0) return;
        Clipboard.SetText(editor.Text);
        status.Text = "Код скопирован. Переключитесь в Конфигуратор и нажмите Ctrl+V.";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, 0x4000 | 0x0002 | 0x0001, (uint)Keys.Q);
        status.Text = hotkeyRegistered
            ? "Ctrl+Alt+Q — открыть помощник. Ctrl+C в 1С → «Из буфера»."
            : "Ctrl+Alt+Q недоступна. Помощник можно открыть через панель задач.";
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (hotkeyRegistered) UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == HotkeyId)
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }
        base.WndProc(ref message);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
