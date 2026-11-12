// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Texturing.Layers;

/// <summary>Where a layer's painted pixels live, and what they are called.</summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A second file, and doc 48 Part 5 says why in one line: a stack is a file people merge
///             and a paint layer is not.
///         </b>
///         A <c>.vxlayers</c> is YAML with layer names, blend modes and
///         anchors in it — three people can edit one and a merge tool can resolve it. A 4K RGBA
///         buffer inside that file would make every stroke a whole-file conflict and every diff
///         useless, and it would put a texture in a text format.
///     </para>
///     <para>
///         ⚠ <b>Pixels and not strokes, which is the decision this whole shape exists to allow.</b>
///         Doc 48 § D10: storing strokes re-renders at any resolution and diffs beautifully, and it
///         makes every brush, every falloff and every blend mode a
///         <em>
///             format compatibility
///             surface
///         </em>
///         — change the falloff curve and every existing project repaints differently.
///         Both references store pixels; the stroke list is the session's undo and is discarded on
///         save. Nothing here writes a <c>.vxpaint</c>: the brush is M9
///         (<a href="https://github.com/Rikarin/Vixen/issues/574">#574</a>). What M7 owes is a name
///         that will not have to change when it arrives.
///     </para>
/// </remarks>
public static class LayerPaint {
    /// <summary>What a painted layer's pixels are written as.</summary>
    public const string Extension = ".vxpaint";

    /// <summary>
    ///     What a paint layer's file is called, relative to the stack: the stack's name, the texture
    ///     set, the layer's id, and — for a mask — the word <c>mask</c>.
    /// </summary>
    /// <param name="stack">The stack's file name without its extension.</param>
    /// <param name="set">The texture set's name.</param>
    /// <param name="layer">The layer's <see cref="LayerAsset.Id" />.</param>
    /// <param name="mask">Whether this is the layer's mask rather than its content.</param>
    /// <returns>The file name.</returns>
    /// <exception cref="ArgumentException">The stack name or the layer id is empty.</exception>
    /// <remarks>
    ///     ⚠ <b>Derived from the identity rather than stored, and only as a <em>default</em>.</b>
    ///     <see cref="LayerAsset.Paint" /> holds whatever path the file actually names, because a
    ///     stack that renames its set would otherwise orphan every painted layer in it. This is what
    ///     a new paint layer is called; it is not how an existing one is found.
    /// </remarks>
    public static string NameFor(string stack, string set, string layer, bool mask = false) {
        ArgumentException.ThrowIfNullOrEmpty(stack);
        ArgumentException.ThrowIfNullOrEmpty(layer);

        var slot = set.Length > 0 ? $".{set}" : string.Empty;

        return $"{stack}{slot}.{layer}{(mask ? ".mask" : string.Empty)}{Extension}";
    }
}
