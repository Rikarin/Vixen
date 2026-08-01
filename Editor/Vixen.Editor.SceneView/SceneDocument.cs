// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.SceneView;

/// <summary>Writes a scene back to wherever it came from.</summary>
/// <remarks>
///     An interface rather than something this assembly implements, because a scene's file format
///     belongs to the asset pipeline and not to the panel that edits one. A document with none
///     refuses to save rather than reporting success and writing nothing — see
///     <see cref="SceneDocument.SaveCore" />.
/// </remarks>
public interface ISceneWriter {
    /// <summary>Writes a scene.</summary>
    /// <param name="document">The document to write.</param>
    void Write(SceneDocument document);
}

/// <summary>Renaming one entity.</summary>
/// <remarks>
///     Public because a rename can arrive from three places — the hierarchy's inline editor, the
///     inspector's name field, and a script — and all three should produce the same history entry.
/// </remarks>
public sealed class RenameEntityCommand : IEditorCommand {
    readonly SceneDocument document;
    readonly Entity entity;
    readonly string oldName;
    readonly string newName;

    /// <inheritdoc />
    public string Name => "Rename";

    /// <summary>Describes renaming an entity.</summary>
    /// <param name="document">The document it lives in.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="oldName">What it was called.</param>
    /// <param name="newName">What it should be called.</param>
    public RenameEntityCommand(SceneDocument document, Entity entity, string oldName, string newName) {
        ArgumentNullException.ThrowIfNull(document);

        this.document = document;
        this.entity = entity;
        this.oldName = oldName;
        this.newName = newName;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.Assign(entity, newName);
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.Assign(entity, oldName);
        context.Touch(document);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Two renames of one entity do not merge.</b> A name is not a slider: they are two
    ///     decisions, and a name typed in two sittings collapsing into one entry is an undo the user
    ///     is entitled to and does not get.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }
}

/// <summary>A scene, open for editing: a world, what is selected in it, and what things are called.</summary>
/// <remarks>
///     <para>
///         The document <c>Vixen.Editor.Core</c>'s README says arrives here rather than there, and
///         the reason is the reference: a scene <i>is</i> an ECS world, and <c>Vixen.Editor.Core</c>
///         does not reference <c>Vixen.Ecs</c> — deliberately, so that the command stack and the asset
///         database are testable without one.
///     </para>
///     <para>
///         <b>The editor names entities and the runtime does not.</b> There is no name component:
///         a name is worth thirty bytes per entity in every chunk of a shipping build to serve a
///         panel that does not exist at run time. The map lives here, which also makes renaming an
///         ordinary document edit with an ordinary undo entry rather than a structural change to the
///         world.
///     </para>
///     <para>
///         <b>Creating and destroying entities are undoable, and the handle survives.</b>
///         <see cref="Create" /> and <see cref="Delete" /> go on the stack; <see cref="Add" /> stays
///         for a host building a scene from a file or a template. Five things come back and only the
///         first was ever hard: the handle (<c>World.TryRecreate</c>), the components (a scratch
///         world), the name, the stable id, and the entity's place among its siblings
///         (<c>Hierarchy.SetParentAfter</c>) — see <see cref="SubtreeSnapshot" />.
///     </para>
///     <para>
///         ⚠ <b>An undo of a delete can refuse.</b> A slot taken since the delete makes its handle
///         unrecoverable for ever, so the command throws rather than half-restoring a subtree.
///         Reaching that needs something else creating entities in this world — a play-mode restore,
///         or a second document.
///     </para>
/// </remarks>
public sealed class SceneDocument : EditorDocument {
    readonly Dictionary<Entity, string> names = [];
    readonly Dictionary<Entity, EntityId> ids = [];

    /// <summary>The entities the editor is not drawing, and the ones it will not let be picked.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Editor state, not scene state, and both engines agree.</b> Unreal keeps "hidden
    ///         in editor" separate from "hidden in game" and Unity's <c>SceneVisibilityManager</c> is
    ///         editor-only, because the alternative — a component — means hiding something to work on
    ///         what is behind it silently changes what ships. So these are sets on the document, not
    ///         columns in a chunk, and nothing here is written to a file.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Keyed by handle, so they move through <see cref="Remap" /> with the names.</b> A
    ///         play-mode restore reissues every handle; a hidden set that did not travel would come
    ///         back hiding whatever happened to take those slots.
    ///     </para>
    /// </remarks>
    readonly HashSet<Entity> hidden = [];

    /// <inheritdoc cref="hidden" />
    readonly HashSet<Entity> locked = [];

    /// <summary>The editable geometry each entity carries, for the ones that carry any.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A table on the document rather than a component in the world, and doc 24's B3 is
    ///         the bargain.</b> A component no build declares is what a content compile refuses, and an
    ///         <c>EditMesh</c> is not a component — it is a mutable object of a few thousand numbers
    ///         belonging to one entity in one scene. Blockout geometry is level data rather than a
    ///         shared asset, which is where it belongs: a designer who had to save six meshes to disk
    ///         to try a corridor has been given the DCC round-trip back under a different name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Keyed by handle, so it travels the same way the hidden and locked sets do.</b> A
    ///         play-mode restore reissues every handle — see <c>WorldSnapshot</c> — and a table that
    ///         did not travel would come back attached to whatever now holds the old numbers.
    ///     </para>
    /// </remarks>
    readonly Dictionary<Entity, EditMesh> meshes = [];
    readonly Dictionary<Entity, int> versions = [];

    /// <summary>The live parameters each shaped entity still has, for the ones that have any.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's D6, and the pairing with <see cref="meshes" /> is the whole model.</b> A
    ///         parametric entity has <i>both</i>: the parameters, and the mesh they generated. The mesh
    ///         is what draws, picks, saves-as-geometry and gets edited; the parameters are what an
    ///         inspector shows and what rebuilds the mesh when one of them changes. Demotion is
    ///         removing the entry from this table and changing nothing else — which is why it is a
    ///         one-way door with nothing to clean up.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Beside the mesh rather than instead of it, and the alternative is worse in three
    ///         places at once.</b> Deriving the geometry on demand would mean the picker, the drawing
    ///         and every selection walk each asking a generator for a mesh they then have to cache;
    ///         and the moment a shape is demoted, every one of them would have to switch source. One
    ///         table answers "what is this made of" and the other answers "what was it made from".
    ///     </para>
    /// </remarks>
    readonly Dictionary<Entity, ShapeParameters> shapes = [];

    /// <summary>Which material each entity's face groups are drawn with, for the ones that say.</summary>
    /// <inheritdoc cref="MaterialsOf" select="remarks" />
    readonly Dictionary<Entity, Dictionary<int, AssetReference>> materials = [];

    /// <summary>The entities whose geometry is a boolean of their children's.</summary>
    /// <remarks>
    ///     ⚠ <b>A table beside the mesh rather than a component, for <see cref="meshes" />' reason and
    ///     one of its own.</b> A boolean is a <i>derivation</i> — what this entity's geometry is a
    ///     function of — and the thing it is a function of is the hierarchy, which is already the
    ///     world's. Two enum values and a hash per entity, and nothing about it belongs in a chunk.
    /// </remarks>
    readonly Dictionary<Entity, CsgNode> booleans = [];
    readonly Dictionary<EntityId, Entity> byId = [];
    readonly QueryDescription tagged = new QueryDescription().RequireAll([ComponentType<SceneTag>.Id]);

    /// <summary>The world the scene lives in.</summary>
    public World World { get; }

    /// <summary>What loads and unloads scenes in that world.</summary>
    public SceneManager Scenes { get; }

    /// <summary>Which scene this document edits.</summary>
    public SceneHandle Scene { get; }

    /// <summary>What is selected, shared with the viewport, the hierarchy and the inspector.</summary>
    /// <remarks>
    ///     Deliberately not undoable, for the reason <c>Selection&lt;T&gt;</c> gives: selection is
    ///     where you are looking, not what you have changed.
    /// </remarks>
    public Selection<Entity> Selection { get; } = new();

    /// <summary>Writes the scene back, or <see langword="null" /> while nothing can.</summary>
    public ISceneWriter? Writer { get; set; }

    /// <summary>Raised when entities appear, disappear or change parent.</summary>
    /// <remarks>
    ///     What the hierarchy panel rebuilds from. Not raised for a transform edit or a rename —
    ///     those change a row's <i>contents</i>, and a panel that rebuilt its tree on every frame of
    ///     a gizmo drag would lose the expansion state forty times a second.
    /// </remarks>
    public event Action<SceneDocument>? StructureChanged;

    /// <summary>Raised when an entity's name changes.</summary>
    public event Action<SceneDocument, Entity>? Renamed;

    /// <summary>What names an entity in a file, rather than in this world.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Its stable id, minted on first ask.</returns>
    /// <remarks>
    ///     Minted lazily rather than at creation, so an entity that is never saved never costs a
    ///     GUID — and so that an id read out of a file is the one that entity keeps, instead of
    ///     being a second id assigned before the file was opened. See <see cref="EntityId" /> for
    ///     why the identity in the file cannot be the handle.
    /// </remarks>
    public EntityId IdOf(Entity entity) {
        if (ids.TryGetValue(entity, out var existing)) {
            return existing;
        }

        var id = EntityId.New();

        ids[entity] = id;
        byId[id] = entity;

        return id;
    }

    /// <summary>Which entity a file's id names, if any.</summary>
    /// <param name="id">The id.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it is in this document and alive.</returns>
    public bool TryGetEntity(EntityId id, out Entity entity) {
        if (byId.TryGetValue(id, out entity) && World.IsAlive(entity)) {
            return true;
        }

        entity = Entity.Null;
        return false;
    }

    /// <summary>Says that an entity is the one a file called something.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="id">What the file called it.</param>
    /// <remarks>
    ///     What a reader calls as it creates each entity, so that references inside the file — and
    ///     the next save — name the same thing the file did rather than a fresh identity.
    /// </remarks>
    public void Adopt(Entity entity, EntityId id) {
        if (id.IsNone) {
            return;
        }

        ids[entity] = id;
        byId[id] = entity;
    }

    /// <summary>Opens a scene for editing.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="world">The world it lives in.</param>
    /// <param name="asset">The asset it edits, or <see cref="AssetId.Empty" /> for an unsaved scene.</param>
    /// <param name="title">What the tab says.</param>
    public SceneDocument(EditorProject project, World world, AssetId asset, string title = "Scene")
        : base(project, asset, title) {
        ArgumentNullException.ThrowIfNull(world);

        World = world;
        Behaviors = new(world);
        Scenes = new(world);
        Scene = Scenes.Create(title);
    }

    /// <summary>The behaviours the entities in this document carry.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A container, not a driver — nothing here ever calls
    ///         <see cref="BehaviorStore.RunLifecycle" />.</b> An editor showing a scene is showing
    ///         <i>authored</i> behaviours: they hold the values somebody typed and they have not run.
    ///         Driving the lifecycle would call <c>Awake</c> and <c>Start</c> on a designer's
    ///         behalf — a script spawning enemies the moment you selected its entity — which is why
    ///         Unity's edit mode does not run them either.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is also why the store's authoring path is
    ///         <see cref="BehaviorStore.Remove" /> and not <c>Destroy</c>.</b> A queued destroy waits
    ///         for a drain that, here, never comes.
    ///     </para>
    ///     <para>
    ///         Play mode is the other half and is <c>PlayMode</c>'s: it takes a snapshot of the world
    ///         and runs it, and the behaviours it runs are the ones a load builds into <i>its</i>
    ///         store rather than these.
    ///     </para>
    /// </remarks>
    public BehaviorStore Behaviors { get; }

    /// <summary>What an entity is called.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Its name, or a generated one if it has never been given a name.</returns>
    /// <remarks>
    ///     A handle rendered as text rather than the empty string, because an unnamed row in a
    ///     hierarchy is one nobody can tell apart from the unnamed row above it.
    /// </remarks>
    public string NameOf(Entity entity) =>
        names.TryGetValue(entity, out var name) ? name : "Entity " + entity.Id.ToString(null as IFormatProvider);

    /// <summary>Whether an entity has been given a name, rather than being shown as its handle.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="name">The name it was given.</param>
    /// <returns>Whether it has one.</returns>
    /// <remarks>
    ///     Distinct from <see cref="NameOf" />, which always answers. Something restoring an entity
    ///     needs to know whether to put a name back or leave it as it found it — assigning the
    ///     generated "Entity 7" would turn a never-named entity into a named one whose name is a
    ///     handle it may no longer have.
    /// </remarks>
    public bool TryGetName(Entity entity, [MaybeNullWhen(false)] out string name) =>
        names.TryGetValue(entity, out name);

    /// <summary>The stable id an entity already has, without minting one.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="id">Its id.</param>
    /// <returns>Whether it has one.</returns>
    /// <remarks>
    ///     <see cref="IdOf" /> mints on ask, which is right for saving and wrong for asking. An entity
    ///     that never had an id should not acquire one because something looked at it.
    /// </remarks>
    public bool TryGetId(Entity entity, out EntityId id) => ids.TryGetValue(entity, out id);

    /// <summary>Renames an entity without recording anything.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="name">What it should be called.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>For a caller that is <i>already</i> being recorded, and there is exactly one:
    ///         a property setter the inspector is writing through.</b> The inspector wraps every
    ///         write in a <c>SetMembersCommand</c>, so a setter that also called
    ///         <see cref="Rename" /> put two entries on the stack for one edit — and the second was
    ///         pushed from inside the first, which is where it stopped being merely untidy. Undoing
    ///         the outer one runs the setter again, the setter asks the stack to execute during an
    ///         undo, and the stack refuses: the entry comes off the history and the name does not
    ///         change. That is precisely the shape of "Ctrl+Z removes the entry and the value stays".
    ///     </para>
    ///     <para>
    ///         Everything a <i>person</i> reaches — the outliner's inline editor, F2, the context
    ///         menu — goes through <see cref="Rename" /> and is one undo step, as it was.
    ///     </para>
    /// </remarks>
    public bool SetName(Entity entity, string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var current = NameOf(entity);

        if (string.Equals(current, name, StringComparison.Ordinal)) {
            return false;
        }

        Assign(entity, name);
        Context.Touch(this);

        return true;
    }

    /// <summary>Renames an entity, undoably.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="name">What it should be called.</param>
    /// <returns>Whether anything changed.</returns>
    public bool Rename(Entity entity, string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var current = NameOf(entity);

        if (string.Equals(current, name, StringComparison.Ordinal)) {
            return false;
        }

        Stack.Execute(new RenameEntityCommand(this, entity, current, name));
        Stack.Seal();

        return true;
    }

    /// <summary>Adds an entity to the scene with a transform and a name.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="local">Where it starts.</param>
    /// <param name="parent">What to hang it from, or <see cref="Entity.Null" /> for a root.</param>
    /// <returns>The entity.</returns>
    /// <remarks>
    ///     ⚠ <b>Not undoable</b>, and the type's own remarks say why. It is here so a host can build
    ///     a scene — from a file, from a template, from a test — and not so a user can press a
    ///     button. A shell should not offer it as a command until there is a pair of commands that
    ///     can put back everything a delete takes away.
    /// </remarks>
    public Entity Add(string name, LocalTransform local, Entity parent = default) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var entity = Scenes.CreateTransform(Scene, local);
        names[entity] = name;

        if (!parent.IsNull) {
            Hierarchy.SetParent(World, entity, parent);
        }

        StructureChanged?.Invoke(this);
        return entity;
    }

    /// <summary>Creates an entity, undoably.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="local">Where it starts.</param>
    /// <param name="parent">What to hang it from, or <see cref="Entity.Null" /> for a root.</param>
    /// <param name="initialise">
    ///     What to put on it beyond a transform, or <see langword="null" />. Run once, on creation —
    ///     see <see cref="CreateEntityCommand.Initialise" /> for why a redo does not run it again.
    /// </param>
    /// <returns>The entity.</returns>
    /// <remarks>
    ///     What a shell puts behind a button, as against <see cref="Add" />, which is what a reader
    ///     or a test uses to build a scene without filling the undo stack with entries nobody made.
    /// </remarks>
    public Entity Create(
        string name,
        LocalTransform local,
        Entity parent = default,
        Action<Entity>? initialise = null
    ) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var command = new CreateEntityCommand(this, name, local, parent) { Initialise = initialise };

        Stack.Execute(command);
        Stack.Seal();

        return command.Entity;
    }

    /// <summary>Creates an entity drawn as one of the built-in shapes, undoably.</summary>
    /// <param name="kind">Which shape.</param>
    /// <param name="local">Where it starts.</param>
    /// <param name="parent">What to hang it from, or <see cref="Entity.Null" /> for a root.</param>
    /// <returns>The entity.</returns>
    /// <remarks>
    ///     Named after the shape, which is what every editor does and what makes a hierarchy of
    ///     block-out geometry readable without clicking each row. Renaming it afterwards does not
    ///     change what it is: the shape is <see cref="PrimitiveShape" /> and the name is a label.
    /// </remarks>
    public Entity CreateShape(PrimitiveKind kind, LocalTransform local, Entity parent = default) =>
        Create(PrimitiveShapes.NameOf(kind), local, parent, entity => PrimitiveShapes.Attach(World, entity, kind));

    /// <summary>Creates a light, undoably.</summary>
    /// <param name="kind">Which kind.</param>
    /// <param name="local">Where it starts.</param>
    /// <param name="parent">What to hang it from, or <see cref="Entity.Null" /> for a root.</param>
    /// <returns>The entity.</returns>
    /// <remarks>
    ///     Named the way the menu names it — "Point Light" rather than "Point" — because a hierarchy
    ///     row saying <c>Spot</c> next to one saying <c>Cube</c> reads as two things of the same sort.
    ///     It carries <see cref="Lights.Default" />'s values rather than a zeroed record, or the first
    ///     thing a new light would do is nothing.
    /// </remarks>
    public Entity CreateLight(LightKind kind, LocalTransform local, Entity parent = default) =>
        Create(Lights.TitleOf(kind), local, parent, entity => Lights.Attach(World, entity, kind));

    /// <summary>Creates a camera, undoably.</summary>
    /// <param name="local">Where it starts.</param>
    /// <param name="parent">What to hang it from, or <see cref="Entity.Null" /> for a root.</param>
    /// <returns>The entity.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="Camera.Perspective" /> and not <c>default</c>.</b> A zeroed camera has a
    ///     zero field of view and a zero far plane, and every matrix built from one is degenerate —
    ///     so a camera created from the menu would be a camera that renders nothing, which reads as
    ///     the command having failed.
    /// </remarks>
    public Entity CreateCamera(LocalTransform local, Entity parent = default) =>
        Create("Camera", local, parent, entity => World.Add(entity, Camera.Perspective));

    /// <summary>Deletes entities and everything below them, undoably.</summary>
    /// <param name="entities">The subtree roots.</param>
    /// <returns>Whether anything was deleted.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The whole subtree goes.</b> A child left behind holds a <c>Parent</c> naming a
    ///         dead entity, and every walk over the hierarchy then throws.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The selection is not part of the command.</b> Selection is where you are looking
    ///         and not what you changed — <c>Selection&lt;T&gt;</c>'s own argument — so this clears
    ///         what it deleted and an undo does not bring it back selected.
    ///     </para>
    /// </remarks>
    public bool Delete(IEnumerable<Entity> entities) {
        ArgumentNullException.ThrowIfNull(entities);

        var roots = entities.Where(World.IsAlive).ToList();

        if (roots.Count == 0) {
            return false;
        }

        Stack.Execute(new DestroyEntitiesCommand(this, roots));
        Stack.Seal();

        Selection.Clear();

        return true;
    }

    /// <summary>Hangs an entity from another one, keeping where it is in the world.</summary>
    /// <param name="entity">The entity to move.</param>
    /// <param name="parent">Its new parent, or <see cref="Entity.Null" /> to make it a root.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    ///     ⚠ <b>Undoable, and putting it back where it was is the whole of what took the work.</b>
    ///     Reparenting was always reversible — the old parent is a handle that still exists — but an
    ///     undo that returned the third of five children to the head of the list is an undo that did
    ///     not undo. <see cref="ReparentCommand" /> records the sibling that was in front, which is
    ///     what the intrusive list already stores and what stays meaningful when its neighbours move.
    /// </remarks>
    public bool Reparent(Entity entity, Entity parent) => Reparent([entity], parent);

    /// <summary>Hangs several entities from one parent, keeping where each is in the world.</summary>
    /// <param name="entities">The entities to move.</param>
    /// <param name="parent">Their new parent, or <see cref="Entity.Null" /> to make them roots.</param>
    /// <returns>Whether anything moved.</returns>
    /// <remarks>
    ///     ⚠ <b>One command for the whole drag, not one per entity.</b> Dragging five rows onto a
    ///     sixth is one thing somebody did, and five undo steps for it is the shape of every "undo
    ///     did not undo what I did" report. What cannot move — a cycle, an entity already there, one
    ///     carried inside a parent that is also moving — is filtered by the command rather than
    ///     refused, so a drag that was partly meaningless still does the meaningful part.
    /// </remarks>
    public bool Reparent(IEnumerable<Entity> entities, Entity parent) {
        ArgumentNullException.ThrowIfNull(entities);

        var command = new ReparentCommand(this, entities, parent);

        if (command.IsEmpty) {
            return false;
        }

        Stack.Execute(command);
        return true;
    }

    /// <summary>The entities in this scene that have no parent, in creation order.</summary>
    public IReadOnlyList<Entity> Roots {
        get {
            List<Entity> roots = [];

            foreach (var entity in Entities) {
                if (Hierarchy.ParentOf(World, entity).IsNull) {
                    roots.Add(entity);
                }
            }

            return roots;
        }
    }

    /// <summary>Every entity this scene owns.</summary>
    /// <remarks>
    ///     Walked rather than kept as a list: the world is the truth, and a list the document
    ///     maintained alongside it would be one more thing that can disagree with what is actually
    ///     there after a play-mode restore.
    /// </remarks>
    public IEnumerable<Entity> Entities {
        get {
            List<Entity> found = [];

            foreach (var chunk in World.Chunks(tagged)) {
                var tags = chunk.ReadValues<SceneTag>();
                var entities = chunk.Entities;

                for (var row = 0; row < chunk.Count; row++) {
                    if (tags[row].SceneId == Scene.Id) {
                        found.Add(entities[row]);
                    }
                }
            }

            found.Sort();
            return found;
        }
    }

    /// <summary>Raised when a component was added to or taken off an entity.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own event, and the panel that issued the change is not the only listener that
    ///     matters.</b> An <i>undo</i> of "remove component" puts the column back without anything
    ///     having been clicked, so a panel that only rebuilt after its own commands would show a
    ///     component that is gone and hide one that is back.
    /// </remarks>
    public event Action<SceneDocument, Entity>? ComponentsChanged;

    /// <summary>Says a component came or went, for whatever is drawing them.</summary>
    internal void Recomposed(Entity entity) => ComponentsChanged?.Invoke(this, entity);

    /// <summary>Raised when an entity's visibility or lock changed.</summary>
    /// <remarks>
    ///     Its own event rather than <see cref="StructureChanged" />: nothing about the tree's shape
    ///     moved, so an outliner that rebuilt its rows for this would throw away the expansion state
    ///     every time somebody clicked an eye.
    /// </remarks>
    public event Action<SceneDocument, Entity>? Marked;

    /// <summary>Whether the editor is drawing an entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it is hidden in the editor.</returns>
    /// <remarks>
    ///     ⚠ <b>An entity under a hidden parent is hidden too, and the walk is upwards.</b> Hiding a
    ///     prop and finding its four children still drawn is what makes a visibility column useless —
    ///     and marking the descendants instead would mean unhiding the parent could not put back
    ///     exactly what was there, because it cannot tell which of them the user had hidden on
    ///     purpose.
    /// </remarks>
    public bool IsHidden(Entity entity) => Inherited(hidden, entity);

    /// <summary>Whether an entity refuses to be picked in the viewport.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it is locked.</returns>
    /// <inheritdoc cref="IsHidden" select="remarks" />
    public bool IsLocked(Entity entity) => Inherited(locked, entity);

    /// <summary>Whether the entity itself carries the mark, ignoring its parents.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it was hidden directly.</returns>
    /// <remarks>
    ///     What a toggle in the outliner reads, as against <see cref="IsHidden" />, which is what a
    ///     renderer asks. A row whose eye is off because its parent's is has to draw differently from
    ///     one the user turned off themselves, or clicking it does nothing visible.
    /// </remarks>
    public bool IsHiddenDirectly(Entity entity) => hidden.Contains(entity);

    /// <inheritdoc cref="IsHiddenDirectly" />
    public bool IsLockedDirectly(Entity entity) => locked.Contains(entity);

    /// <summary>Hides an entity in the editor, or stops hiding it.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="isHidden">Whether to hide it.</param>
    public void SetHidden(Entity entity, bool isHidden) => Mark(hidden, entity, isHidden);

    /// <summary>Stops an entity being picked in the viewport, or allows it again.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="isLocked">Whether to lock it.</param>
    public void SetLocked(Entity entity, bool isLocked) => Mark(locked, entity, isLocked);

    /// <summary>The editable geometry an entity carries, or <see langword="null" /> for none.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Its mesh.</returns>
    /// <remarks>
    ///     ⚠ <b>The mesh itself, not a copy.</b> Editing is what this is for and a copy per read would
    ///     make every drag allocate a mesh; what takes copies is the undo command, once per edit —
    ///     doc 24's D3.
    /// </remarks>
    public EditMesh? MeshOf(Entity entity) => meshes.GetValueOrDefault(entity);

    /// <summary>Whether an entity carries editable geometry.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it does.</returns>
    public bool HasMesh(Entity entity) => meshes.ContainsKey(entity);

    /// <summary>Gives an entity editable geometry, or takes it away.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="mesh">The mesh, or <see langword="null" /> to remove it.</param>
    /// <remarks>
    ///     ⚠ <b>Raises <see cref="Marked" />, which is what a viewport redraws from.</b> A mesh
    ///     arriving, going or being replaced wholesale is the same kind of event as an entity being
    ///     hidden: nothing about the world changed, and everything about what is drawn did.
    /// </remarks>
    public void SetMesh(Entity entity, EditMesh? mesh) {
        if (mesh is null) {
            if (!meshes.Remove(entity)) {
                return;
            }
        } else {
            meshes[entity] = mesh;
        }

        TouchMesh(entity);
    }

    /// <summary>Says that an entity's mesh has been changed in place.</summary>
    /// <param name="entity">Whose.</param>
    /// <remarks>
    ///     ⚠ <b>What a drag calls, and the reason <see cref="SetMesh" /> is not enough.</b> Moving a
    ///     vertex mutates the mesh the document already holds, so nothing about the dictionary changes
    ///     and nothing downstream would know the geometry it uploaded and the elements it cached are
    ///     now of a different shape. A version per entity rather than one for the document, because a
    ///     drag on one wall must not re-upload every other mesh in the level.
    /// </remarks>
    public void TouchMesh(Entity entity) {
        versions[entity] = versions.GetValueOrDefault(entity) + 1;
        Marked?.Invoke(this, entity);
    }

    /// <summary>How many times an entity's mesh has changed.</summary>
    /// <param name="entity">Whose.</param>
    /// <returns>A number that moves whenever the mesh does, and never goes back.</returns>
    /// <remarks>Zero for an entity that has never had one, which is the same answer as "unchanged
    ///     since you last asked" for a caller that has never asked — and both mean "nothing to do".</remarks>
    public int MeshVersion(Entity entity) => versions.GetValueOrDefault(entity);

    /// <summary>The live parameters an entity's shape still has, or <see langword="null" />.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Its parameters.</returns>
    /// <remarks>Null for an entity that never had any and for one that has been demoted — the two are
    ///     the same thing from here, which is what makes the door one-way.</remarks>
    public ShapeParameters? ShapeOf(Entity entity) => shapes.TryGetValue(entity, out var shape) ? shape : null;

    /// <summary>Whether an entity's geometry is still generated from parameters.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it is.</returns>
    public bool IsParametric(Entity entity) => shapes.ContainsKey(entity);

    /// <summary>Whether an entity carries geometry that nothing generates any more.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it does.</returns>
    /// <remarks>
    ///     ⚠ <b>D6's badge, and it is derived rather than recorded.</b> "This was a shape and is now a
    ///     plain mesh" and "this is a plain mesh" are the same fact about what a designer can do to it
    ///     next, so a flag saying which of the two it got there by would be a flag that has to be
    ///     saved, migrated and kept true through an undo — and would put a different badge on a mesh
    ///     that arrived from an import, which is in exactly the same position.
    /// </remarks>
    public bool IsPlainMesh(Entity entity) => meshes.ContainsKey(entity) && !shapes.ContainsKey(entity);

    /// <summary>Gives an entity live parameters and the geometry they make, or takes them away.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="parameters">The parameters, or <see langword="null" /> to demote it to a plain mesh.</param>
    /// <remarks>
    ///     <para>
    ///         <b>Setting rebuilds the mesh; clearing leaves the mesh exactly where it is.</b> That
    ///         asymmetry is doc 24's D6 written as two lines of code: changing a parameter is a new
    ///         shape, and demoting one is the same geometry with nothing generating it any more.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not undoable by itself</b>, for the reason <see cref="SetMesh" /> is not:
    ///         <see cref="ShapeCommand" /> is what a person's edit goes through, and this is what the
    ///         command, a reader and a test call.
    ///     </para>
    /// </remarks>
    public void SetShape(Entity entity, ShapeParameters? parameters) {
        if (parameters is not { } shape) {
            if (shapes.Remove(entity)) {
                TouchMesh(entity);
            }

            return;
        }

        shapes[entity] = shape.Clamped();
        SetMesh(entity, MeshShapes.Create(shape));
    }

    /// <summary>Every entity that carries geometry, with it.</summary>
    public IReadOnlyDictionary<Entity, EditMesh> Meshes => meshes;

    /// <summary>Every entity whose geometry is still generated, with what it is generated from.</summary>
    public IReadOnlyDictionary<Entity, ShapeParameters> Shapes => shapes;

    /// <summary>What material each of an entity's face groups is drawn with.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The assignments, which is empty for an entity whose mesh is all one material.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's P5 per-face material, and the assignment is to a <i>group</i> rather than to
    ///         a face.</b> That is D2's whole reason for having groups: a wall's twelve faces after two
    ///         bevels are still one wall, and a material remembered per face index would be one that a
    ///         loop cut renumbered out from under.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>On the document rather than on the mesh, for <see cref="meshes" />' own reason.</b>
    ///         An <c>AssetReference</c> means nothing to <c>Vixen.Geometry</c>, which references
    ///         <c>Vixen.Core.Mathematics</c> and nothing else — doc 24's D1. The kernel owns which
    ///         faces are in which group; what a group <i>is</i> is the editor's.
    ///     </para>
    /// </remarks>
    public IReadOnlyDictionary<int, AssetReference> MaterialsOf(Entity entity) =>
        materials.TryGetValue(entity, out var assigned) ? assigned : Empty;

    /// <summary>Assigns a material to one of an entity's face groups, or takes one away.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="group">Which group.</param>
    /// <param name="material">The material, or <see cref="AssetReference.Null" /> to clear it.</param>
    /// <remarks>Not undoable by itself, for the reason <see cref="SetMesh" /> is not — see
    ///     <c>BlockoutSurfaces</c>, which is what a person's assignment goes through.</remarks>
    public void SetMaterial(Entity entity, int group, AssetReference material) {
        if (material.IsNull) {
            if (materials.TryGetValue(entity, out var assigned) && assigned.Remove(group)) {
                if (assigned.Count == 0) {
                    materials.Remove(entity);
                }

                TouchMesh(entity);
            }

            return;
        }

        if (!materials.TryGetValue(entity, out var table)) {
            table = [];
            materials[entity] = table;
        }

        table[group] = material;
        TouchMesh(entity);
    }

    /// <summary>Every entity with a material on one of its groups, with the assignments.</summary>
    public IReadOnlyDictionary<Entity, Dictionary<int, AssetReference>> Materials => materials;

    /// <summary>The boolean an entity's geometry is derived by, or <see langword="null" /> for none.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The node.</returns>
    public CsgNode? BooleanOf(Entity entity) => booleans.TryGetValue(entity, out var node) ? node : null;

    /// <summary>Whether an entity's geometry is derived from its children rather than authored.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>What an outliner badges and what an element mode refuses: editing a face of a derived
    ///     mesh is editing something the next re-evaluation will overwrite — see
    ///     <see cref="MeshEdit.Demote" />, which collapses the boolean rather than letting that
    ///     happen.</remarks>
    public bool IsDerived(Entity entity) => booleans.ContainsKey(entity);

    /// <summary>Gives an entity a boolean, or takes one away.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="node">The node, or <see langword="null" /> to collapse it to a plain mesh.</param>
    /// <remarks>Not undoable by itself, for <see cref="SetMesh" />'s reason —
    ///     <see cref="BooleanCommand" /> is what a person's edit goes through.</remarks>
    public void SetBoolean(Entity entity, CsgNode? node) {
        if (node is not { } value) {
            if (booleans.Remove(entity)) {
                TouchMesh(entity);
            }

            return;
        }

        booleans[entity] = value;
    }

    /// <summary>Every entity whose geometry is a boolean, with the node.</summary>
    public IReadOnlyDictionary<Entity, CsgNode> Booleans => booleans;

    static readonly Dictionary<int, AssetReference> Empty = [];

    /// <summary>Everything the editor is not drawing, directly.</summary>
    public IReadOnlyCollection<Entity> Hidden => hidden;

    /// <summary>Everything that refuses to be picked, directly.</summary>
    public IReadOnlyCollection<Entity> Locked => locked;

    void Mark(HashSet<Entity> set, Entity entity, bool marked) {
        if (marked ? !set.Add(entity) : !set.Remove(entity)) {
            return;
        }

        Marked?.Invoke(this, entity);
    }

    bool Inherited(HashSet<Entity> set, Entity entity) {
        if (set.Count == 0) {
            return false;
        }

        for (var current = entity; current != Entity.Null && World.IsAlive(current); current = Hierarchy.ParentOf(World, current)) {
            if (set.Contains(current)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Forgets the names of entities that are no longer alive.</summary>
    /// <returns>How many were forgotten.</returns>
    /// <remarks>
    ///     What a play-mode stop calls, after the selection has been translated: the restored world
    ///     has new handles, and the old ones name nothing. Not automatic, because "is this handle
    ///     still alive" per name per frame is a scan nobody asked for.
    /// </remarks>
    public int PruneNames() {
        List<Entity> dead = [];

        foreach (var entity in names.Keys) {
            if (!World.IsAlive(entity)) {
                dead.Add(entity);
            }
        }

        foreach (var entity in ids.Keys) {
            if (!World.IsAlive(entity) && !dead.Contains(entity)) {
                dead.Add(entity);
            }
        }

        foreach (var entity in dead) {
            names.Remove(entity);
            hidden.Remove(entity);
            locked.Remove(entity);
            meshes.Remove(entity);
            versions.Remove(entity);
            materials.Remove(entity);
            booleans.Remove(entity);
            shapes.Remove(entity);

            if (ids.Remove(entity, out var id)) {
                byId.Remove(id);
            }
        }

        return dead.Count;
    }

    /// <summary>Moves the names across a play-mode restore's translation table.</summary>
    /// <param name="translation">What <see cref="WorldSnapshot.Restore" /> returned.</param>
    /// <remarks>
    ///     Every entity gets a new handle on restore, so a name map keyed by the old ones names
    ///     nothing at all. Called with the same table the selection is translated through, so the two
    ///     cannot end up disagreeing about which entity is which.
    /// </remarks>
    public void Remap(IReadOnlyDictionary<Entity, Entity> translation) {
        ArgumentNullException.ThrowIfNull(translation);

        Dictionary<Entity, string> moved = new(names.Count);

        foreach (var (entity, name) in names) {
            // An entity with no translation was created during play mode and no longer exists, so
            // its name goes with it rather than being carried over onto whatever took its slot.
            if (translation.TryGetValue(entity, out var now)) {
                moved[now] = name;
            }
        }

        // The stable ids move with the names and for the same reason: they are what a file and a
        // reference name an entity by, and a table keyed by handles that no longer exist names
        // nothing at all.
        Dictionary<Entity, EntityId> movedIds = new(ids.Count);

        foreach (var (entity, id) in ids) {
            if (translation.TryGetValue(entity, out var now)) {
                movedIds[now] = id;
            }
        }

        names.Clear();
        ids.Clear();
        byId.Clear();

        foreach (var (entity, name) in moved) {
            names[entity] = name;
        }

        foreach (var (entity, id) in movedIds) {
            ids[entity] = id;
            byId[id] = entity;
        }

        // ⚠ And the marks, for the same reason. A hidden set keyed by handles that no longer exist
        // is one that comes back hiding whatever took those slots — which reads as objects
        // disappearing from the viewport when play mode stops.
        Translate(hidden, translation);
        Translate(locked, translation);

        // ⚠ And the geometry, which the table's own remarks always claimed and this is where it
        // becomes true. An edit mesh keyed by a handle that no longer exists is a corridor a designer
        // spent an hour on, still in memory and attached to nothing — and the entity that took its
        // slot draws whatever it happened to inherit. The parameters travel beside it, because a
        // shape whose mesh survived and whose parameters did not has silently been demoted by
        // pressing Play.
        Move(meshes, translation);
        Move(versions, translation);
        Move(shapes, translation);
        Move(materials, translation);
        Move(booleans, translation);

        StructureChanged?.Invoke(this);

        static void Move<T>(Dictionary<Entity, T> table, IReadOnlyDictionary<Entity, Entity> lookup) {
            if (table.Count == 0) {
                return;
            }

            Dictionary<Entity, T> moved = new(table.Count);

            foreach (var (entity, value) in table) {
                if (lookup.TryGetValue(entity, out var now)) {
                    moved[now] = value;
                }
            }

            table.Clear();

            foreach (var (entity, value) in moved) {
                table[entity] = value;
            }
        }

        static void Translate(HashSet<Entity> set, IReadOnlyDictionary<Entity, Entity> table) {
            if (set.Count == 0) {
                return;
            }

            List<Entity> moved = [];

            foreach (var entity in set) {
                if (table.TryGetValue(entity, out var now)) {
                    moved.Add(now);
                }
            }

            set.Clear();
            set.UnionWith(moved);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Nothing can write a scene yet.</exception>
    /// <remarks>
    ///     ⚠ <b>Throws rather than succeeding quietly.</b> <c>EditorDocument.Save</c> marks the
    ///     document clean afterwards, so a <c>SaveCore</c> that wrote nothing would leave a document
    ///     claiming to match a file that does not exist — and the next crash would take the work with
    ///     it. A shell with no <see cref="Writer" /> should not offer Save.
    /// </remarks>
    protected override void SaveCore() {
        if (Writer is not { } writer) {
            throw new InvalidOperationException(
                "This scene has no writer, so it cannot be saved. A scene's file format belongs to "
                + "the asset pipeline; set SceneDocument.Writer once there is one, and until then do "
                + "not offer Save for a scene."
            );
        }

        writer.Write(this);
    }

    /// <summary>Says the hierarchy has changed shape, for a command that changed it.</summary>
    internal void RaiseStructureChanged() => StructureChanged?.Invoke(this);

    internal void Assign(Entity entity, string name) {
        names[entity] = name;
        Renamed?.Invoke(this, entity);
    }
}
