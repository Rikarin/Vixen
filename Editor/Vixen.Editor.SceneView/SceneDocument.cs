// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Rendering;

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
        Scenes = new(world);
        Scene = Scenes.Create(title);
    }

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
    ///     change what it is: the shape is <see cref="MeshShape" /> and the name is a label.
    /// </remarks>
    public Entity CreateShape(PrimitiveKind kind, LocalTransform local, Entity parent = default) =>
        Create(MeshShapes.NameOf(kind), local, parent, entity => MeshShapes.Attach(World, entity, kind));

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
    ///     ⚠ <b>Not undoable either, and for a different reason.</b> Reparenting <i>is</i> reversible
    ///     — the old parent is a handle that still exists — but undoing it has to put the entity back
    ///     at the same index among its old siblings, and the intrusive sibling list records a
    ///     neighbour rather than a position. Recording the neighbour is the fix and it is a command
    ///     this does not have yet.
    /// </remarks>
    public bool Reparent(Entity entity, Entity parent) {
        if (!World.IsAlive(entity) || Hierarchy.ParentOf(World, entity) == parent) {
            return false;
        }

        if (!parent.IsNull && Hierarchy.IsAncestorOf(World, entity, parent)) {
            // A cycle. The hierarchy would accept it and the transform pass would then walk for ever.
            return false;
        }

        Hierarchy.SetParentKeepingWorldPosition(World, entity, parent);
        StructureChanged?.Invoke(this);

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

        StructureChanged?.Invoke(this);
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
