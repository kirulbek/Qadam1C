using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Qadam1C;

// Navigation only: never invoke an object editor, change metadata or open a search dialog.
internal static class MetadataTreeNavigation
{
    [ThreadStatic] private static AutomationElement? selectedItem;
    private static readonly string[] Sections = ["Общие", "Константы", "Справочники", "Документы", "Журналы документов",
        "Перечисления", "Отчеты", "Обработки", "Планы видов характеристик", "Планы счетов", "Планы видов расчета",
        "Регистры сведений", "Регистры накопления", "Регистры бухгалтерии", "Регистры расчета", "Бизнес-процессы",
        "Задачи", "Внешние источники данных"];
    internal static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    internal static string[] Branch(string path)
    {
        if (!FavoritesStore.Supports(path)) throw new InvalidOperationException("Неподдерживаемый путь объекта.");
        var parts = path.Split('.');
        return parts[0].Equals("ОбщиеМодули", StringComparison.OrdinalIgnoreCase) ? ["Общие", "Общие модули", parts[1]] : [parts[0], parts[1]];
    }
    internal static int FlatChild(IReadOnlyList<string> names, int parent, string wanted, IEnumerable<string> boundaries)
    {
        var stops = boundaries.Select(Normalize).ToHashSet();
        var matches = new List<int>();
        for (var i = parent + 1; i < names.Count; i++)
        {
            if (stops.Contains(Normalize(names[i]))) break;
            if (names[i].Equals(wanted, StringComparison.OrdinalIgnoreCase)) matches.Add(i);
        }
        if (matches.Count > 1) throw new InvalidOperationException("В ветке несколько одноимённых элементов; переход отменён.");
        return matches.Count == 1 ? matches[0] : -1;
    }
    public static async Task Select(ConfiguratorTarget target, string configuration, string path)
    {
        var branch = Branch(path);
        Validate(target);
        for (var attempt = 0; attempt < 40 && InputHeld(); attempt++) await Task.Delay(25);
        if (InputHeld()) throw new InvalidOperationException("Отпустите клавиши и кнопку мыши, затем повторите переход.");
        var window = new IntPtr(target.Window);
        using var audit = new NavigationAudit(window, path);
        if (IsIconic(window)) ShowWindow(window, 9);
        if (!SetForegroundWindow(window)) throw new InvalidOperationException("Windows не разрешила переключиться в 1С.");
        await Task.Delay(150);
        Guard(target);
        using var transition = NavigationTransition.TryShow(window);
        audit.TransitionShown = transition is not null;
        // Ctrl+Shift+C activates the native configuration tree, without opening a search dialog.
        CompletionNative.Chord(0x11, 0x10, 0x43);
        await Task.Delay(200);
        Guard(target); CompletionNative.Chord(0x11, 0x24); // Virtualized trees expose only visible rows.
        await Task.Delay(100);
        await Task.Run(() =>
        {
            selectedItem = null;
            var watch = Stopwatch.StartNew();
            void Check()
            {
                Guard(target);
                if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Дерево 1С не ответило вовремя.");
            }
            var main = AutomationElement.FromHandle(window);
            var candidates = new List<AutomationElement>();
            var pending = new Stack<(AutomationElement Element, int Depth)>();
            pending.Push((main, 0)); var visited = 0;
            while (pending.Count > 0 && visited++ < 1500)
            {
                Check();
                try
                {
                var (node, depth) = pending.Pop(); var current = node.Current;
                if (current.ProcessId != target.Pid) continue;
                if (current.ControlType == ControlType.List || current.ControlType == ControlType.Tree)
                {
                    if (!current.IsOffscreen && Children(node).Any(c =>
                        c.Current.Name.Equals(configuration, StringComparison.OrdinalIgnoreCase))) candidates.Add(node);
                    continue;
                }
                if (depth >= 16 || current.IsOffscreen || current.ControlType == ControlType.Document ||
                    current.ControlType == ControlType.Table || current.ControlType == ControlType.DataGrid ||
                    current.ControlType == ControlType.ToolBar) continue;
                foreach (var child in Children(node, 200)) pending.Push((child, depth + 1));
                }
                catch (ElementNotAvailableException) { /* Ignore controls recreated by 1C during palette updates. */ }
            }
            if (candidates.Count != 1) throw new InvalidOperationException(candidates.Count == 0
                ? "Не найдено дерево конфигурации «" + configuration + "». Откройте его и проверьте выбранный индекс."
                : "Открыто несколько деревьев этой конфигурации. Оставьте одно дерево и повторите переход.");
            var container = candidates[0];
            var roots = Children(container);
            var root = Unique(roots, configuration) ?? throw new InvalidOperationException("Не найден корень конфигурации.");
            SelectItem(root, target);
            Guard(target); CompletionNative.Chord(0x11, 0x12, 0x6D); // Collapse descendants to isolate the selected branch.
            Thread.Sleep(100);
            Guard(target); CompletionNative.Chord(0x27); Thread.Sleep(200); Check();
            var parent = root;
            for (var step = 0; step < branch.Length; step++)
            {
                Check();
                var name = branch[step];
                // Providers may expose nested tree items or a flat list of expanded nodes.
                var nested = Children(parent);
                var child = Unique(nested, name, normalize: step < branch.Length - 1);
                if (child is null)
                {
                    var flat = Children(container);
                    var parentIndex = Array.FindIndex(flat, e => Automation.Compare(e, parent));
                    if (parentIndex < 0) throw new InvalidOperationException("1С изменила структуру дерева. Повторите переход.");
                    var names = flat.Select(e => e.Current.Name).ToArray();
                    if (step == 0)
                    {
                        var matching = flat.Skip(parentIndex + 1).Where(e => Normalize(e.Current.Name) == Normalize(name)).ToArray();
                        if (matching.Length != 1) throw new InvalidOperationException("Не удалось однозначно найти раздел «" + name + "».");
                        child = matching[0];
                    }
                    else
                    {
                        var boundaries = step == 1 && branch.Length == 3 ? Sections :
                            branch.Length == 3 ? Sections.Concat(new[] { "Подсистемы", "Параметры сеанса", "Роли", "Планы обмена",
                                "Критерии отбора", "Подписки на события", "Регламентные задания", "Общие формы", "Общие команды",
                                "Общие макеты", "Общие картинки", "Пакеты XDTO", "Web-сервисы", "HTTP-сервисы", "WS-ссылки", "Языки", "Стили" }) : Sections;
                        var match = FlatChild(names, parentIndex, name, boundaries);
                        if (match >= 0) child = flat[match];
                    }
                }
                if (child is null && step == branch.Length - 1)
                {
                    // Native tree type-ahead scrolls to offscreen rows; it does not open the search UI.
                    SelectItem(parent, target);
                    Guard(target); CompletionNative.Chord(0x27);
                    Thread.Sleep(100);
                    TypeName(name, target);
                    Thread.Sleep(200); Check();
                    child = Unique(Children(container), name);
                }
                if (child is null) throw new InvalidOperationException("Не найден «" + name + "» в нужной ветке. Проверьте отбор дерева и актуальность индекса.");
                if (step == branch.Length - 1) { SelectItem(child, target); Check(); return; }
                SelectItem(child, target);
                Guard(target); CompletionNative.Chord(0x27); Thread.Sleep(200);
                parent = child;
            }
        });
        audit.Completed = true;
    }
    private static AutomationElement[] Children(AutomationElement parent, int maximum = 20000)
    {
        var result = new List<AutomationElement>();
        var child = TreeWalker.ControlViewWalker.GetFirstChild(parent);
        while (child is not null && result.Count < maximum)
        { result.Add(child); child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
        if (child is not null) throw new InvalidOperationException("Слишком большая ветка дерева. Сверните лишние ветки и повторите переход.");
        return result.ToArray();
    }
    private static AutomationElement? Unique(IEnumerable<AutomationElement> elements, string name, bool normalize = false)
    {
        var found = elements.Where(e => normalize ? Normalize(e.Current.Name) == Normalize(name) :
            e.Current.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (found.Length > 1) throw new InvalidOperationException("Неоднозначный элемент дерева: " + name);
        return found.SingleOrDefault();
    }
    private static void SelectItem(AutomationElement item, ConfiguratorTarget target)
    {
        Guard(target);
        // Repeated fallback clicks can be interpreted by 1C as a double-click (open properties).
        if (selectedItem is not null && Automation.Compare(selectedItem, item)) return;
        if (item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
            try { ((ScrollItemPattern)scroll).ScrollIntoView(); } catch (NotImplementedException) { }
        bool selected = false;
        if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var raw))
            try { ((SelectionItemPattern)raw).Select(); selected = ((SelectionItemPattern)raw).Current.IsSelected; }
            catch (NotImplementedException) { }
        try { item.SetFocus(); }
        catch (NotImplementedException)
        {
            var bounds = item.Current.BoundingRectangle;
            Guard(target);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || item.Current.IsOffscreen)
                throw new InvalidOperationException("Элемент дерева не виден для выделения.");
            SetCursorPos((int)(bounds.Left + Math.Min(bounds.Width / 2, 80)), (int)(bounds.Top + bounds.Height / 2));
            mouse_event(2, 0, 0, 0, CompletionNative.InputTag);
            mouse_event(4, 0, 0, 0, CompletionNative.InputTag);
            Thread.Sleep(50);
            selected = true;
        }
        if (!selected && !item.Current.HasKeyboardFocus)
            throw new InvalidOperationException("1С не подтвердила выделение элемента дерева.");
        Guard(target);
        selectedItem = item;
    }
    private static void Validate(ConfiguratorTarget target)
    {
        if (!ConfiguratorTarget.List().Any(t => t.Pid == target.Pid && t.Started == target.Started && t.Window == target.Window))
            throw new InvalidOperationException("Выбранный Конфигуратор закрыт. Обновите список окон.");
    }
    private static bool InputHeld() => new[] { 1, 13, 16, 17, 18 }.Any(CompletionNative.IsDown);
    internal static void TypeName(string name, ConfiguratorTarget target)
    {
        var thread = CompletionNative.GetWindowThreadProcessId(new IntPtr(target.Window), out _);
        var layout = GetKeyboardLayout(thread);
        var keys = name.Select(c => VkKeyScanEx(c, layout)).ToArray();
        if (keys.Any(k => k < 0 || (k & 0x600) != 0))
        { Guard(target); CompletionNative.TypeUnicode(name); return; }
        foreach (var key in keys)
        {
            Guard(target);
            if ((key & 0x100) != 0) CompletionNative.Chord(0x10, (ushort)(key & 0xff));
            else CompletionNative.Chord((ushort)(key & 0xff));
            Thread.Sleep(15);
        }
    }
    private static void Guard(ConfiguratorTarget target)
    {
        if (CompletionNative.GetForegroundWindow() != new IntPtr(target.Window))
            throw new InvalidOperationException("Фокус переключён из Конфигуратора; переход отменён.");
        CompletionNative.GetWindowThreadProcessId(new IntPtr(target.Window), out var pid);
        if (pid != target.Pid) throw new InvalidOperationException("Окно Конфигуратора изменилось.");
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanEx(char character, IntPtr layout);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, IntPtr extra);
}
