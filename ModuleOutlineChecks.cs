using System.IO;

namespace Qadam1C;

internal static class ModuleOutlineChecks
{
    internal static int Run()
    {
        MethodRegionsChecks.Run();
        static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        foreach (var nl in new[] { "\n", "\r\n" })
        {
            var text = string.Join(nl, new[]
            {
                "// Функция Поддельная() Экспорт", "&НаСервере", "Процедура Первая(Знач А, Б = \"скобка )\") Экспорт",
                "    Текст = \"строка", "|Функция ВСтроке()", "|КонецФункции\";", "КонецПроцедуры", "",
                "Функция Вторая(", "    Аргумент)", "    Возврат Аргумент;", "КонецФункции", "Function Third() Export", "EndFunction"
            });
            var methods = ModuleOutline.Parse(text);
            Check(methods.Length == 3, "В список попали комментарии/строки или потеряно объявление.");
            Check(methods[0].Name == "Первая" && methods[0].Exported && methods[0].Line == 3 &&
                methods[0].Parameters == "Знач А, Б = \"скобка )\"", "Неверные параметры/экспорт/строка процедуры.");
            Check(methods[1].Name == "Вторая" && !methods[1].Exported && methods[1].Line == 9 &&
                methods[1].Parameters == "Аргумент", "Неверная многострочная функция.");
            Check(methods[2].Name == "Third" && methods[2].Exported && methods[2].Line == 13, "Английское объявление потеряно.");
            foreach (var method in methods)
                Check(text[method.Offset..].StartsWith(method.Kind), "Смещение перехода неверно для окончания строк.");
        }
        Check(ModuleOutline.Parse("Процедура Незаконченная(\nПроцедура Рабочая()\nКонецПроцедуры").Single().Name == "Рабочая",
            "Незаконченное объявление поглотило следующий метод.");
        var source = new EditorSnapshot(new IntPtr(1), "код", 0, "", 0, 0, "base", "Модуль А");
        Check(ModuleOutline.SameModule(source, source with { Offset = 2 }) &&
            !ModuleOutline.SameModule(source, source with { WindowTitle = "Модуль Б" }) &&
            !ModuleOutline.SameModule(source, source with { Text = "новый код" }) &&
            !ModuleOutline.SameModule(source, source with { Session = "other" }), "Переход допускает устаревший/чужой модуль.");
        var large = string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"Процедура Метод{i}()\nКонецПроцедуры"));
        Check(ModuleOutline.Parse(large).Length == 1000, "Большой список обрезается.");
        var path = Path.Combine(AppContext.BaseDirectory, "outline-check-" + Guid.NewGuid() + ".json");
        try
        {
            foreach (var methods in new[] { true, false })
                foreach (var favorites in new[] { true, false })
                {
                    var settings = new WorkspacePanelSettings(methods, favorites); settings.Save(path);
                    Check(WorkspacePanelSettings.Load(path) == settings, "Переключатели блоков не сохраняются.");
                }
            File.WriteAllText(path, "invalid");
            Check(WorkspacePanelSettings.Load(path) == new WorkspacePanelSettings(), "Повреждение настроек мешает запуску.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        return 0;
    }
}
