using System.IO;

namespace Qadam1C;

internal static class SmartCompletionChecks
{
    public static int Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var file = Path.Combine(AppContext.BaseDirectory, "qadam-completion-" + Guid.NewGuid() + ".json");
        try
        {
            var history = new CompletionHistory(file);
            CompletionChoice[] Find(string text, string session = "base:test", IEnumerable<Snippet>? templates = null)
            {
                var prefix = new string(text.Reverse().TakeWhile(c => char.IsLetterOrDigit(c) || c == '_').Reverse().ToArray());
                return SmartCompletion.Find(new(IntPtr.Zero, text, text.Length, prefix, 0, 0, session), templates ?? [], null, history, false);
            }
            string[] Values(string text) => Find(text).Select(c => c.InsertText ?? c.Label).ToArray();
            Check(Values("ПриходныйКассовыйОрдер = 1;\r\nПКО").Contains("ПриходныйКассовыйОрдер"), "Нет сокращения по заглавным буквам.");
            Check(Values("СуммаОплаты = 1;\r\nОпла").Contains("СуммаОплаты"), "Нет поиска по части имени.");
            Check(Values("ТЗ").Contains("ТаблицаЗначений"), "Нет сокращения встроенного типа.");
            Check(Values("Процедура Тест()\r\nЕсли Истина Тогда\r\nКонец").SequenceEqual(["КонецЕсли"]), "Неверное окончание блока.");
            Check(Values("Процедура Тест()\r\nЕсли Истина Тогда\r\nКонецЕсли;\r\nКонец").SequenceEqual(["КонецПроцедуры"]), "Не снят закрытый блок.");
            Check(Find("// Сооб").Length == 0 && Find("Текст = \"Сооб").Length == 0, "Подсказки в комментарии или строке.");
            Check(Find("Процедура Сооб").Length == 0 && Find("Перем Сооб").Length == 0, "Подсказки при объявлении.");
            Check(Values("#Если Сер").Contains("Сервер"), "Нет препроцессора.");
            var structure = "Данные = Новый Структура(\"Покупатель,Сумма\");\r\nДанные.Вставить(\"Валюта\", 1);\r\n";
            Check(Values(structure + "Данные.").Order().SequenceEqual(new[] { "Валюта", "Покупатель", "Сумма" }.Order()), "Неверные ключи структуры.");
            Check(Values(structure + "Данные = Новый Структура(\"Другое\");\r\nДанные.").SequenceEqual(["Другое"]), "Не учтено повторное присваивание.");
            Check(Values(structure + "// Данные.Вставить(\"Ложное\", 1);\r\nДанные.").All(v => v != "Ложное"), "Ключ из комментария.");
            Check(Values(structure + "Другой.Данные.").Length == 0, "Локальная переменная перепутана со свойством.");
            var table = "Товары = Новый ТаблицаЗначений;\r\nТовары.Колонки.Добавить(\"Цена\");\r\nТовары.Колонки.Добавить(\"Количество\");\r\n";
            Check(Values(table + "Товары.").Length == 2, "Не найдены колонки.");
            Check(Values(table + "Для Каждого СтрокаТовара Из Товары Цикл\r\nСтрокаТовара.").Contains("Цена"), "Нет колонок строки цикла.");
            Check(Values(table + "Для Каждого СтрокаТовара Из Товары Цикл\r\nКонецЦикла;\r\nСтрокаТовара.").Length == 0, "Строка цикла вышла за область видимости.");
            Check(Values("Процедура Один()\r\n" + structure + "КонецПроцедуры\r\nПроцедура Два()\r\nДанные.").Length == 0, "Структура из другой процедуры.");
            var variables = "СуммаПервая = 1;\r\nСуммаВторая = 2;\r\nСум";
            Check(Values(variables)[0] == "СуммаВторая", "Нет приоритета ближайшей переменной.");
            history.Remember("base:test", "СуммаПервая");
            Check(Values(variables)[0] == "СуммаПервая", "История не влияет на сортировку.");
            Check((Find(variables, "base:other")[0].InsertText ?? Find(variables, "base:other")[0].Label) == "СуммаВторая", "История смешана между базами.");
            Check(new CompletionHistory(file).Score("base:test", "СуммаПервая") > 0, "История не сохранена.");
            var snippet = new Snippet("test", "test", "Мой шаблон", "сооб", "Сообщить(<?>);");
            Check(Find("сооб", templates: [snippet]).Any(c => c.Template == snippet), "Потеряны пользовательские шаблоны.");
            var method = Find("Функция РассчитатьСумму(Цена, Количество)\r\nКонецФункции\r\nРС")
                .Single(c => c.SearchText == "РассчитатьСумму");
            Check(method.InsertText == "РассчитатьСумму()" && method.Cursor == "РассчитатьСумму(".Length,
                "Неверные скобки или позиция курсора у метода.");
            var moduleCache = new MetadataIndex("test", "test", "", DateTime.Now,
                [new MetadataEntry("ОбщиеМодули.ОбщегоНазначения", "ОбщегоНазначения", "", [], [])]);
            var moduleSnapshot = new EditorSnapshot(IntPtr.Zero, "Общ", 3, "Общ", 0, 0, "base:test");
            Check(SmartCompletion.Find(moduleSnapshot, [], moduleCache, history, false)
                .Any(c => c.InsertText == "ОбщегоНазначения"), "Название общего модуля отсутствует в глобальной подсказке.");
            Check(!SmartCompletion.Find(moduleSnapshot, [], null, history, false)
                .Any(c => c.InsertText == "ОбщегоНазначения"), "Общий модуль перенесён в другую базу без кэша.");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smart-completion-test-result.txt"),
                "PASS: abbreviations, substring, block scope, lexer, declarations, preprocessor, structure keys, table columns, loops, reassignment, local ranking, persistent per-base history, templates.");
            return 0;
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
