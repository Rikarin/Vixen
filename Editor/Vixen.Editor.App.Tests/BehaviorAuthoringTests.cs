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

/// <summary>A behaviour somebody can put on an entity from the inspector.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Until this existed a <c>Behavior</c> could only be attached in code.</b> The Add
///         Component menu is built from a registry, a scene entry is an alias resolved against one,
///         and behaviours were in neither — so the surface doc 04 calls "the one most users touch"
///         was the one the editor could not reach.
///     </para>
///     <para>
///         Everything here goes through <c>IComponentBridge</c>, which is the point: the menu, the
///         foldout, the rows, the undo and the remove button are the component path, unchanged.
///     </para>
/// </remarks>
public class BehaviorAuthoringTests {
    /// <remarks>
    ///     ⚠ Registered by hand rather than by the generator, because this assembly does not run the
    ///     engine's — which is the seam a project's own code will use and is not what this is testing.
    /// </remarks>
    static BehaviorAuthoringTests() => SceneBehaviorRegistry.Register<PatrolBehavior>();

    static ComponentsView Components(EditorSession editor) {
        editor.Open("inspector");

        return Descendants(editor.Panel("inspector")).OfType<ComponentsView>().FirstOrDefault()
            ?? throw editor.Fail("the inspector has no components section");
    }

    static EditorSession Selected() {
        var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");
        editor.Open("inspector");
        editor.Frames(2);

        return editor;
    }

    [Fact]
    public void A_described_behaviour_is_offered_beside_the_components() {
        using var editor = Selected();

        var offered = Offered(editor);

        // Written out, exactly as a component's is — a behaviour reaches the menu through the same
        // bridge, so it gets the same treatment with nothing said about it.
        Assert.Contains("Patrol Behavior", offered);
        Assert.Contains("Camera", offered);
    }

    /// <summary>
    ///     ⚠ <b>Doc 36 § D5's exit criterion.</b> The list used to come out in registration order,
    ///     which put every component above every behaviour — so somebody adding a script had to know
    ///     it was a script before they could find it. One sorted list is the fix, and the assertion
    ///     that matters is that the behaviour is <i>interleaved</i> rather than merely present.
    /// </summary>
    [Fact]
    public void The_menu_is_one_list_sorted_by_name() {
        using var editor = Selected();

        var offered = Offered(editor);

        Assert.Equal(offered.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase), offered);

        var script = offered.ToList().IndexOf("Patrol Behavior");

        Assert.True(script > 0, "the behaviour should not be first — 'Camera' sorts above it");
        Assert.True(script < offered.Count - 1, "nor last, which is where registration order put it");
    }

    /// <summary>
    ///     ⚠ <b>The kind is a subtitle rather than a heading, and only the script carries one.</b> Two
    ///     sections would restore exactly the ordering the sort removes; a column of the word
    ///     "Component" down the right of a list where it is the default is noise that makes the one
    ///     distinction worth seeing harder to see.
    /// </summary>
    [Fact]
    public void Only_the_script_says_what_kind_it_is() {
        using var editor = Selected();

        Press(editor.Panel("inspector"), "Add Component");

        var menu = Descendants(editor.Document.Root)
            .OfType<ContextMenu>()
            .FirstOrDefault(candidate => candidate.IsOpen)
            ?? throw editor.Fail("the Add Component menu did not open");

        var script = menu.Items.First(item => item.Label == "Patrol Behavior");
        var component = menu.Items.First(item => item.Label == "Camera");

        Assert.Equal("Script", script.Detail.Text);
        Assert.DoesNotContain(Descendants(component).OfType<TextBlock>(), text => text.HasClass("menu-detail"));

        menu.Close(CloseReason.Code);
        editor.Settle();
    }

    [Fact]
    public void Choosing_one_attaches_it_and_draws_its_fields() {
        using var editor = Selected();

        var entity = editor.Scene.Selection[0];

        Assert.Null(editor.Scene.Behaviors.Get<PatrolBehavior>(entity));

        Choose(editor, "Patrol Behavior");

        var attached = editor.Scene.Behaviors.Get<PatrolBehavior>(entity);

        Assert.NotNull(attached);

        // Its constructor's defaults, which is the one place a behaviour is easier than a component:
        // a zeroed struct needs `ComponentsView.Initial` and a field initialiser does not.
        Assert.Equal(3f, attached.Speed);

        var section = Components(editor).Sections.Single(fold => fold.Label == "Patrol Behavior");
        var rows = Descendants(section).OfType<InspectorRow>().Select(row => row.Field.Member.Name).ToList();

        Assert.Contains("Speed", rows);
        Assert.Contains("Distance", rows);

        // ⚠ And none of the base's plumbing. The reflection descriptor deliberately does not honour
        // [DataMemberIgnore], so without [EditorVisible(false)] beside it a foldout would show the
        // store's own fields and a second copy of the entity's position.
        Assert.DoesNotContain("World", rows);
        Assert.DoesNotContain("Position", rows);
        Assert.DoesNotContain("IsAwake", rows);
    }

    /// <summary>
    ///     ⚠ <b>The trap a reference type sets for a panel built on boxes.</b> The rows edit what
    ///     <c>Read</c> handed them and the command records what it read as the "before" — so a bridge
    ///     that returned the live instance would have both pointing at one object, and every undo
    ///     would restore the value the edit had already written. <c>ISceneBehaviorBinder.Copy</c> is
    ///     what keeps the two apart.
    /// </summary>
    [Fact]
    public void Editing_a_field_is_one_undo_step_that_actually_goes_back() {
        using var editor = Selected();

        var entity = editor.Scene.Selection[0];

        Choose(editor, "Patrol Behavior");

        var section = Components(editor).Sections.Single(fold => fold.Label == "Patrol Behavior");
        var row = Descendants(section).OfType<InspectorRow>().Single(candidate => candidate.Field.Member.Name == "Speed");

        Assert.True(row.Field.Write(12f));
        editor.Settle();

        Assert.Equal(12f, editor.Scene.Behaviors.Get<PatrolBehavior>(entity)!.Speed);

        editor.Run("edit.undo");
        editor.Settle();

        Assert.Equal(3f, editor.Scene.Behaviors.Get<PatrolBehavior>(entity)!.Speed);

        editor.Run("edit.redo");
        editor.Settle();

        Assert.Equal(12f, editor.Scene.Behaviors.Get<PatrolBehavior>(entity)!.Speed);
    }

    /// <summary>
    ///     ⚠ <b>Removing records what was there, so undo puts the values back and not just the
    ///     foldout</b> — the rule <c>SetComponentCommand</c> already states, and it holds for a
    ///     behaviour because the command is the same one.
    /// </summary>
    [Fact]
    public void Removing_one_can_be_undone_with_what_was_in_it() {
        using var editor = Selected();

        var entity = editor.Scene.Selection[0];

        Choose(editor, "Patrol Behavior");

        var section = Components(editor).Sections.Single(fold => fold.Label == "Patrol Behavior");
        var row = Descendants(section).OfType<InspectorRow>().Single(candidate => candidate.Field.Member.Name == "Distance");

        Assert.True(row.Field.Write(42f));
        editor.Settle();

        Press(section, "Remove Component");
        editor.Settle();

        Assert.Null(editor.Scene.Behaviors.Get<PatrolBehavior>(entity));

        editor.Run("edit.undo");
        editor.Settle();

        var restored = editor.Scene.Behaviors.Get<PatrolBehavior>(entity);

        Assert.NotNull(restored);
        Assert.Equal(42f, restored.Distance);
    }

    /// <summary>
    ///     ⚠ <b>Add, remove, add — which is what undo and redo do — leaves one behaviour and not
    ///     three.</b> <c>BehaviorStore.Destroy</c> queues for a lifecycle drain that an editor never
    ///     runs, so the authoring path is <c>Remove</c>, which detaches now.
    /// </summary>
    [Fact]
    public void Adding_and_removing_repeatedly_leaves_one_behaviour() {
        using var editor = Selected();

        var entity = editor.Scene.Selection[0];

        for (var round = 0; round < 3; round++) {
            Choose(editor, "Patrol Behavior");

            var section = Components(editor).Sections.Single(fold => fold.Label == "Patrol Behavior");

            Press(section, "Remove Component");
            editor.Settle();
        }

        Choose(editor, "Patrol Behavior");

        Assert.Single(editor.Scene.Behaviors.AllOn(entity).ToArray());
    }

    static IReadOnlyList<string> Offered(EditorSession editor) {
        Press(editor.Panel("inspector"), "Add Component");

        var menu = Descendants(editor.Document.Root)
            .OfType<ContextMenu>()
            .FirstOrDefault(candidate => candidate.IsOpen)
            ?? throw editor.Fail("the Add Component menu did not open");

        List<string> offered = [.. menu.Items.Where(item => !item.Disabled).Select(item => item.Label ?? "")];

        menu.Close(CloseReason.Code);
        editor.Settle();

        return offered;
    }

    static void Choose(EditorSession editor, string component) {
        Press(editor.Panel("inspector"), "Add Component");

        var menu = Descendants(editor.Document.Root)
            .OfType<ContextMenu>()
            .FirstOrDefault(candidate => candidate.IsOpen)
            ?? throw editor.Fail("the Add Component menu did not open");

        (menu.Items.FirstOrDefault(item => item.Label == component)
            ?? throw editor.Fail($"the menu does not offer '{component}'")).Activate();

        editor.Settle();
    }

    static void Press(UiElement root, string label) =>
        Descendants(root).OfType<ButtonBase>().First(button => button.Label == label).Activate();

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}

/// <summary>A behaviour a scene may name, standing in for a game's own.</summary>
[DataContract("PatrolBehavior")]
public sealed class PatrolBehavior : Behavior {
    /// <summary>How fast it walks.</summary>
    public float Speed { get; set; } = 3f;

    /// <summary>How far it goes before turning round.</summary>
    public float Distance { get; set; } = 10f;
}
