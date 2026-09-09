// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Curves;
using Vixen.Core.Yaml;
using Vixen.Editor.Inspector;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20 § B5's curve preset library, which is a user-store file rather than an asset.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Where it lives is the decision, not an implementation detail.</b> Doc 20 says a
///         library of saved presets belongs beside the layouts and the keymap rather than in an asset
///         editor, and the consequence is the point of saying it: presets are not project assets, get
///         no importer, and travel with the person rather than with the repository. A team-shared
///         library is a different feature and must not be smuggled in by making this one an asset.
///     </para>
///     <para>
///         ⚠ <b>The shipped shapes are defaults rather than entries.</b> A store seeded with linear
///         and the eases is one where deleting a built-in is a thing that can happen and cannot be
///         undone — so they are never written to the file, and an emptied store still offers them.
///         That is the assertion below that cannot pass by accident.
///     </para>
/// </remarks>
public class CurvePresetTests {
    /// <summary>
    ///     ⚠ <b>An empty library is a full menu.</b> The shipped shapes are a property of the class
    ///     and not rows in the file, which is what makes "delete every preset" a survivable thing to
    ///     do.
    /// </summary>
    [Fact]
    public void An_empty_library_still_offers_the_shipped_shapes() {
        var library = new CurvePresetLibrary();

        Assert.Empty(library.Curves);

        var offered = library.Offered().Select(entry => entry.Name).ToList();

        Assert.Equal(["Linear", "Ease In", "Ease Out", "Ease In Out", "Constant"], offered);

        // And a shipped shape cannot be forgotten, because it was never kept.
        Assert.False(library.Forget("Linear"));
        Assert.Contains("Linear", library.Offered().Select(entry => entry.Name));
    }

    /// <summary>
    ///     ⚠ <b>Saved, written, read back, and still the same shape.</b> The tangent mode is stored
    ///     by name because it is an enum somebody will insert a member into — one stored as an
    ///     integer comes back meaning something else after a version that edits the declaration,
    ///     which is the failure nobody reports because it looks like the editor forgetting.
    /// </summary>
    [Fact]
    public void A_saved_curve_survives_the_round_trip_through_the_file() {
        var library = new CurvePresetLibrary();

        var shape = new AnimationCurve(
            new CurveKey(0f, 0.25f, TangentMode.Constant),
            new CurveKey(1f, 0.75f, TangentMode.Linear) { InTangent = 3f, OutTangent = -2f }
        );

        library.Save("Mine", shape);

        var read = YamlSerializer.Parse<CurvePresetLibrary>(YamlSerializer.ToYaml(library));

        var saved = Assert.Single(read.Curves);

        Assert.Equal("Mine", saved.Name);

        var back = CurvePresetLibrary.ToCurve(saved);

        Assert.Equal(2, back.Keys.Count);
        Assert.Equal(0.25f, back.Keys[0].Value);
        Assert.Equal(TangentMode.Constant, back.Keys[0].Mode);
        Assert.Equal(TangentMode.Linear, back.Keys[1].Mode);
        Assert.Equal(3f, back.Keys[1].InTangent);
        Assert.Equal(-2f, back.Keys[1].OutTangent);

        // ⚠ And the shipped shapes are not in the file. Six offered, one written.
        Assert.Single(read.Curves);
        Assert.Equal(6, read.Offered().Count);
    }

    /// <summary>
    ///     ⚠ <b>A copy, not the object.</b> A library holding the curve an inspector row is editing
    ///     would be a preset that changed every time somebody dragged the key it was made from —
    ///     which is the aliasing <c>CurveDrawer</c>'s own remarks refuse for the same reason.
    /// </summary>
    [Fact]
    public void Saving_copies_the_keys_out_of_the_curve_it_was_given() {
        var library = new CurvePresetLibrary();
        var shape = AnimationCurve.Linear();

        library.Save("Mine", shape);

        shape.Add(new CurveKey(0.5f, 9f));

        var saved = library.Offered().Single(entry => entry.Name == "Mine").Curve;

        Assert.Equal(2, saved.Keys.Count);
        Assert.DoesNotContain(saved.Keys, key => key.Value == 9f);
    }

    /// <summary>
    ///     ⚠ <b>A saved shape with a shipped one's name wins rather than being refused.</b> Somebody
    ///     who saves their own "Ease In" has said what they mean by it; refusing the name would be
    ///     the editor arguing with them, and offering both would be two identical lines on a menu.
    /// </summary>
    [Fact]
    public void A_saved_name_replaces_the_shipped_one_rather_than_doubling_it() {
        var library = new CurvePresetLibrary();

        library.Save("Ease In", new AnimationCurve(new CurveKey(0f, 0f), new CurveKey(0.5f, 1f), new CurveKey(1f, 0f)));

        var offered = library.Offered();

        Assert.Single(offered, entry => entry.Name == "Ease In");
        Assert.Equal(3, offered.Single(entry => entry.Name == "Ease In").Curve.Keys.Count);

        // And forgetting it puts the shipped one back, which is the whole reason it is not stored.
        Assert.True(library.Forget("Ease In"));
        Assert.Equal(2, library.Offered().Single(entry => entry.Name == "Ease In").Curve.Keys.Count);
    }

    /// <summary>
    ///     ⚠ <b>Applied through the row somebody right-clicked.</b> A library nothing applies is
    ///     this repository's commonest defect, so the assertion has to go through the menu and land
    ///     on the object — not on the control.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The fixture is an <c>[Inspector]</c> type rather than a component, and that is a
    ///     finding rather than a convenience.</b> No production type in this tree declares an
    ///     <c>AnimationCurve</c> member at all — a sweep of <c>*.cs</c> and <c>*.vxml</c> finds the
    ///     drawer, its registration and its tests and nothing else — and a <c>Behavior</c> cannot
    ///     carry one. ⚠ <b>Not because the type is unserialisable, which is the reason
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1148">#1148</a> gives and it is the
    ///     second obstacle rather than the first:</b> <c>Vixen.Engine</c> references no UI assembly
    ///     at all, so a behaviour cannot <em>name</em> <c>AnimationCurve</c> however many attributes
    ///     it grows. The binary round trip is real — <c>ISceneBehaviorBinder.Copy</c> is
    ///     <c>Restore(Save(…))</c>, so an unserialisable member draws a foldout with zero rows and no
    ///     error — and it is what would bite a type that <em>could</em> name it. The two AI asset
    ///     editors that do edit curves build a <c>CurveEditor</c> directly and call <c>Apply</c>, so
    ///     they never pass through a row.
    /// </remarks>
    [Fact]
    public void A_preset_applies_to_the_curve_row_the_menu_was_opened_on() {
        using var editor = Inspecting(out var view, out var fixture);

        var row = view.Rows.FirstOrDefault(candidate => candidate.Field.Member.Name == nameof(CurveFixture.Shape))
            ?? throw editor.Fail("the fixture has no Shape row");

        var control = Assert.IsType<CurveEditor>(row.Editor);

        // Linear is two keys with linear tangents; Constant is two with constant ones, so the mode is
        // what says something happened and the count says nothing was lost.
        Assert.Equal(2, control.Curve.Keys.Count);
        Assert.All(fixture.Shape.Keys, key => Assert.Equal(TangentMode.Linear, key.Mode));

        Open(editor, row);
        Choose(editor, "Curve Presets", "Constant");

        Assert.Equal(2, control.Curve.Keys.Count);
        Assert.All(control.Curve.Keys, key => Assert.Equal(TangentMode.Constant, key.Mode));

        // ⚠ And it reached the object rather than the control. `CurveEditor.Apply` copies keys into
        // the curve the row is holding — keeping the object the caller is holding — which is what
        // raises the drawer's change and writes it home. An apply that swapped the object would
        // leave the fixture holding the curve it had before.
        Assert.All(fixture.Shape.Keys, key => Assert.Equal(TangentMode.Constant, key.Mode));
    }

    /// <summary>
    ///     ⚠ <b>A preset somebody saved is offered beside the shipped ones, in the same submenu.</b>
    ///     A library that could be written and not read would be exactly the half-feature this
    ///     repository files as "built but never fed".
    /// </summary>
    [Fact]
    public void A_saved_preset_is_offered_on_the_menu_and_applies() {
        using var editor = Inspecting(out var view, out var fixture);

        var row = view.Rows.First(candidate => candidate.Field.Member.Name == nameof(CurveFixture.Shape));

        // Saved through the library the application holds, which is what the Save line writes to —
        // the line itself opens a modal prompt for the name and is not what this asserts.
        editor.Curves.Save("Spike", new AnimationCurve(new CurveKey(0f, 0f), new CurveKey(0.5f, 1f), new CurveKey(1f, 0f)));

        Open(editor, row);
        Choose(editor, "Curve Presets", "Spike");

        Assert.Equal(3, fixture.Shape.Keys.Count);
    }

    /// <summary>
    ///     ⚠ <b>The lines are offered only where there is a curve.</b> A menu that showed "Save Curve
    ///     as Preset" over a text field is one that teaches people the line does nothing.
    /// </summary>
    [Fact]
    public void The_preset_lines_are_disabled_on_a_row_that_is_not_a_curve() {
        using var editor = Inspecting(out var view, out _);

        var row = view.Rows.FirstOrDefault(candidate => candidate.Field.Member.Name == nameof(CurveFixture.Speed))
            ?? throw editor.Fail("the fixture has no Speed row");

        Open(editor, row);

        var menu = Menu(editor);

        Assert.True(
            menu.Items.First(item => item.Label == "Save Curve as Preset…").Disabled,
            "the save line is offered over a row that is not a curve"
        );

        Assert.True(
            menu.Items.First(item => item.Label == "Curve Presets").Disabled,
            "the presets submenu is offered over a row that is not a curve"
        );
    }

    /// <summary>An editor with the fixture in its inspector, settled.</summary>
    /// <remarks>
    ///     ⚠ <b>Pushed into the panel rather than selected in the scene.</b> The application hands
    ///     the inspector whatever the selection is when it changes; nothing changes it here, so an
    ///     object inspected by hand stays inspected — which is how a type nothing in the scene
    ///     carries can be put in front of the rows that draw it.
    /// </remarks>
    static EditorSession Inspecting(out InspectorView view, out CurveFixture fixture) {
        var editor = EditorSession.Start();

        editor.Open("inspector");
        editor.Settle();

        view = Descendants(editor.Panel("inspector")).OfType<InspectorView>().FirstOrDefault()
            ?? throw editor.Fail("the inspector panel has no inspector in it");

        fixture = new CurveFixture();

        view.Inspect(fixture);
        editor.Settle();

        return editor;
    }

    /// <summary>Opens the row's context menu the way a person does.</summary>
    static void Open(EditorSession editor, InspectorRow row) {
        var bounds = row.Bounds;

        editor.Ui.At(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f)).RightClick();
        editor.Settle();
    }

    static ContextMenu Menu(EditorSession editor) =>
        Descendants(editor.Document.Root)
            .OfType<ContextMenu>()
            .FirstOrDefault(candidate =>
                candidate.IsOpen && candidate.Items.Any(item => item.Label == "Save Curve as Preset…")
            )
        ?? throw editor.Fail("no inspector-row menu with the preset lines on it is open");

    /// <summary>Walks into a submenu and presses one of its lines.</summary>
    static void Choose(EditorSession editor, string submenu, string line) {
        var opener = Menu(editor).Items.FirstOrDefault(item => item.Label == submenu)
            ?? throw editor.Fail($"the menu has no '{submenu}'");

        Assert.False(opener.Disabled, $"'{submenu}' is disabled on a curve row");

        var inner = opener.Submenu ?? throw editor.Fail($"'{submenu}' opens no menu");

        var chosen = inner.Items.FirstOrDefault(item => item.Label == line)
            ?? throw editor.Fail(
                $"'{submenu}' does not offer '{line}'. Offering: "
                + string.Join(", ", inner.Items.Select(item => item.Label ?? "?"))
                + "."
            );

        chosen.Activate();
        editor.Settle();
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

/// <summary>A described type with a curve on it, so that a curve row exists to right-click.</summary>
/// <remarks>
///     ⚠ <b>It is a fixture because there is nothing else, and that is a decision rather than a
///     gap.</b> No production type in this tree declares an <c>AnimationCurve</c> member — the drawer
///     is registered and has never had one to draw — and the panes that do edit curves build the
///     control directly. The member the drawer is for is an <em>application's</em>, exactly as
///     <c>GradientEditor</c>'s consumer is: the <c>WaterMaterial</c> in the Inspector README is the
///     shape, and this fixture is the same shape written for a test.
/// </remarks>
public sealed class CurveFixture {
    /// <summary>The curve the presets are applied to.</summary>
    [Inspector]
    public AnimationCurve Shape { get; set; } = AnimationCurve.Linear();

    /// <summary>An ordinary member, so that "not a curve" has a row to be asserted on.</summary>
    [Inspector]
    public float Speed { get; set; } = 1f;
}
