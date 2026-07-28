// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Transforms;

namespace Vixen.Editor.SceneView;

/// <summary>Everything a deleted subtree has to remember in order to come back.</summary>
/// <remarks>
///     <para>
///         <b>A handle is the easy part.</b> <c>World.TryRecreate</c> gives that back; what it does
///         not give back is the entity — the components it held, what it was called, its stable id,
///         which parent it hung from and <i>where</i> among that parent's children it sat. All five
///         are recorded here before the delete, and put back in that order after an undo.
///     </para>
///     <para>
///         ⚠ <b>The components live in a world of their own rather than in a list of boxes.</b>
///         Components are unconstrained structs stored by type in chunks, so the only thing that can
///         hold an arbitrary one without knowing what it is, is a chunk — and the only thing that
///         makes chunks is a world. <c>CopyComponentsFrom</c> then moves them across in both
///         directions. Boxing them would need a per-type reflective path this engine deliberately
///         does not have.
///     </para>
///     <para>
///         ⚠ <b>The hierarchy components are not copied back, they are re-established.</b>
///         <c>Parent</c>, <c>Sibling</c> and <c>Child</c> are handles into a doubly-linked list whose
///         other ends are entities the delete also touched — the surviving parent's <c>Child</c>
///         pointer, in particular, was rewritten when the subtree was unlinked. Restoring the raw
///         values would produce a list that is internally consistent and detached from the list it
///         belongs to. So the restore archetype leaves them out and
///         <c>Hierarchy.SetParentAfter</c> puts each entity back where it was.
///     </para>
/// </remarks>
sealed class SubtreeSnapshot : IDisposable {
    /// <summary>One entity, and where it goes back to.</summary>
    /// <param name="Handle">The handle it had, which is the one it must get again.</param>
    /// <param name="Restore">The archetype to recreate it in, without the hierarchy's components.</param>
    /// <param name="Mirror">Where its components are being kept.</param>
    /// <param name="Parent">What it hung from, or null for a root.</param>
    /// <param name="After">Which sibling it sat behind, or null if it was first.</param>
    /// <param name="Name">What the document called it, or null if it was never named.</param>
    /// <param name="Id">Its stable id, or empty if one was never minted.</param>
    readonly record struct Item(
        Entity Handle,
        Archetype Restore,
        Entity Mirror,
        Entity Parent,
        Entity After,
        string? Name,
        EntityId Id
    );

    static readonly ComponentTypeId[] Structural = [
        ComponentType<Parent>.Id,
        ComponentType<Child>.Id,
        ComponentType<Sibling>.Id,
        ComponentType<HierarchyDepth>.Id
    ];

    readonly World scratch = new("Deleted");
    readonly List<Item> items = [];

    /// <summary>How many entities were taken.</summary>
    public int Count => items.Count;

    /// <summary>The handles that were taken, parents before children.</summary>
    public IEnumerable<Entity> Handles => items.Select(item => item.Handle);

    /// <summary>Records a subtree and then destroys it.</summary>
    /// <param name="document">The document it belongs to.</param>
    /// <param name="root">The subtree's root.</param>
    /// <returns>The snapshot, or <see langword="null" /> if the root was not alive.</returns>
    public static SubtreeSnapshot? Take(SceneDocument document, Entity root) {
        ArgumentNullException.ThrowIfNull(document);

        var world = document.World;

        if (!world.IsAlive(root)) {
            return null;
        }

        var snapshot = new SubtreeSnapshot();

        // ⚠ Top-down, so restoring in this order always has a parent to hang from. Recording
        // bottom-up would be the natural way to write a destroy and the wrong way to write a restore.
        snapshot.Record(document, root);

        Hierarchy.DestroySubtree(world, root);
        return snapshot;
    }

    /// <summary>Whether every handle can still be given back.</summary>
    /// <param name="world">The world.</param>
    /// <returns>Whether restoring would succeed.</returns>
    /// <remarks>
    ///     ⚠ <b>Asked before anything is restored, and that is the point.</b> A slot taken since the
    ///     delete is refused for ever — see <c>World.TryRecreate</c> — and finding that out halfway
    ///     through would leave a half-restored subtree with no way back. This is what lets the
    ///     command refuse as a whole.
    /// </remarks>
    public bool CanRestore(World world) {
        ArgumentNullException.ThrowIfNull(world);
        return items.All(item => world.CanRecreate(item.Handle));
    }

    /// <summary>Puts the subtree back, exactly where it was.</summary>
    /// <param name="document">The document.</param>
    /// <exception cref="InvalidOperationException">A handle has been taken since the delete.</exception>
    public void Restore(SceneDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var world = document.World;

        if (!CanRestore(world)) {
            throw new InvalidOperationException(
                "This delete cannot be undone: something has taken one of the entity slots it freed, "
                + "and the ECS refuses to hand a handle back once that has happened — see "
                + "World.TryRecreate. Nothing has been changed."
            );
        }

        foreach (var item in items) {
            world.TryRecreate(item.Handle, item.Restore);
            world.CopyComponentsFrom(item.Handle, scratch, item.Mirror);

            if (!item.Parent.IsNull) {
                // Position first and parentage with it, because `SetParentAfter` does both — and the
                // parent is either an entity outside the subtree, which never moved, or one restored
                // earlier in this loop, which is why the recording is top-down.
                Hierarchy.SetParentAfter(world, item.Handle, item.Parent, item.After);
            }

            if (item.Name is { } name) {
                document.Assign(item.Handle, name);
            }

            document.Adopt(item.Handle, item.Id);
        }
    }

    /// <inheritdoc />
    public void Dispose() => scratch.Dispose();

    void Record(SceneDocument document, Entity entity) {
        var world = document.World;
        var signature = world.ArchetypeOf(entity).Signature;
        var restore = signature;

        foreach (var structural in Structural) {
            restore = restore.Without(structural);
        }

        // ⚠ The mirror keeps the *whole* signature, including the hierarchy's components, because
        // `CopyComponentsFrom` moves the intersection of two archetypes — and the intersection with
        // the restore archetype is what leaves them behind on the way back. A mirror that dropped
        // them would work too; keeping them costs four handles and makes the asymmetry live in one
        // place instead of two.
        var mirror = scratch.Create(scratch.ArchetypeOf(signature.Ids));

        scratch.CopyComponentsFrom(mirror, world, entity);

        items.Add(
            new(
                entity,
                world.ArchetypeOf(restore.Ids),
                mirror,
                Hierarchy.ParentOf(world, entity),
                Hierarchy.PreviousSiblingOf(world, entity),
                document.TryGetName(entity, out var name) ? name : null,
                document.TryGetId(entity, out var id) ? id : EntityId.None
            )
        );

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            Record(document, child);
        }
    }
}
