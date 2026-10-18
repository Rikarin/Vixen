// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     The <c>api</c> rows of the scoped residue, held to the reason they give: that the public API they
///     name puts that control where the rule looks for it (#531).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Twenty-one of the residue's rows were prose.</b>
///         <c>Editor/Vixen.Editor.App.Tests/UnjudgedScopedSelectors.txt</c> files a control-sheet
///         selector as <c>api</c> when "a composition the public API offers and nothing in this tree
///         calls" builds it — <c>field-content combo-box</c> is a combo box given to a
///         <see cref="LabeledContent" />, <c>key-value-value slider</c> one handed to
///         <see cref="KeyValueRow.Content{T}" />. <c>CombinatorCensusDriftTests</c> checks that every
///         such reason cites a line that exists, and nothing checked that the API at that line
///         reaches the rule: a host whose part was renamed, or a reason naming the wrong one, left
///         a row calling a dead rule "live, just not called".
///     </para>
///     <para>
///         ⚠ <b>This proves the reason and credits nothing.</b> The harness does the nesting here,
///         which is exactly what the seeded sweep refuses as evidence that the tree builds a pairing
///         — so these rows stay <c>api</c> and stay in the residue. What changes is their standing:
///         an <c>api</c> row is now a selector the cascade's own matcher (<see cref="UiTest.Get" />)
///         has matched through the API its reason names, and one this file cannot build that way is
///         red here, naming the row to chase or to re-file.
///     </para>
/// </remarks>
public class ApiCompositionScopedSelectorTests {
    const string UnjudgedFile = "Editor/Vixen.Editor.App.Tests/UnjudgedScopedSelectors.txt";

    /// <summary>
    ///     Each host part an <c>api</c> reason names, the API that puts a control in it, and the words
    ///     the reason has to use for that API — so a reason edited to name another one is noticed.
    /// </summary>
    static readonly Dictionary<string, (string Api, Func<UiElement, Type, UiElement> Put)> Hosts = new(StringComparer.Ordinal) {
        ["field-content"] = ("LabeledContent.Content", static (parent, type) => Add(parent.Add<LabeledContent>().Content, type)),
        ["key-value-value"] = ("KeyValueRow.Content<T>()", static (parent, type) => Content(parent.Add<KeyValueRow>(), type))
    };

    /// <summary>Two rows that must be among the ones asked and matched, so an empty read cannot pass.</summary>
    static readonly string[] Anchors = ["field-content combo-box", "key-value-value slider"];

    /// <summary>
    ///     ⚠ <b>Every <c>api</c> row is matched through the API its reason names</b>, and its mirror
    ///     is not — the mirror is what stops a matcher that answers yes to everything from passing.
    /// </summary>
    [Fact]
    public void Every_api_row_is_reached_by_the_api_its_reason_names() {
        var rows = ApiRows();
        var tags = TagsToTypes();

        List<string> failures = [];
        HashSet<string> matched = new(StringComparer.Ordinal);

        foreach (var (selector, reason) in rows) {
            var compounds = selector.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (compounds.Length != 2 || !Hosts.TryGetValue(compounds[0], out var host)) {
                failures.Add($"{selector} — no API here puts a control under '{compounds[0]}'; teach this file the reason's API, or re-file the row");
                continue;
            }

            if (!reason.Contains(host.Api, StringComparison.Ordinal)) {
                failures.Add($"{selector} — the reason does not name {host.Api}, which is the API this file reaches it through");
                continue;
            }

            if (!tags.TryGetValue(compounds[1], out var type)) {
                failures.Add($"{selector} — no public control with a parameterless constructor has the tag '{compounds[1]}'");
                continue;
            }

            using var ui = new AdvancedFixture();
            host.Put(ui.Document.Root, type);
            ui.Update();

            var test = UiTest.Adopt(ui.Document);

            if (test.Get(selector).Count == 0) {
                failures.Add($"{selector} — {host.Api} put a <{compounds[1]}> in the tree and the rule does not match it");
            } else if (test.Get($"{compounds[1]} {compounds[0]}").Count > 0) {
                failures.Add($"{selector} — its mirror matched too, so the matcher answered nothing");
            } else {
                matched.Add(selector);
            }
        }

        Assert.True(failures.Count == 0, $"{UnjudgedFile} has api rows whose API does not reach the rule:\n  {string.Join("\n  ", failures)}");

        foreach (var anchor in Anchors) {
            Assert.Contains(anchor, matched);
        }

        Assert.Equal(rows.Count, matched.Count);
    }

    /// <summary>The <c>api</c> rows of the residue: selector to reason.</summary>
    static Dictionary<string, string> ApiRows() {
        var lines = File.ReadAllLines(Path.Combine(Root(), UnjudgedFile));

        Assert.True(lines.Count(static line => line.StartsWith('#')) >= 5, $"{UnjudgedFile} has lost its header, so it was emptied rather than answered.");

        return lines
            .Where(static line => line.Length != 0 && !line.StartsWith('#'))
            .Select(static line => line.Split('\t'))
            .Where(static columns => columns.Length == 3 && columns[1].Trim() == "api")
            .ToDictionary(static columns => columns[0].Trim(), static columns => columns[2].Trim(), StringComparer.Ordinal);
    }

    /// <summary>
    ///     Every public control in the two control assemblies, by the tag it renders as.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A control's tag is its <c>TagName</c> override, not its type name kebab-cased</b> —
    ///     <c>TextBox</c> is <c>textbox</c> (#531's first audit). Read off an uninitialised instance,
    ///     because every override is a constant and constructing a hundred controls to read one string
    ///     each is a minute of somebody's test run.
    /// </remarks>
    static Dictionary<string, Type> TagsToTypes() {
        var tagName = typeof(UiElement).GetProperty("TagName", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var map = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var type in new[] { typeof(Button).Assembly, typeof(DataGrid).Assembly }.SelectMany(static assembly => assembly.GetTypes())) {
            if (!type.IsPublic || type.IsAbstract || type.IsGenericTypeDefinition || !typeof(UiElement).IsAssignableFrom(type)
                || type.GetConstructor(Type.EmptyTypes) is null) {
                continue;
            }

            if (tagName.GetValue(RuntimeHelpers.GetUninitializedObject(type)) is string tag) {
                map.TryAdd(tag, type);
            }
        }

        return map;
    }

    static UiElement Add(UiElement parent, Type type) =>
        (UiElement)typeof(ApiCompositionScopedSelectorTests)
            .GetMethod(nameof(AddOf), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type)
            .Invoke(null, [parent])!;

    static UiElement AddOf<T>(UiElement parent) where T : UiElement, new() => parent.Add<T>();

    static UiElement Content(KeyValueRow row, Type type) =>
        (UiElement)typeof(KeyValueRow).GetMethod(nameof(KeyValueRow.Content))!.MakeGenericMethod(type).Invoke(row, [])!;

    static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
