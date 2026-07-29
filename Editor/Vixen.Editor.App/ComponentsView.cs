// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
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

    /// <summary>The rows on screen, so that <see cref="Reload" /> can re-read them.</summary>
    /// <remarks>
    ///     Kept beside the boxes rather than walked out of the element tree, because a row is an
    ///     <c>InspectorRow</c> with a field and a drawer on it and the tree only has elements — and
    ///     because the walk would be per undo rather than per rebuild.
    /// </remarks>
    readonly List<(IComponentBridge Bridge, InspectorRow Row)> rows = [];

    /// <summary>The order the foldouts are shown in, by component name.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A view order rather than a fact about the entity, and it has to be.</b> An
    ///         archetype is a set — the ECS hands out dense component ids in first-touch order and a
    ///         chunk has no notion of "third" — so there is nowhere on an entity to record that
    ///         somebody dragged Light above Mesh Shape. What a person is arranging when they drag a
    ///         foldout is <i>their inspector</i>, which is the same kind of thing as a panel layout,
    ///         and it applies to every entity for the same reason a layout applies to every project.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Names, not bridges.</b> The list outlives any particular set of bridges — a
    ///         plugin's component is in it after the plugin is unloaded and back again when it
    ///         returns — and a name is what a preferences file can hold. Anything not named here
    ///         sorts after everything that is, in the order the bridges were given.
    ///     </para>
    /// </remarks>
    readonly List<string> order = [];

    /// <summary>The foldout being dragged, or <see langword="null" />.</summary>
    Expander? dragging;

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

    /// <summary>The foldouts, in the order they are shown.</summary>
    public IReadOnlyList<Expander> Sections => [.. host.Children.OfType<Expander>()];

    /// <summary>What order the foldouts are shown in, by component name.</summary>
    /// <inheritdoc cref="order" select="remarks" />
    public IReadOnlyList<string> Order {
        get => order;

        set {
            ArgumentNullException.ThrowIfNull(value);

            order.Clear();
            order.AddRange(value);

            Rebuild();
        }
    }

    /// <summary>Raised after a drag has rearranged the foldouts.</summary>
    /// <remarks>
    ///     ⚠ <b>Because a panel's factory runs again when it is reopened.</b> This control is built
    ///     fresh every time the inspector is opened, so an arrangement it kept to itself would be
    ///     forgotten by closing the tab — which is the same failure the outliner's filter and the
    ///     content browser's view toggle both had. Whoever built it holds the answer.
    /// </remarks>
    public event Action<IReadOnlyList<string>>? Reordered;

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

        // ⚠ With the boxes, and the two have to go together. A row left here after its foldout was
        // removed is one `Reload` would push a value into, which is a write into a detached element.
        rows.Clear();

        var alive = entity != Entity.Null && scene.World.IsAlive(entity);

        if (alive) {
            add.RemoveClass("hidden");
        } else {
            add.AddClass("hidden");
            return;
        }

        foreach (var bridge in Shown()) {
            Section(bridge);
        }
    }

    /// <summary>What the entity carries, in the order the user arranged.</summary>
    /// <remarks>
    ///     ⚠ <b>A stable sort on the position in <see cref="order" />, with everything unnamed after
    ///     everything named.</b> A component added since the last drag has no place in the list, and
    ///     putting it first would make adding one silently rearrange the panel; putting it last, in
    ///     the bridges' own order, is where a new thing goes.
    /// </remarks>
    IEnumerable<IComponentBridge> Shown() {
        var carried = bridges.Where(bridge => bridge.Has(scene.World, entity)).ToList();

        return carried
            .OrderBy(bridge => order.IndexOf(bridge.Name) is var at && at >= 0 ? at : int.MaxValue)
            .ToList();
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

        // ⚠ On the header rather than on the whole foldout, so that dragging a slider inside a
        // component is not also dragging the component. The header is the grab handle every
        // rearrangeable list in this editor uses, which is also why it is the thing that looks
        // pressable.
        fold.Header.AddHandler<DragEvent>((_, args) => Rearrange(fold, args));

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

            if (InspectorRows.Add(
                    fold.Content,
                    field,
                    DrawerRegistry.Default,
                    made => field.Changed += _ => Commit(bridge, made)
                ) is { } row) {
                rows.Add((bridge, row));
            }
        }
    }

    /// <summary>Reads every editor back from the entity, without rebuilding anything.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What an undo needs, and what a value edit deliberately does not raise.</b>
    ///         <c>SetComponentCommand</c> announces itself only when the <i>set</i> of components
    ///         changed — telling the panel on every field write would rebuild it under the pointer of
    ///         whoever is dragging a slider — so a Ctrl+Z that put a light's intensity back changed
    ///         the world, the viewport and the undo history, and left the number on screen as it was.
    ///         The row is read from its box when it is built and after an edit it made itself;
    ///         nothing else ever told it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The boxes are re-read first.</b> They are copies — that is the whole arrangement,
    ///         one command carrying a whole component — so refreshing the rows against the boxes they
    ///         already hold would faithfully redisplay what the undo just discarded.
    ///     </para>
    ///     <para>
    ///         <c>Reload</c> and not <see cref="Rebuild" />: the foldouts, their expansion and the
    ///         focus survive, which is the same trade <c>InspectorView.Reload</c> makes.
    ///     </para>
    /// </remarks>
    public void Reload() {
        if (entity == Entity.Null || !scene.World.IsAlive(entity)) {
            return;
        }

        foreach (var (bridge, box) in working) {
            if (box.Count > 0 && bridge.Has(scene.World, entity)) {
                box[0] = bridge.Read(scene.World, entity);
            }
        }

        foreach (var (_, row) in rows) {
            InspectorRows.Show(row);
        }
    }

    /// <summary>Moves a foldout to where it was dropped.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The dragged section comes from the drag's own start rather than from a hit test
    ///         under the pointer.</b> A drag does not begin until the pointer has passed the slop
    ///         threshold, which in a panel of twenty-six-pixel headers is most of the way to the next
    ///         one — so asking what is under the pointer picks up the neighbour and drags the wrong
    ///         component. It is the same reason <c>TreeView.Dragged</c> reads the event's source.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing is recorded on the undo stack.</b> Rearranging the panel does not change
    ///         the entity — see <see cref="order" /> — and a Ctrl+Z that undid a drag of the
    ///         inspector rather than the edit before it would be the worst kind of surprise.
    ///     </para>
    /// </remarks>
    void Rearrange(Expander section, DragEvent args) {
        switch (args.Stage) {
            case DragStage.Started:
                dragging = section;
                section.AddClass("dragging");

                break;

            case DragStage.Completed when dragging is { } moved:
                moved.RemoveClass("dragging");
                dragging = null;

                Drop(moved, args.Y);
                break;

            case DragStage.Cancelled:
                dragging?.RemoveClass("dragging");
                dragging = null;

                break;

            default:
                break;
        }
    }

    /// <summary>Puts a section where a drop at a height means, and reports the new order.</summary>
    void Drop(Expander moved, float y) {
        var sections = Sections;
        var from = -1;
        var to = sections.Count - 1;

        for (var index = 0; index < sections.Count; index++) {
            var bounds = sections[index].Bounds;

            if (ReferenceEquals(sections[index], moved)) {
                from = index;
            }

            // The first section whose middle is below the pointer is the one it lands above.
            if (y < bounds.Y + (bounds.Height * 0.5f)) {
                to = Math.Min(to, index);
            }
        }

        if (from < 0 || from == to) {
            return;
        }

        // ⚠ Rebuilt from what is on screen rather than edited in place, because `order` also holds
        // the names of components this entity does not carry — a list this reordered by index would
        // interleave the two and shuffle everything the panel is not showing.
        var names = sections.Select(Named).Where(name => name is not null).Select(name => name!).ToList();
        var name = Named(moved);

        if (name is null) {
            return;
        }

        names.RemoveAt(from);
        names.Insert(Math.Clamp(to, 0, names.Count), name);

        // Whatever the panel is not showing keeps its place behind what it is.
        var updated = names.Concat(order.Where(existing => !names.Contains(existing, StringComparer.Ordinal))).ToList();

        order.Clear();
        order.AddRange(updated);

        Rebuild();
        Reordered?.Invoke(updated);
    }

    /// <summary>Which component a foldout is showing.</summary>
    static string? Named(Expander section) => section.Label;

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

    /// <summary>The components the editor can show.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The loop and nothing else, which is the whole of what this method has to be.</b> A
    ///         component carrying <c>[Component]</c> and <c>[DataContract]</c> is declared to
    ///         <c>SceneComponentRegistry</c> by the engine's component generator, so it appears here —
    ///         and in the Add Component menu, in the <c>.vxscene</c> and in the compiled scene — with no
    ///         registration call and nothing added to this list. That holds for a game's own components
    ///         and for the engine's alike; <c>Light</c> and <c>PrimitiveShape</c> were hand-written
    ///         entries here until they became <c>Vixen.Rendering</c>'s, and their going is the
    ///         arrangement working rather than a special case being removed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A component is here only once its declaring assembly has been loaded</b>, because a
    ///         module initializer runs on assembly load. The editor references the subsystems it draws
    ///         for, and a project's own assemblies have to be loaded before this is asked — which is
    ///         also what makes a scene naming a component from an unloaded assembly fail at the load
    ///         with a message rather than silently.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<IComponentBridge> Default() {
        List<IComponentBridge> found = [];

        foreach (var binder in SceneComponentRegistry.Binders) {
            found.Add(new SceneComponentBridge(binder, Initial(binder.ComponentType)));
        }

        return found;
    }

    /// <summary>What a freshly added component of a type should hold, when zero is the wrong answer.</summary>
    /// <param name="component">The component type.</param>
    /// <returns>A factory, or <see langword="null" /> to take the zeroed struct.</returns>
    /// <remarks>
    ///     ⚠ <b>Two entries, and both are here because a zeroed value of that type is not merely
    ///     unhelpful but looks like a defect.</b> A black light reads as a broken renderer and a camera
    ///     with a zero far plane produces a degenerate projection — see
    ///     <c>SceneComponentBridge</c>'s own remarks. This is deliberately a short list keyed by type
    ///     rather than a convention the registry enforces: most components are data whose zero is a
    ///     perfectly good starting point, and a mechanism obliging every one of them to declare a
    ///     default would be paid for by all of them to serve these two.
    /// </remarks>
    static Func<object>? Initial(Type component) {
        if (component == typeof(Light)) {
            return static () => Lights.Default(LightKind.Point);
        }

        return component == typeof(Camera) ? static () => Camera.Perspective : null;
    }
}
