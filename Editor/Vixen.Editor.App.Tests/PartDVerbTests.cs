// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20 § Part D's selection and transform verbs, which had no command id at all.</summary>
/// <remarks>
///     ⚠ <b>Registration is the first assertion because absence was the defect.</b> Nine of Part D's
///     entries were in no menu, no palette entry and not even in the <c>Planned</c> list — so nothing
///     in the editor said they had ever been intended, and from outside that is indistinguishable
///     from the feature never having been thought of. A test that only exercised the behaviour would
///     pass over a command nothing can reach.
/// </remarks>
public class PartDVerbTests {
    /// <summary>Every verb § Selection and § Transform name, as an id the editor knows.</summary>
    static readonly string[] PartDIds = [
        "edit.isolate",
        "edit.select-by-name",
        "edit.select-by-type",
        "edit.save-selection-set",
        "edit.recall-selection-set",
        "entity.reset-transform",
        "entity.copy-transform",
        "entity.paste-transform",
        "entity.align",
        "entity.distribute",
        "entity.relative-transform"
    ];

    /// <summary>The same list, as a theory's rows.</summary>
    public static TheoryData<string> PartD => [.. PartDIds];

    [Theory]
    [MemberData(nameof(PartD))]
    public void Every_verb_part_d_names_is_registered(string id) {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.Shell.Commands.TryGet(id, out _), id + " is in Part D and is not registered");
    }

    /// <summary>And each one is on a menu, because the palette is not where a person looks first.</summary>
    /// <remarks>
    ///     ⚠ <b>A menu entry naming a command nothing registered is skipped in silence.</b> That is
    ///     what lets the shell's Edit menu name an application's verbs — and it means a typo in an id
    ///     costs a line off the menu and no error anywhere, which is exactly the shape of the defect
    ///     this suite exists for.
    /// </remarks>
    [Fact]
    public void Every_verb_part_d_names_has_a_line_on_a_menu() {
        using var fixture = EditorSession.Start();

        var ids = Lines(fixture).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(ids);

        foreach (var id in PartDIds) {
            Assert.Contains(id, ids);
        }
    }

    /// <summary>Isolate hides what is not selected, and puts back exactly what was hidden before.</summary>
    [Fact]
    public void Isolate_keeps_the_selection_with_its_ancestors_and_children_and_restores_what_was_hidden() {
        using var fixture = EditorSession.Start();

        var shelf = fixture.Scene.Add("Part D Shelf", LocalTransform.Identity);
        var crate = fixture.Scene.Add("Part D Crate", LocalTransform.Identity);
        var nail = fixture.Scene.Add("Part D Nail", LocalTransform.Identity);
        var other = fixture.Scene.Add("Part D Other", LocalTransform.Identity);

        fixture.Scene.Reparent(crate, shelf);
        fixture.Scene.Reparent(nail, crate);

        // Hidden on purpose beforehand, which is what leaving isolate must not undo.
        fixture.Scene.SetHidden(other, true);
        fixture.Scene.Selection.Set([crate]);
        fixture.Settle();

        fixture.Run("edit.isolate").Settle();

        Assert.False(fixture.Scene.IsHidden(crate));
        Assert.False(fixture.Scene.IsHidden(shelf), "the parent of an isolated entity hides it too");
        Assert.False(fixture.Scene.IsHidden(nail), "isolating a crate has to keep what is inside it");
        Assert.True(fixture.Scene.IsHidden(other));

        var isolated = fixture.Scene.Entities.Count(fixture.Scene.IsHidden);
        Assert.True(isolated > 0, "isolate hid nothing at all");

        fixture.Run("edit.isolate").Settle();

        // ⚠ Back to what was hidden before, not to nothing hidden.
        Assert.True(fixture.Scene.IsHidden(other));
        Assert.False(fixture.Scene.IsHidden(shelf));
        Assert.Equal(1, fixture.Scene.Entities.Count(fixture.Scene.IsHiddenDirectly));
    }

    /// <summary>Select By Name is the outliner's filter as a verb.</summary>
    [Fact]
    public void Select_by_name_takes_everything_whose_name_contains_the_text() {
        using var fixture = EditorSession.Start();

        fixture.Scene.Add("Part D Lamp A", LocalTransform.Identity);
        fixture.Scene.Add("Part D Lamp B", LocalTransform.Identity);
        fixture.Scene.Add("Part D Bucket", LocalTransform.Identity);
        fixture.Settle();

        fixture.Run("edit.select-by-name").Settle();
        Assert.True(fixture.IsAsking);

        Type(fixture, "part d lamp");
        fixture.Answer("Select");

        Assert.Equal(2, fixture.Scene.Selection.Count);

        foreach (var entity in fixture.Scene.Selection) {
            Assert.Contains("Lamp", fixture.Scene.NameOf(entity), StringComparison.Ordinal);
        }
    }

    /// <summary>Reset, Copy and Paste Transform, and one undo step for the whole selection.</summary>
    [Fact]
    public void A_transform_is_copied_onto_a_multi_selection_as_one_undoable_step() {
        using var fixture = EditorSession.Start();

        var source = fixture.Scene.Add("Part D Source", LocalTransform.At(new Vector3(3f, 4f, 5f)));
        var first = fixture.Scene.Add("Part D First", LocalTransform.At(new Vector3(1f, 0f, 0f)));
        var second = fixture.Scene.Add("Part D Second", LocalTransform.At(new Vector3(2f, 0f, 0f)));

        fixture.Scene.Selection.Set([source]);
        fixture.Settle();

        Assert.False(fixture.CanRun("entity.paste-transform"), "there is nothing on the clipboard yet");

        fixture.Run("entity.copy-transform").Settle();
        Assert.True(fixture.CanRun("entity.paste-transform"));

        fixture.Scene.Selection.Set([first, second]);
        fixture.Settle();

        var depth = fixture.Scene.Stack.Depth.Value;

        fixture.Run("entity.paste-transform").Settle();

        Assert.Equal(new Vector3(3f, 4f, 5f), Position(fixture, first));
        Assert.Equal(new Vector3(3f, 4f, 5f), Position(fixture, second));

        // ⚠ One step for two entities. Two would be the shape of every "undo did not undo what I
        // did" report.
        Assert.Equal(depth + 1, fixture.Scene.Stack.Depth.Value);

        fixture.Run("edit.undo").Settle();

        Assert.Equal(new Vector3(1f, 0f, 0f), Position(fixture, first));
        Assert.Equal(new Vector3(2f, 0f, 0f), Position(fixture, second));

        fixture.Scene.Selection.Set([first]);
        fixture.Settle();
        fixture.Run("entity.reset-transform").Settle();

        Assert.Equal(Vector3.Zero, Position(fixture, first));
    }

    /// <summary>Align moves everything onto the last-selected entity's line; Distribute spaces them.</summary>
    /// <remarks>
    ///     ⚠ <b>The ends do not move, which is the property that makes Distribute a distribute rather
    ///     than a re-lay-out.</b> A version that moved the outermost objects would change the extent
    ///     of a row somebody had already placed.
    /// </remarks>
    [Fact]
    public void Align_uses_the_primary_and_distribute_leaves_the_two_ends_where_they_are() {
        using var fixture = EditorSession.Start();

        var left = fixture.Scene.Add("Part D Left", LocalTransform.At(new Vector3(0f, 0f, 0f)));
        var middle = fixture.Scene.Add("Part D Middle", LocalTransform.At(new Vector3(1f, 7f, 0f)));
        var right = fixture.Scene.Add("Part D Right", LocalTransform.At(new Vector3(9f, 0f, 0f)));

        // Selected last, so it is `Selection.Primary` and the one everything aligns onto.
        fixture.Scene.Selection.Set([left, right, middle]);
        fixture.Settle();

        fixture.Run("entity.align").Settle();
        fixture.Answer("Y");

        Assert.Equal(7f, Position(fixture, left).Y, 3);
        Assert.Equal(7f, Position(fixture, right).Y, 3);
        Assert.Equal(7f, Position(fixture, middle).Y, 3);

        fixture.Run("entity.distribute").Settle();
        fixture.Answer("X");

        Assert.Equal(0f, Position(fixture, left).X, 3);
        Assert.Equal(9f, Position(fixture, right).X, 3);
        Assert.Equal(4.5f, Position(fixture, middle).X, 3);
    }

    /// <summary>A named selection comes back, and says so when part of it has been deleted.</summary>
    [Fact]
    public void A_selection_set_is_saved_by_name_and_recalled_without_what_has_been_deleted() {
        using var fixture = EditorSession.Start();

        var first = fixture.Scene.Add("Part D Kept", LocalTransform.Identity);
        var second = fixture.Scene.Add("Part D Doomed", LocalTransform.Identity);

        fixture.Scene.Selection.Set([first, second]);
        fixture.Settle();

        Assert.False(fixture.CanRun("edit.recall-selection-set"), "there is no set to recall yet");

        fixture.Run("edit.save-selection-set").Settle();
        Type(fixture, "Part D Set");
        fixture.Answer("Save");

        Assert.True(fixture.CanRun("edit.recall-selection-set"));

        fixture.Scene.Delete([second]);
        fixture.Scene.Selection.Clear();
        fixture.Settle();

        fixture.Run("edit.recall-selection-set").Settle();
        fixture.Answer("Part D Set");

        Assert.Equal(first, Assert.Single(fixture.Scene.Selection.ToList()));
    }

    static Vector3 Position(EditorSession fixture, Entity entity) =>
        fixture.Scene.World.Read<LocalTransform>(entity).Position;

    static void Type(EditorSession fixture, string text) {
        var dialog = fixture.Shell.Dialogs.Current ?? throw new InvalidOperationException("Nothing asked anything.");
        var field = Descendants(dialog).OfType<TextBox>().First();

        field.Value = text;
        fixture.Settle();
    }

    static IEnumerable<string> Lines(EditorSession fixture) {
        foreach (var menu in fixture.Shell.Menus.Menus) {
            foreach (var id in Ids(menu)) {
                yield return id;
            }
        }

        static IEnumerable<string> Ids(MenuGroup group) {
            foreach (var entry in group.Entries) {
                switch (entry) {
                    case MenuCommand command:
                        yield return command.CommandId;
                        break;

                    case MenuSubmenu submenu:
                        foreach (var nested in Ids(submenu.Group)) {
                            yield return nested;
                        }

                        break;

                    case MenuDynamic dynamic:
                        foreach (var id in dynamic.CommandIds()) {
                            yield return id;
                        }

                        break;
                }
            }
        }
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
