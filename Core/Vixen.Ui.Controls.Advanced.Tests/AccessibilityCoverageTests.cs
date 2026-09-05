// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     Every public element type in <c>Vixen.Ui.Controls.Advanced</c> either has a role or has a
///     written reason for not having one.
/// </summary>
/// <remarks>
///     <para>
///         <b>The other half of doc 46 § A2's third acceptance line</b> — see
///         <c>Vixen.Ui.Controls.Tests.AccessibilityCoverageTests</c> for why the rule is "a role or a
///         reason" and why the reason has to be prose. The two test assemblies cannot see each
///         other, so this is the same sweep with this assembly's own table.
///     </para>
///     <para>
///         ⚠ <b>This is the assembly where the sweep's filter matters.</b> Filtering on
///         <c>Control</c> — which the existing <c>AccessibilityTreeTests</c> sweep does — silently
///         omits seven public element types here, <c>ViewportGizmo</c> among them, which doc 46 § A2
///         names as genuinely owed. A sweep whose stated coverage is "every control" and whose real
///         coverage stops at a base class is worse than no sweep, because it is quoted as one.
///     </para>
///     <para>
///         ⚠ <b>Three of the pointer-only sub-parts doc 46 owes are still exempted here with a
///         pointer to their issue rather than reported as failures</b>, because giving them a role
///         before giving them a keyboard would be worse than not: a screen reader would announce a
///         set of widgets that cannot be operated. <c>ColorField</c>, <c>ColorStrip</c> and
///         <c>ColorSwatch</c> have left the table, and the order they left it in is the point — the
///         arrows and the roving tab stop landed in the same change as the roles, never before. The
///         name doc 46 lists that was never in the table is <c>TimelineTrack</c>: it is not a
///         <see cref="UiElement" /> at all, so it can never have a role and this sweep cannot see it.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class AccessibilityCoverageTests {
    /// <summary>The elements that answer <c>None</c>, and why each of them does.</summary>
    /// <remarks>
    ///     ⚠ <b>Two categories, and the reasons say which.</b> Most of these are structure — a paint
    ///     layer, a lane, a row, a container whose children carry the roles — and are correct
    ///     forever. Three are owed: the pointer-only sub-parts of the canvases, which need a
    ///     keyboard before they can be given a role, and whose reason names the issue that owes it.
    /// </remarks>
    static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal) {
        // Owed, in the order doc 46 § A2 lists them. Keyboard first, role second — see #420. The
        // colour picker's three left this table by being given a keyboard, not by being given a
        // role, and `GradientRail` has now left it the same way; the two below are the ones still
        // waiting for one.
        ["NodeItem"] = "pointer-only: a node is dragged and has no keyboard yet — #420",
        ["ViewportGizmo"] = "pointer-only: a manipulator handle with no keyboard yet — #420",

        // Structure. A role on any of these would announce a picture as a widget.
        ["GradientBar"] = "the painted preview of the gradient; it refuses focus and there is nothing to operate",
        ["DockingHost"] = "the docking layout itself; `DockTab` is `tab` and `DockPanel` is `tabpanel`",
        ["DockGroupView"] = "a split of the docking layout; the tabs and panels inside it carry the roles",
        ["NodeGroupView"] = "a frame drawn round a set of nodes on a canvas that is already `application`",
        ["NodeMinimap"] = "a picture of the graph, navigated by pointer; the canvas it steers is `application`",
        ["NodePortView"] = "a connection point drawn on a node; the canvas owns the keyboard",
        ["NodePortEditor"] = "a container of `NumericInput`s and a `CheckBox`, each of which carries its own role",
        ["NodeWireLayer"] = "a paint layer for the wires between nodes",
        ["NodeOverlayLayer"] = "a paint layer for the selection marquee and the wire being dragged",
        ["PropertyGrid"] = "a stack of rows; each row's editor carries the role and the row names it",
        ["PropertyRow"] = "a label and an editor side by side; the label names the editor through a relation",
        ["TimelineHeader"] = "the track-name column beside the lanes; a layout column",
        ["TimelineLanes"] = "the lanes are painted, and the timeline that owns their keyboard is `application`",
        ["TimelineRuler"] = "the time scale drawn above the lanes; a ruler is read, not operated",
        ["CodeLine"] = "one line inside a `CodeEditor`, which is a `textbox` and is read as text",
        ["CodeSpan"] = "one highlighted run inside a line; a colour is not a role",
        ["CodeGutterRow"] = "one line number beside a line; announcing it would read the number before every line"
    };

    /// <summary>How many public element types this assembly is expected to offer, at least.</summary>
    /// <remarks>
    ///     ⚠ <b>Forty today, and a floor rather than an equality</b> for the reason the sister test
    ///     gives: the number exists to fail on the day the reflection query matches nothing, which
    ///     is the state in which every other assertion here passes.
    /// </remarks>
    const int Elements = 37;

    /// <summary>And how many of them are expected to answer with a role.</summary>
    /// <remarks>Twenty today, which is what stops the first floor being met by exempted types.</remarks>
    const int Roled = 19;

    [Fact]
    public void Every_element_type_has_a_role_or_a_written_reason_for_not() {
        using var fixture = new AdvancedFixture();

        var built = new List<string>();
        var roled = new List<string>();
        var roleless = new List<string>();
        var offenders = new List<string>();

        var make = typeof(AccessibilityCoverageTests)
            .GetMethod(nameof(Make), BindingFlags.NonPublic | BindingFlags.Static)!;

        var types = typeof(TreeView).Assembly.GetTypes().OrderBy(static type => type.Name, StringComparer.Ordinal);

        foreach (var type in types) {
            if (!type.IsPublic || type.IsAbstract || !typeof(UiElement).IsAssignableFrom(type)) {
                continue;
            }

            // ⚠ Reported rather than skipped — a `continue` here is a hole the size of a control.
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

        Assert.True(built.Count >= Elements, $"only {built.Count} element types were built");
        Assert.True(roled.Count >= Roled, $"only {roled.Count} of {built.Count} elements answered with a role");

        // ⚠ The offenders before the equality, because this one names the control and says what is
        // wrong with it; a set difference firing first reports two lists diverging at an index.
        Assert.Empty(offenders);

        // The residue. Also the only assertion that can see an entry naming a type that no longer
        // exists, since a deleted control never comes round the loop to contradict it.
        Assert.Equal(roleless.Order(StringComparer.Ordinal), Exempt.Keys.Order(StringComparer.Ordinal));
    }

    static UiElement Make<T>(UiElement parent) where T : UiElement, new() => parent.Add<T>();
}
