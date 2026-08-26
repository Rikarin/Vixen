// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Transforms;

namespace Vixen.Editor.SceneView;

/// <summary>Deleting entities, and putting them back.</summary>
/// <remarks>
///     <para>
///         <b>Delete is the primitive and create is written in terms of it.</b> Undoing a creation is
///         a deletion and undoing a deletion is a restore, so one snapshot type serves both and the
///         two commands differ only in which way round <c>Do</c> and <c>Undo</c> are. Writing them
///         separately would be two chances to get the sibling order wrong.
///     </para>
///     <para>
///         ⚠ <b>A delete takes the whole subtree.</b> Destroying a parent and leaving its children is
///         a set of orphans holding a <c>Parent</c> that names a dead entity, and every walk over the
///         hierarchy then throws — see <c>Hierarchy.DestroySubtree</c>. So the children go too, and
///         the snapshot remembers all of them.
///     </para>
/// </remarks>
public sealed class DestroyEntitiesCommand : IEditorCommand, IDisposable {
    readonly SceneDocument document;
    readonly List<SubtreeSnapshot> taken = [];
    readonly List<Entity> roots;

    /// <summary>Which of a template's entities this delete recorded as removed, and from where.</summary>
    /// <remarks>
    ///     ⚠⚠ <b>Recorded on the way out and taken back on the way in.</b> Deleting a child of a prefab
    ///     instance is the author saying "not in this one", and the file has to hold that sentence
    ///     before anything ever adds a template's children back — doc 47 § 6. An undo has to unsay it
    ///     just as exactly: a level in which Ctrl+Z restored the lamp and left "the lamp was deleted"
    ///     in the file is one that loses the lamp again the next time a prefab grows a child.
    /// </remarks>
    readonly List<(Entity Root, EntityId Source)> noted = [];

    /// <inheritdoc />
    public string Name => taken.Count == 1 && taken[0].Count == 1 ? "Delete Entity" : "Delete Entities";

    /// <summary>Describes deleting some entities and everything below them.</summary>
    /// <param name="document">The document they live in.</param>
    /// <param name="entities">The subtree roots to delete.</param>
    public DestroyEntitiesCommand(SceneDocument document, IEnumerable<Entity> entities) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(entities);

        this.document = document;
        roots = [.. entities];
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Release();
        noted.Clear();

        foreach (var root in roots) {
            // ⚠ Before the snapshot, because finding which prefab instance a subtree belonged to means
            // walking up from it — and the delete is what takes those parent links away.
            document.NoteRemoved(root, noted);

            // ⚠ Re-checked rather than trusted, because a redo runs against a world that has moved on
            // — and because deleting a parent and one of its own children in the same command means
            // the second root is already gone by the time this reaches it.
            if (SubtreeSnapshot.Take(document, root) is { } snapshot) {
                taken.Add(snapshot);
            }
        }

        Finish(context);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        // ⚠ All of them checked before any of them is restored. A slot taken since the delete is
        // refused for ever, and discovering that halfway through would leave a subtree half back.
        foreach (var snapshot in taken) {
            if (!snapshot.CanRestore(document.World)) {
                throw new InvalidOperationException(
                    "This delete cannot be undone: something has taken one of the entity slots it "
                    + "freed. Nothing has been changed."
                );
            }
        }

        // Backwards, so that entities restored earlier are in place to be sat behind: the snapshots
        // were taken in order and each one's recorded neighbour may be in the one before it.
        for (var index = taken.Count - 1; index >= 0; index--) {
            taken[index].Restore(document);
        }

        // ⚠ And the removals are unsaid, after the subtrees are back. A level in which the child
        // returned and "the child was deleted" stayed in the file is one that loses it again the next
        // time anything reconciles — which is the failure the list exists to prevent, arriving through
        // its own front door.
        foreach (var (root, source) in noted) {
            document.Prefabs.Restore(root, source);
        }

        noted.Clear();

        Finish(context);
    }

    /// <inheritdoc />
    /// <remarks>Two deletes never merge: they are two decisions, and undo should take them apart.</remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The snapshots hold a world each, and a command stack that drops an entry does not
    ///     dispose it.</b> So this is reachable only from a caller that clears the stack — which is
    ///     honest rather than complete, and is why a snapshot holds components and not textures.
    /// </remarks>
    public void Dispose() => Release();

    void Release() {
        foreach (var snapshot in taken) {
            snapshot.Dispose();
        }

        taken.Clear();
    }

    void Finish(EditorContext context) {
        document.PruneNames();
        document.RaiseStructureChanged();
        context.Touch(document);
    }
}

/// <summary>Putting a whole subtree into a scene at once, undoably.</summary>
/// <remarks>
///     <para>
///         <b>What a prefab drop is, and what <see cref="CreateEntityCommand" /> cannot be.</b> That
///         command makes one entity and takes its components from an <c>Initialise</c> callback;
///         placing a prefab makes a root and everything below it, and how many entities that is is
///         the file's business rather than the command's. So the caller supplies the making and this
///         supplies the undo.
///     </para>
///     <para>
///         ⚠ <b>The subtree is made by <c>Do</c> and only ever once.</b> A redo restores the snapshot
///         the undo took, which already holds every entity, every component, every name, every stable
///         id and every prefab link — running the factory again would place a <i>second</i> instance
///         with fresh handles, and every reference to the first would then be addressing nothing.
///         That is <see cref="CreateEntityCommand.Initialise" />'s rule, at subtree scale.
///     </para>
///     <para>
///         ⚠ <b>A factory that makes nothing is not an error here.</b> A prefab whose file has gone
///         between the drag and the drop is a real case; the command then has no subtree, undoes to
///         nothing and redoes to nothing rather than throwing out of a gesture.
///     </para>
/// </remarks>
public sealed class InstantiateSubtreeCommand : IEditorCommand, IDisposable {
    readonly SceneDocument document;
    readonly Func<Entity> make;
    readonly string name;

    SubtreeSnapshot? taken;
    Entity created;
    bool made;

    /// <inheritdoc />
    public string Name => name;

    /// <summary>The subtree's root, once <c>Do</c> has run.</summary>
    public Entity Entity => created;

    /// <summary>Describes placing a subtree.</summary>
    /// <param name="document">The document to place it in.</param>
    /// <param name="name">What the undo history calls it.</param>
    /// <param name="make">What makes the subtree and hands back its root. Run once.</param>
    public InstantiateSubtreeCommand(SceneDocument document, string name, Func<Entity> make) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(make);

        this.document = document;
        this.name = name;
        this.make = make;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        if (taken is { } snapshot) {
            snapshot.Restore(document);
            snapshot.Dispose();
            taken = null;
        } else if (!made) {
            made = true;
            created = make();
        }

        document.RaiseStructureChanged();
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        if (!created.IsNull) {
            taken = SubtreeSnapshot.Take(document, created);
        }

        // ⚠ After the snapshot and not before it. `PruneNames` is what drops the names, the stable
        // ids and the prefab links of dead handles, and the snapshot is what remembers them — taking
        // it second would be remembering a subtree the document had already forgotten.
        document.PruneNames();
        document.RaiseStructureChanged();
        context.Touch(document);
    }

    /// <inheritdoc />
    /// <remarks>Two placements never merge: they are two things in the level.</remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }

    /// <inheritdoc />
    public void Dispose() {
        taken?.Dispose();
        taken = null;
    }
}

/// <summary>Creating an entity, undoably.</summary>
/// <remarks>
///     ⚠ <b>The entity is created by <c>Do</c> and not by the constructor.</b> A command that had
///     already done its work before being executed would do it twice on a redo — and the handle a
///     redo produces has to be the same one, which is what makes the snapshot the right way to
///     undo it.
/// </remarks>
public sealed class CreateEntityCommand : IEditorCommand, IDisposable {
    readonly SceneDocument document;
    readonly string name;
    readonly LocalTransform local;
    readonly Entity parent;

    SubtreeSnapshot? taken;
    Entity created;

    /// <inheritdoc />
    public string Name => "Create Entity";

    /// <summary>The entity, once <c>Do</c> has run.</summary>
    public Entity Entity => created;

    /// <summary>What to put on the entity beyond a transform and a name, if anything.</summary>
    /// <remarks>
    ///     ⚠ <b>Run on the first <c>Do</c> and never again.</b> A redo restores the snapshot the undo
    ///     took, which already carries every component the entity had — running this a second time
    ///     would at best repeat itself and at worst overwrite whatever the user changed between the
    ///     creation and the undo.
    /// </remarks>
    public Action<Entity>? Initialise { get; init; }

    /// <summary>Describes creating an entity.</summary>
    /// <param name="document">The document.</param>
    /// <param name="name">What to call it.</param>
    /// <param name="local">Where it starts.</param>
    /// <param name="parent">What to hang it from, or null for a root.</param>
    public CreateEntityCommand(SceneDocument document, string name, LocalTransform local, Entity parent = default) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(name);

        this.document = document;
        this.name = name;
        this.local = local;
        this.parent = parent;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        if (taken is { } snapshot) {
            // A redo. Putting the same entity back is what keeps the handle stable across the whole
            // undo history — a fresh `Add` here would hand back a different one and every reference
            // to the first would be addressing nothing.
            snapshot.Restore(document);
            snapshot.Dispose();
            taken = null;
        } else {
            created = document.Add(name, local, parent);
            Initialise?.Invoke(created);
        }

        document.RaiseStructureChanged();
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        taken = SubtreeSnapshot.Take(document, created);

        document.PruneNames();
        document.RaiseStructureChanged();
        context.Touch(document);
    }

    /// <inheritdoc />
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }

    /// <inheritdoc />
    public void Dispose() {
        taken?.Dispose();
        taken = null;
    }
}
