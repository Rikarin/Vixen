// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Geometry;

namespace Vixen.Editor.SceneView;

/// <summary>One undoable edit to one mesh, at whichever of the two granularities it needs.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's D3, and it is one command type with two shapes rather than two types.</b> A
///         <i>position</i> change — a gizmo drag on selected vertices — records the before and after
///         positions of the elements it touched, and merges with the drag before it. A <i>topology</i>
///         change — extrude, bevel, boolean, loop cut — records the whole mesh, before and after.
///     </para>
///     <para>
///         ⚠ <b>The second looks wasteful and is the only honest answer.</b> A boolean has no inverse
///         to record; a bevel with three segments touches every table in the structure; and an "undo"
///         implemented as an inverse operation is a second implementation of every tool, which will
///         disagree with the first. A blockout mesh is a few thousand vertices — tens of kilobytes —
///         and <c>CommandStack.Capacity</c> defaults to 256, so the worst case is a few megabytes of
///         history for a mesh nobody has that big.
///     </para>
///     <para>
///         ⚠ <b>State the bound rather than discovering it.</b> A designer who spends an hour on one
///         mesh generates the deep history this is measured against, and <c>Capacity</c> is settable
///         for exactly that. A budget in bytes rather than in entries is the change to make if it is
///         ever hit — recorded here so that it is a decision rather than a surprise.
///     </para>
///     <para>
///         ⚠ <b>The mesh is <i>replaced</i> on undo, not mutated back.</b> The document holds a
///         reference and so does everything drawing it, so putting the recorded object back is what
///         makes a redo of a topology change exact — and it is why the recorded meshes are deep copies
///         rather than the live one. A command that stored a reference where it needed a copy is
///         precisely what <c>Vixen.Editor.Core</c>'s randomised do/undo/redo suite exists to catch.
///     </para>
/// </remarks>
public sealed class EditMeshCommand : IEditorCommand {
    readonly SceneDocument document;
    readonly Entity entity;

    /// <summary>The whole mesh either side of the edit, for a topology change.</summary>
    /// <remarks>Set by <see cref="Rebuilt" /> through an object initialiser, which is why neither is
    ///     <c>readonly</c> — the constructor is private and takes what every command needs.</remarks>
    EditMesh? before;
    EditMesh? after;

    /// <summary>Which positions moved, and where they were and are, for a position change.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>readonly</c>, because a merge writes into <see cref="from" />.</b> The arrays
    ///     themselves are never replaced after construction — the object identity is what
    ///     <see cref="TryMergeWith" /> relies on — but the values in one of them are, which is what
    ///     makes a merged drag undo to where the first of them started.
    /// </remarks>
    int[] moved = [];
    Vector3[] from = [];
    Vector3[] to = [];

    EditMeshCommand(SceneDocument document, Entity entity, string name) {
        this.document = document;
        this.entity = entity;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Whether this one records the whole mesh rather than a handful of positions.</summary>
    /// <remarks>
    ///     ⚠ <b>A stored flag rather than "is there an <c>after</c>", because removing a mesh
    ///     entirely is a topology change whose <c>after</c> is nothing.</b> That is what a bake does —
    ///     the geometry becomes an asset and the entity stops carrying any — and deriving the flag
    ///     from the field made that command silently take the <i>position</i> path and undo to
    ///     nothing at all.
    /// </remarks>
    public bool IsTopology { get; private init; }

    /// <summary>Records a move of some of a mesh's shared positions, already applied.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">Whose mesh.</param>
    /// <param name="positions">Which positions moved.</param>
    /// <param name="was">Where each of them was, in the same order.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <returns>The command.</returns>
    /// <remarks>
    ///     ⚠ <b>Built <i>after</i> the move, from where things are now.</b> Everything else in the
    ///     editor records this way — see <c>TransformTargetsCommand</c> — because a drag applies as it
    ///     goes and the command is what makes the finished state undoable rather than what performs
    ///     it.
    /// </remarks>
    public static EditMeshCommand Moved(
        SceneDocument document,
        Entity entity,
        ReadOnlySpan<int> positions,
        ReadOnlySpan<Vector3> was,
        string name = "Move Vertices"
    ) {
        ArgumentNullException.ThrowIfNull(document);

        if (positions.Length != was.Length) {
            throw new ArgumentException(
                $"{positions.Length} positions moved and {was.Length} previous values were given.",
                nameof(was)
            );
        }

        var mesh = document.MeshOf(entity)
            ?? throw new InvalidOperationException("The entity has no mesh to have moved.");

        var command = new EditMeshCommand(document, entity, name) {
            moved = positions.ToArray(),
            from = was.ToArray(),
            to = new Vector3[positions.Length]
        };

        for (var index = 0; index < positions.Length; index++) {
            command.to[index] = mesh.Positions[positions[index]];
        }

        return command;
    }

    /// <summary>Records a change that altered the mesh's structure, already applied.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">Whose mesh.</param>
    /// <param name="was">The whole mesh as it was. A copy is taken.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <returns>The command.</returns>
    public static EditMeshCommand Rebuilt(
        SceneDocument document,
        Entity entity,
        EditMesh? was,
        string name = "Edit Mesh"
    ) {
        ArgumentNullException.ThrowIfNull(document);

        var now = document.MeshOf(entity);

        return new EditMeshCommand(document, entity, name) {
            // ⚠ Copies of both, and neither is the live mesh. The next edit mutates whatever the
            // document holds; a command holding that object would record a "before" that changes
            // under it, which is an undo that puts things back where they already are.
            before = was is null ? null : new EditMesh(was),
            after = now is null ? null : new EditMesh(now),
            IsTopology = true
        };
    }

    /// <summary>Records an entity's geometry being taken away, before it is.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">Whose mesh.</param>
    /// <param name="name">What the history calls it.</param>
    /// <returns>The command, which has not been run.</returns>
    /// <remarks>
    ///     ⚠ <b>Built <i>before</i> the mesh is removed, unlike <see cref="Rebuilt" />.</b> What this
    ///     records is a state that will not exist once the change has happened, so there is nothing to
    ///     read afterwards — which is why it is a second factory rather than a null argument to the
    ///     first. Doc 24's P7 bake is the caller: the geometry becomes an asset and the entity stops
    ///     carrying any.
    /// </remarks>
    public static EditMeshCommand Removed(SceneDocument document, Entity entity, string name = "Remove Mesh") {
        ArgumentNullException.ThrowIfNull(document);

        var was = document.MeshOf(entity);

        return new EditMeshCommand(document, entity, name) {
            before = was is null ? null : new EditMesh(was),
            after = null,
            IsTopology = true
        };
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        if (IsTopology) {
            document.SetMesh(entity, after is null ? null : new EditMesh(after));
            return;
        }

        Apply(to);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        if (IsTopology) {
            document.SetMesh(entity, before is null ? null : new EditMesh(before));
            return;
        }

        Apply(from);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only a position change merges, and only with one that moved the same positions of
    ///         the same mesh.</b> That is what makes a drag one entry in the history however many
    ///         frames it took — and merging two topology changes would mean throwing away the middle
    ///         state of a sequence whose steps are not reversible individually.
    ///     </para>
    ///     <para>
    ///         The <i>from</i> stays this command's and the <i>to</i> becomes the newer one's, which is
    ///         the same arithmetic <c>TransformTargetsCommand</c> uses: a merged drag undoes to where
    ///         the first of them started.
    ///     </para>
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        if (previous is not EditMeshCommand older
            || IsTopology
            || older.IsTopology
            || older.entity != entity
            || !ReferenceEquals(older.document, document)
            || !older.moved.AsSpan().SequenceEqual(moved)) {
            return false;
        }

        // ⚠ The older command's *from* and this one's *to*, which is what "undo to before the drag
        // started" means. The receiver is the new command being asked whether it can swallow the old
        // one, so taking this one's `from` would undo to one mouse-move ago.
        older.from.CopyTo(from.AsSpan());

        merged = this;
        return true;
    }

    void Apply(Vector3[] values) {
        if (document.MeshOf(entity) is not { } mesh) {
            return;
        }

        for (var index = 0; index < moved.Length; index++) {
            mesh.MovePosition(moved[index], values[index]);
        }

        document.SetMesh(entity, mesh);
    }
}
