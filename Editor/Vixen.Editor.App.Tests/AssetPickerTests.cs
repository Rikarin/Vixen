// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Inspector.Drawers;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>An inspectable thing with an asset field on it.</summary>
/// <remarks>
///     Declared here rather than borrowed, because a descriptor is a build artefact: the generator
///     runs over this assembly, and a hand-written stand-in would be a test of the stand-in.
/// </remarks>
#pragma warning disable CA1051 // The generator describes fields, which is what the fixtures it is
                              // exercised against have to be — see Vixen.Editor.Inspector.Tests.
public sealed class PickerFixture {
    /// <summary>An asset reference with no type filter, which offers everything.</summary>
    [Inspector]
    public AssetId Anything;

    /// <summary>One that cannot be cleared, which is the picker's other case.</summary>
    [Inspector]
    [AssetPicker(typeof(PickerFixture), AllowNull = false)]
    public AssetId Required;
}
#pragma warning restore CA1051

/// <summary>The picker an asset field opens, which nothing opened.</summary>
/// <remarks>
///     ⚠ <b>Doc 20's B3: "AssetDrawer raises PickRequested and nothing opens. Small, and every asset
///     field is dead without it."</b> The button was there, it was enabled, and pressing it did
///     precisely nothing — doc 20's second bar failed on the first click rather than the second.
/// </remarks>
public class AssetPickerTests {
    static InspectorDescriptor Fixture =>
        InspectorRegistry.Find(typeof(PickerFixture))
        ?? throw new InvalidOperationException("the generator registered no descriptor for PickerFixture");

    static InspectorMember Member(string name) => Fixture.Members.Single(member => member.Name == name);

    [Fact]
    public void The_editor_wires_the_drawers_to_its_project() {
        using var fixture = new EditorFixture();

        var drawers = DrawerRegistry.Default.Drawers.OfType<AssetDrawer>().ToList();

        // Two of them — one for the type and one for the attribute — and both have to be pointed at
        // a project or half the asset fields in the editor stay dead.
        Assert.NotEmpty(drawers);
        Assert.All(drawers, drawer => Assert.NotNull(drawer.Resolve));
    }

    [Fact]
    public void An_asset_id_resolves_to_the_name_the_project_knows_it_by() {
        using var fixture = new EditorFixture();

        var picker = new AssetPicker(fixture.Editor.Project, fixture.Editor.Shell.Dialogs);
        var scene = Scene(fixture);

        Assert.Equal("Main.vxscene", picker.NameOf(scene));

        // ⚠ Null rather than an invented name: the drawer draws an unresolved id as `Missing (…)`,
        // and an asset deleted out from under a scene is exactly what somebody needs to see.
        Assert.Null(picker.NameOf(new AssetId(Guid.NewGuid())));
    }

    [Fact]
    public void Choosing_a_row_writes_the_asset_and_seals_it_as_one_undo_step() {
        using var fixture = new EditorFixture();

        var target = new PickerFixture();
        var field = new InspectorField(Fixture, Member("Anything"), [target], fixture.Editor.Scene);

        Open(fixture, field);

        Row(fixture, "Main.vxscene").Activate();
        fixture.Frames(2);

        Assert.Equal(Scene(fixture), target.Anything);

        // Written through the field and sealed, which is what puts it on the document's stack as one
        // step — the same path the text and number drawers take.
        Assert.True(fixture.Editor.Scene.Stack.CanUndo.Value);
    }

    [Fact]
    public void Backing_out_leaves_the_field_alone() {
        using var fixture = new EditorFixture();

        var target = new PickerFixture();
        var field = new InspectorField(Fixture, Member("Anything"), [target], fixture.Editor.Scene);

        Open(fixture, field);
        Press(fixture, "Cancel");
        fixture.Frames(2);

        Assert.Equal(AssetId.Empty, target.Anything);
        Assert.False(fixture.Editor.Scene.Stack.CanUndo.Value);
    }

    [Fact]
    public void The_search_box_narrows_the_list() {
        using var fixture = new EditorFixture();

        var field = new InspectorField(Fixture, Member("Anything"), [new PickerFixture()], fixture.Editor.Scene);

        Open(fixture, field);

        var search = Find<SearchBox>(Dialog(fixture))!;

        search.Value = "definitely-not-an-asset";
        fixture.Frames(2);

        Assert.Empty(Rows(fixture));

        search.Value = "main";
        fixture.Frames(2);

        Assert.NotEmpty(Rows(fixture));
    }

    /// <summary>
    ///     ⚠ A field that cannot be null offering a "None" button is a button that either fails
    ///     silently or writes something the type forbids.
    /// </summary>
    [Fact]
    public void Only_a_nullable_field_offers_none() {
        using var fixture = new EditorFixture();

        Open(fixture, new InspectorField(Fixture, Member("Anything"), [new PickerFixture()]));
        Assert.Contains(Buttons(fixture), button => button.Label == "None");

        Press(fixture, "Cancel");
        fixture.Frames(2);

        Open(fixture, new InspectorField(Fixture, Member("Required"), [new PickerFixture()]));
        Assert.DoesNotContain(Buttons(fixture), button => button.Label == "None");
    }

    static AssetId Scene(EditorFixture fixture) =>
        fixture.Editor.Project.Assets.Entries.First(entry => entry.Name == "Main.vxscene").Guid;

    static void Open(EditorFixture fixture, InspectorField field) {
        new AssetPicker(fixture.Editor.Project, fixture.Editor.Shell.Dialogs).Open(field);
        fixture.Frames(2);
    }

    static Dialog Dialog(EditorFixture fixture) =>
        fixture.Editor.Shell.Dialogs.Current ?? throw new InvalidOperationException("the picker did not open");

    static IEnumerable<Button> Buttons(EditorFixture fixture) => Dialog(fixture).Footer.Children.OfType<Button>();

    static void Press(EditorFixture fixture, string label) =>
        Buttons(fixture).First(button => button.Label == label).Activate();

    static List<Button> Rows(EditorFixture fixture) =>
        [.. Descendants(Dialog(fixture).Body).OfType<Button>().Where(button => button.HasClass("asset-picker-row"))];

    static Button Row(EditorFixture fixture, string label) =>
        Rows(fixture).FirstOrDefault(button => button.Label == label)
        ?? throw new InvalidOperationException($"the picker has no '{label}' row");

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T match) {
            return match;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
