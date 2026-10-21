using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qadam1C;

internal sealed record MetadataFavorite(string Path, string Synonym)
{
    internal static string Display(string path)
    {
        var parts = path.Split('.', 2);
        if (parts.Length != 2) return path;
        var root = parts[0] switch
        {
            "Справочники" => "Справочник", "Документы" => "Документ", "ОбщиеМодули" => "ОбщийМодуль",
            "Константы" => "Константа", "Перечисления" => "Перечисление", "Отчеты" => "Отчет", "Обработки" => "Обработка",
            "РегистрыСведений" => "РегистрСведений", "РегистрыНакопления" => "РегистрНакопления",
            "РегистрыБухгалтерии" => "РегистрБухгалтерии", "РегистрыРасчета" => "РегистрРасчета",
            "ПланыСчетов" => "ПланСчетов", "ПланыВидовХарактеристик" => "ПланВидовХарактеристик",
            "ПланыВидовРасчета" => "ПланВидовРасчета", "БизнесПроцессы" => "БизнесПроцесс", "Задачи" => "Задача",
            _ => parts[0]
        };
        return root + "." + parts[1];
    }
    public override string ToString() => Display(Path);
}

internal sealed class FavoritesStore
{
    private readonly string file;
    private Dictionary<string, List<MetadataFavorite>> items;
    private readonly string selectionFile;
    private readonly Dictionary<string, string> selections;
    public FavoritesStore(string file)
    {
        this.file = file;
        selectionFile = Path.ChangeExtension(file, "selection.json");
        selections = File.Exists(selectionFile)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(selectionFile)) ?? new() : new();
        items = File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<string, List<MetadataFavorite>>>(File.ReadAllText(file))
                ?? throw new InvalidDataException("Не удалось прочитать избранные метаданные.")
            : new();
    }
    public IReadOnlyList<MetadataFavorite> For(string cacheId) => items.GetValueOrDefault(cacheId)?.ToArray() ?? [];
    public string? SelectedIndex(string targetKey) => selections.GetValueOrDefault(targetKey);
    public void RememberIndex(string targetKey, string cacheId)
    {
        if (SelectedIndex(targetKey) == cacheId) return;
        var next = new Dictionary<string, string>(selections) { [targetKey] = cacheId };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(selectionFile))!);
        File.WriteAllText(selectionFile + ".tmp", JsonSerializer.Serialize(next));
        File.Move(selectionFile + ".tmp", selectionFile, true);
        selections[targetKey] = cacheId;
    }
    internal static bool Supports(string path)
    {
        var parts = path.Split('.');
        return parts.Length == 2 && MetadataReport.Roots.Contains(parts[0], StringComparer.OrdinalIgnoreCase) &&
            Regex.IsMatch(parts[1], @"^[\p{L}_][\p{L}\p{N}_]*$");
    }
    public void Add(string cacheId, MetadataFavorite favorite)
    {
        if (!Supports(favorite.Path)) throw new InvalidOperationException("Выберите объект конфигурации.");
        var list = For(cacheId).ToList();
        if (list.Any(f => f.Path.Equals(favorite.Path, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(favorite); Save(cacheId, list);
    }
    public void Remove(string cacheId, string path) => Save(cacheId,
        For(cacheId).Where(f => !f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList());
    private void Save(string cacheId, List<MetadataFavorite> list)
    {
        var next = new Dictionary<string, List<MetadataFavorite>>(items) { [cacheId] = list };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
        items = next;
    }
}

internal sealed class FavoritesWindow : Form
{
    protected override bool ShowWithoutActivation => true;
    private readonly MetadataStore metadata;
    private readonly FavoritesStore favorites;
    private readonly ComboBox targets = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
        Font = PanelTypography.Secondary, ForeColor = PanelTypography.Muted };
    private readonly ComboBox caches = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
        Font = PanelTypography.Secondary, ForeColor = PanelTypography.Muted };
    private readonly ListBox list = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(8, 5, 8, 3), AutoEllipsis = true,
        Font = PanelTypography.Secondary, ForeColor = PanelTypography.Muted };
    private readonly ToolTip contextHints = new();
    private readonly FlowLayoutPanel buttons = new() { Dock = DockStyle.Top, Height = 42 };
    private readonly FavoritesAttachment attachment;
    private readonly ModuleOutlinePane outline = new() { Dock = DockStyle.Fill };
    private readonly SplitContainer sections = new()
    {
        Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(440, 460),
        SplitterDistance = 220, Panel1MinSize = 100, Panel2MinSize = 100, SplitterWidth = 7
    };
    private readonly CheckBox showMethods = new() { Text = "Методы и области", AutoSize = true, Margin = new Padding(5) };
    private readonly CheckBox showFavorites = new() { Text = "Избранное", AutoSize = true, Margin = new Padding(5) };
    private readonly Label emptyPanel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        Text = "Оба блока отключены.\nВключите нужный блок переключателями сверху." };
    internal bool Navigating { get; private set; }
    private MetadataIndex? Index => caches.SelectedItem as MetadataIndex;

    public FavoritesWindow(MetadataStore metadata, FavoritesStore favorites)
    {
        this.metadata = metadata; this.favorites = favorites;
        attachment = new FavoritesAttachment(this, () => Navigating);
        Text = "Qadam1C — панель разработчика"; Icon = AppIcon.Value;
        Size = new Size(700, 750); MinimumSize = new Size(560, 560);
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 24, area.Top + 100);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(248, 250, 253);
        list.BackColor = Color.White; list.BorderStyle = BorderStyle.None;
        list.Font = PanelTypography.Identifier; list.ForeColor = PanelTypography.Primary;
        status.TextChanged += (_, _) => contextHints.SetToolTip(status, status.Text);
        void Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Font = PanelTypography.Secondary };
            button.Click += (_, _) => { if (!Navigating) Try(action); };
            buttons.Controls.Add(button);
        }
        Button("＋ Добавить…", Add);
        Button("Убрать", () => { if (Index is { } index && list.SelectedItem is MetadataFavorite item)
            { favorites.Remove(index.Id, item.Path); ReloadList(); } });
        Button("Обновить", () => RefreshTargets());
        var pin = new CheckBox { Text = "Прикрепить справа к 1С", AutoSize = true, Margin = new Padding(5),
            Font = PanelTypography.Secondary, ForeColor = PanelTypography.Muted };
        pin.CheckedChanged += (_, _) =>
        {
            try { attachment.SetEnabled(pin.Checked); }
            catch (Exception ex) { pin.Checked = false; status.Text = ex.Message; }
        };
        var settings = WorkspacePanelSettings.Load(AppDataPaths.FilePath("workspace-panel.json"));
        showMethods.Checked = settings.Methods; showFavorites.Checked = settings.Favorites;
        var switches = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        switches.Controls.Add(showMethods); switches.Controls.Add(showFavorites);
        switches.Controls.Add(pin);
        var favoritesPanel = new Panel { Dock = DockStyle.Fill };
        favoritesPanel.Controls.Add(list); favoritesPanel.Controls.Add(buttons);
        favoritesPanel.Controls.Add(caches);
        favoritesPanel.Controls.Add(new Label { Text = "Избранное · индекс конфигурации", Dock = DockStyle.Top, Height = 22,
            Padding = new Padding(6, 3, 0, 0), Font = PanelTypography.Secondary, ForeColor = PanelTypography.Muted });
        sections.Panel1.Controls.Add(outline);
        sections.Panel2.Controls.Add(favoritesPanel);
        Controls.Add(sections); Controls.Add(emptyPanel); Controls.Add(switches);
        Controls.Add(targets);
        Controls.Add(new Label { Text = "Открытый Конфигуратор", Dock = DockStyle.Top, Height = 21,
            Padding = new Padding(5, 2, 0, 0), Font = PanelTypography.Secondary, ForeColor = PanelTypography.Muted });
        Controls.Add(status);
        void UpdateSections()
        {
            var any = showMethods.Checked || showFavorites.Checked;
            sections.Visible = any; emptyPanel.Visible = !any;
            if (any)
            {
                // Expand before collapsing the other side: SplitContainer does
                // not permit both panels to be collapsed at the same time.
                if (showMethods.Checked) sections.Panel1Collapsed = false;
                if (showFavorites.Checked) sections.Panel2Collapsed = false;
                sections.Panel1Collapsed = !showMethods.Checked;
                sections.Panel2Collapsed = !showFavorites.Checked;
            }
        }
        void SaveSections()
        {
            UpdateSections();
            Try(() => new WorkspacePanelSettings(showMethods.Checked, showFavorites.Checked).Save(AppDataPaths.FilePath("workspace-panel.json")));
            if (!showMethods.Checked) outline.ClearEditor();
        }
        showMethods.CheckedChanged += (_, _) => SaveSections();
        showFavorites.CheckedChanged += (_, _) => SaveSections();
        outline.NavigateRequested = async (snapshot, method) =>
        {
            if (Navigating) return;
            Navigating = true; Enabled = false;
            try { await ModuleOutline.Navigate(snapshot, method); status.Text = $"{method.Name} · строка {method.Line}"; }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { Navigating = false; Enabled = true; }
        };
        outline.ReportStatus = message => status.Text = message;
        outline.ApplyRequested = async (snapshot, plan) =>
        {
            if (Navigating) return false;
            Navigating = true; Enabled = false;
            try
            {
                await MethodRegions.Apply(snapshot, plan);
                status.Text = "Порядок применён. Отмена всей перестановки: Ctrl+Z в 1С.";
                return true;
            }
            catch (Exception ex) { status.Text = ex.Message; return false; }
            finally { Navigating = false; Enabled = true; }
        };
        UpdateSections();
        targets.SelectedIndexChanged += (_, _) =>
        {
            LoadCaches();
            attachment.SetTarget(targets.SelectedItem is ConfiguratorTarget selected ? new IntPtr(selected.Window) : IntPtr.Zero);
        };
        caches.SelectedIndexChanged += (_, _) =>
        {
            if (targets.SelectedItem is ConfiguratorTarget selected && Index is { } chosen)
                Try(() => favorites.RememberIndex(selected.Key, chosen.Id));
            ReloadList();
        };
        list.MouseDoubleClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left && list.IndexFromPoint(e.Location) >= 0)
                await Navigate();
        };
        list.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true; await Navigate();
        };
        FormClosing += (_, e) =>
        {
            if (Navigating) e.Cancel = true;
            else if (outline.HasPendingChanges)
            {
                e.Cancel = true;
                status.Text = "Есть неприменённый порядок. Нажмите «Применить порядок» или «Отменить» перед закрытием панели.";
            }
        };
        FormClosed += (_, _) => { attachment.Dispose(); contextHints.Dispose(); };
        RefreshTargets();
    }
    private void Try(Action action)
    {
        try { action(); } catch (Exception ex) { status.Text = ex.Message; }
    }
    internal void RefreshTargets()
    {
        if (Navigating) return;
        var previous = targets.SelectedItem as ConfiguratorTarget;
        var windows = ConfiguratorTarget.List();
        targets.Items.Clear(); targets.Items.AddRange(windows);
        var foreground = CompletionNative.GetForegroundWindow().ToInt64();
        targets.SelectedItem = windows.FirstOrDefault(t => t.Window == foreground)
            ?? windows.FirstOrDefault(t => t.Pid == previous?.Pid && t.Started == previous.Started)
            ?? windows.FirstOrDefault();
        if (targets.SelectedItem is null) LoadCaches();
    }
    private void LoadCaches()
    {
        var previous = targets.SelectedItem is ConfiguratorTarget selected ? favorites.SelectedIndex(selected.Key) : null;
        caches.Items.Clear(); caches.Items.AddRange(metadata.Caches.Cast<object>().ToArray());
        var bound = targets.SelectedItem is ConfiguratorTarget target ? metadata.For(target.Key) : null;
        caches.SelectedItem = bound ?? metadata.Caches.FirstOrDefault(c => c.Id == previous)
            ?? (metadata.Caches.Count == 1 ? metadata.Caches[0] : null);
        ReloadList();
    }
    private void ReloadList()
    {
        list.Items.Clear();
        if (Index is { } index) list.Items.AddRange(favorites.For(index.Id).Cast<object>().ToArray());
        status.Text = Index is null ? "Выберите индекс. Если его нет, сначала откройте «Метаданные» и выполните сканирование."
            : list.Items.Count == 0 ? "Добавьте нужные объекты. Двойной щелчок выделяет объект в дереве 1С."
            : "Двойной щелчок или Enter — перейти. В дереве или модуле 1С: Ctrl+Alt+B — добавить текущий объект.";
    }
    internal void ShowAdded(ConfiguratorTarget target, MetadataIndex index, string path)
    {
        RefreshTargets();
        targets.SelectedItem = targets.Items.Cast<ConfiguratorTarget>().FirstOrDefault(t => t.Pid == target.Pid && t.Started == target.Started);
        caches.SelectedItem = index;
        ReloadList();
        list.SelectedItem = list.Items.Cast<MetadataFavorite>().FirstOrDefault(f => f.Path == path);
        status.Text = "В избранном: " + MetadataFavorite.Display(path);
    }
    internal void ReportError(string message) => status.Text = message;
    internal void UpdateEditor(EditorSnapshot snapshot)
    {
        if (IsDisposed || Navigating) return;
        var target = targets.Items.Cast<ConfiguratorTarget>().FirstOrDefault(t => t.Window == snapshot.Window.ToInt64());
        if (target is null) { RefreshTargets(); target = targets.Items.Cast<ConfiguratorTarget>().FirstOrDefault(t => t.Window == snapshot.Window.ToInt64()); }
        if (target is null) return;
        if (!Equals(targets.SelectedItem, target)) targets.SelectedItem = target;
        if (showMethods.Checked) outline.UpdateEditor(snapshot);
    }
    internal void ClearEditorWhenInConfigurator()
    {
        if (!IsDisposed && !Navigating && targets.SelectedItem is ConfiguratorTarget target &&
            CompletionNative.GetForegroundWindow().ToInt64() == target.Window)
            outline.ClearEditor();
    }
    private void Add()
    {
        if (Index is not { } index) throw new InvalidOperationException("Сначала выберите индекс конфигурации.");
        using var picker = new FavoritePicker(index, favorites.For(index.Id));
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } selected) return;
        favorites.Add(index.Id, new(selected.Path, selected.Synonym)); ReloadList();
    }
    private async Task Navigate()
    {
        if (Navigating || list.SelectedItem is not MetadataFavorite favorite || Index is not { } index) return;
        if (targets.SelectedItem is not ConfiguratorTarget target) { status.Text = "Откройте Конфигуратор и нажмите «Обновить»."; return; }
        var bound = metadata.For(target.Key);
        if (bound is not null && bound.Id != index.Id)
        { status.Text = "Выбранный индекс относится к другой конфигурации. Выберите подключённый к этому Конфигуратору индекс."; return; }
        Navigating = true; buttons.Enabled = targets.Enabled = caches.Enabled = list.Enabled = false;
        status.Text = "Перехожу к " + favorite.Path + "…";
        try
        {
            // Keep the owned tool visible, but exclude it from activation while 1C receives navigation keys.
            Enabled = false;
            await MetadataTreeNavigation.Select(target, index.Name, favorite.Path);
            status.Text = "Выделен: " + favorite.Path;
        }
        catch (Exception ex)
        {
            status.Text = TrayNotifications.ErrorText(ex, "Переход не выполнен. Подробности: favorites-error.log.");
            try { File.WriteAllText(AppDataPaths.FilePath("favorites-error.log"), ex.ToString()); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally
        {
            Navigating = false; buttons.Enabled = targets.Enabled = caches.Enabled = list.Enabled = true;
            Enabled = true;
        }
    }
}

internal sealed class FavoritePicker : Form
{
    private readonly ListBox list = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    public MetadataEntry? Selected => (list.SelectedItem as Item)?.Entry;
    private sealed record Item(MetadataEntry Entry)
    {
        public override string ToString() => MetadataFavorite.Display(Entry.Path);
    }
    public FavoritePicker(MetadataIndex index, IReadOnlyList<MetadataFavorite> existing)
    {
        Text = "Добавить в избранное"; Icon = AppIcon.Value;
        Size = new Size(720, 480); StartPosition = FormStartPosition.CenterParent;
        var filter = new SearchTextBox { Dock = DockStyle.Top, PlaceholderText = "Имя или синоним объекта" };
        var add = new Button { Text = "Добавить", Dock = DockStyle.Bottom, Height = 36, DialogResult = DialogResult.OK };
        var entries = index.Entries.Where(e => FavoritesStore.Supports(e.Path) &&
            !existing.Any(f => f.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        void Reload()
        {
            list.BeginUpdate(); list.Items.Clear();
            list.Items.AddRange(entries.Where(e => (e.Path + " " + e.Synonym).Contains(filter.Text, StringComparison.OrdinalIgnoreCase))
                .Take(1000).Select(e => (object)new Item(e)).ToArray());
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            add.Enabled = list.Items.Count > 0; list.EndUpdate();
        }
        filter.TextChanged += (_, _) => Reload();
        list.DoubleClick += (_, _) => { if (Selected is not null) { DialogResult = DialogResult.OK; Close(); } };
        Controls.Add(list); Controls.Add(filter); Controls.Add(add); AcceptButton = add;
        Reload();
    }
}
