namespace Qadam1C;

internal static class MethodRegionsChecks
{
    internal static void Run()
    {
        static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        static void Reject(Action action, string reason)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException(reason);
        }
        foreach (var nl in new[] { "\n", "\r\n" })
        {
            var a = "Процедура А()" + nl + "КонецПроцедуры" + nl;
            var b = "Функция Б()" + nl + "Возврат 2;" + nl + "КонецФункции" + nl;
            var c = "// Описание В" + nl + "&НаКлиенте" + nl + "Процедура В()" + nl + "КонецПроцедуры" + nl;
            foreach (var gap in new[] { "", nl, "  " + nl, nl + nl })
            {
                var spacingSource = a + gap + b + gap + c;
                var spacingMethods = ModuleOutline.Parse(spacingSource);
                var spacing = MethodRegions.PlanRelative(spacingSource, spacingMethods[2], spacingMethods[0], false);
                Check(spacing.Result.Contains(c + nl + a), "Нет пустой строки после перенесённого метода либо добавлена лишняя.");
                Check(spacing.Result.Contains("// Описание В" + nl + "&НаКлиенте" + nl + "Процедура В()"), "Разрыв попал внутрь заголовка метода.");
                var beforeNext = MethodRegions.PlanRelative(spacingSource, spacingMethods[2], spacingMethods[1], false);
                Check(beforeNext.Result.Contains(a + (gap.Length == 0 ? nl : gap) + c + nl + b), "Пустые строки не проверяются с обеих сторон вставленного метода.");
                Check(spacingSource[..beforeNext.Start] + beforeNext.Replacement + spacingSource[(beforeNext.Start + beforeNext.Length)..] == beforeNext.Result,
                    "Разрывы нарушили диапазон единой вставки.");
                var afterFirst = MethodRegions.PlanRelative(spacingSource, spacingMethods[2], spacingMethods[0], true);
                Check(afterFirst.Result.Contains(a + nl + c + (gap.Length == 0 ? nl : gap) + b), "Существующая пустая строка после вставки не учтена.");
            }
            var eofSource = a + b.TrimEnd('\r', '\n');
            var eofMethods = ModuleOutline.Parse(eofSource);
            var eofMove = MethodRegions.PlanRelative(eofSource, eofMethods[0], eofMethods[1], true);
            Check(eofMove.Result.Contains("КонецФункции" + nl + nl + "Процедура А"), "Не хватает пустой строки при вставке в конец файла без перевода строки.");
            var block = "// Описание метода" + nl + "&НаСервере" + nl + "Процедура Переносим(Параметр) Экспорт" + nl +
                "    Сообщить(\"КонецПроцедуры #КонецОбласти\");" + nl + "КонецПроцедуры" + nl;
            var source = block + nl + "#Область Назначение" + nl + "#КонецОбласти" + nl + "// Хвост";
            var method = ModuleOutline.Parse(source).Single();
            var region = MethodRegions.Parse(source).Single();
            var plan = MethodRegions.Plan(source, method, region);
            Check(plan.Result.IndexOf("#Область Назначение", StringComparison.Ordinal) < plan.Result.IndexOf(block, StringComparison.Ordinal) &&
                plan.Result.IndexOf(block, StringComparison.Ordinal) < plan.Result.IndexOf("#КонецОбласти", plan.Result.IndexOf(block, StringComparison.Ordinal) + block.Length, StringComparison.Ordinal),
                "Метод не перемещён внутрь целевой области.");
            Check(plan.Result.Contains(block, StringComparison.Ordinal) && plan.Result.EndsWith("// Хвост", StringComparison.Ordinal),
                "Потеряны директива, комментарий или код за областью.");
            Check(source[..plan.Start] + plan.Replacement + source[(plan.Start + plan.Length)..] == plan.Result,
                "Однократная вставка не совпадает с результатом переноса.");
            Check(plan.Replacement[plan.Cursor..].StartsWith("Процедура Переносим", StringComparison.Ordinal), "Курсор не попадает в перемещённый метод.");
            Check(ModuleOutline.Parse(plan.Result).Length == 1 && MethodRegions.Parse(plan.Result).Length == 1, "Метод продублирован или потеряна область.");
            var reverse = "#Область Назначение" + nl + "#КонецОбласти" + nl + nl + block;
            var backward = MethodRegions.Plan(reverse, ModuleOutline.Parse(reverse).Single(), MethodRegions.Parse(reverse).Single());
            Check(backward.Result.IndexOf(block, StringComparison.Ordinal) < backward.Result.IndexOf("#КонецОбласти", StringComparison.Ordinal), "Перенос вверх не работает.");
            Check(reverse[..backward.Start] + backward.Replacement + reverse[(backward.Start + backward.Length)..] == backward.Result, "Неверный диапазон при переносе вверх.");
        }
        var nested = "#Область Родитель\n#Область Вложенная\nФункция Метод()\nВозврат 1;\nКонецФункции\n#КонецОбласти\n#КонецОбласти";
        var nestedRegions = MethodRegions.Parse(nested);
        Check(nestedRegions[1].Path == "Родитель / Вложенная", "Иерархия вложенных областей потеряна.");
        Reject(() => MethodRegions.Plan(nested, ModuleOutline.Parse(nested).Single(), nestedRegions[1]), "Повторный перенос в текущую область должен отменяться.");
        var outer = MethodRegions.Plan(nested, ModuleOutline.Parse(nested).Single(), nestedRegions[0]);
        var firstClose = outer.Result.IndexOf("#КонецОбласти", StringComparison.Ordinal);
        Check(outer.Result.IndexOf("Функция Метод", StringComparison.Ordinal) > firstClose, "Перенос из дочерней области в родительскую оставил метод внутри дочерней.");
        var conditional = "#Если Сервер Тогда\nПроцедура Метод()\nКонецПроцедуры\n#КонецЕсли\n#Область Цель\n#КонецОбласти";
        var preserved = MethodRegions.Plan(conditional, ModuleOutline.Parse(conditional).Single(), MethodRegions.Parse(conditional).Single());
        Check(preserved.Result.Contains("#Область Цель\n\n#Если Сервер Тогда\nПроцедура Метод()\nКонецПроцедуры\n#КонецЕсли\n#КонецОбласти"),
            "Перенос в обычную область не сохранил исходное условие #Если.");
        Check(preserved.Replacement[preserved.Cursor..].StartsWith("Процедура Метод"), "Обёртка #Если сместила курсор мимо метода.");
        var branches = "#Если Сервер Тогда\nПроцедура Метод()\nКонецПроцедуры\n#Иначе\n#Область Цель\n#КонецОбласти\n#КонецЕсли";
        Reject(() => MethodRegions.Plan(branches, ModuleOutline.Parse(branches).Single(), MethodRegions.Parse(branches).Single()), "Перенос между #Если и #Иначе разрешён.");
        var sameBranch = "#Если Сервер Тогда\nПроцедура Метод()\nКонецПроцедуры\n#Область Цель\n#КонецОбласти\n#КонецЕсли";
        Check(MethodRegions.Plan(sameBranch, ModuleOutline.Parse(sameBranch).Single(), MethodRegions.Parse(sameBranch).Single()).Result.Contains("Метод"), "Перенос в одной ветви #Если запрещён.");
        var insideMethod = "Процедура Первая()\nКонецПроцедуры\nПроцедура Вторая()\n#Область ВнутриМетода\n#КонецОбласти\nКонецПроцедуры";
        Reject(() => MethodRegions.Plan(insideMethod, ModuleOutline.Parse(insideMethod)[0], MethodRegions.Parse(insideMethod).Single()), "Перенос создаёт вложенную процедуру.");
        var internalRegion = "Процедура Первая()\n#Область Внутри\n#КонецОбласти\nКонецПроцедуры\n#Область Цель\n#КонецОбласти";
        var withInternal = MethodRegions.Plan(internalRegion, ModuleOutline.Parse(internalRegion).Single(), MethodRegions.Parse(internalRegion).Last());
        Check(MethodRegions.Parse(withInternal.Result).Length == 2 && withInternal.Result.Contains("Процедура Первая()\n#Область Внутри\n#КонецОбласти\nКонецПроцедуры"), "Метод потерял внутреннюю область при переносе.");
        var crossing = "#Область Начало\nПроцедура Метод()\n#КонецОбласти\nКонецПроцедуры\n#Область Цель\n#КонецОбласти";
        Reject(() => MethodRegions.Plan(crossing, ModuleOutline.Parse(crossing).Single(), MethodRegions.Parse(crossing).Last()), "Перенос разрывает область, пересекающую границу метода.");
        var twoMethods = "#Область Первая\nПроцедура А()\nКонецПроцедуры\n#КонецОбласти\n#Область Вторая\nФункция Б() Экспорт\nВозврат 2;\nКонецФункции\n#КонецОбласти";
        var twoPlan = MethodRegions.Plan(twoMethods, ModuleOutline.Parse(twoMethods)[0], MethodRegions.Parse(twoMethods)[1]);
        Check(ModuleOutline.Parse(twoPlan.Result).Select(m => m.Name).OrderBy(n => n).SequenceEqual(new[] { "А", "Б" }) &&
            twoPlan.Result.Contains("Функция Б() Экспорт\nВозврат 2;\nКонецФункции"), "Повреждён соседний метод при переносе между областями.");
        Reject(() => MethodRegions.Parse("#Область НетКонца"), "Незакрытая область не распознана.");
        Reject(() => MethodRegions.Parse("#КонецОбласти"), "Лишний конец области не распознан.");
        Check(MethodRegions.Parse("#Область ВПроцессеВвода\nПроцедура Тест()", strict: false).Single().Name == "ВПроцессеВвода",
            "Навигация теряет область во время набора незавершённого кода.");
        Check(MethodRegions.Parse("#КонецОбласти\n#Область", strict: false).Single().Name == "(без имени)",
            "Незавершённая область ломает список навигации.");
        var english = "Procedure Run()\nEndProcedure\n#Region Target\n#EndRegion";
        Check(MethodRegions.Plan(english, ModuleOutline.Parse(english).Single(), MethodRegions.Parse(english).Single()).Result.Contains("Procedure Run()"), "Английские методы/области не поддержаны.");
        foreach (var nl in new[] { "\n", "\r\n" })
        {
            var first = "// Первый" + nl + "&НаКлиенте" + nl + "Процедура А()" + nl + "КонецПроцедуры" + nl;
            var second = "// Второй" + nl + "&НаКлиенте" + nl + "Функция Б()" + nl + "Возврат 2;" + nl + "КонецФункции" + nl;
            var third = "Процедура В()" + nl + "КонецПроцедуры" + nl;
            var original = "#Область Общая" + nl + first + nl + second + nl + third + "#КонецОбласти";
            var declarations = ModuleOutline.Parse(original);
            var down = MethodRegions.PlanStep(original, declarations[0], 1);
            Check(ModuleOutline.Parse(down.Result).Select(m => m.Name).SequenceEqual(new[] { "Б", "А", "В" }), "Перемещение вниз не меняет порядок соседей.");
            Check(down.Result.Contains(first) && down.Result.Contains(second), "При перестановке потерялись комментарии/директивы.");
            var up = MethodRegions.PlanStep(original, declarations[1], -1);
            Check(ModuleOutline.Parse(up.Result).Select(m => m.Name).SequenceEqual(new[] { "Б", "А", "В" }), "Перемещение вверх не работает.");
            var drag = MethodRegions.PlanRelative(original, declarations[2], declarations[0], false);
            Check(ModuleOutline.Parse(drag.Result).Select(m => m.Name).SequenceEqual(new[] { "В", "А", "Б" }), "Перетаскивание перед методом не работает.");
            var dragAfter = MethodRegions.PlanRelative(original, declarations[0], declarations[2], true);
            Check(ModuleOutline.Parse(dragAfter.Result).Select(m => m.Name).SequenceEqual(new[] { "Б", "В", "А" }), "Перетаскивание после метода не работает.");
            var toEnd = MethodRegions.Plan(original, declarations[0], MethodRegions.Parse(original).Single());
            Check(ModuleOutline.Parse(toEnd.Result).Select(m => m.Name).SequenceEqual(new[] { "Б", "В", "А" }), "Перенос в текущую область не ставит метод в конец.");
            var arrangement = new ModuleArrangement(new EditorSnapshot(IntPtr.Zero, original, 0, "", 0, 0));
            arrangement.Stage(down);
            var stagedMethods = ModuleOutline.Parse(arrangement.Text);
            arrangement.Stage(MethodRegions.PlanRelative(arrangement.Text, stagedMethods[2], stagedMethods[0], false));
            var combined = arrangement.BuildPlan();
            Check(arrangement.Changes == 2 && arrangement.Original.Text == original &&
                ModuleOutline.Parse(arrangement.Text).Select(m => m.Name).SequenceEqual(new[] { "В", "Б", "А" }), "План нескольких переносов неверен или меняет исходный текст.");
            Check(original[..combined.Start] + combined.Replacement + original[(combined.Start + combined.Length)..] == arrangement.Text &&
                combined.Replacement[combined.Cursor..].StartsWith("Процедура В"), "Общий план вставки неверен для нескольких перемещений.");
            foreach (var plan in new[] { up, down, drag, dragAfter, toEnd })
                Check(original[..plan.Start] + plan.Replacement + original[(plan.Start + plan.Length)..] == plan.Result &&
                    plan.Replacement[plan.Cursor..].StartsWith("Процедура") ||
                    original[..plan.Start] + plan.Replacement + original[(plan.Start + plan.Length)..] == plan.Result && plan.Replacement[plan.Cursor..].StartsWith("Функция"),
                    "Диапазон вставки или позиция курсора после перестановки неверны.");
        }
        var across = "#Область Первая\nПроцедура А()\nКонецПроцедуры\n#КонецОбласти\n#Область Вторая\nПроцедура Б()\nКонецПроцедуры\n#КонецОбласти";
        var acrossMethods = ModuleOutline.Parse(across);
        Reject(() => MethodRegions.PlanStep(across, acrossMethods[0], 1), "Команда ниже самовольно пересекает область.");
        var draggedAcross = MethodRegions.PlanRelative(across, acrossMethods[0], acrossMethods[1], false);
        Check(draggedAcross.Result.IndexOf("#Область Вторая", StringComparison.Ordinal) < draggedAcross.Result.IndexOf("Процедура А", StringComparison.Ordinal), "Перетаскивание между областями не работает.");
        var conditionalDrag = "#Если Сервер Тогда\nПроцедура А()\nКонецПроцедуры\n#КонецЕсли\nПроцедура Б()\nКонецПроцедуры";
        var conditionalMethods = ModuleOutline.Parse(conditionalDrag);
        var guardedDrag = MethodRegions.PlanRelative(conditionalDrag, conditionalMethods[0], conditionalMethods[1], true);
        Check(guardedDrag.Result.Contains("#Если Сервер Тогда\nПроцедура А()\nКонецПроцедуры\n#КонецЕсли"), "Перетаскивание потеряло условие #Если.");
        Reject(() => MethodRegions.PlanRelative(conditionalDrag, conditionalMethods[1], conditionalMethods[0], false), "Общий метод попал под чужое ограничивающее условие.");
        var nestedCondition = "#Если Сервер Тогда\n#Если Клиент Тогда\nПроцедура Метод()\nКонецПроцедуры\n#КонецЕсли\n#КонецЕсли\n#Область Цель\n#КонецОбласти";
        Check(MethodRegions.Plan(nestedCondition, ModuleOutline.Parse(nestedCondition).Single(), MethodRegions.Parse(nestedCondition).Single()).Result.Contains(
            "#Если Сервер Тогда\n#Если Клиент Тогда\nПроцедура Метод()\nКонецПроцедуры\n#КонецЕсли\n#КонецЕсли"), "Вложенные условия потеряны при переносе.");
        var elseCondition = "#Если Сервер Тогда\n#ИначеЕсли Клиент Тогда\n#Иначе\nПроцедура Метод()\nКонецПроцедуры\n#КонецЕсли\n#Область Цель\n#КонецОбласти";
        Check(MethodRegions.Plan(elseCondition, ModuleOutline.Parse(elseCondition).Single(), MethodRegions.Parse(elseCondition).Single()).Result.Contains(
            "#Если Не ((Сервер) Или (Клиент)) Тогда\nПроцедура Метод()"), "Перенос из #Иначе потерял исключение предыдущих ветвей.");
        var equivalent = "#Если Сервер Тогда\nПроцедура Метод()\nКонецПроцедуры\n#КонецЕсли\n#Если Сервер Тогда\n#Область Цель\n#КонецОбласти\n#КонецЕсли";
        Check(MethodRegions.Plan(equivalent, ModuleOutline.Parse(equivalent).Single(), MethodRegions.Parse(equivalent).Single()).Result.Contains("Процедура Метод()"),
            "Одинаковые условия в разных местах ошибочно запрещают перенос.");
        using var pane = new ModuleOutlinePane();
        var view = "#Область Родитель\n#Область Вложенная\nПроцедура Тестовая()\nКонецПроцедуры\n#КонецОбласти\nПроцедура Вторая()\nКонецПроцедуры\n#КонецОбласти\nПроцедура Снаружи()\nКонецПроцедуры";
        var snapshot = new EditorSnapshot(IntPtr.Zero, view, 0, "", 0, 0, "test", "module");
        pane.UpdateEditor(snapshot);
        var grid = pane.Controls.OfType<DataGridView>().Single(); var search = pane.Controls.OfType<TextBox>().Single();
        Check(grid.Rows.Count == 5, "Группы и методы не показаны.");
        grid.ContextMenuStrip!.Items.Cast<ToolStripItem>().Single(i => i.Text == "Свернуть все области").PerformClick();
        Check(grid.Rows.Count == 2, "Сворачивание оставило вложенные методы/области видимыми.");
        search.Text = "Тестовая";
        Check(grid.Rows.Count == 3, "Поиск не раскрывает цепочку родительских областей.");
        search.Text = ""; Check(grid.Rows.Count == 2, "Поиск потерял состояние сворачивания.");
        pane.UpdateEditor(snapshot with { Text = "\n" + view });
        Check(grid.Rows.Count == 2, "Изменение строк сбросило сворачивание.");
        grid.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(i => i.Text == "Развернуть все области").PerformClick();
        Check(grid.Rows.Count == 5, "Разворачивание не восстановило методы.");
        pane.UpdateEditor(snapshot);
        pane.Stage(MethodRegions.Plan(view, ModuleOutline.Parse(view)[0], MethodRegions.Parse(view)[0]));
        pane.UpdateEditor(snapshot); // Repeated polling must not undo the preview.
        pane.ClearEditor();
        Check(pane.HasPendingChanges && ((ModuleMethod)grid.Rows[2].Tag!).Name == "Вторая", "Опрос редактора сбросил будущий порядок.");
        var bar = pane.Controls.OfType<FlowLayoutPanel>().Single();
        var apply = bar.Controls.OfType<Button>().First();
        pane.UpdateEditor(snapshot with { Text = view + "\n// Правка вручную" });
        Check(!apply.Enabled && pane.HasPendingChanges, "Изменённый вручную модуль разрешает затереть правки.");
        pane.CancelOrder();
        Check(!pane.HasPendingChanges && grid.Rows.Count == 5, "Отмена не вернула актуальный список.");
        pane.UpdateEditor(snapshot);
        pane.Stage(MethodRegions.Plan(view, ModuleOutline.Parse(view)[0], MethodRegions.Parse(view)[0]));
        var calls = 0;
        pane.ApplyRequested = (expected, plan) =>
        {
            calls++;
            Check(expected.Text == view && expected.Text[..plan.Start] + plan.Replacement + expected.Text[(plan.Start + plan.Length)..] == plan.Result,
                "Применение использует виртуальный текст вместо исходного.");
            return Task.FromResult(true);
        };
        var applyMethod = typeof(ModuleOutlinePane).GetMethod("ApplyOrder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((Task)applyMethod.Invoke(pane, null)!).GetAwaiter().GetResult();
        Check(calls == 1 && !pane.HasPendingChanges, "Все переносы не применяются одной операцией.");
    }
}
