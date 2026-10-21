using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Qadam1C;

internal static class CurrentMetadata
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MetadataIndex, HashSet<string>> ObjectNames = new();
    internal static string Diagnostic { get; private set; } = "";
    internal static string? FromCaption(string caption, MetadataIndex index)
    {
        var cut = caption.IndexOf(" - Конфигуратор", StringComparison.OrdinalIgnoreCase);
        if (cut >= 0) caption = caption[..cut];
        var match = Regex.Match(caption, @"^(?<kind>.+?)\s+(?<name>[\p{L}_][\p{L}\p{N}_]*)(?::|$)");
        if (!match.Success) return null;
        return index.Entries.FirstOrDefault(e => FavoritesStore.Supports(e.Path) &&
            e.Name.Equals(match.Groups["name"].Value, StringComparison.OrdinalIgnoreCase) &&
            MetadataTreeNavigation.Normalize(MetadataFavorite.Display(e.Path).Split('.')[0]) ==
            MetadataTreeNavigation.Normalize(match.Groups["kind"].Value))?.Path;
    }
    internal static string ResolveTreeName(string name, MetadataIndex index)
    {
        var matches = index.Entries.Where(e => FavoritesStore.Supports(e.Path) &&
            (e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
             e.Path.Equals(name, StringComparison.OrdinalIgnoreCase) ||
             MetadataFavorite.Display(e.Path).Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        return matches.Length == 1 ? matches[0].Path : throw new InvalidOperationException(matches.Length == 0
            ? "Выберите сам объект в дереве 1С, затем нажмите Ctrl+Alt+B."
            : "Имя встречается в нескольких ветках. Откройте модуль нужного объекта и нажмите Ctrl+Alt+B.");
    }
    internal static string? PathInCategory(string name, string category, MetadataIndex index) =>
        index.Entries.SingleOrDefault(e => FavoritesStore.Supports(e.Path) &&
            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            MetadataTreeNavigation.Normalize(e.Path.Split('.')[0]) == MetadataTreeNavigation.Normalize(category))?.Path;
    public static string Read(ConfiguratorTarget target, MetadataIndex index)
    {
        var window = new IntPtr(target.Window);
        if (CompletionNative.GetForegroundWindow() != window) throw new InvalidOperationException("Выберите объект в 1С и нажмите Ctrl+Alt+B.");
        if (target.Title.Contains(".mxl", StringComparison.OrdinalIgnoreCase) || target.Title.Contains("Табличный документ"))
            throw new InvalidOperationException("Выберите объект метаданных в дереве 1С.");
        Diagnostic = "Window: " + target.Title;
        var thread = CompletionNative.GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(thread, ref info) && info.Focus != IntPtr.Zero)
        {
            Diagnostic += $"\nFocus HWND: {info.Focus}, caret: {info.Caret}, rect: {info.Left},{info.Top},{info.Right},{info.Bottom}";
            var name = SelectedName(info.Focus);
            if (!string.IsNullOrWhiteSpace(name)) return ResolveFromTree(name, target, index);
        }
        var selectedName = ReadTreeSelection(window, target.Pid, index);
        if (selectedName is not null) return ResolveFromTree(selectedName, target, index);
        // 1C can report a focused document for a property palette; require a real module caption as well.
        var caption = EditorReader.CurrentEditorCaption(window);
        Diagnostic += "\nEditor: " + caption;
        var path = FromCaption(caption.Length > 0 ? caption : target.Title, index);
        var document = EditorReader.FocusedDocument(window);
        if (path is not null && document is not null) return path;
        if (document is not null)
        {
            if (CompletionNative.GetForegroundWindow() != window) throw new InvalidOperationException("Фокус переключён из 1С.");
            // The native command knows the owner of an object/form module even when the main caption is generic.
            CompletionNative.Chord(0x11, 0x54);
            Thread.Sleep(150);
            CompletionNative.Chord(0x11, 0x10, 0x43);
            Thread.Sleep(100);
            try
            {
                for (var parent = 0; parent < 6; parent++)
                {
                    if (CompletionNative.GetForegroundWindow() != window) throw new InvalidOperationException("Фокус переключён из 1С.");
                    var owner = ReadTreeSelection(window, target.Pid, index);
                    if (owner is not null) return ResolveFromTree(owner, target, index);
                    CompletionNative.Chord(0x25); Thread.Sleep(100);
                }
            }
            finally
            {
                if (CompletionNative.GetForegroundWindow() == window)
                    try { document.SetFocus(); } catch (NotImplementedException) { } catch (ElementNotAvailableException) { } catch (COMException) { }
            }
        }
        throw new InvalidOperationException("Не удалось прочитать выделенный объект. Выберите его имя в дереве или откройте его модуль и нажмите Ctrl+Alt+B.");
    }
    private static string? SelectedName(IntPtr window)
    {
        object? raw = null;
        object? selection = null;
        try
        {
            var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            if (AccessibleObjectFromWindow(window, 0xFFFFFFFC, ref iid, out raw) != 0 || raw is not Accessibility.IAccessible accessible) return null;
            Diagnostic += "\nFocus role: " + accessible.get_accRole(0);
            try { selection = accessible.accSelection; } catch (COMException) { } catch (NotImplementedException) { }
            if (selection is null)
                try { selection = accessible.accFocus; } catch (COMException) { } catch (NotImplementedException) { }
            if (selection is int child && child > 0)
            {
                var role = Convert.ToInt32(accessible.get_accRole(child));
                Diagnostic += "\nSelected child: " + child + ", role: " + role + ", name: " + accessible.get_accName(child);
                return role is 0x22 or 0x24 ? accessible.get_accName(child) : null;
            }
            if (selection is Accessibility.IAccessible selected)
            {
                var role = Convert.ToInt32(selected.get_accRole(0));
                Diagnostic += "\nSelected role: " + role + ", name: " + selected.get_accName(0);
                return role is 0x22 or 0x24 ? selected.get_accName(0) : null;
            }
        }
        catch (COMException ex) { Diagnostic += "\nMSAA: " + ex.Message; }
        catch (NotImplementedException) { Diagnostic += "\nMSAA not implemented"; }
        catch (InvalidCastException) { }
        finally
        {
            if (selection is not null && !ReferenceEquals(raw, selection) && Marshal.IsComObject(selection)) Marshal.ReleaseComObject(selection);
            if (raw is not null && Marshal.IsComObject(raw)) Marshal.ReleaseComObject(raw);
        }
        return null;
    }
    private static string ResolveFromTree(string name, ConfiguratorTarget target, MetadataIndex index)
    {
        var matches = index.Entries.Where(e => FavoritesStore.Supports(e.Path) &&
            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length <= 1) return ResolveTreeName(name, index);
        var window = new IntPtr(target.Window);
        for (var level = 0; level < 3; level++)
        {
            if (CompletionNative.GetForegroundWindow() != window) throw new InvalidOperationException("Фокус переключён из 1С.");
            CompletionNative.Chord(0x25); Thread.Sleep(120);
            var parent = ReadTreeSelection(window, target.Pid, index, true);
            Diagnostic += "\nParent category: " + parent;
            var match = PathInCategory(name, parent ?? "", index);
            if (match is null) continue;
            CompletionNative.Chord(0x27); Thread.Sleep(120);
            MetadataTreeNavigation.TypeName(name, target);
            return match;
        }
        throw new InvalidOperationException("Не удалось определить раздел объекта. Добавьте его через кнопку «Добавить».");
    }
    private static string? ReadTreeSelection(IntPtr window, int pid, MetadataIndex index, bool anyFocusedRow = false)
    {
        var root = AutomationElement.FromHandle(window);
        var main = root.Current.BoundingRectangle;
        var names = ObjectNames.GetValue(index, key => key.Entries.Where(e => FavoritesStore.Supports(e.Path))
            .Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var pending = new Stack<(AutomationElement Node, int Depth)>();
        pending.Push((root, 0)); var count = 0;
        while (pending.Count > 0 && count++ < 1500)
        {
            try
            {
            var (node, depth) = pending.Pop(); var value = node.Current;
            if (value.ProcessId != pid || value.IsOffscreen) continue;
            if (value.ControlType == ControlType.List || value.ControlType == ControlType.Tree)
            {
                var rect = value.BoundingRectangle;
                if (rect.Left > main.Left + 80 || rect.Height < 150) continue;
                Diagnostic += $"\nTree HWND: {value.NativeWindowHandle}";
                if (value.NativeWindowHandle != 0)
                {
                    var legacyName = SelectedName(new IntPtr(value.NativeWindowHandle));
                    if (!string.IsNullOrWhiteSpace(legacyName)) return legacyName;
                }
                var item = TreeWalker.ControlViewWalker.GetFirstChild(node); var rows = 0;
                string? focusedName = null; double focusedLeft = 0, objectLeft = double.MaxValue;
                while (item is not null && rows++ < 200)
                {
                    var current = item.Current;
                    if (current.HasKeyboardFocus)
                    {
                        if (anyFocusedRow) return current.Name;
                        focusedName = current.Name; focusedLeft = current.BoundingRectangle.Left;
                    }
                    if (names.Contains(current.Name))
                    {
                        objectLeft = Math.Min(objectLeft, current.BoundingRectangle.Left);
                        var selected = item.GetCurrentPropertyValue(SelectionItemPattern.IsSelectedProperty, true);
                        var legacyState = AutomationProperty.LookupById(30096);
                        var state = legacyState is null ? null : item.GetCurrentPropertyValue(legacyState, true);
                        Diagnostic += $"\nRow {current.Name}: focus={current.HasKeyboardFocus}, selected={selected}, legacy={state}";
                        if (selected is true || state is int bits && (bits & 5) != 0)
                        { focusedName = current.Name; focusedLeft = current.BoundingRectangle.Left; }
                    }
                    item = TreeWalker.ControlViewWalker.GetNextSibling(item);
                }
                if (focusedName is not null && (anyFocusedRow || names.Contains(focusedName) && focusedLeft <= objectLeft + 1)) return focusedName;
                continue;
            }
            if (depth >= 16 || value.ControlType == ControlType.Document || value.ControlType == ControlType.Table ||
                value.ControlType == ControlType.DataGrid || value.ControlType == ControlType.ToolBar) continue;
            var child = TreeWalker.ControlViewWalker.GetFirstChild(node); var siblings = 0;
            while (child is not null && siblings++ < 200)
            { pending.Push((child, depth + 1)); child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
            }
            catch (ElementNotAvailableException) { /* 1C recreates virtual controls while updating the property palette. */ }
            catch (COMException ex) { Diagnostic += "\nTree provider: " + ex.HResult; }
        }
        return null;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size; public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? accessible);
}
