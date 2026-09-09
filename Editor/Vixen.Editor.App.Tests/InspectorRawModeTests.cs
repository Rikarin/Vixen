// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Testing;
using Vixen.Engine.Behaviors;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Debug (raw) mode: what the file holds, rather than what the author chose to expose.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The diagnostic argument, which is the whole reason this row of doc 20 § B1 is not an
///         ergonomic one.</b> <c>[Inspector]</c> and the serialization generator between them decide
///         what a component shows, so a member nobody annotated is invisible in the editor and
///         present in the file — and without a raw mode "this value is wrong" and "this value is not
///         drawn" are the same picture. That ambiguity has cost this repository real time.
///     </para>
///     <para>
///         ⚠ <b>Two bypasses, not one, and <see cref="RawFixture" /> exercises both in one type.</b>
///         <c>ReflectedDescriptor.For</c> answers with an <c>[Inspector]</c> descriptor where there
///         is one — so a custom inspector drawing one of a type's three members is a case no filter
///         over its output could reach — and <c>Build</c> drops an <c>[EditorVisible(false)]</c>
///         member before there is a row to filter. A raw mode has to be a second descriptor.
///     </para>
///     <para>
///         ⚠ <b>The counts are asserted, not just the names.</b> A test that only says "World is in
///         the raw list" passes just as happily against a raw list that is every member of every
///         type; and an assertion made inside a loop over rows passes vacuously when the panel drew
///         none.
///     </para>
/// </remarks>
public class InspectorRawModeTests {
    /// <remarks>
    ///     Registered by hand rather than by the engine's generator, which this assembly does not
    ///     run — the same seam <c>BehaviorAuthoringTests</c> uses, and for the same reason.
    /// </remarks>
    static InspectorRawModeTests() => SceneBehaviorRegistry.Register<RawProbeBehavior>();

    /// <summary>
    ///     ⚠ <b>The ordinary descriptor is the author's answer and the raw one is the file's.</b> A
    ///     type with an <c>[Inspector]</c> member draws exactly that one; the other two are in every
    ///     save and in no panel, which is precisely the state somebody opens raw mode to see.
    /// </summary>
    [Fact]
    public void Raw_mode_shows_what_a_custom_descriptor_and_editor_visibility_both_hide() {
        var ordinary = ReflectedDescriptor.For(typeof(RawFixture))
            ?? throw new InvalidOperationException("RawFixture has no descriptor at all");

        var raw = ReflectedDescriptor.Raw(typeof(RawFixture))
            ?? throw new InvalidOperationException("RawFixture has no raw descriptor");

        // The author's answer: one member, because one carries [Inspector].
        Assert.Equal(["Exposed"], ordinary.Members.Select(member => member.Name));

        // The file's answer: everything the serializer describes, in declared order.
        Assert.Equal(["Exposed", "Undecorated", "Plumbing"], raw.Members.Select(member => member.Name));
    }

    /// <summary>
    ///     ⚠ <b>Looking under the panel rather than editing under it.</b> <c>[EditorVisible(false)]</c>
    ///     is an author saying a member is written to a file and is not somebody's business to type
    ///     into, so raw mode draws those read-only and leaves the ordinarily visible ones alone. A
    ///     raw mode that made them writable would answer a question nobody asked.
    /// </summary>
    [Fact]
    public void The_rows_raw_mode_adds_are_read_only_and_the_ones_it_did_not_add_are_not() {
        var raw = ReflectedDescriptor.Raw(typeof(RawFixture))!;

        var exposed = raw.Members.Single(member => member.Name == "Exposed");
        var plumbing = raw.Members.Single(member => member.Name == "Plumbing");

        Assert.False(exposed.IsReadOnly);
        Assert.True(plumbing.IsReadOnly);

        // And the mark the panel styles, so a raw dump can say *which* row is the hidden one.
        Assert.False(Assert.IsType<ReflectedMember>(exposed).IsHidden);
        Assert.True(Assert.IsType<ReflectedMember>(plumbing).IsHidden);
    }

    /// <summary>
    ///     ⚠ <b>Through the button a person presses, and back again.</b> The panel is what the issue
    ///     asks for; a descriptor that can produce the rows and a panel that never asks it for them
    ///     is this repository's commonest defect. Pressing the toggle twice is what says the mode is
    ///     a mode rather than a one-way door.
    /// </summary>
    [Fact]
    public void The_panel_draws_the_hidden_members_only_while_the_raw_toggle_is_down() {
        using var editor = Probed(out var components);

        Assert.False(components.IsRaw);

        var ordinary = Rows(editor, components);

        Assert.Contains("Speed", ordinary);

        // Serialised, and hidden from the panel: present in the file and invisible in the editor,
        // which is the state the whole feature is for.
        Assert.DoesNotContain("Seed", ordinary);

        components.RawToggle.Activate();
        editor.Settle();

        Assert.True(components.IsRaw);

        var raw = Rows(editor, components);

        Assert.Contains("Speed", raw);
        Assert.Contains("Seed", raw);

        // ⚠ And *not* the façades. `Behavior.Position` and `Behavior.World` are hidden as well, and
        // they are `[DataMemberIgnore]` — not in the file, so not what raw mode was asked for. This
        // half is the one that was wrong first: drawing everything the descriptor keeps put thirteen
        // rows of plumbing in the foldout and then threw on `Coroutines`, whose getter demands a
        // store the panel's detached copy does not have.
        Assert.DoesNotContain("World", raw);
        Assert.DoesNotContain("Position", raw);
        Assert.DoesNotContain("Coroutines", raw);

        // ⚠ The count, because "contains Seed" is also true of a panel that drew every member of
        // every type it could reach. Raw mode adds rows to this foldout and takes none away.
        Assert.True(
            raw.Count > ordinary.Count,
            $"raw mode drew {raw.Count} rows and the ordinary panel drew {ordinary.Count}"
        );

        Assert.All(ordinary, name => Assert.Contains(name, raw));

        components.RawToggle.Activate();
        editor.Settle();

        Assert.False(components.IsRaw);
        Assert.DoesNotContain("Seed", Rows(editor, components));
    }

    /// <summary>
    ///     ⚠ <b>The panel says which rows it is showing that the ordinary one would not.</b> A raw
    ///     dump that looked like the ordinary panel with more rows in it would answer the question it
    ///     was opened to answer and leave nobody able to point at the undrawn member — which is the
    ///     question. The note and the per-row mark are both part of the feature rather than of the
    ///     theme.
    /// </summary>
    [Fact]
    public void Raw_mode_marks_the_rows_the_ordinary_panel_would_not_have_drawn() {
        using var editor = Probed(out var components);

        Assert.Empty(Marked(editor, components));

        components.RawToggle.Activate();
        editor.Settle();

        var marked = Marked(editor, components);

        Assert.Contains("Seed", marked);
        Assert.DoesNotContain("Speed", marked);

        // And the sentence that says what the panel is showing, which is the other half of it.
        Assert.Contains(
            Descendants(editor.Panel("inspector")),
            element => element.Tag == "component-raw-note"
        );
    }

    /// <summary>An editor showing a crate with the probe behaviour on it, settled.</summary>
    static EditorSession Probed(out ComponentsView components) {
        var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");
        editor.Open("inspector");
        editor.Settle();

        var found = Panel(editor);

        editor.Scene.Behaviors.Add(editor.Scene.Selection[0], new RawProbeBehavior());

        found.Show(editor.Scene.Selection[0]);
        editor.Settle();

        components = found;
        return editor;
    }

    /// <summary>The probe's foldout, which is the only one either mode changes the size of.</summary>
    static Expander Section(EditorSession editor, ComponentsView components) =>
        components.Sections.FirstOrDefault(fold => fold.Label == "Raw Probe Behavior")
        ?? throw editor.Fail(
            "no 'Raw Probe Behavior' foldout. Showing: "
            + string.Join(", ", components.Sections.Select(fold => fold.Label ?? "?"))
            + "."
        );

    /// <summary>What the probe's foldout is drawing, by member name.</summary>
    static List<string> Rows(EditorSession editor, ComponentsView components) =>
        Descendants(Section(editor, components))
            .OfType<InspectorRow>()
            .Select(row => row.Field.Member.Name)
            .ToList();

    /// <summary>Which of them the panel marked as only-in-raw.</summary>
    static List<string> Marked(EditorSession editor, ComponentsView components) =>
        Descendants(Section(editor, components))
            .OfType<InspectorRow>()
            .Where(row => row.HasClass("raw-member"))
            .Select(row => row.Field.Member.Name)
            .ToList();

    /// <summary>
    ///     ⚠ Found by pattern rather than by <c>OfType&lt;ComponentsView&gt;</c>, which is the call
    ///     #1022 reports nine <c>CA2021</c> errors over on this assembly.
    /// </summary>
    static ComponentsView Panel(EditorSession editor) {
        foreach (var child in Descendants(editor.Panel("inspector"))) {
            if (child is ComponentsView view) {
                return view;
            }
        }

        throw editor.Fail("the inspector has no components section");
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}

/// <summary>
///     A described type whose author drew one of its three members, so that raw mode's two bypasses
///     are one fixture rather than two.
/// </summary>
/// <remarks>
///     ⚠ <b>Every member here is serialised.</b> That is the point: what separates them is what the
///     <i>editor</i> was told, and the three states are the three a raw mode has to tell apart — a
///     member the author exposed, one nobody annotated, and one somebody hid on purpose.
/// </remarks>
[DataContract("RawFixture")]
public sealed class RawFixture {
    /// <summary>Drawn by the ordinary panel, because it carries the annotation.</summary>
    [Inspector]
    public float Exposed { get; set; } = 1f;

    /// <summary>In every save and in no panel, because nobody said anything about it.</summary>
    public float Undecorated { get; set; } = 2f;

    /// <summary>Hidden on purpose, which the reflected descriptor honours and raw mode looks under.</summary>
    [EditorVisible(false)]
    public float Plumbing { get; set; } = 3f;
}

/// <summary>A behaviour with one ordinary member and one that is in the file and in no panel.</summary>
/// <remarks>
///     ⚠ <b>It is a <c>Behavior</c> because its base is the sharpest test of the other half of the
///     rule.</b> <c>Behavior</c> carries thirteen members that are hidden <i>and</i>
///     <c>[DataMemberIgnore]</c> — <c>World</c>, <c>Position</c>, <c>Coroutines</c> — so a raw mode
///     that meant "every member the descriptor keeps" rather than "every member the file holds"
///     drowns <see cref="Seed" /> in them and then throws on the one whose getter needs a store.
/// </remarks>
[DataContract("RawProbeBehavior")]
public sealed class RawProbeBehavior : Behavior {
    /// <summary>An ordinary member, drawn in both modes.</summary>
    public float Speed { get; set; } = 3f;

    /// <summary>
    ///     In every save and in no panel, which is the exact state raw mode exists to make visible.
    /// </summary>
    [EditorVisible(false)]
    public int Seed { get; set; } = 42;
}
