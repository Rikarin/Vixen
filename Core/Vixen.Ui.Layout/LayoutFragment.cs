// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Ui.Layout;

/// <summary>Which real ends of a fragmented box this fragment carries.</summary>
/// <remarks>
///     ⚠ <b>The geometry and the flags answer two different questions and a painter needs both.</b>
///     A fragment's rectangle already <i>includes</i> the horizontal border and padding at whichever
///     ends are real, so laying a background over it is correct with no reference to these flags.
///     What the flags say is which vertical edges to <i>stroke</i>: CSS Display §2.2 draws a
///     fragmented inline box's left border once, at its first fragment, and its right border once, at
///     its last — the breaks in between are not edges of the box and get no border, no padding and no
///     rounded corner.
/// </remarks>
[Flags]
public enum LayoutFragmentEnds : byte {
    /// <summary>Neither end — a fragment whose both sides are line breaks.</summary>
    None = 0,

    /// <summary>The inline-start end, where the box really begins.</summary>
    Start = 1 << 0,

    /// <summary>The inline-end end, where the box really ends.</summary>
    End = 1 << 1,

    /// <summary>Both — an unfragmented box, which is what every node that has no fragments is.</summary>
    Both = Start | End
}

/// <summary>One of the several boxes a single node was fragmented into.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the store's answer to the one invariant its first four algorithms preserved
///         without stating: one node produces one box.</b> A <see cref="LayoutResult" /> holds one
///         rectangle, and CSS Display §2.2's non-replaced <c>inline</c> box needs several — a
///         <c>span</c> crossing a line break is one box per line. The rectangle lives here and the
///         node keeps a handle to a run of them.
///     </para>
///     <para>
///         ⚠ <b>Both the raw and the rounded rectangle, for the reason <see cref="LayoutResult" />
///         keeps <see cref="LayoutResult.Position" /> apart from
///         <see cref="LayoutResult.RoundedPosition" />.</b> Rounding has to stay a pure function of the
///         raw layout or an incremental pass and a cold pass stop agreeing — the drift the rounding
///         pass was restructured to avoid. A fragment rounded in place would reintroduce exactly that,
///         one level down and somewhere nobody is looking.
///     </para>
///     <para>
///         Coordinates match <see cref="LayoutResult.Position" />'s: an offset from the <i>owning
///         node's</i> border-box origin, which for a fragmented node is the top-left of the union of
///         its fragments. So a consumer that has already walked to the node's absolute position adds a
///         fragment's offset to it and is done, and the first fragment of a single-fragment box is at
///         (0, 0).
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
struct LayoutFragment {
    /// <summary>The raw offset from the owning node's border-box origin.</summary>
    public float Left;

    /// <inheritdoc cref="Left" />
    public float Top;

    /// <summary>The raw border-box size of this fragment.</summary>
    public float Width;

    /// <inheritdoc cref="Width" />
    public float Height;

    /// <summary>The offset after pixel rounding. What <see cref="LayoutTree.GetFragment" /> returns.</summary>
    public float RoundedLeft;

    /// <inheritdoc cref="RoundedLeft" />
    public float RoundedTop;

    /// <summary>The size after pixel rounding.</summary>
    public float RoundedWidth;

    /// <inheritdoc cref="RoundedWidth" />
    public float RoundedHeight;

    /// <summary>Which of the box's real ends this fragment carries.</summary>
    public LayoutFragmentEnds Ends;
}
