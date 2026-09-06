// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     Every public element type in <c>Vixen.Ui.Controls</c> either has a role or has a written
///     reason for not having one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 46 § A2's third acceptance line asked for the ARIA-role snapshot to be writable
///         "for every control in both control assemblies", and every assertion that answered it was
///         per fixture.</b> <c>Unnamed(button)</c>, <c>Unnamed(tabs)</c>, <c>Unnamed(select)</c> —
///         each is a statement about the control somebody remembered to write a fixture for, and a
///         control nobody wrote one for is covered by nothing at all. This is the sweep: the type
///         list, one instance of each, one rule.
///     </para>
///     <para>
///         ⚠ <b>The rule is "a role or a reason", and the reason has to be written down.</b>
///         Roughly a quarter of this assembly is deliberately <c>None</c> — a tree that reported
///         <c>Panel</c>, <c>Card</c> and <c>ScrollView</c> would read a four-field form as thirty
///         nested groups, which is how an accessibility tree comes to be complete and useless. That
///         is a decision rather than an omission, and the difference between the two is exactly what
///         a file count or a type count cannot see. <see cref="Exempt" /> is where the difference is
///         recorded, and a control missing from both the roled set and that table fails here by
///         name.
///     </para>
///     <para>
///         ⚠ <b>The exemption table is held to its own residue, not only to its regressions.</b> An
///         entry for a control that has since been given a role fails too. Otherwise the table is a
///         ratchet that only ever grows, and the day the last pointer-only sub-part gets a keyboard
///         nothing would say the reason had expired.
///     </para>
///     <para>
///         ⚠ <b>An exemption cannot cover a tab stop, whatever it says.</b> A control the keyboard
///         can reach and the accessibility tree cannot see is a place a screen-reader user lands on
///         silence, and no written reason makes that acceptable — so a focusable element in the
///         table fails on the exemption rather than being excused by it.
///     </para>
///     <para>
///         ⚠ <b>A sweep that enumerates nothing passes perfectly</b>, which is the shape of every
///         instrument in this repository that reported success on the day it did not run. So the
///         population is asserted as a number twice — how many types were built, and how many of
///         them answered with a role — and neither floor can be met by an empty reflection query.
///     </para>
///     <para>
///         ⚠ <b>The filter is <see cref="UiElement" /> rather than <c>Control</c>, and that is not a
///         detail.</b> <c>AccessibilityTreeTests</c>' existing sweep filters on <c>Control</c>, and
///         one assembly over that silently omits <c>ViewportGizmo</c>, <c>CodeLine</c>,
///         <c>CodeSpan</c>, <c>CodeGutterRow</c>, <c>TimelineLanes</c>, <c>NodeWireLayer</c> and
///         <c>NodeOverlayLayer</c> — public element types a screen reader would walk straight
///         through and one of which doc 46 names as owed. A screen reader does not know what
///         <c>Control</c> is.
///     </para>
///     <para>
///         <b>Naming is deliberately not asserted here</b>, for the reason doc 46 § A2 states: a
///         bare control has no caption, and <c>TextField</c> answers <c>null</c> on purpose so that
///         an unlabelled field is caught rather than given a plausible name. Names are held down by
///         the reference windows, with <c>AccessibilitySnapshot.Unnamed</c>.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class AccessibilityCoverageTests {
    /// <summary>The elements that answer <c>None</c> on purpose, and why each of them does.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Fourteen of these are doc 46 § A2's "left <c>None</c> on purpose" list</b>, quoted
    ///         from the document rather than reconstructed. The other two are decisions the document
    ///         records in prose elsewhere: <c>ComboBox</c>, whose role ARIA 1.2 puts on the text
    ///         input rather than on the box drawn round it, and <c>KeyValueRow</c>, whose words
    ///         reach the tree on the editor the key names.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A reason is prose on purpose.</b> A boolean would let a control be excused by
    ///         somebody who did not have to say why, which is the state this test exists to end.
    ///     </para>
    /// </remarks>
    static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal) {
        ["Panel"] = "a layout box; announcing it would put a group round every four fields",
        ["Card"] = "a layout box with a border; its body's controls carry the roles",
        ["Accordion"] = "a stack of expanders; each header is a button and says what it opens",
        ["Expander"] = "the header is the button and carries `Controls` to the content; the shell is layout",
        ["ScrollView"] = "scrolling is a viewport property, not a widget; the scroll bars inside it are `scrollbar`",
        ["Tabs"] = "ARIA puts `tablist` on the strip and `tab` on each item; the shell holding both is neither",
        ["KeyValueList"] = "a two-column layout; each row's editor carries the role and the key names it",
        ["DiagnosticsPanel"] =
            "a shell over a `KeyValueList` whose rows are the words; a role on the frame would "
            + "announce a debug view as a widget and say nothing a reader could not already read",
        ["KeyValueRow"] = "a key and an editor side by side; the key names the editor through `LabelledBy`",
        ["Popover"] = "a positioned surface; what is inside it is what the user operates",
        ["Icon"] = "decoration beside a word that already says it — an icon announced twice is read twice",
        ["TextBlock"] = "text is read as text; a role would make a paragraph a widget",
        ["Skeleton"] = "a loading placeholder standing where content will be; there is nothing to announce yet",
        ["KeyboardShortcut"] = "a rendering of a key combination, read as the text it is",
        ["VirtualizingPanel"] = "a windowed layout; the realised children carry whatever roles they have",
        ["VirtualizingGrid"] = "ditto, in two dimensions",
        ["ComboBox"] =
            "ARIA 1.2 puts `combobox` on the text input, which is what takes focus and what "
            + "`aria-expanded` is read from; the private `ComboEditor : TextBox` carries it (doc 46 § A2)"
    };

    /// <summary>How many public element types this assembly is expected to offer, at least.</summary>
    /// <remarks>
    ///     ⚠ <b>Sixty today, and the floor is under it rather than equal to it</b> so that adding a
    ///     control is not a failing test — but not far under it, because the whole purpose of the
    ///     number is to fail on the day the reflection query stops matching. A filter that quietly
    ///     matched nothing would satisfy every other assertion in this file.
    /// </remarks>
    const int Elements = 56;

    /// <summary>And how many of them are expected to answer with a role.</summary>
    /// <remarks>
    ///     Forty-four today. The second number is what stops the first one being met by an assembly
    ///     of exempted types: a population that had been reverted wholesale would still build sixty
    ///     elements.
    /// </remarks>
    const int Roled = 40;

    [Fact]
    public void Every_element_type_has_a_role_or_a_written_reason_for_not() {
        using var fixture = new ControlFixture();

        var built = new List<string>();
        var roled = new List<string>();
        var roleless = new List<string>();
        var offenders = new List<string>();

        // ⚠ Through `Make<T>` rather than `UiElement.Add<T>` directly: `Add`'s last parameter is a
        // `ReadOnlySpan<string>`, which reflection cannot pass at all.
        var make = typeof(AccessibilityCoverageTests)
            .GetMethod(nameof(Make), BindingFlags.NonPublic | BindingFlags.Static)!;

        var types = typeof(Button).Assembly.GetTypes().OrderBy(static type => type.Name, StringComparer.Ordinal);

        foreach (var type in types) {
            if (!type.IsPublic || type.IsAbstract || !typeof(UiElement).IsAssignableFrom(type)) {
                continue;
            }

            // ⚠ Reported rather than skipped. A type reflection cannot build is a hole in the sweep
            // exactly the size of a control, and a `continue` here is how a sweep comes to cover
            // less than it says while staying green.
            if (type.GetConstructor(Type.EmptyTypes) is null) {
                offenders.Add($"{type.Name} has no parameterless constructor, so the sweep cannot reach it");
                continue;
            }

            var element = (UiElement) make.MakeGenericMethod(type).Invoke(null, [fixture.Document.Root])!;
            fixture.Update();
            built.Add(type.Name);

            if (element.Role != AccessibleRole.None) {
                roled.Add(type.Name);

                if (Exempt.ContainsKey(type.Name)) {
                    offenders.Add(
                        $"{type.Name} is exempted as roleless and answers {element.Role}; the exemption has expired"
                    );
                }

                continue;
            }

            roleless.Add(type.Name);

            if (!Exempt.TryGetValue(type.Name, out var reason) || string.IsNullOrWhiteSpace(reason)) {
                offenders.Add($"{type.Name} has no role and no written reason for not having one");
            } else if (element.Focusable) {
                offenders.Add($"{type.Name} is a tab stop, and no reason excuses a tab stop that is not in the tree");
            }
        }

        // ⚠ First, and both of them: an assembly whose reflection found nothing satisfies every
        // assertion below perfectly, and so does one whose population had been reverted.
        Assert.True(built.Count >= Elements, $"only {built.Count} element types were built");
        Assert.True(roled.Count >= Roled, $"only {roled.Count} of {built.Count} elements answered with a role");

        // ⚠ Then the offenders, and before the equality below rather than after it: this one names
        // the control and says what is wrong with it, and a set difference that fired first would
        // report the same failure as two sorted lists diverging at index twelve.
        Assert.Empty(offenders);

        // And the residue, stated rather than implied: the exemption table is exactly the roleless
        // set. This is the only assertion that can see an entry naming a type that no longer
        // exists, since a deleted control never comes round the loop to contradict it.
        Assert.Equal(roleless.Order(StringComparer.Ordinal), Exempt.Keys.Order(StringComparer.Ordinal));
    }

    static UiElement Make<T>(UiElement parent) where T : UiElement, new() => parent.Add<T>();
}
