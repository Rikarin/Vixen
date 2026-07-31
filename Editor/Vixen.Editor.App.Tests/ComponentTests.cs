// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What is on an entity, shown, added and taken off.</summary>
/// <remarks>
///     ⚠ <b>The two halves that made this the last item in doc 20's B1.</b> The ECS could not say
///     what an entity carries, because an archetype knows dense ids handed out in first-touch order;
///     and the inspector could not draw a component, because no runtime component carries
///     <c>[Inspector]</c> and none should. The rows here come from the <c>[DataContract]</c>
///     description the serializer generates, which means a game's components appear with nothing
///     asked of the game.
/// </remarks>
public class ComponentTests {
    static Entity Named(EditorSession editor, string name) =>
        editor.Scene.Entities.First(entity => editor.Scene.NameOf(entity) == name);

    static ComponentsView Components(EditorSession editor) {
        editor.Open("inspector");

        return Descendants(editor.Panel("inspector")).OfType<ComponentsView>().FirstOrDefault()
            ?? throw editor.Fail("the inspector has no components section");
    }

    static Expander Section(EditorSession editor, string name) =>
        Components(editor).Sections.FirstOrDefault(fold => fold.Label == name)
        ?? throw editor.Fail(
            $"no '{name}' component is shown. Showing: "
            + string.Join(", ", Components(editor).Sections.Select(fold => fold.Label ?? "?"))
            + "."
        );

    /// <summary>
    ///     ⚠ A serialization descriptor and an inspector one are the same information under different
    ///     names, and this is the assertion that the adapter reads it rather than approximating it.
    /// </summary>
    [Fact]
    public void A_data_contract_type_is_drawable_with_no_editor_annotation() {
        Assert.Null(InspectorRegistry.Find(typeof(Light)));

        var descriptor = ReflectedDescriptor.For(typeof(Light))
            ?? throw new InvalidOperationException("Light has no reflected descriptor");

        Assert.Equal(typeof(Light), descriptor.Type);
        Assert.Contains(descriptor.Members, member => member.Name == "Intensity");
        Assert.Contains(descriptor.Members, member => member.Name == "Colour");
    }

    /// <summary>
    ///     ⚠ <b>The generator went out of its way for this and the feature rests on it.</b> The
    ///     obvious setter unboxes into a temporary, which the language will not compile and which
    ///     would silently write to a copy if it did. `Unsafe.Unbox` gives a reference into the box.
    /// </summary>
    [Fact]
    public void A_member_can_be_written_through_a_boxed_struct() {
        var descriptor = ReflectedDescriptor.For(typeof(Light))!;
        var member = descriptor.Members.Single(candidate => candidate.Name == "Intensity");

        object box = new Light { Intensity = 1f };

        member.SetBoxed(box, 7.5f);

        Assert.Equal(7.5f, ((Light) box).Intensity);
        Assert.Equal(7.5f, member.GetBoxed(box));
    }

    [Fact]
    public void An_entity_shows_the_components_it_carries_and_not_the_others() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");

        var shown = Components(editor).Sections.Select(fold => fold.Label).ToList();

        Assert.Contains("Mesh Shape", shown);
        Assert.DoesNotContain("Light", shown);

        editor.ClickRow(editor.Hierarchy, "Directional Light");

        shown = Components(editor).Sections.Select(fold => fold.Label).ToList();

        Assert.Contains("Light", shown);
        Assert.DoesNotContain("Mesh Shape", shown);
    }

    /// <summary>
    ///     ⚠ <b>One undo step for the whole component, not one per field.</b> The rows edit a boxed
    ///     copy; recording each would put a step on the stack that undoes a change to something
    ///     nobody can see, and the visible change would belong to a different step.
    /// </summary>
    [Fact]
    public void Editing_a_component_reaches_the_entity_and_is_one_undo() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ClickRow(editor.Hierarchy, "Directional Light");

        var light = Named(editor, "Directional Light");
        var before = editor.Scene.World.Read<Light>(light).Intensity;
        var steps = Depth(editor);

        var row = Rows(Section(editor, "Light")).Single(candidate => candidate.Field.Member.Name == "Intensity");

        Assert.True(row.Field.Write(12f));
        editor.Settle();

        Assert.Equal(12f, editor.Scene.World.Read<Light>(light).Intensity);
        Assert.Equal(steps + 1, Depth(editor));

        editor.Run("edit.undo");
        Assert.Equal(before, editor.Scene.World.Read<Light>(light).Intensity);

        editor.Run("edit.redo");
        Assert.Equal(12f, editor.Scene.World.Read<Light>(light).Intensity);
    }

    [Fact]
    public void A_component_can_be_added_from_the_menu_and_undone() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");

        var crate = Named(editor, "Crate");

        Assert.False(editor.Scene.World.Has<Camera>(crate));

        Choose(editor, "Camera");

        Assert.True(editor.Scene.World.Has<Camera>(crate));
        Assert.Contains("Camera", Components(editor).Sections.Select(fold => fold.Label));

        editor.Run("edit.undo");

        Assert.False(editor.Scene.World.Has<Camera>(crate));

        // ⚠ And the panel followed the undo. Nothing was clicked, so a view that rebuilt only after
        // its own commands would still be drawing a component that is gone.
        Assert.DoesNotContain("Camera", Components(editor).Sections.Select(fold => fold.Label));
    }

    /// <summary>
    ///     ⚠ <b>Undo puts the values back, not just the column.</b> An undo that restored a zeroed
    ///     component would be worse than none: the hierarchy would look right and the light would be
    ///     black.
    /// </summary>
    [Fact]
    public void Removing_a_component_can_be_undone_with_what_was_in_it() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ClickRow(editor.Hierarchy, "Directional Light");

        var light = Named(editor, "Directional Light");

        editor.Scene.World.Set(light, new Light { Kind = LightKind.Directional, Intensity = 3.5f, Range = 42f });
        editor.Settle();

        Press(Section(editor, "Light"), "Remove Component");
        editor.Settle();

        Assert.False(editor.Scene.World.Has<Light>(light));

        editor.Run("edit.undo");

        Assert.True(editor.Scene.World.Has<Light>(light));

        var restored = editor.Scene.World.Read<Light>(light);

        Assert.Equal(3.5f, restored.Intensity);
        Assert.Equal(42f, restored.Range);
    }

    /// <summary>
    ///     ⚠ The menu is dynamic over what the entity does *not* have, and both halves move — the
    ///     registered set and the last click.
    /// </summary>
    [Fact]
    public void The_add_menu_offers_only_what_is_not_already_there() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ClickRow(editor.Hierarchy, "Directional Light");

        Assert.DoesNotContain("Light", Offered(editor));
        Assert.Contains("Camera", Offered(editor));

        Choose(editor, "Camera");

        Assert.DoesNotContain("Camera", Offered(editor));
    }

    /// <summary>
    ///     ⚠ <b>The menu offered <c>Camera</c> and nothing else, in a build where the viewport was
    ///     drawing lights and meshes.</b> A component registers itself from a
    ///     <c>[ModuleInitializer]</c>, and the runtime runs one when the declaring module is first
    ///     touched — so a registry read while the editor was being constructed saw
    ///     <c>Vixen.Engine</c> and whatever else happened to have loaded, which was nothing.
    ///     <c>ComponentsView.Prime</c> is what makes the answer the same on the first frame as on the
    ///     thousandth, and this test asks for it with no editor session at all, because a session is
    ///     precisely the thing that used to load the subsystems and hide the bug.
    /// </summary>
    [Fact]
    public void The_subsystems_that_declare_components_are_loaded_before_the_list_is_read() {
        var offered = ComponentsView.Default().Select(bridge => bridge.ComponentType).ToList();

        Assert.Contains(typeof(Camera), offered);
        Assert.Contains(typeof(Light), offered);
        Assert.Contains(typeof(MeshRenderable), offered);
        Assert.Contains(typeof(PrimitiveShape), offered);

        // ⚠ And the audio emitter, which is the other half. Its components carried neither
        // [Component] nor [DataContract] and Vixen.Audio did not run the registration generator, so
        // referencing the package would not have helped: a sound could be put on an entity in code
        // and never from the editor, and never saved into a scene.
        Assert.Contains(typeof(AudioSource), offered);
        Assert.Contains(typeof(AudioSpatial), offered);
        Assert.Contains(typeof(AudioListenerComponent), offered);
    }

    /// <summary>
    ///     ⚠ <b>The list is a view over the registry and not a copy of it.</b>
    ///     <c>ComponentsView.Offer</c> says the offered set is "everything registered minus what is
    ///     already on this entity" and that "both halves move" — and while the bridges were built
    ///     once into a <c>List</c>, only the second one did. A component whose assembly loads after
    ///     the editor is up — a plugin's, a project's own — could be drawn in the inspector and still
    ///     be missing from the menu that adds it.
    /// </summary>
    [Fact]
    public void A_component_registered_after_the_list_was_built_is_still_offered() {
        var bridges = ComponentsView.Default();
        var before = bridges.Count;

        SceneComponentRegistry.Register<LateComponent>();

        Assert.Equal(before + 1, bridges.Count);
        Assert.Contains(bridges, bridge => bridge.ComponentType == typeof(LateComponent));
    }

    [Fact]
    public void An_asset_selection_shows_no_components_at_all() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ClickRow(editor.Hierarchy, "Directional Light");

        Assert.NotEmpty(Components(editor).Sections);

        editor.Open("project");
        editor.ExpandAll(editor.Assets);
        editor.ClickRow(editor.Assets, "Main.vxscene");

        Assert.Empty(Components(editor).Sections);
    }

    static IReadOnlyList<string> Offered(EditorSession editor) {
        Press(editor, "Add Component");

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
        Press(editor, "Add Component");

        var menu = Descendants(editor.Document.Root)
            .OfType<ContextMenu>()
            .FirstOrDefault(candidate => candidate.IsOpen)
            ?? throw editor.Fail("the Add Component menu did not open");

        (menu.Items.FirstOrDefault(item => item.Label == component)
            ?? throw editor.Fail($"the menu does not offer '{component}'")).Activate();

        editor.Settle();
    }

    static void Press(EditorSession editor, string label) => Press(editor.Panel("inspector"), label);

    static void Press(UiElement root, string label) =>
        Descendants(root).OfType<ButtonBase>().First(button => button.Label == label).Activate();

    static int Depth(EditorSession editor) {
        var count = 0;

        while (editor.Scene.Stack.CanUndo.Value && count < 500) {
            editor.Scene.Stack.Undo();
            count++;
        }

        for (var index = 0; index < count; index++) {
            editor.Scene.Stack.Redo();
        }

        return count;
    }

    static IEnumerable<InspectorRow> Rows(UiElement element) => Descendants(element).OfType<InspectorRow>();

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}

/// <summary>A component registered by hand after the editor is up, standing in for a plugin's.</summary>
/// <remarks>
///     ⚠ <b>At namespace scope and internal.</b> The serialization generator writes a serializer for
///     the type it is given, and a type nested inside a test class is one it would have to name
///     through its containing class — which is a shape no component in the engine has, so it is not
///     the shape to prove anything with.
/// </remarks>
[Component]
[DataContract("ComponentTestsLate")]
struct LateComponent {
    /// <summary>Something for a row to draw.</summary>
    public float Value = 1f;

    public LateComponent() {
    }
}
