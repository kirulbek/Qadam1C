using System.IO;
using System.Text.RegularExpressions;

namespace Qadam1C;

internal sealed record ModuleRegion(string Name, string Path, int Start, int BodyStart, int End, int Line)
{
    public override string ToString() => $"{Path}  · строка {Line}";
}
internal sealed record MethodMovePlan(int Start, int Length, string Replacement, int Cursor, string Result);

internal static class MethodRegions
{
    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static int LineStart(string text, int offset) => offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
    private static int AfterLine(string text, int offset)
    {
        var end = text.IndexOf('\n', offset); return end < 0 ? text.Length : end + 1;
    }
    internal static ModuleRegion[] Parse(string text, bool strict = true)
    {
        var stack = new Stack<(string Name, string Path, int Start, int BodyStart, int Line)>();
        var result = new List<ModuleRegion>();
        var cursor = 0; var line = 1;
        foreach (Match m in Matches(MetadataCompletion.Code(text), @"^[\t ]*#(?<kind>Область|Region|КонецОбласти|EndRegion)\b(?<name>[^\r\n]*)"))
        {
            while (cursor < m.Index) { if (text[cursor++] == '\n') line++; }
            var kind = m.Groups["kind"].Value;
            if (kind.Equals("Область", StringComparison.OrdinalIgnoreCase) || kind.Equals("Region", StringComparison.OrdinalIgnoreCase))
            {
                var name = m.Groups["name"].Value.Trim();
                if (name.Length == 0)
                {
                    if (strict) throw new InvalidOperationException("В модуле есть область без имени. Исправьте её перед переносом.");
                    name = "(без имени)";
                }
                stack.Push((name, stack.Count == 0 ? name : stack.Peek().Path + " / " + name, m.Index, AfterLine(text, m.Index), line));
            }
            else
            {
                if (!stack.TryPop(out var open))
                {
                    if (strict) throw new InvalidOperationException("В модуле есть #КонецОбласти без #Область.");
                    continue;
                }
                result.Add(new(open.Name, open.Path, open.Start, open.BodyStart, m.Index, open.Line));
            }
        }
        if (stack.Count != 0 && strict) throw new InvalidOperationException("В модуле есть незакрытая область. Перенос отменён.");
        foreach (var open in stack) result.Add(new(open.Name, open.Path, open.Start, open.BodyStart, text.Length, open.Line));
        return result.OrderBy(r => r.Start).ToArray();
    }
    private sealed class ConditionalFrame(string condition)
    {
        internal readonly List<string> Previous = [condition];
        internal string Expression = condition;
        internal bool ElseSeen;
    }
    private static string[] ConditionalFrames(string text, int offset)
    {
        var stack = new Stack<ConditionalFrame>();
        string[]? context = null;
        foreach (Match m in Matches(MetadataCompletion.Code(text), @"^[\t ]*#(?<kind>Если|ИначеЕсли|Иначе|КонецЕсли|If|ElsIf|Else|EndIf)\b(?<tail>[^\r\n]*)"))
        {
            if (context is null && m.Index >= offset) context = stack.Reverse().Select(f => f.Expression).ToArray();
            string Condition()
            {
                var tail = m.Groups["tail"].Value.Trim();
                if (!Regex.IsMatch(tail, @"\b(?:Тогда|Then)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new InvalidOperationException("Незавершённое условие #Если. Перенос отменён.");
                var value = Regex.Replace(tail, @"\b(?:Тогда|Then)$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
                if (value.Length == 0) throw new InvalidOperationException("Пустое условие #Если.");
                return value;
            }
            switch (m.Groups["kind"].Value.ToUpperInvariant())
            {
                case "ЕСЛИ": case "IF": stack.Push(new(Condition())); break;
                case "ИНАЧЕЕСЛИ": case "ELSIF":
                    if (stack.Count == 0 || stack.Peek().ElseSeen) throw new InvalidOperationException("Нарушены директивы условной компиляции.");
                    var frame = stack.Peek(); var condition = Condition();
                    frame.Expression = "Не (" + string.Join(" Или ", frame.Previous.Select(p => "(" + p + ")")) + ") И (" + condition + ")";
                    frame.Previous.Add(condition); break;
                case "ИНАЧЕ": case "ELSE":
                    if (stack.Count == 0 || stack.Peek().ElseSeen) throw new InvalidOperationException("Нарушены директивы условной компиляции.");
                    var otherwise = stack.Peek(); otherwise.ElseSeen = true;
                    otherwise.Expression = "Не (" + string.Join(" Или ", otherwise.Previous.Select(p => "(" + p + ")")) + ")"; break;
                default:
                    if (stack.Count == 0) throw new InvalidOperationException("Нарушены директивы условной компиляции.");
                    stack.Pop(); break;
            }
        }
        context ??= stack.Reverse().Select(f => f.Expression).ToArray();
        if (stack.Count != 0) throw new InvalidOperationException("Есть незакрытая директива условной компиляции.");
        return context;
    }
    private static string NormalizeCondition(string value) => Regex.Replace(value, @"\s+", "").ToUpperInvariant();
    private static string ConditionalContext(string text, int offset) => string.Join(";", ConditionalFrames(text, offset).Select(NormalizeCondition));
    private static string[] RequiredGuards(string text, int source, int destination)
    {
        var original = ConditionalFrames(text, source); var target = ConditionalFrames(text, destination);
        if (target.Length > original.Length || !target.Select(NormalizeCondition).SequenceEqual(original.Take(target.Length).Select(NormalizeCondition)))
            throw new InvalidOperationException("Область назначения имеет другое условие #Если. Выберите область без ограничений или с тем же условием.");
        return original.Skip(target.Length).ToArray();
    }
    internal static MethodMovePlan Plan(string text, ModuleMethod method, ModuleRegion destination)
    {
        var regions = Parse(text);
        if (!regions.Contains(destination) || !ModuleOutline.Parse(text).Contains(method))
            throw new InvalidOperationException("Список методов или областей устарел.");
        var code = MetadataCompletion.Code(text);
        var endings = Matches(code, @"^[\t ]*(?<kind>КонецПроцедуры|КонецФункции|EndProcedure|EndFunction)\b[^\r\n]*");
        var declarations = ModuleOutline.Parse(text);
        if (endings.Count != declarations.Length)
            throw new InvalidOperationException("Число объявлений и окончаний методов не совпадает. Проверьте синтаксис.");
        foreach (var declaration in declarations)
        {
            var close = endings.Cast<Match>().FirstOrDefault(m => m.Index > declaration.Offset)
                ?? throw new InvalidOperationException("Не найден конец одного из методов. Проверьте синтаксис.");
            var tail = code[(close.Groups["kind"].Index + close.Groups["kind"].Length)..(close.Index + close.Length)].Trim();
            var isFunction = declaration.Kind.Equals("Функция", StringComparison.OrdinalIgnoreCase) || declaration.Kind.Equals("Function", StringComparison.OrdinalIgnoreCase);
            var closeFunction = close.Groups["kind"].Value.Equals("КонецФункции", StringComparison.OrdinalIgnoreCase) || close.Groups["kind"].Value.Equals("EndFunction", StringComparison.OrdinalIgnoreCase);
            if (isFunction != closeFunction || declarations.Any(other => other.Offset > declaration.Offset && other.Offset < close.Index))
                throw new InvalidOperationException("Границы методов неоднозначны. Проверьте синтаксис.");
            if (tail.Length != 0 && tail != ";") throw new InvalidOperationException("После конца метода есть код на той же строке. Перенос отменён.");
            if (destination.Start > declaration.Offset && destination.Start < AfterLine(text, close.Index) ||
                destination.End > declaration.Offset && destination.End < AfterLine(text, close.Index))
                throw new InvalidOperationException("Область назначения расположена внутри метода. В неё нельзя перенести другой метод.");
        }
        var ending = endings.Cast<Match>().FirstOrDefault(m => m.Index > method.Offset)
            ?? throw new InvalidOperationException("Не найден конец метода. Проверьте синтаксис.");
        var function = method.Kind.Equals("Функция", StringComparison.OrdinalIgnoreCase) || method.Kind.Equals("Function", StringComparison.OrdinalIgnoreCase);
        var endKind = ending.Groups["kind"].Value;
        if ((function && !endKind.Equals("КонецФункции", StringComparison.OrdinalIgnoreCase) && !endKind.Equals("EndFunction", StringComparison.OrdinalIgnoreCase)) ||
            (!function && !endKind.Equals("КонецПроцедуры", StringComparison.OrdinalIgnoreCase) && !endKind.Equals("EndProcedure", StringComparison.OrdinalIgnoreCase)) ||
            ModuleOutline.Parse(text).Any(m => m.Offset > method.Offset && m.Offset < ending.Index))
            throw new InvalidOperationException("Границы метода неоднозначны. Проверьте синтаксис.");
        var start = LineStart(text, method.Offset);
        var end = AfterLine(text, ending.Index);
        // Keep attached comments and execution/extension directives with the method.
        var scan = start;
        while (scan > 0)
        {
            var previous = LineStart(text, scan - 1);
            var raw = text[previous..scan].Trim();
            var clean = code[previous..scan].Trim();
            if (raw.StartsWith("//", StringComparison.Ordinal) || clean.StartsWith('&')) start = previous;
            else if (raw.Length != 0) break;
            scan = previous;
        }
        if (regions.Any(r => (r.Start >= start && r.Start < end) != (r.End >= start && r.End < end)))
            throw new InvalidOperationException("Граница области пересекает границу метода. Перенос отменён.");
        var current = regions.Where(r => r.BodyStart <= start && r.End >= end).OrderByDescending(r => r.Start).FirstOrDefault();
        if (current == destination && text[end..destination.End].Trim().Length == 0)
            throw new InvalidOperationException("Метод уже находится в конце этой области.");
        var guards = RequiredGuards(text, method.Offset, destination.End);
        return Relocate(text, method, start, end, destination.End, guards);
    }
    private static MethodMovePlan Relocate(string text, ModuleMethod method, int start, int end, int destinationOffset, string[]? guards = null)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var block = text[start..end];
        var declarationInBlock = method.Offset - start;
        if (!block.EndsWith('\n')) block += newline;
        if (guards is { Length: > 0 })
        {
            var opening = string.Concat(guards.Select(condition => "#Если " + condition + " Тогда" + newline));
            block = opening + block + string.Concat(guards.Reverse().Select(_ => "#КонецЕсли" + newline));
            declarationInBlock += opening.Length;
        }
        var remaining = text.Remove(start, end - start);
        var insert = destinationOffset > start ? destinationOffset - (end - start) : destinationOffset;
        var before = remaining[..insert];
        var separator = "";
        if (before.Trim().Length > 0 && !Regex.IsMatch(before, @"\n[\t ]*\r?\n[\t ]*$"))
            separator = Regex.IsMatch(before, @"\n[\t ]*$") ? newline : newline + newline;
        var after = remaining[insert..];
        var trailingSeparator = "";
        if (after.Length > 0 && !Regex.IsMatch(after, @"^[\t ]*\r?\n"))
        {
            var remainingMethods = ModuleOutline.Parse(remaining);
            var next = remainingMethods.FirstOrDefault(m => m.Offset >= insert);
            if (next is not null && MethodBlock(remaining, MetadataCompletion.Code(remaining), next, remainingMethods).Start == insert)
                trailingSeparator = newline;
        }
        var insertion = separator + block + trailingSeparator;
        var result = remaining.Insert(insert, insertion);
        // Replace a single contiguous span, yielding one editor paste/undo step.
        var replaceStart = Math.Min(start, destinationOffset);
        var replaceEnd = Math.Max(end, destinationOffset);
        var suffixLength = text.Length - replaceEnd;
        var replacement = result.Substring(replaceStart, result.Length - replaceStart - suffixLength);
        var cursor = insert + separator.Length + declarationInBlock - replaceStart;
        return new(replaceStart, replaceEnd - replaceStart, replacement, cursor, result);
    }
    private static (int Start, int End) MethodBlock(string text, string code, ModuleMethod method, ModuleMethod[] methods)
    {
        var ending = Matches(code, @"^[\t ]*(?<kind>КонецПроцедуры|КонецФункции|EndProcedure|EndFunction)\b[^\r\n]*")
            .Cast<Match>().FirstOrDefault(m => m.Index > method.Offset) ?? throw new InvalidOperationException("Не найден конец метода.");
        var function = method.Kind.Equals("Функция", StringComparison.OrdinalIgnoreCase) || method.Kind.Equals("Function", StringComparison.OrdinalIgnoreCase);
        var closeFunction = ending.Groups["kind"].Value.Equals("КонецФункции", StringComparison.OrdinalIgnoreCase) || ending.Groups["kind"].Value.Equals("EndFunction", StringComparison.OrdinalIgnoreCase);
        var tail = code[(ending.Groups["kind"].Index + ending.Groups["kind"].Length)..(ending.Index + ending.Length)].Trim();
        if (function != closeFunction || methods.Any(m => m.Offset > method.Offset && m.Offset < ending.Index) || tail.Length != 0 && tail != ";")
            throw new InvalidOperationException("Границы методов неоднозначны. Проверьте синтаксис.");
        var start = LineStart(text, method.Offset); var end = AfterLine(text, ending.Index); var scan = start;
        while (scan > 0)
        {
            var previous = LineStart(text, scan - 1); var raw = text[previous..scan].Trim();
            if (raw.StartsWith("//", StringComparison.Ordinal) || code[previous..scan].Trim().StartsWith('&')) start = previous;
            else if (raw.Length != 0) break;
            scan = previous;
        }
        return (start, end);
    }
    internal static MethodMovePlan PlanStep(string text, ModuleMethod method, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var regions = Parse(text); var methods = ModuleOutline.Parse(text);
        var index = Array.IndexOf(methods, method);
        if (index < 0) throw new InvalidOperationException("Список методов устарел.");
        var next = index + direction;
        if (next < 0 || next >= methods.Length) throw new InvalidOperationException(direction < 0 ? "Выше нет другого метода." : "Ниже нет другого метода.");
        var neighbor = methods[next];
        ModuleRegion? Owner(ModuleMethod m) => regions.Where(r => r.BodyStart <= m.Offset && r.End > m.Offset).OrderByDescending(r => r.Start).FirstOrDefault();
        if (Owner(method) != Owner(neighbor)) throw new InvalidOperationException("Соседний метод находится в другой области. Используйте «Переместить в область…».");
        if (ConditionalContext(text, method.Offset) != ConditionalContext(text, neighbor.Offset))
            throw new InvalidOperationException("Методы находятся в разных ветвях #Если. Перестановка отменена.");
        var code = MetadataCompletion.Code(text);
        var source = MethodBlock(text, code, method, methods); var target = MethodBlock(text, code, neighbor, methods);
        foreach (var block in new[] { source, target })
            if (regions.Any(r => (r.Start >= block.Start && r.Start < block.End) != (r.End >= block.Start && r.End < block.End)))
                throw new InvalidOperationException("Граница области пересекает границу метода.");
        var gapStart = Math.Min(source.End, target.End); var gapEnd = Math.Max(source.Start, target.Start);
        if (code[gapStart..gapEnd].Trim().Length != 0) throw new InvalidOperationException("Между методами есть код или директивы. Перестановка отменена.");
        return Relocate(text, method, source.Start, source.End, direction < 0 ? target.Start : target.End);
    }
    internal static MethodMovePlan PlanRelative(string text, ModuleMethod method, ModuleMethod targetMethod, bool after)
    {
        if (method == targetMethod) throw new InvalidOperationException("Выберите другой метод для вставки.");
        var regions = Parse(text); var methods = ModuleOutline.Parse(text);
        if (!methods.Contains(method) || !methods.Contains(targetMethod)) throw new InvalidOperationException("Список методов устарел.");
        var code = MetadataCompletion.Code(text);
        var source = MethodBlock(text, code, method, methods); var target = MethodBlock(text, code, targetMethod, methods);
        foreach (var block in new[] { source, target })
            if (regions.Any(r => (r.Start >= block.Start && r.Start < block.End) != (r.End >= block.Start && r.End < block.End)))
                throw new InvalidOperationException("Граница области пересекает границу метода.");
        var insert = after ? target.End : target.Start;
        var guards = RequiredGuards(text, method.Offset, insert);
        if (insert == source.Start || insert == source.End ||
            insert < source.Start && text[insert..source.Start].Trim().Length == 0 ||
            insert > source.End && text[source.End..insert].Trim().Length == 0)
            throw new InvalidOperationException("Метод уже находится на этом месте.");
        return Relocate(text, method, source.Start, source.End, insert, guards);
    }
    internal static async Task Move(EditorSnapshot expected, ModuleMethod method, ModuleRegion destination)
    {
        var plan = await Task.Run(() => Plan(expected.Text, method, destination));
        await Apply(expected, plan);
    }
    internal static async Task Step(EditorSnapshot expected, ModuleMethod method, int direction)
    {
        var plan = await Task.Run(() => PlanStep(expected.Text, method, direction));
        await Apply(expected, plan);
    }
    internal static async Task MoveRelative(EditorSnapshot expected, ModuleMethod method, ModuleMethod target, bool after)
    {
        var plan = await Task.Run(() => PlanRelative(expected.Text, method, target, after));
        await Apply(expected, plan);
    }
    internal static async Task Apply(EditorSnapshot expected, MethodMovePlan plan)
    {
        if (expected.WindowTitle.Contains("Только для чтения", StringComparison.OrdinalIgnoreCase) ||
            expected.WindowTitle.Contains("Read-only", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Модуль открыт только для чтения. Сначала разрешите редактирование в 1С.");
        var current = await ModuleOutline.FocusCurrent(expected);
        var backupDirectory = Path.Combine(Path.GetDirectoryName(AppDataPaths.FilePath("workspace-panel.json"))!, "module-move-backups");
        Directory.CreateDirectory(backupDirectory);
        File.WriteAllText(Path.Combine(backupDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bsl"), current.Text);
        var selected = EditorReader.SelectRange(current, plan.Start, plan.Start + plan.Length);
        if (!await SnippetInsertion.Insert(selected, 0, new ExpandedSnippet(plan.Replacement, plan.Cursor), forActions: true))
            throw new InvalidOperationException("Перенос не подтверждён. Проверьте текст в 1С; повтор автоматически не выполняется.");
        EditorReader.ScrollSelectionIntoView(expected.Window);
    }
}

internal sealed class MethodRegionPicker : Form
{
    private readonly ListBox regions = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    private sealed record Choice(ModuleRegion Region, int Depth)
    {
        public override string ToString() => new string(' ', Depth * 3) + Region.Name;
    }
    internal ModuleRegion? Selected => (regions.SelectedItem as Choice)?.Region;
    internal MethodRegionPicker(string method, ModuleRegion[] choices)
    {
        Text = "Переместить «" + method + "» в область"; Icon = AppIcon.Value;
        Size = new Size(600, 400); MinimumSize = new Size(400, 260); Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterParent;
        var move = new Button { Text = "Переместить", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(cancel); actions.Controls.Add(move);
        Controls.Add(regions); Controls.Add(actions);
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 38, Text = "Выберите область текущего модуля", Padding = new Padding(6) });
        regions.Items.AddRange(choices.Select(r => (object)new Choice(r, choices.Count(p => p.Start < r.Start && p.End >= r.End))).ToArray());
        if (regions.Items.Count > 0) regions.SelectedIndex = 0;
        regions.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left && regions.IndexFromPoint(e.Location) >= 0) { DialogResult = DialogResult.OK; Close(); } };
        AcceptButton = move; CancelButton = cancel;
    }
}
