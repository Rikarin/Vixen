// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Geometry;

namespace Vixen.Editor.SceneView;

/// <summary>Assigning a material to one of an entity's face groups, undoably.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's P5 per-face material, on the stack.</b> Two references and a group number, which
///         is the whole of what an assignment is — see <see cref="SceneDocument.MaterialsOf" /> for why
///         it is filed against the group rather than against the face.
///     </para>
///     <para>
///         ⚠ <b>It does not demote a parametric shape, unlike every other surface verb.</b> The
///         assignment lives on the document beside the mesh rather than inside it, so regenerating the
///         geometry from its parameters leaves it exactly where it was — which is what lets a designer
///         dress a corridor and then still make it a metre wider.
///     </para>
/// </remarks>
public sealed class MaterialCommand : IEditorCommand {
    readonly SceneDocument document;
    readonly Entity entity;
    readonly int group;
    readonly AssetReference before;
    readonly AssetReference after;

    /// <summary>Describes assigning a material to a face group.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">Whose.</param>
    /// <param name="group">Which group.</param>
    /// <param name="before">What it was, or <see cref="AssetReference.Null" />.</param>
    /// <param name="after">What it should be, or <see cref="AssetReference.Null" /> to clear it.</param>
    public MaterialCommand(
        SceneDocument document,
        Entity entity,
        int group,
        AssetReference before,
        AssetReference after
    ) {
        ArgumentNullException.ThrowIfNull(document);

        this.document = document;
        this.entity = entity;
        this.group = group;
        this.before = before;
        this.after = after;
    }

    /// <inheritdoc />
    public string Name => "Assign Material";

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.SetMaterial(entity, group, after);
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.SetMaterial(entity, group, before);
        context.Touch(document);
    }

    /// <inheritdoc />
    /// <remarks>⚠ <b>Two assignments do not merge</b>, for <c>RenameEntityCommand</c>'s reason: they
    ///     are two decisions rather than two frames of one, and a designer trying two materials in a
    ///     row is entitled to step back to the first.</remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }
}

/// <summary>One undoable change to a parametric shape: its numbers, or the loss of them.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's D6 on the undo stack, and it records no geometry at all.</b> A parametric
///         entity's mesh is a function of its parameters, so putting the parameters back is what puts
///         the mesh back — exactly, and for four numbers rather than for a copy of the mesh. That is
///         the one place this differs from <see cref="EditMeshCommand" />, which has to record whole
///         meshes because a topology change has no inverse.
///     </para>
///     <para>
///         ⚠ <b>Demoting records nothing either, and leaves the geometry exactly where it is.</b> The
///         mesh a shape generated is already in the document; taking the parameters away changes
///         nothing about it. An undo puts them back and regenerates — which produces the same mesh,
///         because nothing has edited it yet: the first edit is its own entry on the stack and is
///         undone first.
///     </para>
///     <para>
///         ⚠ <b>Two parameter edits of one shape merge and two of different shapes do not.</b>
///         Dragging a corridor's width field is one decision made over forty frames, and forty entries
///         for it is the shape of every "undo did not undo what I did" report — the same argument
///         <see cref="EditMeshCommand" /> makes about a gizmo drag. A <i>demotion</i> never merges with
///         anything, because it is a different kind of act and one people are entitled to step back
///         over on its own.
///     </para>
/// </remarks>
public sealed class ShapeCommand : IEditorCommand {
    readonly SceneDocument document;
    readonly Entity entity;
    readonly ShapeParameters? before;

    ShapeParameters? after;

    ShapeCommand(SceneDocument document, Entity entity, ShapeParameters? before, ShapeParameters? after, string name) {
        this.document = document;
        this.entity = entity;
        this.before = before;
        this.after = after;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Whether this is the one-way door rather than a change of numbers.</summary>
    public bool IsDemotion => after is null;

    /// <summary>Which entity's shape it changes.</summary>
    public Entity Entity => entity;

    /// <summary>Records giving an entity a shape, or changing the one it has.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="parameters">What it should be.</param>
    /// <param name="name">What the history calls it.</param>
    /// <returns>The command, not yet run.</returns>
    /// <remarks>The state before is read from the document at construction, so this must be built
    ///     before anything applies the change — which is the same contract <c>EditMeshCommand.Rebuilt</c>
    ///     has and the opposite of its position form.</remarks>
    public static ShapeCommand Set(
        SceneDocument document,
        Entity entity,
        ShapeParameters parameters,
        string name = "Shape"
    ) {
        ArgumentNullException.ThrowIfNull(document);

        return new(document, entity, document.ShapeOf(entity), parameters, name);
    }

    /// <summary>Records the demotion: the parameters go and the geometry stays.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="name">What the history calls it.</param>
    /// <returns>The command, not yet run.</returns>
    public static ShapeCommand Demote(SceneDocument document, Entity entity, string name = "Make Editable") {
        ArgumentNullException.ThrowIfNull(document);

        return new(document, entity, document.ShapeOf(entity), null, name);
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.SetShape(entity, after);
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.SetShape(entity, before);
        context.Touch(document);
    }

    /// <inheritdoc />
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        if (IsDemotion
            || previous is not ShapeCommand earlier
            || earlier.IsDemotion
            || earlier.entity != entity
            || earlier.document != document) {
            return false;
        }

        // ⚠ The earlier command is the one that survives, with this one's values written into it. Its
        // `before` is where the drag started, which is what an undo of the whole gesture has to
        // restore — taking this one and copying the old `before` across would work equally well and
        // would leave two live objects claiming the same history slot.
        earlier.after = after;
        merged = earlier;

        return true;
    }
}
