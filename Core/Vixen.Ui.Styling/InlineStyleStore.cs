// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;

namespace Vixen.Ui.Styling;

/// <summary>A handle on one element's inline declarations.</summary>
/// <param name="Index">Its slot in the store.</param>
/// <remarks>
///     A type of its own rather than a <see cref="DeclarationRange" />, and the reason is a bug this
///     had before it was one. Inline declarations live in a different arena from rules': a stylesheet
///     reload throws the rules away and inline styles belong to elements, which outlive it. Both
///     arenas held <see cref="Declaration" />s, so a range into one was assignment-compatible with a
///     range into the other, and the resolver read inline styles out of the rule store — finding
///     whatever declarations happened to sit at that offset. Every test passed but the one that
///     asked what an inline style did.
/// </remarks>
public readonly record struct InlineStyleId(int Index) {
    /// <inheritdoc />
    public override string ToString() => "inline style " + Index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The declarations written on elements themselves.</summary>
/// <remarks>
///     Separate from the rule set because the two are thrown away on completely different occasions.
///     A <c>.vcss</c> saved in the editor reloads every rule; the elements, and whatever a component
///     wrote directly onto them, are still there.
/// </remarks>
public sealed class InlineStyleStore {
    readonly List<Declaration> declarations = [];
    readonly List<DeclarationRange> blocks = [];

    /// <summary>How many blocks have been recorded.</summary>
    public int Count => blocks.Count;

    /// <summary>Records a block of declarations.</summary>
    /// <param name="block">The declarations.</param>
    /// <returns>A handle on them.</returns>
    public InlineStyleId Add(ReadOnlySpan<Declaration> block) {
        var start = declarations.Count;
        foreach (var declaration in block) {
            declarations.Add(declaration);
        }

        blocks.Add(new DeclarationRange(start, block.Length));
        return new InlineStyleId(blocks.Count - 1);
    }

    /// <summary>Replaces what a handle points at, keeping the handle.</summary>
    /// <param name="id">The handle.</param>
    /// <param name="block">The new declarations.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this, dragging a splitter allocates a block per frame for as long as the
    ///         drag lasts.</b> An element writing its own width every frame is exactly what inline
    ///         styles are for — a docked panel's size, a scrolled row's offset — and
    ///         <see cref="Add" /> only ever appends, so the arena would grow for the whole life of
    ///         the document and nothing would ever reclaim it.
    ///     </para>
    ///     <para>
    ///         <b>In place when the count is unchanged, which is the case that repeats.</b> A control
    ///         writing the same properties with different values keeps its block; one that adds a
    ///         property gets a new range and leaves the old one behind as garbage. That second case
    ///         is a shape change rather than a value change, so it happens a handful of times per
    ///         element rather than sixty times a second — which is the whole reason the distinction
    ///         is worth making instead of always appending.
    ///     </para>
    /// </remarks>
    public void Replace(InlineStyleId id, ReadOnlySpan<Declaration> block) {
        var range = blocks[id.Index];

        if (range.Count == block.Length) {
            var span = CollectionsMarshal.AsSpan(declarations).Slice(range.Start, range.Count);
            block.CopyTo(span);

            return;
        }

        var start = declarations.Count;
        foreach (var declaration in block) {
            declarations.Add(declaration);
        }

        blocks[id.Index] = new DeclarationRange(start, block.Length);
    }

    /// <summary>The declarations behind a handle.</summary>
    /// <param name="id">The handle.</param>
    /// <returns>The declarations.</returns>
    public ReadOnlySpan<Declaration> DeclarationsOf(InlineStyleId id) {
        var range = blocks[id.Index];
        return CollectionsMarshal.AsSpan(declarations).Slice(range.Start, range.Count);
    }
}
