using System.IO;
using System.Text.Json;

namespace Qadam1C;

internal sealed record ExportSettings(string Root, string Platform)
{
    internal static string SettingsPath => AppDataPaths.FilePath("export-settings.json");
    internal static ExportSettings Load() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<ExportSettings>(File.ReadAllText(SettingsPath)) ?? new("", "") : new("", "");
    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(this));
        File.Move(SettingsPath + ".tmp", SettingsPath, true);
    }
}

internal sealed class ExportWizard : Form
{
    private readonly MetadataStore store;
    private readonly CheckedListBox bases = new() { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
    private readonly TextBox root = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly ComboBox platform = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox extensions = new() { Text = "Выгружать и подключать все расширения (снять — оставить подключённой только основную)", Checked = true, Dock = DockStyle.Fill };
    private readonly TextBox progress = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button run = new() { Text = "Выгрузить и подключить", AutoSize = true };
    private readonly Button cancel = new() { Text = "Отменить выгрузку", AutoSize = true, Enabled = false };
    private readonly List<Control> inputs = [];
    private CancellationTokenSource? cancellation;
    private bool loading;
    internal bool Running => cancellation is not null;
    internal ExportWizard(MetadataStore store, Infobase[]? previewBases = null)
    {
        this.store = store; Icon = AppIcon.Value;
        Text = "Qadam1C — Подключение баз"; Size = new Size(1060, 800); MinimumSize = new Size(900, 700);
        StartPosition = FormStartPosition.CenterScreen;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 12 };
        panel.ColumnStyles.Add(new(SizeType.Percent, 100)); panel.ColumnStyles.Add(new(SizeType.Absolute, 180));
        foreach (var h in new[] { 56, 28, 190, 28, 34, 40, 28, 34, 34, 48 }) panel.RowStyles.Add(new(SizeType.Absolute, h));
        panel.RowStyles.Add(new(SizeType.Percent, 100)); panel.RowStyles.Add(new(SizeType.Absolute, 40));
        void Wide(Control control, int row) { panel.Controls.Add(control, 0, row); panel.SetColumnSpan(control, 2); }
        Wide(new Label { Dock = DockStyle.Fill, Text = "Отметьте базы из списка 1С и выберите общую папку. Qadam выгрузит конфигурации и подключит их автоматически.\nПеред началом сохраните изменения и закройте Конфигураторы выбранных баз." }, 0);
        Wide(new Label { Dock = DockStyle.Fill, Text = "1. Информационные базы" }, 1);
        panel.Controls.Add(bases, 0, 2);
        var reload = new Button { Text = "Обновить список", Dock = DockStyle.Top, Height = 32 };
        reload.Click += async (_, _) => await LoadBases(); panel.Controls.Add(reload, 1, 2);
        Wide(new Label { Dock = DockStyle.Fill, Text = "2. Общая папка для всех выгрузок" }, 3);
        panel.Controls.Add(root, 0, 4);
        var folder = new Button { Text = "Выбрать папку…", Dock = DockStyle.Fill };
        folder.Click += (_, _) => { using var d = new FolderBrowserDialog { Description = "Общая локальная папка Qadam1C", UseDescriptionForTitle = true };
            if (d.ShowDialog(this) == DialogResult.OK) root.Text = d.SelectedPath; };
        panel.Controls.Add(folder, 1, 4);
        Wide(new Label { Dock = DockStyle.Fill, Text = "Внутри: Имя базы / Основная и Имя базы / Расширения / Имя расширения.\nК имени папки добавляется короткий идентификатор, чтобы не смешать одноимённые базы." }, 5);
        Wide(new Label { Dock = DockStyle.Fill, Text = "3. Платформа 1С для выгрузки (версия должна подходить выбранным базам)" }, 6);
        panel.Controls.Add(platform, 0, 7);
        var browse = new Button { Text = "Указать 1cv8.exe…", Dock = DockStyle.Fill };
        browse.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Платформа 1С (1cv8.exe)|1cv8.exe" };
            if (d.ShowDialog(this) == DialogResult.OK) { platform.Items.Add(d.FileName); platform.SelectedItem = d.FileName; } };
        panel.Controls.Add(browse, 1, 7); Wide(extensions, 8);
        Wide(new Label { Dock = DockStyle.Fill, Text = "Если 1С запросит вход — введите данные в её окне. Пароли Qadam не хранит.\nПри ошибке прежняя выгрузка остаётся рабочей. Успешные предыдущие версии сохраняются в общей папке «.предыдущие»." }, 9);
        Wide(progress, 10);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        actions.Controls.Add(run); actions.Controls.Add(cancel); Wide(actions, 11);
        inputs.AddRange([bases, root, platform, extensions, reload, folder, browse, run]);
        Controls.Add(panel);
        var settings = ExportSettings.Load(); root.Text = settings.Root;
        platform.Items.AddRange(InfobaseCatalog.Platforms());
        if (platform.Items.Contains(settings.Platform)) platform.SelectedItem = settings.Platform;
        run.Click += async (_, _) => await ExportSelected();
        cancel.Click += (_, _) => cancellation?.Cancel();
        Shown += async (_, _) =>
        {
            if (previewBases is null) await LoadBases();
            else { bases.Items.AddRange(previewBases); progress.Text = "Выберите базы и общую папку. Выгрузка ещё не запущена."; }
        };
        FormClosing += (_, e) => { if (Running) { e.Cancel = true; progress.AppendText("\r\nСначала отмените выгрузку или дождитесь её завершения."); } };
        bases.ItemCheck += (_, e) => BeginInvoke(new Action(() =>
        {
            if (platform.SelectedIndex >= 0 || bases.CheckedItems.Count != 1) return;
            if (bases.CheckedItems[0] is not Infobase b) return;
            if (b.Version.Length > 0)
                platform.SelectedItem = platform.Items.Cast<string>().FirstOrDefault(p => InfobaseCatalog.VersionMatches(new DirectoryInfo(Path.GetDirectoryName(p)!).Parent!.Name, b.Version));
        }));
    }
    private async Task LoadBases()
    {
        if (Running || loading) return;
        loading = true;
        foreach (var control in inputs) control.Enabled = false;
        try
        {
            var result = await Task.Run(() => { var list = InfobaseCatalog.Load(out var warnings); return (list, warnings); });
            if (IsDisposed) return;
            var selected = bases.CheckedItems.Cast<Infobase>().Select(b => b.Key).ToHashSet();
            bases.Items.Clear();
            foreach (var b in result.list) bases.Items.Add(b, selected.Contains(b.Key));
            progress.Text = $"Найдено баз: {result.list.Length}. Веб-подключения и группы списка не показываются.\r\n" + string.Join("\r\n", result.warnings);
        }
        catch (Exception ex) { if (!IsDisposed) progress.Text = "Не удалось прочитать список баз: " + ex.Message; }
        finally { loading = false; if (!IsDisposed) foreach (var control in inputs) control.Enabled = true; }
    }
    private async Task ExportSelected()
    {
        if (Running || loading) return;
        try
        {
            var selected = bases.CheckedItems.Cast<Infobase>().ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("Отметьте хотя бы одну базу.");
            if (string.IsNullOrWhiteSpace(root.Text)) throw new InvalidOperationException("Выберите общую папку для файлов.");
            if (platform.SelectedItem is not string exe || !File.Exists(exe)) throw new InvalidOperationException("Выберите установленную платформу 1С.");
            var version = new DirectoryInfo(Path.GetDirectoryName(exe)!).Parent!.Name;
            foreach (var b in selected)
            {
                if (!InfobaseCatalog.VersionMatches(version, b.Version))
                    throw new InvalidOperationException($"Для «{b.Name}» в списке 1С указана версия {b.Version}. Выберите соответствующую платформу. Базы с разными версиями выгружайте отдельно.");
                DesignerExport.CheckClosed(b);
            }
            new ExportSettings(root.Text, exe).Save();
            cancellation = new(); var token = cancellation.Token;
            foreach (var c in inputs) c.Enabled = false; cancel.Enabled = true;
            progress.Clear();
            var reporter = new Progress<string>(s => progress.AppendText(s + "\r\n"));
            foreach (var b in selected)
                await ConfigurationExport.Export(b, exe, root.Text, extensions.Checked, store, reporter, token);
            progress.AppendText("Готово. Откройте нужную базу в Конфигураторе; ручное подключение больше не требуется.\r\n");
        }
        catch (OperationCanceledException) { progress.AppendText("Отменено. Незавершённая выгрузка не подключена.\r\n"); }
        catch (Exception ex) { progress.AppendText(ex.Message + "\r\n"); MessageBox.Show(this, ex.Message, "Выгрузка не завершена", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally
        {
            cancellation?.Dispose(); cancellation = null;
            foreach (var c in inputs) c.Enabled = true; cancel.Enabled = false;
        }
    }
}
