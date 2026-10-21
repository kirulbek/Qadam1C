using System.IO;

namespace Qadam1C;

internal static class SnippetChecks
{
    internal static int Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var seed = Path.Combine(AppContext.BaseDirectory, "data", "1cnik.st");
        var entries = StReader.Read(File.ReadAllText(seed));
        Check(entries.Count == 184, $"Импортировано {entries.Count}, ожидалось 184 записи.");
        Check(entries.Count(s => s.Trigger.Length > 0) == 169, "Потеряны сокращения.");
        Check(entries.All(s => !s.Group.Contains("1CNIK", StringComparison.OrdinalIgnoreCase)), "Остались подписи в группах.");
        Check(!entries.Any(SnippetCleanup.IsKeyboardLayout), "Остались шаблоны раскладки.");
        foreach (var entry in entries)
        {
            var value = SnippetExpansion.Expand(entry.Body);
            Check(!value.Text.Contains("<?"), $"Не удалено поле: {entry.Name}");
            Check(value.Cursor >= 0 && value.Cursor <= value.Text.Length, "Неверное положение курсора.");
        }
        var sample = SnippetExpansion.Expand("Если <?> Тогда\r\n\t<?\"Имя\">A\r\nКонецЕсли;");
        Check(sample.Text == "Если  Тогда\r\n\tA\r\nКонецЕсли;" && sample.Cursor == 5, "Ошибка разворачивания.");
        var nested = SnippetExpansion.Expand("x<?\"\", ВыборВарианта, \"a>\", \"<?>\">y");
        Check(nested.Text == "xy" && nested.Cursor == 1, "Вложенное поле повреждено.");
        var indented = SnippetExpansion.Indent(SnippetExpansion.Expand("a\n\t<?>b\nc"), "\t");
        Check(indented.Text == "a\r\n\t\tb\r\n\tc" && indented.Cursor == 5, "Ошибка отступов.");
        var snapshot = new EditorSnapshot(IntPtr.Zero, "Если", 4, "Если", 0, 0);
        Check(CompletionChoice.Find(snapshot, entries, false).Any(c => c.Template?.Name == "Если" && c.ReplaceLength == 4), "Полное сокращение не найдено.");
        snapshot = snapshot with { Text = "Для каж", Offset = 7, Prefix = "каж" };
        Check(CompletionChoice.Find(snapshot, entries, false).Any(c => c.Template?.Name == "Для каждого" && c.ReplaceLength == 7), "Составное сокращение не найдено.");
        var path = Path.Combine(AppContext.BaseDirectory, "snippet-check-" + Guid.NewGuid() + ".json");
        try
        {
            var library = new SnippetLibrary(path);
            var defaultsPath = Path.Combine(AppContext.BaseDirectory, "data", "default-snippets.json");
            library.InitializeDefaults(defaultsPath);
            Check(library.Items.Count == 183 && !library.Items.Any(SnippetCleanup.IsKeyboardLayout), "Неверные шаблоны первого запуска.");
            library.Save([]);
            library.InitializeDefaults(defaultsPath);
            Check(library.Items.Count == 0, "Обновление заменяет сохранённую библиотеку.");
            Check(library.Import(seed) == 184 && library.Import(seed) == 0, "Повторный импорт создаёт дубли.");
            var custom = new Snippet("custom", "Мои", "Тест", "тест", "Сообщить(<?>);");
            library.Save(library.Items.Append(custom));
            Check(new SnippetLibrary(path).Items.Contains(custom), "Пользовательский шаблон не сохранён.");
            using var manager = new SnippetManager(library);
            manager.Show(); Application.DoEvents();
            manager.CheckCreation();
            Check(new SnippetLibrary(path).Items.Any(s => s.Trigger == "тестшаблон"), "Запись редактора не сохранилась на диске.");
            manager.Close();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "snippet-test-result.txt"),
            $"PASS: {entries.Count} записей, 169 сокращений; поля, курсор, отступы, поиск, повторный импорт, сохранение, окно редактора.");
        return 0;
    }
}
