// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Editor.Core;

namespace Vixen.Editor.Texturing.Layers;

/// <summary>Where a layer is in a stack: the texture set it belongs to, and its own identity.</summary>
/// <param name="Set">The <see cref="TextureSetAsset.Name" />.</param>
/// <param name="Id">The <see cref="LayerAsset.Id" />.</param>
/// <remarks>
///     ⚠ <b>An id and not an index, and that is the whole reason <see cref="LayerAsset.Id" />
///     exists.</b> A command records where it acted so that its undo can act in the same place, and
///     both of the obvious coordinates move: an index moves when anything under the layer is
///     reordered, and a name moves when somebody renames it. An anchor already names a layer this
///     way for exactly that reason, and <c>LayerStackGraph.Duplicates</c> refuses a stack in which
///     the key is not unique — so an addressing scheme built on it inherits a check that already
///     exists rather than needing one of its own.
/// </remarks>
readonly record struct LayerPath(string Set, string Id);

/// <summary>Finding and moving a layer inside a stack, by <see cref="LayerPath" />.</summary>
/// <remarks>
///     ⚠ <b>A layer's parent list is what a reorder happens in, and it is not always the set's.</b> A
///     group's children are their own list, composited over whatever is beneath the group — so
///     "move this layer up" inside a group means a different list from the same words outside one,
///     and a lookup that only walked <see cref="TextureSetAsset.Layers" /> would silently do nothing
///     for every layer an artist put in a group.
/// </remarks>
static class LayerStackEdit {
    /// <summary>The list holding a layer, and where in it.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="parent">The list it is in.</param>
    /// <param name="index">Where in that list, counting from the bottom of the composite.</param>
    /// <returns>Whether it was found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    public static bool TryFind(
        LayerStackAsset stack,
        LayerPath path,
        [NotNullWhen(true)] out List<LayerAsset>? parent,
        out int index
    ) {
        ArgumentNullException.ThrowIfNull(stack);

        parent = null;
        index = -1;

        if (stack.SetNamed(path.Set) is not { } set) {
            return false;
        }

        return Walk(set.Layers, path.Id, out parent, out index);
    }

    /// <summary>The layer at a path, or <see langword="null" />.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    public static LayerAsset? Find(LayerStackAsset stack, LayerPath path) =>
        TryFind(stack, path, out var parent, out var index) ? parent[index] : null;

    /// <summary>Puts a different layer in that place.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="value">What to put there.</param>
    /// <returns>Whether there was a layer to replace.</returns>
    /// <exception cref="ArgumentNullException">The stack or the value is null.</exception>
    public static bool Replace(LayerStackAsset stack, LayerPath path, LayerAsset value) {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryFind(stack, path, out var parent, out var index)) {
            return false;
        }

        parent[index] = value;

        return true;
    }

    /// <summary>Moves a layer within the list it is already in.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="delta">How far, in composite order: <c>+1</c> is one step later, over its neighbour.</param>
    /// <returns>Whether it moved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>In composite order, which is the file's order and the reverse of the panel's.</b>
    ///     <see cref="TextureSetAsset.Layers" /> is stored bottom first so that reading the file is
    ///     reading the arithmetic; a panel draws the top layer at the top and therefore asks for
    ///     <c>+1</c> when somebody presses the button labelled <em>up</em>. Expressing the move in
    ///     the file's terms keeps the one reversal in the view, which is where that member's own
    ///     remarks ask for it — a command that took "up" would put a second copy of the reversal in
    ///     the undo history, where nobody reading the file can see it.
    /// </remarks>
    public static bool Move(LayerStackAsset stack, LayerPath path, int delta) {
        if (delta == 0 || !TryFind(stack, path, out var parent, out var index)) {
            return false;
        }

        var target = index + delta;

        if (target < 0 || target >= parent.Count) {
            return false;
        }

        var layer = parent[index];

        parent.RemoveAt(index);
        parent.Insert(target, layer);

        return true;
    }

    static bool Walk(
        List<LayerAsset> layers,
        string id,
        [NotNullWhen(true)] out List<LayerAsset>? parent,
        out int index
    ) {
        for (var position = 0; position < layers.Count; position++) {
            if (string.Equals(layers[position].Id, id, StringComparison.Ordinal)) {
                parent = layers;
                index = position;

                return true;
            }

            if (Walk(layers[position].Children, id, out parent, out index)) {
                return true;
            }
        }

        parent = null;
        index = -1;

        return false;
    }
}

/// <summary>One layer replaced by another version of itself, as one undo entry.</summary>
/// <remarks>
///     <para>
///         <b>One command type for every property of a layer, rather than one per property.</b> A
///         <see cref="LayerAsset" /> is a record with fifteen members and a command per member would
///         be fifteen types whose bodies are the same two lines; what actually differs between
///         "set the blend mode" and "drag the opacity" is the sentence in the undo menu and whether
///         two of them are one edit, so both of those are parameters and nothing else is.
///     </para>
///     <para>
///         ⚠ <b>The record's collection members are shared by <c>with</c>, and a command that forgets
///         it has no undo at all.</b> <c>after = before with { Blend = … }</c> hands the new value the
///         <em>same</em> <c>List&lt;string&gt; Channels</c> instance — so a caller that then mutated
///         that list in place would be mutating the before-image too, and <see cref="Undo" /> would
///         put back a layer that already holds the new value. Every caller here builds a new
///         collection instead; that is not a style preference, it is what makes the entry reversible.
///     </para>
///     <para>
///         ⚠ <b>Merging is by an explicit key rather than by type, because the two cases really are
///         different.</b> Dragging an opacity slider is one edit and produces one entry; choosing a
///         blend mode twice is two decisions and produces two. A command type that merged with every
///         command of its own type would collapse the second into the first, and one that never
///         merged would put three hundred entries in the history for one drag. The key is what the
///         call site knows and the type cannot: <c>PaintStrokeCommand</c> answers the same question
///         by writing <em>never</em> down and saying why.
///     </para>
/// </remarks>
sealed class SetLayerCommand : IEditorCommand {
    readonly LayerStackDocument document;
    readonly LayerPath path;
    readonly LayerAsset before;
    readonly LayerAsset after;
    readonly string mergeKey;

    /// <summary>Records a replacement.</summary>
    /// <param name="document">The stack it happens in.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="before">What it was.</param>
    /// <param name="after">What it becomes.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <param name="mergeKey">
    ///     What makes two of these one edit, or empty for a change that always stands alone.
    /// </param>
    /// <exception cref="ArgumentNullException">The document, the before or the after is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    public SetLayerCommand(
        LayerStackDocument document,
        LayerPath path,
        LayerAsset before,
        LayerAsset after,
        string name,
        string mergeKey = ""
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentException.ThrowIfNullOrEmpty(name);

        this.document = document;
        this.path = path;
        this.before = before;
        this.after = after;
        this.mergeKey = mergeKey;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Unlike <c>PaintStrokeCommand</c>, this one applies the change rather than recording an
    ///     applied one.</b> A stroke exists because a brush already painted texels and the command is
    ///     built at pointer-up; a property set has nothing that happened first, so the caller builds
    ///     the command and <c>Execute</c> is what makes the edit. Both shapes are in this repository
    ///     and the difference is whose job the first application is.
    /// </remarks>
    public void Do(EditorContext context) => LayerStackEdit.Replace(document.Document, path, after);

    /// <inheritdoc />
    public void Undo(EditorContext context) => LayerStackEdit.Replace(document.Document, path, before);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The merged entry keeps <em>this</em> command's after-image and the <em>previous</em>
    ///     one's before-image</b>, which is what <see cref="IEditorCommand.TryMergeWith" /> asks for:
    ///     undoing a merged drag has to reach the value the slider held before the drag started, not
    ///     the one it held a frame ago.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        if (mergeKey.Length == 0
            || previous is not SetLayerCommand earlier
            || !ReferenceEquals(earlier.document, document)
            || earlier.path != path
            || !string.Equals(earlier.mergeKey, mergeKey, StringComparison.Ordinal)) {
            return false;
        }

        merged = new SetLayerCommand(document, path, earlier.before, after, Name, mergeKey);

        return true;
    }
}

/// <summary>A layer moved past its neighbour, as one undo entry.</summary>
/// <remarks>
///     <para>
///         <b>What <a href="https://github.com/Rikarin/Vixen/issues/819">#819</a> names first, and the
///         one edit whose effect is not a number.</b> A reorder changes which layer's operator reads
///         which backdrop, so what it produces is a different <em>graph</em> — which is why the test
///         that covers it compares two compiled plans rather than two lists.
///     </para>
///     <para>
///         ⚠ <b>It never merges, and that is a decision rather than the default.</b> Two presses of
///         the same button are two edits: an artist who moved a layer up twice and pressed undo means
///         to be one step down, not back where they started. That is the opposite answer from the
///         opacity slider next to it, and the two sitting in one file is the clearest place to say
///         why they differ — a drag is one gesture reported many times, a reorder is many gestures.
///     </para>
/// </remarks>
sealed class MoveLayerCommand : IEditorCommand {
    readonly LayerStackDocument document;
    readonly LayerPath path;
    readonly int delta;

    /// <summary>Records a move.</summary>
    /// <param name="document">The stack it happens in.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="delta">How far in composite order. <c>+1</c> is one step later.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty, or the move is nowhere.</exception>
    public MoveLayerCommand(LayerStackDocument document, LayerPath path, int delta, string name) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (delta == 0) {
            throw new ArgumentException(
                "A move of nothing is not an undo entry; ask CanMove before making one.",
                nameof(delta)
            );
        }

        this.document = document;
        this.path = path;
        this.delta = delta;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Whether a layer could move that far in the list it is in.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="delta">How far in composite order.</param>
    /// <returns>Whether the move would do anything.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    /// <remarks>
    ///     What a panel disables the button from, so that the top row's <em>up</em> is greyed rather
    ///     than pressed to no effect — and the same question <see cref="Do" /> would answer with a
    ///     silent no.
    /// </remarks>
    public static bool CanMove(LayerStackAsset stack, LayerPath path, int delta) {
        if (delta == 0 || !LayerStackEdit.TryFind(stack, path, out var parent, out var index)) {
            return false;
        }

        var target = index + delta;

        return target >= 0 && target < parent.Count;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) => LayerStackEdit.Move(document.Document, path, delta);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The opposite delta and not a remembered index, which is only the same thing because
    ///     the move stays inside one list.</b> Removing at <c>i</c> and inserting at <c>i + d</c>
    ///     shifts everything between them by one step the other way, and putting the layer back at
    ///     <c>i</c> restores every one of them — so the inverse of a move is a move. A command that
    ///     could also change a layer's parent would need the whole position rather than the offset.
    /// </remarks>
    public void Undo(EditorContext context) => LayerStackEdit.Move(document.Document, path, -delta);

    /// <inheritdoc />
    /// <remarks>Never. See the type's remarks.</remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        return false;
    }
}
