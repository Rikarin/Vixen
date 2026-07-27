// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui;

/// <summary>Which way an arrow key points.</summary>
/// <remarks>
///     Deliberately not folded into <see cref="FocusDirection" />. Tab walks an <i>order</i> — a
///     list the document decides in advance — and an arrow walks a <i>layout</i>, which is decided by
///     where things ended up. They are two different questions that happen to move the same focus,
///     and one enum would suggest Tab is just another direction.
/// </remarks>
public enum NavigationDirection : byte {
    /// <summary>Towards smaller x.</summary>
    Left,

    /// <summary>Towards smaller y.</summary>
    Up,

    /// <summary>Towards larger x.</summary>
    Right,

    /// <summary>Towards larger y.</summary>
    Down
}

public sealed partial class UiDocument {
    /// <summary>Moves the focus to whatever lies in a direction.</summary>
    /// <param name="direction">Which way.</param>
    /// <returns>Whether it moved.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Reads the last layout pass</b>, so it answers with where things were when
    ///         <see cref="Update" /> last ran. Called on a tree that has never been laid out, every
    ///         element is a zero box at the origin and nothing is in any direction from anything —
    ///         which is the correct answer to a question about a layout that does not exist yet, and
    ///         a confusing one to debug, so it is said here.
    ///     </para>
    ///     <para>
    ///         <b>Arrows do not wrap.</b> Tab is a cycle because it walks an order and an order has
    ///         no far end; an arrow points at somewhere, and running out of somewhere is a wall. A UI
    ///         where pressing Down at the bottom of a list jumps to the top is one where holding Down
    ///         never settles.
    ///     </para>
    /// </remarks>
    public bool MoveFocus(NavigationDirection direction) {
        if (Focused is null) {
            // No origin means no direction. The first focusable is the least surprising answer and
            // the one that gets a keyboard user into the interface, which is what the first arrow
            // press is for.
            var stops = new List<UiElement>();
            Collect(Scope(), stops);

            return stops.Count > 0 && Focus(stops[0]);
        }

        return FindInDirection(Focused, direction) is { } next && Focus(next);
    }

    /// <summary>What lies in a direction from an element.</summary>
    /// <param name="origin">Where to look from.</param>
    /// <param name="direction">Which way.</param>
    /// <returns>The element an arrow key would move to, or <c>null</c> if there is nothing there.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The beam model.</b> A candidate has to start past the edge the arrow points at —
    ///         Right means "beyond my right edge", not "somewhere rightish" — which is what makes an
    ///         element's own focusable children invisible to it: they are inside it, so they are past
    ///         nothing. Among those, the ones whose <i>other</i> axis overlaps this element's are
    ///         <b>in the beam</b>, and any of them beats any candidate outside it however close that
    ///         one is. Inside the beam, nearest along the axis wins; outside it, nearest by straight
    ///         line between the two rectangles.
    ///     </para>
    ///     <para>
    ///         The alternative is a weighted score — distance along plus some multiple of distance
    ///         across — and the multiple is the problem. It has no principled value, so it gets tuned
    ///         until the layouts someone happened to test behave, and pressing Down in a grid drifts
    ///         diagonally in the layouts they did not. The beam has no constant to tune: pressing Down
    ///         moves down, and only when nothing is down does it consider anywhere else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Touching is not overlapping.</b> The beam test is a strictly positive overlap, and
    ///         it has to be: two cells of a grid share an edge exactly, so a non-strict test puts the
    ///         diagonal neighbour in the beam alongside the one directly below and the grid navigates
    ///         sideways.
    ///     </para>
    ///     <para>
    ///         Ties go to document order, because the candidates are gathered in it and the comparison
    ///         is strict. Two elements exactly on top of each other are a layout question rather than
    ///         a navigation one.
    ///     </para>
    /// </remarks>
    public UiElement? FindInDirection(UiElement origin, NavigationDirection direction) {
        ArgumentNullException.ThrowIfNull(origin);

        var candidates = new List<UiElement>();
        Collect(Scope(), candidates);

        var from = origin.Bounds;
        UiElement? best = null;
        var bestInBeam = false;
        var bestScore = float.PositiveInfinity;

        foreach (var candidate in candidates) {
            if (ReferenceEquals(candidate, origin) || candidate.Bounds.IsEmpty) {
                continue;
            }

            var to = candidate.Bounds;
            if (!Beyond(direction, from, to, out var along)) {
                continue;
            }

            var across = Across(direction, from, to);
            var inBeam = Overlap(direction, from, to) > 0f;

            // In the beam beats out of it outright, and only then does distance decide. Ranking the
            // two against one number is exactly the weighted score this is here to avoid.
            var score = inBeam ? along : MathF.Sqrt((along * along) + (across * across));

            var better = best is null
                || (inBeam && !bestInBeam)
                || (inBeam == bestInBeam && score < bestScore);

            if (!better) {
                continue;
            }

            best = candidate;
            bestInBeam = inBeam;
            bestScore = score;
        }

        return best;
    }

    /// <summary>Whether a candidate starts past the edge the arrow points at, and by how far.</summary>
    /// <remarks>
    ///     Non-strict, so two elements sharing an edge are one step apart rather than unreachable.
    ///     Flexbox with no gap produces exactly that, and it is the common case rather than the
    ///     degenerate one.
    /// </remarks>
    static bool Beyond(NavigationDirection direction, Rectangle from, Rectangle to, out float along) {
        along = direction switch {
            NavigationDirection.Left => from.Left - to.Right,
            NavigationDirection.Up => from.Top - to.Bottom,
            NavigationDirection.Right => to.Left - from.Right,
            _ => to.Top - from.Bottom
        };

        return along >= 0f;
    }

    /// <summary>How far apart two rectangles are on the axis the arrow does not point along.</summary>
    /// <remarks>Zero when they overlap, which is what makes the straight-line fallback a straight line.</remarks>
    static float Across(NavigationDirection direction, Rectangle from, Rectangle to) =>
        MathF.Max(0f, -Overlap(direction, from, to));

    /// <summary>How much two rectangles share on the axis the arrow does not point along.</summary>
    /// <remarks>Negative when they do not, which is the gap between them.</remarks>
    static float Overlap(NavigationDirection direction, Rectangle from, Rectangle to) =>
        direction is NavigationDirection.Left or NavigationDirection.Right
            ? MathF.Min(from.Bottom, to.Bottom) - MathF.Max(from.Top, to.Top)
            : MathF.Min(from.Right, to.Right) - MathF.Max(from.Left, to.Left);
}
