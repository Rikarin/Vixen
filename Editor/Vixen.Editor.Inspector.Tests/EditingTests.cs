// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.Inspector.Tests;

/// <summary>A document that holds nothing, so an undo stack can be had without a project on disk.</summary>
sealed class ScratchDocument(EditorProject project) : EditorDocument(project, AssetId.Empty, "Scratch") {
    public int Saves { get; private set; }

    protected override void SaveCore() => Saves++;
}

/// <summary>Multi-object editing, mixed values, and the undo that comes with them.</summary>
public class EditingTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-inspector-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly ScratchDocument document;

    public EditingTests() {
        Directory.CreateDirectory(root);
        project = new(new ProjectPaths(root));
        document = new(project);
    }

    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    static InspectorDescriptor Water =>
        InspectorRegistry.Find(typeof(WaterMaterial))
        ?? throw new InvalidOperationException("The generator registered no descriptor for WaterMaterial.");

    static InspectorMember Member(string name) => Water.Members.Single(member => member.Name == name);

    InspectorField Field(string name, params object[] targets) =>
        new(Water, Member(name), targets, document);

    [Fact]
    public void Objects_that_agree_read_as_one_value() {
        var field = Field("Roughness", new WaterMaterial(), new WaterMaterial());

        Assert.Equal(new EditValue(0.2f, false), field.Read());
    }

    [Fact]
    public void Objects_that_disagree_read_as_mixed_rather_than_as_one_of_them() {
        var first = new WaterMaterial { Roughness = 0.1f };
        var second = new WaterMaterial { Roughness = 0.9f };

        var value = Field("Roughness", first, second).Read();

        // Not 0.1, and not an average. Showing either would let a user change something they were
        // never told about.
        Assert.True(value.IsMixed);
        Assert.Null(value.Value);
    }

    [Fact]
    public void Writing_a_mixed_field_applies_to_every_object() {
        var first = new WaterMaterial { Roughness = 0.1f };
        var second = new WaterMaterial { Roughness = 0.9f };
        var field = Field("Roughness", first, second);

        Assert.True(field.Write(0.5f));

        Assert.Equal(0.5f, first.Roughness);
        Assert.Equal(0.5f, second.Roughness);
    }

    [Fact]
    public void Undoing_a_mixed_edit_puts_each_object_back_to_its_own_value() {
        var first = new WaterMaterial { Roughness = 0.1f };
        var second = new WaterMaterial { Roughness = 0.9f };

        Field("Roughness", first, second).Write(0.5f);
        Assert.True(document.Stack.Undo());

        // The interesting half of "apply to all": one command, two different values to restore.
        Assert.Equal(0.1f, first.Roughness);
        Assert.Equal(0.9f, second.Roughness);
    }

    [Fact]
    public void The_whole_selection_is_one_entry_in_the_history() {
        var objects = Enumerable.Range(0, 20).Select(static _ => new WaterMaterial()).Cast<object>().ToArray();

        Field("Roughness", objects).Write(0.7f);

        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.Equal("Set Roughness (20)", document.Stack.UndoName.Value);
    }

    [Fact]
    public void A_drag_across_a_slider_is_one_undo_step_that_goes_back_to_before_it() {
        var material = new WaterMaterial();
        var field = Field("Roughness", material);

        foreach (var step in new[] { 0.3f, 0.4f, 0.5f, 0.6f }) {
            field.Write(step);
        }

        Assert.Equal(1, document.Stack.Depth.Value);

        document.Stack.Undo();
        Assert.Equal(0.2f, material.Roughness);
    }

    [Fact]
    public void Sealing_ends_the_run_so_the_next_edit_is_its_own_step() {
        var material = new WaterMaterial();
        var field = Field("Roughness", material);

        field.Write(0.3f);
        field.Seal();
        field.Write(0.6f);

        Assert.Equal(2, document.Stack.Depth.Value);

        document.Stack.Undo();
        Assert.Equal(0.3f, material.Roughness);
    }

    [Fact]
    public void An_edit_that_changes_nothing_records_nothing() {
        var field = Field("Roughness", new WaterMaterial());

        Assert.False(field.Write(0.2f));
        Assert.Equal(0, document.Stack.Depth.Value);
    }

    [Fact]
    public void A_value_that_does_not_coalesce_gets_an_entry_per_edit() {
        var material = new WaterMaterial();
        var field = Field("Kind", material);

        field.Write(SurfaceKind.Rough);
        field.Write(SurfaceKind.Mixed);

        // Two decisions, two undos. Merging a dropdown would take away an undo the user is entitled
        // to — the same rule EditorProperty.CoalescesEdits states.
        Assert.Equal(2, document.Stack.Depth.Value);
    }

    [Fact]
    public void A_condition_decides_who_an_edit_reaches() {
        var foamy = new WaterMaterial { UseFoam = true, FoamWidth = 1f };
        var dry = new WaterMaterial { UseFoam = false, FoamWidth = 1f };
        var field = Field("FoamWidth", foamy, dry);

        // The row is shown, because one of them would show it.
        Assert.True(field.IsVisible);

        field.Write(5f);

        Assert.Equal(5f, foamy.FoamWidth);
        Assert.Equal(1f, dry.FoamWidth);
    }

    [Fact]
    public void A_row_nobody_would_show_is_not_shown() {
        var dry = new WaterMaterial { UseFoam = false };

        Assert.False(Field("FoamWidth", dry).IsVisible);
        Assert.True(Field("DryWidth", dry).IsVisible);
    }

    [Fact]
    public void Reset_puts_a_member_back_to_what_a_fresh_instance_has() {
        var material = new WaterMaterial { Roughness = 0.9f };
        var field = Field("Roughness", material);

        Assert.True(field.IsModified);
        Assert.True(field.Reset());
        Assert.Equal(0.2f, material.Roughness);
        Assert.False(field.IsModified);
    }

    [Fact]
    public void A_read_only_member_is_not_written() {
        var material = new WaterMaterial();
        var field = Field("Version", material);

        Assert.False(field.CanWrite);
        Assert.False(field.Write(9));
        Assert.Equal(3, material.Version);
    }

    [Fact]
    public void An_unbound_field_writes_and_is_not_undoable() {
        var material = new WaterMaterial();
        var field = new InspectorField(Water, Member("Roughness"), [material]);

        Assert.True(field.Write(0.9f));
        Assert.Equal(0.9f, material.Roughness);
        Assert.Equal(0, document.Stack.Depth.Value);
    }

    [Fact]
    public void A_refreshing_field_refuses_writes_so_a_mixed_row_does_not_destroy_what_it_shows() {
        var first = new WaterMaterial { Roughness = 0.1f };
        var second = new WaterMaterial { Roughness = 0.9f };
        var field = Field("Roughness", first, second);

        // This is what a drawer's own changed event does while a mixed row is being parked at a
        // neutral position. Without the guard it would write that neutral value to everything.
        using (field.Refreshing()) {
            Assert.False(field.Write(0f));
        }

        Assert.Equal(0.1f, first.Roughness);
        Assert.Equal(0.9f, second.Roughness);
    }

    [Fact]
    public void A_struct_member_is_edited_without_the_others_being_rewritten() {
        var material = new WaterMaterial { Flow = new Vector3(1f, 2f, 3f) };
        var field = Field("Flow", material);

        field.Write(new Vector3(1f, 9f, 3f));

        Assert.Equal(new Vector3(1f, 9f, 3f), material.Flow);

        document.Stack.Undo();
        Assert.Equal(new Vector3(1f, 2f, 3f), material.Flow);
    }

    [Fact]
    public void Copy_and_paste_move_a_value_between_members_of_one_type() {
        var source = new WaterMaterial { Roughness = 0.75f };
        var target = new WaterMaterial();
        var clipboard = new PropertyClipboard();

        Assert.True(clipboard.Copy(Field("Roughness", source)));
        Assert.True(clipboard.Paste(Field("Roughness", target)));
        Assert.Equal(0.75f, target.Roughness);
    }

    [Fact]
    public void Paste_refuses_a_type_it_would_have_to_convert() {
        var clipboard = new PropertyClipboard();
        clipboard.Copy(Field("Roughness", new WaterMaterial { Roughness = 0.75f }));

        // A float into an int would be a truncation nothing told anybody about.
        Assert.False(clipboard.CanPaste(Field("Version", new WaterMaterial())));
    }

    [Fact]
    public void Copying_a_mixed_field_copies_nothing() {
        var clipboard = new PropertyClipboard();

        var mixed = Field(
            "Roughness",
            new WaterMaterial { Roughness = 0.1f },
            new WaterMaterial { Roughness = 0.9f }
        );

        Assert.False(clipboard.Copy(mixed));
        Assert.False(clipboard.HasValue);
    }

    [Fact]
    public void Revert_puts_each_object_back_to_its_own_prefab_s_value() {
        var first = new WaterMaterial { Roughness = 0.9f };
        var second = new WaterMaterial { Roughness = 0.8f };

        var prefab = new StubPrefab {
            Values = {
                [first] = 0.1f,
                [second] = 0.5f
            }
        };

        var field = new InspectorField(Water, Member("Roughness"), [first, second], document, prefab);

        Assert.True(field.IsOverridden);
        Assert.True(field.RevertToPrefab());

        // Two instances of different prefabs revert to different values, and the whole revert is one
        // undo step.
        Assert.Equal(0.1f, first.Roughness);
        Assert.Equal(0.5f, second.Roughness);

        document.Stack.Undo();

        Assert.Equal(0.9f, first.Roughness);
        Assert.Equal(0.8f, second.Roughness);
    }

    sealed class StubPrefab : IPrefabSource {
        public Dictionary<object, float> Values { get; } = [];

        public bool IsOverridden(object target, InspectorMember member) =>
            TryGetPrefabValue(target, member, out var original) && !Equals(member.GetBoxed(target), original);

        public bool TryGetPrefabValue(object target, InspectorMember member, out object? value) {
            if (member.Name == "Roughness" && Values.TryGetValue(target, out var original)) {
                value = original;
                return true;
            }

            value = null;
            return false;
        }
    }
}
