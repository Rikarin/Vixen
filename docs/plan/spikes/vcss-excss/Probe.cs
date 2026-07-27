// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0
//
// The ExCSS spike. Not built by the solution — it is the evidence behind RESULT.md, kept so the
// next person can re-run it against a newer ExCSS rather than take this file's word for it.

using System.Collections;
using System.Reflection;
using ExCSS;

var parser = new StylesheetParser(true, true, true, true, true);
var sel = ((StyleRule) parser.Parse(".a > .b:hover .c + span { color: red }").Children.First()).Selector;

foreach (var item in (IEnumerable) sel) {
    Console.WriteLine($"{item.GetType().Name}:");
    foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
        object? v = null;
        try { v = p.GetValue(item); } catch { }
        Console.WriteLine($"   {p.Name} = [{v}] ({v?.GetType().Name})");
    }
}

Console.WriteLine("--- pseudo detail ---");
var ps = ((StyleRule) parser.Parse("a:nth-child(2n+1):not(.x)::before { color: red }").Children.First()).Selector;
Dump(ps, "  ");

static void Dump(ISelector s, string indent) {
    Console.WriteLine($"{indent}{s.GetType().Name} [{s.Text}]");
    foreach (var p in s.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
        if (p.Name is "Text" or "Length" or "IsReady" or "Specificity") continue;
        object? v = null;
        try { v = p.GetValue(s); } catch { }
        Console.WriteLine($"{indent}  .{p.Name} = [{v}]");
    }
    if (s is IEnumerable e and not string) {
        foreach (var c in e) if (c is ISelector cs) Dump(cs, indent + "    ");
    }
}
