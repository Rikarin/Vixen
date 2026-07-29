// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Engine.Scenes;
using Vixen.Rendering;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>What is on an entity, as a foldout each, with add and remove.</summary>
/// <remarks>
///     <para>
///         <b>The last of doc 20's B1 inspector row, and the reason it was last is that it is not a
///         drawer.</b> A drawer edits a member of a described type; this asks a different question —
///         <i>which</i> types are on this entity — and neither the ECS nor the inspector could answer
///         it. The ECS could not because an archetype knows dense ids handed out in first-touch
///         order, which mean nothing to a person; the inspector could not because no runtime
///         component carries <c>[Inspector]</c>, and none should, since that would be a runtime
///         assembly referencing an editor one.
///     </para>
///     <para>
///         Both halves are now answerable. <see cref="IComponentBridge" /> enumerates and asks; and
///         <see cref="ReflectedDescriptor" /> draws the rows from the <c>[DataContract]</c>
///         description the serializer already generates — so a game's components appear with nothing
///         asked of the game.
///     </para>
///     <para>
///         ⚠ <b>A component is read as a box, edited, and written back whole.</b> The child rows are
///         bound with no document, so they write into the copy and record nothing; this view is what
///         puts one <see cref="SetComponentCommand" /> on the stack. Recording each field instead
///         would put a step on the stack that undoes a change to a copy nobody can see, and the
///         visible change would belong to a different step.
///     </para>
/// </remarks>
sealed partial class ComponentsView : Control {
    SceneDocument scene = null!;
    IReadOnlyList<IComponentBridge> bridges = [];
    UiElement host = null!;
    Button add = null!;
    ContextMenu? menu;

    /// <summary>The boxes the rows are editing, one per shown component.</summary>
    /// <remarks>
    ///     ⚠ <b>Re-read on every rebuild rather than kept.</b> A gizmo drag, an undo and a play-mode
    ///     restore all change what is in the chunk without going through these rows, and a box held
    ///     across one of those is a panel showing what the entity used to hold.
    /// </remarks>
    readonly Dictionary<IComponentBridge, List<object>> working = [];

    Entity entity;

    /// <inheritdoc />
    protected override string TagName => "components";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        host = Part("component-list");

        add = Part<Button>();
        add.Label = "Add Component";
        add.AddClass("add-component");
        add.AddClass("hidden");
        add.Clicked += _ => Offer();
    }

    /// <summary>Points the section at a document and the components it may show.</summary>
    /// <param name="document">The document an edit is recorded against.</param>
    /// <param name="shown">Which components can be shown, in menu order.</param>
    /// <remarks>
    ///     Separate from construction because a <see cref="Control" /> is made by the framework with
    ///     no arguments — and because a panel's factory runs again when it is reopened, so this runs
    ///     once per panel rather than once per session.
    /// </remarks>
    public void Attach(SceneDocument document, IReadOnlyList<IComponentBridge> shown) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(shown);

        scene = document;
        bridges = shown;

        Show(Entity.Null);
    }

    /// <summary>Which entity is being shown, or <see cref="Entity.Null" /> for none.</summary>
    public Entity Entity => entity;

    /// <summary>The foldouts, in the order the bridges were given.</summary>
    public IReadOnlyList<Expander> Sections => [.. host.Children.OfType<Expander>()];

    /// <summary>Shows an entity's components.</summary>
    /// <param name="target">The entity, or <see cref="Entity.Null" /> to show nothing.</param>
    /// <remarks>
    ///     ⚠ <b>One entity, not the selection.</b> Two entities rarely carry the same set, and a
    ///     panel that showed the intersection would hide the component somebody selected the second
    ///     object to look at. Multi-object component editing is a real feature and it is a different
    ///     one; this is the primary object's, which is what the header of every other editor's
    ///     component panel says it is.
    /// </remarks>
    public void Show(Entity target) {
        entity = target;
        Rebuild();
    }

    /// <summary>Rebuilds the foldouts from what the entity currently carries.</summary>
    public void Rebuild() {
        while (host.Children.Count > 0) {
            host.Children[^1].Remove();
        }

        working.Clear();

        var alive = entity != Entity.Null && scene.World.IsAlive(entity);

        if (alive) {
            add.RemoveClass("hidden");
        } else {
            add.AddClass("hidden");
            return;
        }

        foreach (var bridge in bridges) {
            if (bridge.Has(scene.World, entity)) {
                Section(bridge);
            }
        }
    }

    void Section(IComponentBridge bridge) {
        var fold = host.Add<Expander>();

        fold.Label = bridge.Name;
        fold.IsExpanded = true;
        fold.AddClass("component");

        var remove = fold.Header.Add<IconButton>();

        remove.LeadingIcon.Geometry = ControlIcons.Close;
        remove.Variant = ControlVariant.Subtle;
        remove.Size = ControlSize.Small;
        remove.Label = "Remove Component";
        remove.TabIndex = -1;
        remove.AddClass("remove-component");
        remove.Clicked += _ => Remove(bridge);

        if (ReflectedDescriptor.For(bridge.ComponentType) is not { } descriptor) {
            // ⚠ Named rather than skipped. A component with no description is a component whose
            // declaring assembly does not run the serialization generator, and a foldout that is
            // simply absent reads as the entity not having it.
            var absent = fold.Content.Add<TextBlock>();

            absent.AddClass("property-readonly");
            absent.Text = "No description, so there is nothing to draw. Its assembly needs [DataContract].";

            return;
        }

        // One box, held in a list because that is what an `InspectorField` takes as its targets, and
        // re-read by `Rebuild` rather than carried across one.
        List<object> box = [bridge.Read(scene.World, entity)];

        working[bridge] = box;

        foreach (var member in descriptor.Members) {
            // ⚠ No document. The row writes into the box and records nothing; the command below is
            // the one thing that reaches the stack, and it carries the whole component.
            var field = new InspectorField(descriptor, member, box);

            InspectorRows.Add(
                fold.Content,
                field,
                DrawerRegistry.Default,
                made => field.Changed += _ => Commit(bridge, made)
            );
        }
    }

    /// <summary>Writes the edited box back to the entity as one undo step.</summary>
    void Commit(IComponentBridge bridge, InspectorRow row) {
        if (!working.TryGetValue(bridge, out var box) || box.Count == 0 || !scene.World.IsAlive(entity)) {
            return;
        }

        var before = bridge.Read(scene.World, entity);

        scene.Stack.Execute(
            new SetComponentCommand(scene, bridge, entity, before, box[0], "Set " + bridge.Name)
        );

        scene.Stack.Seal();
        InspectorRows.Restate(row);
    }

    void Remove(IComponentBridge bridge) {
        if (!scene.World.IsAlive(entity) || !bridge.Has(scene.World, entity)) {
            return;
        }

        scene.Stack.Execute(
            new SetComponentCommand(
                scene,
                bridge,
                entity,
                bridge.Read(scene.World, entity),
                after: null,
                "Remove " + bridge.Name
            )
        );

        scene.Stack.Seal();
    }

    /// <summary>Offers what the entity does not already have.</summary>
    /// <remarks>
    ///     ⚠ <b>Rebuilt on every open, which is what makes it dynamic.</b> The list is "everything
    ///     registered minus what is already on this entity", and both halves move — a plugin loading
    ///     changes the first and the last click changed the second.
    /// </remarks>
    void Offer() {
        // ⚠ Made on first use rather than in `OnCreated`. A menu is a child of the document root and
        // a control has no document until it is in one.
        menu ??= Document.Root.Add<ContextMenu>();
        menu.Clear();

        if (!scene.World.IsAlive(entity)) {
            return;
        }

        var offered = 0;

        foreach (var bridge in bridges) {
            if (bridge.Has(scene.World, entity)) {
                continue;
            }

            var chosen = bridge;
            var item = menu.AddItem(bridge.Name);

            item.Clicked += _ => Add(chosen);
            offered++;
        }

        if (offered == 0) {
            // A menu that opens onto nothing reads as broken rather than as empty — the same rule
            // the Open Recent submenu follows.
            menu.AddItem("Nothing left to add").Disabled = true;
        }

        menu.OpenAt(add.Bounds.X, add.Bounds.Y + add.Bounds.Height);
    }

    void Add(IComponentBridge bridge) {
        if (!scene.World.IsAlive(entity) || bridge.Has(scene.World, entity)) {
            return;
        }

        scene.Stack.Execute(
            new SetComponentCommand(scene, bridge, entity, before: null, bridge.Create(), "Add " + bridge.Name)
        );

        scene.Stack.Seal();
    }

    /// <summary>The components the editor can show, runtime ones first.</summary>
    /// <remarks>
    ///     ⚠ <b>Both kinds, and the editor-side two are not an afterthought.</b> <c>Light</c> and
    ///     <c>MeshShape</c> live in the scene view because the runtime has nowhere to name them yet,
    ///     and they are what the seeded scene is mostly made of — a component panel that showed only
    ///     what <c>SceneComponentRegistry</c> holds would omit the light on the light.
    /// </remarks>
    public static IReadOnlyList<IComponentBridge> Default() {
        List<IComponentBridge> found = [
            new ComponentBridge<Light>("Light", () => Lights.Default(LightKind.Point)),
            new ComponentBridge<MeshShape>("Mesh Shape")
        ];

        foreach (var binder in SceneComponentRegistry.Binders) {
            found.Add(new SceneComponentBridge(binder));
        }

        return found;
    }
}
