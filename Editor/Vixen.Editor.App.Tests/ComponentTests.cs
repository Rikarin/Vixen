// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Terrain;
using Vixen.Rendering.Water;
using Vixen.Ui;
using Vixen.Water.Physics;
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

        // ⚠ "Primitive Shape" and not "PrimitiveShape": a foldout is labelled with the component's
        // written-out name, and the alias the registry keys it under is a serialization identifier
        // that has no business being read by anybody. See `IComponentBridge.DisplayName` — this test
        // asked for "Mesh Shape" until the component was renamed out from under it, which is how the
        // label came to be the alias in the first place.
        Assert.Contains("Primitive Shape", shown);
        Assert.DoesNotContain("Light", shown);

        editor.ClickRow(editor.Hierarchy, "Directional Light");

        shown = Components(editor).Sections.Select(fold => fold.Label).ToList();

        Assert.Contains("Light", shown);
        Assert.DoesNotContain("Primitive Shape", shown);
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
    ///     ⚠ <b>A component's identity and its label are two strings, and only one of them is for
    ///     reading.</b> <c>Name</c> is the serializer's alias — what a <c>.vxscene</c> carries and
    ///     what the preferences file keys an arrangement under — and <c>DisplayName</c> is that
    ///     written out. The panel showed the alias everywhere for a while, so an inspector read
    ///     <c>PrimitiveShape</c> one row above a member the same panel had written out as
    ///     <c>Cone Inner Angle</c>.
    /// </summary>
    [Fact]
    public void A_component_is_offered_and_labelled_by_a_name_a_person_would_write() {
        AuthoringSubsystems.Load();

        var bridges = ComponentsView.Default().ToList();

        var shape = bridges.Single(bridge => bridge.ComponentType == typeof(PrimitiveShape));
        var source = bridges.Single(bridge => bridge.ComponentType == typeof(AudioSource));

        Assert.Equal("PrimitiveShape", shape.Name);
        Assert.Equal("Primitive Shape", shape.DisplayName);

        Assert.Equal("AudioSource", source.Name);
        Assert.Equal("Audio Source", source.DisplayName);

        // ⚠ The listener's alias drops the `Component` suffix its CLR type needs, because
        // `AudioListener` is taken in that assembly and is not taken in a scene file. Written out by
        // the same rule, the suffix would have been read by everybody: "Audio Listener Component".
        var listener = bridges.Single(bridge => bridge.ComponentType == typeof(AudioListenerComponent));

        Assert.Equal("AudioListener", listener.Name);
        Assert.Equal("Audio Listener", listener.DisplayName);

        // A one-word component is the same either way, which is why `Light` never showed the fault
        // that `PrimitiveShape` did.
        var light = bridges.Single(bridge => bridge.ComponentType == typeof(Light));

        Assert.Equal(light.Name, light.DisplayName);
    }

    /// <summary>The Add Component menu offers the written-out name, not the alias.</summary>
    [Fact]
    public void The_add_menu_offers_the_written_out_name() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ClickRow(editor.Hierarchy, "Directional Light");

        var offered = Offered(editor);

        Assert.Contains("Primitive Shape", offered);
        Assert.DoesNotContain("PrimitiveShape", offered);
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
    ///     <c>ComponentsView.Prime</c> was what made the answer the same on the first frame as on the
    ///     thousandth, and this test asked for it with no editor session at all, because a session is
    ///     precisely the thing that used to load the subsystems and hide the bug.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>What is asserted here is that the components are registered, and no longer that
    ///     anything loaded them on time.</b> Doc 36 § D5 retired <c>Prime</c>: the touching is
    ///     <see cref="AuthoringAssembly" /> now, and <c>ComponentsView.Default</c> does it only for
    ///     the declarations the registry it is handed holds — which the no-argument overload used
    ///     here has none of. That left this passing on an accident: every <c>typeof</c> below is in
    ///     the method's own body, so the JIT resolves the token — and loads the assembly — before the
    ///     first statement runs, where a <c>typeof</c> inside a lambda is resolved later and does not.
    ///     <c>A_component_is_offered_and_labelled_by_a_name_a_person_would_write</c> is the same test
    ///     with its <c>typeof</c>s in lambdas, and it failed alone for years' worth of runs. The
    ///     establishment is written down rather than left to that, and the on-time claim is
    ///     <see cref="Every_subsystem_the_editor_draws_for_is_declared_to_it" />'s.
    /// </remarks>
    [Fact]
    public void The_subsystems_that_declare_components_are_loaded_before_the_list_is_read() {
        AuthoringSubsystems.Load();

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
    ///     ⚠ <b>The claim the test above stopped being able to make: that somebody named each
    ///     subsystem, so its components are registered before anything asks.</b>
    /// </summary>
    /// <remarks>
    ///     <c>EditorApplication.BuiltInSubsystems</c> is a list of <see cref="AuthoringAssembly" />
    ///     registered and touched in the constructor, before the scene file is read — and a line
    ///     deleted from it is a component that vanishes from Add ▸ on a machine where nothing else
    ///     happened to touch that assembly, which is a failure no test in this process could see,
    ///     because the test process has touched all of them. So this asserts the <i>declaration</i>
    ///     rather than the load: what a person wrote down is checkable, and this is where it is
    ///     checked.
    /// </remarks>
    [Fact]
    public void Every_subsystem_the_editor_draws_for_is_declared_to_it() {
        using var editor = EditorSession.Start();

        var declared = editor.Extensions.All<AuthoringAssembly>()
            .Select(entry => entry.Marker.Assembly)
            .ToHashSet();

        Assert.Contains(typeof(Camera).Assembly, declared); // Vixen.Engine
        Assert.Contains(typeof(Light).Assembly, declared); // Vixen.Rendering
        Assert.Contains(typeof(AudioSource).Assembly, declared); // Vixen.Audio
        Assert.Contains(typeof(TerrainComponent).Assembly, declared); // Vixen.Rendering.Terrain
        Assert.Contains(typeof(WaterZoneComponent).Assembly, declared); // Vixen.Rendering.Water
        Assert.Contains(typeof(BuoyancyBody).Assembly, declared); // Vixen.Water.Physics
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

    /// <summary>
    ///     ⚠ <b>The payoff, end to end: a component added from the menu holds what its type says a
    ///     fresh one holds.</b>
    /// </summary>
    /// <remarks>
    ///     A zeroed camera has a zero far plane and every matrix built from it is degenerate, so this
    ///     is the difference between adding a camera and adding a bug report. It went through a
    ///     hardcoded chain of four types in this assembly until <c>IDefaultComponent</c>; the value is
    ///     the same and the reach is not.
    /// </remarks>
    [Fact]
    public void A_component_added_from_the_menu_holds_its_declared_default() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");

        var crate = Named(editor, "Crate");

        Choose(editor, "Camera");

        var camera = editor.Scene.World.Read<Camera>(crate);

        Assert.Equal(Camera.Perspective.FarPlane, camera.FarPlane);
        Assert.NotEqual(0f, camera.FarPlane);
    }

    /// <summary>
    ///     ⚠ <b>The component this mechanism was built for.</b> A zeroed <c>VirtualCamera</c> is
    ///     <i>disabled</i> as well as lensless, so a shot added from the menu neither rendered nor
    ///     looked broken — and it never appeared on the editor's hardcoded list, because a list kept
    ///     in the editor's own assembly is one somebody has to remember to extend.
    /// </summary>
    [Fact]
    public void A_virtual_camera_added_from_the_menu_is_a_shot_that_would_render() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");

        var crate = Named(editor, "Crate");

        Choose(editor, "Virtual Camera");

        var shot = editor.Scene.World.Read<VirtualCamera>(crate);

        Assert.True(shot.Enabled);
        Assert.NotEqual(0f, shot.Lens.FarPlane);
    }

    /// <summary>
    ///     Every subsystem's adopted component, in the one assembly that can see all of them at once.
    /// </summary>
    /// <remarks>
    ///     The four at the top are what <c>ComponentsView.Initial</c> hardcoded; the rest had the same
    ///     problem and no way to say so. <c>CharacterMovement</c> is the seventh and is asserted in
    ///     <c>Vixen.Physics.Tests</c> — this assembly does not reference physics.
    /// </remarks>
    [Theory]
    [InlineData(typeof(Light))]
    [InlineData(typeof(PostProcessVolume))]
    [InlineData(typeof(TerrainComponent))]
    [InlineData(typeof(Camera))]
    [InlineData(typeof(VirtualCamera))]
    [InlineData(typeof(CameraDirector))]
    [InlineData(typeof(AudioSource))]
    [InlineData(typeof(MeshRenderable))]
    [InlineData(typeof(VfxEmitter))]
    public void Every_adopted_component_creates_something_other_than_a_zero(Type component) {
        AuthoringSubsystems.Load();

        var bridge = ComponentsView.Default().Single(candidate => candidate.ComponentType == component);
        var fresh = bridge.Create();

        Assert.Equal(component, fresh.GetType());
        Assert.NotEqual(Activator.CreateInstance(component), fresh);
    }

    /// <summary>
    ///     ⚠ <b>The zeroes that mean something, which must stay zero.</b>
    /// </summary>
    /// <remarks>
    ///     A grass rule's <c>Range</c> of zero is the instruction to take the project's, and a
    ///     <c>PrimitiveShape</c> is a kind and a material where every value is as good as any other.
    ///     A mechanism that had started inventing values for these would be one that had stopped
    ///     being opt-in.
    /// </remarks>
    [Theory]
    [InlineData(typeof(TerrainGrassComponent))]
    [InlineData(typeof(PrimitiveShape))]
    public void A_component_that_declares_nothing_still_creates_a_zero(Type component) {
        AuthoringSubsystems.Load();

        var bridge = ComponentsView.Default().Single(candidate => candidate.ComponentType == component);

        Assert.Equal(Activator.CreateInstance(component), bridge.Create());
    }

    /// <summary>
    ///     ⚠ <b>A component the editor has never heard of, carrying its own default.</b> This is the
    ///     shape a game's component has: registered from outside this assembly, drawn from its
    ///     <c>[DataContract]</c> description, and started from a value the editor does not know and
    ///     did not choose.
    /// </summary>
    [Fact]
    public void A_component_registered_from_outside_the_editor_brings_its_own_default() {
        SceneComponentRegistry.RegisterWithDefault<LateDefaulted>();

        var bridge = ComponentsView.Default()
            .Single(candidate => candidate.ComponentType == typeof(LateDefaulted));

        var fresh = Assert.IsType<LateDefaulted>(bridge.Create());

        Assert.Equal(42f, fresh.Reach);
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

    static AddComponentMenu Picker(EditorSession editor) {
        Press(editor, "Add Component");

        return Descendants(editor.Document.Root)
            .OfType<AddComponentMenu>()
            .FirstOrDefault(candidate => candidate.IsOpen)
            ?? throw editor.Fail("the Add Component picker did not open");
    }

    static IReadOnlyList<string> Offered(EditorSession editor) {
        var picker = Picker(editor);

        // ⚠ The whole offer rather than what is on screen, which since the picker grew categories
        // are two different things: the top level is a list of headings. What these tests are about
        // is which components an entity may be given, and that is the same question it always was.
        List<string> offered = [.. picker.Offered.Select(entry => entry.Bridge.DisplayName)];

        picker.Close(CloseReason.Code);
        editor.Settle();

        return offered;
    }

    static void Choose(EditorSession editor, string component) {
        var picker = Picker(editor);

        // Through the search, which is the path somebody who knows the name takes — and the one that
        // does not depend on which category the component happens to be filed under.
        picker.Field.Value = component;
        editor.Settle();

        (picker.Rows.FirstOrDefault(row => row.Index >= 0 && row.Label == component)
            ?? throw editor.Fail($"the picker does not offer '{component}'")).Activate();

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

/// <summary>The same, declaring what a fresh one holds — a game's component with an opinion.</summary>
/// <remarks>
///     ⚠ <b>The value is on the type and not in a field initializer</b>, which is the distinction the
///     whole mechanism turns on. <see cref="LateComponent" /> above has an initializer, and it runs on
///     <c>new()</c> and never on the ECS paths that hand back a zeroed row — honoured in some places
///     and silently ignored in others. This one is honoured exactly where something asks.
/// </remarks>
[Component]
[DataContract("ComponentTestsLateDefaulted")]
struct LateDefaulted : IDefaultComponent<LateDefaulted> {
    /// <summary>Something for a row to draw, whose zero would be useless.</summary>
    public float Reach;

    static LateDefaulted IDefaultComponent<LateDefaulted>.DefaultValue => new() { Reach = 42f };
}
