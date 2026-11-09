using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Qadam1C;

internal sealed record MethodDefinition(string Folder, string File, string Sha256, string Module,
    string Method, string Configuration, DateTime Updated);
internal sealed record DefinitionDocument(MethodDefinition Definition, string Text, ModuleMethod Method);

internal static class DefinitionFocus
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    internal static void Return(IntPtr window) => SetForegroundWindow(window);
}

internal static class DefinitionReader
{
    internal static DefinitionDocument Read(MethodDefinition definition)
    {
        var parts = definition.File.Replace('\\', '/').Split('/');
        if (parts.Length != 4 || !parts[0].Equals("CommonModules", StringComparison.OrdinalIgnoreCase) ||
            !parts[1].Equals(definition.Module, StringComparison.OrdinalIgnoreCase) ||
            !parts[2].Equals("Ext", StringComparison.OrdinalIgnoreCase) || !parts[3].Equals("Module.bsl", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Индекс содержит некорректный путь общего модуля.");
        var path = ExportPaths.Child(definition.Folder, definition.File);
        if (!System.IO.File.Exists(path)) throw new FileNotFoundException("Файл модуля не найден. Обновите выгрузку.");
        var bytes = System.IO.File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(definition.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл изменился после индексирования. Обновите выгрузку перед переходом к определению.");
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        var text = reader.ReadToEnd();
        var matches = ModuleOutline.Parse(text).Where(m => m.Exported && m.Name.Equals(definition.Method, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Определение метода не найдено однозначно. Обновите выгрузку.");
        return new(definition, text, matches[0]);
    }
}

internal sealed class MethodDefinitionWindow : Form
{
    internal readonly RichTextBox Code = new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false,
        DetectUrls = false, Font = new Font("Consolas", 11), BackColor = Color.White, HideSelection = false,
        ScrollBars = RichTextBoxScrollBars.Both, AccessibleName = "Определение метода — только чтение" };
    internal int DefinitionOffset { get; }
    internal MethodDefinitionWindow(DefinitionDocument document)
    {
        var target = document.Definition;
        Icon = AppIcon.Value;
        Text = $"{target.Module}.{target.Method} — Qadam1C · определение";
        Size = new Size(1000, 700); MinimumSize = new Size(680, 400); StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        var header = new Label { Dock = DockStyle.Top, Height = 58, Padding = new Padding(10), AutoEllipsis = true,
            Text = $"{target.Configuration} · выгрузка от {target.Updated:g}\nТолько чтение. Несохранённые изменения в 1С здесь не отображаются." };
        var footer = new Label { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(10, 6, 4, 0),
            Text = $"Строка {document.Method.Line} · Esc — закрыть и вернуться в 1С · Ctrl+C — скопировать выделенное" };
        Code.Text = document.Text;
        // RichEdit normalizes CRLF; locate the declaration again in the displayed text.
        var method = ModuleOutline.Parse(Code.Text).Single(m => m.Exported && m.Name.Equals(target.Method, StringComparison.OrdinalIgnoreCase));
        DefinitionOffset = method.Offset;
        Controls.Add(Code); Controls.Add(header); Controls.Add(footer);
        Shown += (_, _) => { Code.Focus(); Code.Select(DefinitionOffset, method.Kind.Length + 1 + method.Name.Length); Code.ScrollToCaret(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); } };
    }
}
