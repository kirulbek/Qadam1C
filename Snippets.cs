using System.IO;
using System.Text;
using System.Text.Json;

namespace Qadam1C;

internal sealed record Snippet(string Id, string Group, string Name, string Trigger, string Body, bool Enabled = true)
{
    public override string ToString() => $"{Name}   [{Group}]";
    public string MatchText => Trigger.Replace("[", "").Replace("]", "");
}

internal sealed class SnippetLibrary
{
    private readonly string path;
    public List<Snippet> Items { get; private set; }
    public SnippetLibrary(string path)
    {
        this.path = path;
        Items = File.Exists(path)
            ? JsonSerializer.Deserialize<List<Snippet>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Пустая база шаблонов.")
            : [];
        if (File.Exists(path) && !File.Exists(path + ".cleanup-v2"))
        {
            if (!File.Exists(path + ".before-cleanup")) File.Copy(path, path + ".before-cleanup");
            Save(Items.Where(s => !SnippetCleanup.IsKeyboardLayout(s)).Select(SnippetCleanup.Clean));
            File.WriteAllText(path + ".cleanup-v2", "1");
        }
    }
    public void Save(IEnumerable<Snippet> items)
    {
        var next = items.ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
        Items = next;
    }
    internal void InitializeDefaults(string seedPath)
    {
        // Even an empty saved library is a user choice; do not replace it on updates.
        if (File.Exists(path)) return;
        var defaults = JsonSerializer.Deserialize<List<Snippet>>(File.ReadAllText(seedPath))
            ?? throw new InvalidDataException("Пустой набор шаблонов по умолчанию.");
        Save(defaults.Where(s => !SnippetCleanup.IsKeyboardLayout(s)).Select(SnippetCleanup.Clean));
    }
    public int Import(string file)
    {
        var imported = StReader.Read(File.ReadAllText(file));
        var next = Items.ToList();
        foreach (var item in imported)
            if (!next.Any(s => s.Group == item.Group && s.Name == item.Name && s.Trigger == item.Trigger && s.Body == item.Body))
                next.Add(item);
        var added = next.Count - Items.Count;
        if (added > 0) Save(next);
        return added;
    }
}

// A recursive reader preserves commas, braces and doubled quotes inside BSL strings.
internal sealed class StReader(string text)
{
    private int position;
    public static List<Snippet> Read(string text)
    {
        var reader = new StReader(text.TrimStart('\uFEFF'));
        var root = reader.Array(0);
        reader.Space();
        if (reader.position != reader.textLength) throw new InvalidDataException("Лишние данные после структуры .st.");
        var result = new List<Snippet>();
        if (root.Count < 1 || root[0] is not string count || !int.TryParse(count, out var n) || n != root.Count - 1)
            throw new InvalidDataException("Неверный корень файла .st.");
        foreach (var node in root.Skip(1)) Walk(node, "", result);
        return result.Where(s => !SnippetCleanup.IsKeyboardLayout(s)).Select(SnippetCleanup.Clean).ToList();
    }
    private int textLength => text.Length;
    private void Space() { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
    private List<object> Array(int depth)
    {
        Space();
        if (depth > 100 || position >= text.Length || text[position++] != '{') throw new InvalidDataException("Неверная структура .st.");
        var items = new List<object>();
        while (true)
        {
            Space();
            if (position >= text.Length) throw new InvalidDataException("Незавершённый файл .st.");
            if (text[position] == '}') { position++; return items; }
            if (items.Count > 0)
            {
                if (text[position++] != ',') throw new InvalidDataException("Ожидалась запятая в .st.");
                Space();
            }
            if (position >= text.Length) throw new InvalidDataException("Незавершённый файл .st.");
            if (text[position] == '{') items.Add(Array(depth + 1));
            else if (text[position] == '"')
            {
                position++;
                var value = new StringBuilder();
                var closed = false;
                while (position < text.Length)
                {
                    var c = text[position++];
                    if (c == '"')
                    {
                        if (position < text.Length && text[position] == '"') { position++; value.Append('"'); }
                        else { closed = true; break; }
                    }
                    else value.Append(c);
                }
                if (!closed) throw new InvalidDataException("Незавершённая строка в .st.");
                items.Add(value.ToString());
            }
            else
            {
                var start = position;
                while (position < text.Length && text[position] is not ',' and not '}') position++;
                items.Add(text[start..position].Trim());
            }
        }
    }
    private static void Walk(object raw, string group, List<Snippet> result)
    {
        if (raw is not List<object> node || node.Count < 2 ||
            node[0] is not string size || !int.TryParse(size, out var children) || children != node.Count - 2 ||
            node[1] is not List<object> header || header.Count != 5 || header.Any(x => x is not string))
            throw new InvalidDataException("Неверная запись шаблона .st.");
        var h = header.Cast<string>().ToArray();
        if (h[1] == "1")
        {
            var path = string.IsNullOrEmpty(group) ? h[0].Trim() : group + " / " + h[0].Trim();
            foreach (var child in node.Skip(2)) Walk(child, path, result);
        }
        else if (h[1] == "0" && children == 0)
            result.Add(new(Guid.NewGuid().ToString("N"), group, h[0], h[3], h[4]));
        else throw new InvalidDataException("Неизвестный тип записи .st.");
    }
}

internal sealed record ExpandedSnippet(string Text, int Cursor);

internal static class SnippetCleanup
{
    internal static bool IsKeyboardLayout(Snippet value) => value.Group.Split(" / ")
        .Any(p => p.Trim().Equals("Раскладка клавиатуры", StringComparison.OrdinalIgnoreCase));
    internal static Snippet Clean(Snippet value)
    {
        var parts = value.Group.Split(" / ", StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.TrimStart().StartsWith("1CNIK.BY", StringComparison.OrdinalIgnoreCase)).ToArray();
        var group = parts.Length == 0 ? "Шаблоны" : string.Join(" / ", parts);
        var layout = parts.Any(p => p.Equals("Раскладка клавиатуры", StringComparison.OrdinalIgnoreCase));
        return value with { Group = group, Enabled = value.Enabled && !layout && value.Name != "История изменений" };
    }
}

internal static class SnippetExpansion
{
    public static ExpandedSnippet Expand(string body)
    {
        var output = new StringBuilder();
        int? cursor = null;
        int? firstField = null;
        for (var i = 0; i < body.Length;)
        {
            if (i + 1 < body.Length && body[i] == '<' && body[i + 1] == '?')
            {
                var start = i;
                i += 2;
                var quoted = false;
                for (; i < body.Length; i++)
                {
                    if (body[i] == '"')
                    {
                        if (quoted && i + 1 < body.Length && body[i + 1] == '"') { i++; continue; }
                        quoted = !quoted;
                    }
                    else if (body[i] == '>' && !quoted) break;
                }
                if (i == body.Length) throw new InvalidDataException("Незакрытый параметр шаблона <?…>.");
                firstField ??= output.Length;
                if (i == start + 2) cursor ??= output.Length;
                i++;
            }
            else output.Append(body[i++]);
        }
        return new(output.ToString(), cursor ?? firstField ?? output.Length);
    }

    public static ExpandedSnippet Indent(ExpandedSnippet value, string indent)
    {
        var before = value.Text[..value.Cursor].Replace("\r\n", "\n").Replace('\r', '\n');
        var text = value.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        // Clipboard text uses CRLF. Carry the existing line indentation to each subsequent line.
        return new(text.Replace("\n", "\r\n" + indent), before.Replace("\n", "\r\n" + indent).Length);
    }
}

internal sealed class SnippetManager : Form
{
    private readonly SnippetLibrary library;
    private readonly ListBox list = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly SearchTextBox search = new() { Dock = DockStyle.Top, PlaceholderText = "Поиск по названию, сокращению или группе" };
    private readonly TextBox name = new() { Dock = DockStyle.Fill };
    private readonly TextBox trigger = new() { Dock = DockStyle.Fill };
    private readonly TextBox group = new() { Dock = DockStyle.Fill };
    private readonly TextBox body = new() { Dock = DockStyle.Fill, Multiline = true, AcceptsTab = true, AcceptsReturn = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 11) };
    private readonly CheckBox enabled = new() { Text = "Предлагать в подсказках", Checked = true, AutoSize = true };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 26 };
    private Snippet? selected;
    public SnippetManager(SnippetLibrary library)
    {
        Icon = AppIcon.Value;
        this.library = library;
        Text = "Qadam1C — шаблоны";
        Size = new Size(1150, 730);
        MinimumSize = new Size(850, 560);
        StartPosition = FormStartPosition.CenterScreen;
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1100, 650), SplitterDistance = 360 };
        Shown += (_, _) => split.SplitterDistance = Math.Max(230, split.ClientSize.Width / 3);
        split.Panel1.Controls.Add(list);
        split.Panel1.Controls.Add(search);
        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(8) };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) editor.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 4 ? 46 : 32));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editor.Controls.Add(new Label { Text = "Название", AutoSize = true }, 0, 0); editor.Controls.Add(name, 1, 0);
        editor.Controls.Add(new Label { Text = "Сокращение", AutoSize = true }, 0, 1); editor.Controls.Add(trigger, 1, 1);
        editor.Controls.Add(new Label { Text = "Группа", AutoSize = true }, 0, 2); editor.Controls.Add(group, 1, 2);
        editor.Controls.Add(enabled, 1, 3);
        var hint = new Label { Text = "<?> — место курсора. Остальные поля <?…> при вставке удаляются без вопросов.\nБез сокращения шаблон доступен только здесь.", Dock = DockStyle.Fill };
        editor.Controls.Add(hint, 0, 4); editor.SetColumnSpan(hint, 2);
        editor.Controls.Add(body, 0, 5); editor.SetColumnSpan(body, 2);
        split.Panel2.Controls.Add(editor);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        void Button(string title, Action action)
        {
            var button = new Button { Text = title, AutoSize = true };
            button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Шаблоны", MessageBoxButtons.OK, MessageBoxIcon.Error); } };
            actions.Controls.Add(button);
        }
        Button("Новый", () => { list.ClearSelected(); selected = null; name.Clear(); trigger.Clear(); group.Text = "Мои шаблоны"; body.Clear(); enabled.Checked = true; name.Focus(); });
        Button("Сохранить", Save);
        Button("Удалить", () => { if (selected is null) return;
            if (MessageBox.Show(this, $"Удалить «{selected.Name}»?", "Шаблоны", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            library.Save(library.Items.Where(s => s.Id != selected.Id)); Reload(); });
        Button("Импорт .st", () => { using var dialog = new OpenFileDialog { Filter = "Шаблоны 1С (*.st)|*.st" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { var added = library.Import(dialog.FileName); Reload(); status.Text = $"Добавлено: {added}. Всего: {library.Items.Count}."; } });
        Button("Место курсора", () => { body.SelectedText = "<?>"; body.Focus(); });
        Controls.Add(split); Controls.Add(actions); Controls.Add(status);
        list.SelectedIndexChanged += (_, _) =>
        {
            selected = list.SelectedItem as Snippet;
            if (selected is null) return;
            name.Text = selected.Name; trigger.Text = selected.Trigger; group.Text = selected.Group;
            body.Text = selected.Body; enabled.Checked = selected.Enabled;
        };
        search.TextChanged += (_, _) => Reload();
        Reload();
    }
    private void Reload()
    {
        list.Items.Clear();
        foreach (var item in library.Items.Where(s => (s + " " + s.Trigger).Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            list.Items.Add(item);
        status.Text = $"Всего: {library.Items.Count}. Найдено: {list.Items.Count}. После изменения нажмите «Сохранить».";
    }
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(body.Text))
            throw new InvalidOperationException("Заполните название и текст шаблона.");
        _ = SnippetExpansion.Expand(body.Text);
        var value = new Snippet(selected?.Id ?? Guid.NewGuid().ToString("N"), group.Text.Trim(), name.Text.Trim(), trigger.Text.Trim(), body.Text, enabled.Checked);
        library.Save(library.Items.Where(s => s.Id != value.Id).Append(value));
        Reload();
        list.SelectedItem = value;
        status.Text = "Сохранено. Шаблон уже доступен в подсказках.";
    }
    internal void CheckCreation()
    {
        selected = null;
        name.Text = "Проверка сохранения из редактора";
        trigger.Text = "тестшаблон";
        group.Text = "Тест";
        body.Text = "Сообщить(<?>);";
        Save();
        if (selected is null || !library.Items.Any(s => s.Id == selected.Id && s.Trigger == "тестшаблон"))
            throw new InvalidOperationException("Редактор не сохранил новый шаблон.");
        body.Text = "Если <?> Тогда\r\nКонецЕсли;";
        var id = selected.Id;
        Save();
        if (library.Items.Count(s => s.Id == id) != 1 || library.Items.Single(s => s.Id == id).Body != body.Text)
            throw new InvalidOperationException("Редактор не обновил шаблон.");
    }
}
