using System.IO;

namespace Qadam1C;

internal static class MetadataChecks
{
    internal static int Run(string report)
    {
        static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
        var index = MetadataReport.Read(report);
        var sample = File.ReadAllBytes(report);
        var firstBrace = Array.IndexOf(sample, (byte)'{', 6);
        var previousCells = System.Text.RegularExpressions.Regex.Matches(
            System.Text.Encoding.UTF8.GetString(sample, firstBrace, sample.Length - firstBrace),
            "\\{\"#\",\"((?:\"\"|[^\"])*)\"\\}")
            .Select(m => m.Groups[1].Value.Replace("\"\"", "\""));
        Check(MetadataReport.Cells(report, default).SequenceEqual(previousCells),
            "Потоковое чтение изменило ячейки или экранирование кавычек.");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { MetadataReport.Read(report, cancellation: cancelled.Token); throw new InvalidOperationException("Отмена импорта не сработала."); }
            catch (OperationCanceledException) { }
        }
        Check(index.Entries.Any(e => e.Path == "Документы.ПриходныйКассовыйОрдер.Реквизиты.Покупатель" &&
            e.Types.Contains("СправочникСсылка.Покупатели")), "Не прочитан тип реквизита.");
        CompletionChoice[] Find(string code, string title = "") => MetadataCompletion.Find(
            new(IntPtr.Zero, code, code.Length, code.Split('.').Last(), 0, 0, "test", title), index) ?? [];
        Check(Find("Документы.").Any(c => c.InsertText == "ПриходныйКассовыйОрдер"), "Не найдены документы.");
        Check(index.Name == "Конфигурация", "Свойства вложенных объектов перезаписали имя конфигурации.");
        var baseA = BaseIdentity.FromCommandLine("1cv8.exe DESIGNER /F \"D:\\Bases\\Example\" /N User /P secret");
        Check(baseA is not null && baseA == BaseIdentity.FromCommandLine("1cv8.exe DESIGNER /Fd:\\bases\\example\\ /P other"),
            "Ключ файловой базы зависит от пользователя или формы пути.");
        Check(baseA != BaseIdentity.FromCommandLine("1cv8.exe DESIGNER /F D:\\Bases\\Other"), "Смешались разные базы.");
        var serverKey = BaseIdentity.FromCommandLine("1cv8.exe DESIGNER /S \"server\\base\"");
        Check(serverKey is not null && serverKey == BaseIdentity.FromCommandLine("1cv8.exe DESIGNER /Sserver\\base"), "Серверная строка запуска разобрана неверно.");
        Check(serverKey == BaseIdentity.FromCommandLine("1cv8.exe DESIGNER /IBConnectionString \"Srvr=server;Ref=base;Usr=user;Pwd=secret;\""),
            "Строка соединения не совпадает с ключом сервера.");
        Check(BaseIdentity.FromCommandLine("1cv8.exe DESIGNER /IBName \"Конфигурация\"") is null,
            "База ошибочно определена только по названию.");
        Check(Find("Справочники.П").Any(c => c.InsertText == "Покупатели"), "Не найден справочник по префиксу.");
        Check(Find("Объект.", "Документ ПриходныйКассовыйОрдер: Модуль объекта - Конфигуратор").Any(c => c.InsertText == "Покупатель"), "Не определён текущий объект.");
        Check(Find("Объект.", "Документ АктОбОказанииУслуг: Модуль объекта - Конфигуратор").All(c => c.InsertText != "КурсОплаты"), "Смешались поля документов.");
        Check(Find("Движения.", "Документ ПриходныйКассовыйОрдер: Модуль объекта - Конфигуратор").Any(c => c.InsertText == "РасчетыСПокупателями"), "Не найдены движения.");
        Check(Find("Движение = Движения.РасчетыСПокупателями.Добавить();\r\nДвижение.",
            "Документ ПриходныйКассовыйОрдер: Модуль объекта - Конфигуратор").Any(c => c.InsertText == "Сумма"), "Не определён тип записи регистра.");
        Check(Find("д = Документы.ПриходныйКассовыйОрдер.СоздатьДокумент();\r\nд.").Any(c => c.InsertText == "КурсОплаты"), "Не определён тип переменной.");
        Check(Find("// Документы.").Length == 0 && Find("\"Документы.").Length == 0, "Подсказки внутри комментария или строки.");
        var file = Path.Combine(AppContext.BaseDirectory, "metadata-test-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new MetadataStore(file); store.Put(index, "session1");
            Check(store.For("session2") is null, "Кэш попал в другую базу.");
            store = new MetadataStore(file);
            Check(store.For("session1")?.Entries.Count == index.Entries.Count, "Кэш не сохранился.");
            store.Unbind("session1"); Check(store.For("session1") is null, "Не отключён кэш.");
            store.Bind(baseA!, index.Id);
            store = new MetadataStore(file);
            Check(store.For(BaseIdentity.FromCommandLine("1cv8.exe DESIGNER /F D:\\Bases\\Example")!)?.Id == index.Id,
                "Автоматическое подключение после перезапуска не сохранилось.");
            store.Unbind(baseA!);
            Check(new MetadataStore(file).For(baseA!) is null, "Отключение базы не сохранилось.");
            using var form = new MetadataWindow(store); form.Show(); Application.DoEvents(); form.Close();
        }
        finally { if (File.Exists(file)) File.Delete(file); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "metadata-test-result.txt"), $"PASS: {index.Entries.Count} объектов/полей, типы, контекст, движения, переменные, изоляция кэшей, сохранение, окно.");
        return 0;
    }
}
