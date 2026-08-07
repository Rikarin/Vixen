// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>Which of an element's two axes cut off what hangs outside it.</summary>
/// <param name="Horizontal">Whether the left and right edges cut.</param>
/// <param name="Vertical">Whether the top and bottom edges cut.</param>
readonly record struct ClipAxes(bool Horizontal, bool Vertical) {
    /// <summary>Nothing is cut, which is <c>overflow: visible</c> and CSS's default.</summary>
    public static ClipAxes None => default;

    /// <summary>Whether anything is cut at all.</summary>
    public bool Any => Horizontal || Vertical;
}

/// <summary>Reads <c>overflow</c> and its two per-axis longhands off a computed style.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Shared because painting and hit testing must give the same answer.</b> The clip stack
///         and the hit test each used to intern <c>overflow</c> for themselves and each compared it to
///         <c>visible</c>; two copies of one rule is how a control ends up visibly clipped and
///         invisibly clickable, or the reverse. One reader, two callers.
///     </para>
///     <para>
///         ⚠ <b>The shorthand is read first and each longhand over it, rather than in source order.</b>
///         Nothing expands <c>overflow</c> into <c>overflow-x</c> and <c>overflow-y</c> on the way in —
///         ExCSS treats the three as unrelated properties — so a computed style holds whichever of them
///         a rule set and no record of which was written last. A named axis therefore wins over the
///         shorthand unconditionally, which agrees with CSS whenever the longhand really did come last
///         and is the order every stylesheet here is written in anyway.
///     </para>
///     <para>
///         ⚠ <b>No coercion between the axes.</b> CSS computes a <c>visible</c> to <c>auto</c> when its
///         partner is not visible, so a browser cannot clip one axis alone; that rule exists because a
///         scrollport is a single rectangle. This clips with a rectangle whose unclipped pair of edges
///         is pushed past any viewport, so one axis alone is expressible exactly — and
///         <c>overflow-x: auto</c> then means what its author wrote rather than quietly also hiding
///         everything below the box.
///     </para>
/// </remarks>
sealed class OverflowReader {
    readonly int shorthand;
    readonly int horizontal;
    readonly int vertical;
    readonly int visible;

    /// <summary>Interns the three property names and the one keyword that means "do not cut".</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    public OverflowReader(NameTable properties, NameTable values) {
        shorthand = properties.Intern("overflow");
        horizontal = properties.Intern("overflow-x");
        vertical = properties.Intern("overflow-y");
        visible = values.Intern("visible");
    }

    /// <summary>Which axes a style cuts.</summary>
    /// <param name="style">The element's computed style.</param>
    /// <returns>The pair. <see cref="ClipAxes.None" /> when neither axis cuts.</returns>
    /// <remarks>
    ///     Anything that is not <c>visible</c> cuts — <c>hidden</c>, <c>scroll</c> and <c>auto</c>
    ///     alike. A scroll container clips at its own edge and offsets what is inside it; the offset is
    ///     a control's business and the clip is this one's, and the two are independent.
    /// </remarks>
    public ClipAxes Of(ComputedStyle style) {
        var both = style.TryGet(shorthand, out var value) && value != visible;
        var x = style.TryGet(horizontal, out var byX) ? byX != visible : both;
        var y = style.TryGet(vertical, out var byY) ? byY != visible : both;

        return new ClipAxes(x, y);
    }
}
