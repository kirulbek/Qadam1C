using System.IO;

namespace Qadam1C;

internal static class FavoritesChecks
{
    public static int Run()
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var emptyError = new System.Runtime.InteropServices.COMException("", unchecked((int)0x80004005));
        var errorText = TrayNotifications.ErrorText(emptyError, "Ошибка чтения объекта");
        Check(errorText.Contains("80004005") && errorText.Contains("Ошибка чтения объекта"), "Пустая COM-ошибка не имеет пояснения.");
        var displayed = false;
        TrayNotifications.TryShow((title, text) => { Check(title.Length > 0 && text.Length > 0, "Пустое уведомление."); displayed = true; }, "", "");
        Check(displayed, "Проверка уведомления не выполнялась.");
        TrayNotifications.TryShow((_, _) => throw new ArgumentException("Сбой уведомления"), "Qadam1C", errorText);
        var file = Path.Combine(AppContext.BaseDirectory, "favorites-check-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new FavoritesStore(file);
            var document = new MetadataFavorite("Документы.ПриходныйКассовыйОрдер", "ПКО");
            var catalog = new MetadataFavorite("Справочники.Контрагенты", "Контрагенты");
            Check(catalog.ToString() == "Справочник.Контрагенты" && document.ToString() == "Документ.ПриходныйКассовыйОрдер",
                "В названии избранного остались подписи реквизитов.");
            var index = new MetadataIndex("a", "База", "", DateTime.Now, new()
            {
                new(catalog.Path, "Контрагенты", "Рабочее наименование", [], []),
                new(document.Path, "ПриходныйКассовыйОрдер", "Номер", [], [])
            });
            Check(CurrentMetadata.FromCaption("Документ ПриходныйКассовыйОрдер: Модуль объекта - Конфигуратор - База", index) == document.Path,
                "Неверное определение открытого модуля.");
            Check(CurrentMetadata.ResolveTreeName("Контрагенты", index) == catalog.Path,
                "Неверное определение выбранного объекта дерева.");
            index.Entries.Add(new("Документы.Контрагенты", "Контрагенты", "", [], []));
            var ambiguous = false;
            try { CurrentMetadata.ResolveTreeName("Контрагенты", index); } catch (InvalidOperationException) { ambiguous = true; }
            Check(ambiguous && CurrentMetadata.FromCaption("Справочник Контрагенты: Модуль объекта - Конфигуратор", index) == catalog.Path,
                "Неоднозначное имя из дерева добавляется без уточнения вида объекта.");
            Check(CurrentMetadata.PathInCategory("Контрагенты", "Справочники", index) == catalog.Path &&
                CurrentMetadata.PathInCategory("Контрагенты", "Документы", index) == "Документы.Контрагенты" &&
                CurrentMetadata.PathInCategory("Контрагенты", "Реквизиты", index) is null,
                "Одноимённые объекты перепутаны при определении родительского раздела.");
            store.Add("base-a", document); store.Add("base-a", catalog);
            store.Add("base-a", document with { Path = document.Path.ToUpperInvariant() });
            Check(store.For("base-a").Count == 2, "Повторное добавление создаёт дубли.");
            Check(store.For("base-b").Count == 0, "Избранное другой конфигурации смешивается с текущим.");
            store.Add("base-b", catalog);
            store.Remove("base-a", catalog.Path);
            var loaded = new FavoritesStore(file);
            Check(loaded.For("base-a").SequenceEqual(new[] { document }) && loaded.For("base-b").SequenceEqual(new[] { catalog }),
                "Сохранение/удаление избранного повреждает другую конфигурацию.");
            store.RememberIndex("session-a", "base-a");
            Check(new FavoritesStore(file).SelectedIndex("session-a") == "base-a" && store.SelectedIndex("session-b") is null,
                "Выбор индекса не сохраняется или смешивается между окнами 1С.");
            Check(!FavoritesStore.Supports("Справочники.Контрагенты.Реквизиты.Код") &&
                !FavoritesStore.Supports("Конфигурации.Тест") && FavoritesStore.Supports("ОбщиеМодули.ОбщегоНазначения"),
                "Неверная фильтрация объектов.");
            Check(MetadataTreeNavigation.Branch("ОбщиеМодули.ОбщегоНазначения").SequenceEqual(new[] { "Общие", "Общие модули", "ОбщегоНазначения" }),
                "Неверная ветка общих модулей.");
            Check(MetadataTreeNavigation.Normalize("Регистры сведений") == MetadataTreeNavigation.Normalize("РегистрыСведений"),
                "Не распознаются названия разделов с пробелами.");
            var names = new[] { "Конфигурация", "Справочники", "Контрагенты", "Документы", "Контрагенты", "ПриходныйКассовыйОрдер", "Перечисления" };
            var sections = new[] { "Справочники", "Документы", "Перечисления" };
            Check(MetadataTreeNavigation.FlatChild(names, 1, "Контрагенты", sections) == 2 &&
                MetadataTreeNavigation.FlatChild(names, 3, "Контрагенты", sections) == 4 &&
                MetadataTreeNavigation.FlatChild(names, 1, "ПриходныйКассовыйОрдер", sections) == -1,
                "Навигация выходит за нужную ветку или путает одинаковые имена разных видов.");
            var refused = false;
            try { MetadataTreeNavigation.FlatChild(new[] { "Справочники", "Тест", "Тест" }, 0, "Тест", sections); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "Неоднозначный элемент выбирается без проверки.");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            var selectionFile = Path.ChangeExtension(file, "selection.json");
            if (File.Exists(selectionFile)) File.Delete(selectionFile);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "favorites-test-result.txt"),
            "PASS: сохранение, удаление, изоляция конфигураций, дубли, пути общих модулей, границы веток, определение вида одноимённых объектов. Автотест не взаимодействует с живой 1С; результаты ручной проверки — в README и research.");
        return 0;
    }
}
