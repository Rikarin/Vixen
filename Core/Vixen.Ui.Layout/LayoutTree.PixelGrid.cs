// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>Snapping the finished layout to whole device pixels.</summary>
/// <remarks>
///     <para>
///         Rounding is not cosmetic and it is not per-node. A box whose left edge lands on 10.5 with
///         a width of 9.5 ends at 20; rounding position and size independently gives 11 and 10,
///         which ends at 21 and leaves a one-pixel seam against whatever sits next to it. So the
///         <i>absolute</i> edges are what get rounded, and the size is the difference between two
///         rounded edges — which is why the walk carries an absolute offset down the tree.
///     </para>
///     <para>
///         Text is the exception in the other direction: a node that measured itself is never
///         rounded down, because a glyph that fits in 40.2 points does not fit in 40 and the visible
///         result is a truncated word rather than a seam.
///     </para>
///     <para>
///         The pass reads the raw layout and writes somewhere else. The reference implementation
///         rounds in place, which means the next pass reads rounded values for every node it does
///         not recompute — so an incremental layout and a cold one drift apart by up to half a pixel
///         per level. A property test comparing the two found exactly that.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    void RoundToPixelGrid(int index, double absoluteLeft, double absoluteTop) {
        var scale = (double) PointScaleFactor;

        var nodeLeft = (double) results[index].Position[(int) Edge.Left];
        var nodeTop = (double) results[index].Position[(int) Edge.Top];
        var nodeWidth = (double) results[index].Dimensions[(int) Dimension.Width];
        var nodeHeight = (double) results[index].Dimensions[(int) Dimension.Height];

        var absoluteNodeLeft = absoluteLeft + nodeLeft;
        var absoluteNodeTop = absoluteTop + nodeTop;
        var absoluteNodeRight = absoluteNodeLeft + nodeWidth;
        var absoluteNodeBottom = absoluteNodeTop + nodeHeight;

        // The far edges are not rounded — nothing derives a seam from them, and the reference does
        // not round them either. They are carried across so that everything a caller can read comes
        // from one place.
        results[index].RoundedPosition[(int) Edge.Right] = results[index].Position[(int) Edge.Right];
        results[index].RoundedPosition[(int) Edge.Bottom] = results[index].Position[(int) Edge.Bottom];

        if (scale == 0d) {
            results[index].RoundedPosition[(int) Edge.Left] = (float) nodeLeft;
            results[index].RoundedPosition[(int) Edge.Top] = (float) nodeTop;
            results[index].RoundedDimensions[(int) Dimension.Width] = (float) nodeWidth;
            results[index].RoundedDimensions[(int) Dimension.Height] = (float) nodeHeight;
        } else {
            var textRounding = (flags[index] & LayoutNodeState.HasMeasureFunction) != 0;

            results[index].RoundedPosition[(int) Edge.Left] = RoundToPixelGrid(nodeLeft, scale, false, textRounding);
            results[index].RoundedPosition[(int) Edge.Top] = RoundToPixelGrid(nodeTop, scale, false, textRounding);

            var scaledWidth = nodeWidth * scale;
            var hasFractionalWidth = !Inexact((float) Math.Round(scaledWidth), (float) scaledWidth);
            var scaledHeight = nodeHeight * scale;
            var hasFractionalHeight = !Inexact((float) Math.Round(scaledHeight), (float) scaledHeight);

            results[index].RoundedDimensions[(int) Dimension.Width] =
                RoundToPixelGrid(absoluteNodeRight, scale, textRounding && hasFractionalWidth, textRounding && !hasFractionalWidth)
                - RoundToPixelGrid(absoluteNodeLeft, scale, false, textRounding);

            results[index].RoundedDimensions[(int) Dimension.Height] =
                RoundToPixelGrid(absoluteNodeBottom, scale, textRounding && hasFractionalHeight, textRounding && !hasFractionalHeight)
                - RoundToPixelGrid(absoluteNodeTop, scale, false, textRounding);
        }

        RoundFragmentsToPixelGrid(index, absoluteNodeLeft, absoluteNodeTop, scale);

        // Descending is the expensive half — the pass is O(whole tree) if it always does, which
        // measured at 60–70 % of an incremental frame. It is safe to stop here when two things
        // hold: the algorithm did not run for this node, so nothing rewrote its children's raw
        // positions or sizes; and the offset their rounding is relative to has not moved, so what
        // they already hold is what they would be given again.
        var subtreeMoved = !Inexact((float) absoluteNodeLeft, results[index].RoundedAbsoluteLeft)
            || !Inexact((float) absoluteNodeTop, results[index].RoundedAbsoluteTop);

        results[index].RoundedAbsoluteLeft = (float) absoluteNodeLeft;
        results[index].RoundedAbsoluteTop = (float) absoluteNodeTop;

        if (!subtreeMoved && results[index].ImplGeneration != generation) {
            return;
        }

        foreach (var child in ChildIds(index)) {
            RoundToPixelGrid(child, absoluteNodeLeft, absoluteNodeTop);
        }
    }

    /// <summary>Snaps a fragmented node's extra boxes onto the same grid its own box went onto.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Rounded exactly the way the node above is, and the reason to say so is that the
    ///         obvious shortcut is wrong.</b> The shortcut is to round a fragment's own width, which
    ///         reintroduces per-box rounding and the one-pixel seam this whole pass exists to remove —
    ///         between two fragments of the same span, where it is most visible, because a background
    ///         that stops half a pixel short of the line below it reads as a broken highlight rather
    ///         than as a rounding artefact. So the <i>absolute</i> edges are rounded and the size is
    ///         the difference, which is the rule one function up.
    ///     </para>
    ///     <para>
    ///         The offsets stay relative to the node and the sizes come from absolute edges, which is
    ///         the same split <see cref="LayoutResult.RoundedPosition" /> and
    ///         <see cref="LayoutResult.RoundedDimensions" /> already have. A consumer that adds a
    ///         rounded fragment offset to the node's accumulated absolute position therefore lands
    ///         where the node's own rounded box would have put it.
    ///     </para>
    /// </remarks>
    void RoundFragmentsToPixelGrid(int index, double absoluteNodeLeft, double absoluteNodeTop, double scale) {
        var run = FragmentsOf(index);
        if (run.IsEmpty) {
            return;
        }

        // A fragmented box is never a measured leaf — it is an inline box with box children, and a
        // measure function makes a node a leaf. So there is no text-rounding case to carry down here,
        // and asserting that by passing `false` is more honest than threading a flag that cannot be
        // set.
        for (var i = 0; i < run.Length; i++) {
            ref var box = ref run[i];

            if (scale == 0d) {
                box.RoundedLeft = box.Left;
                box.RoundedTop = box.Top;
                box.RoundedWidth = box.Width;
                box.RoundedHeight = box.Height;

                continue;
            }

            var left = absoluteNodeLeft + box.Left;
            var top = absoluteNodeTop + box.Top;

            box.RoundedLeft = RoundToPixelGrid(box.Left, scale, false, false);
            box.RoundedTop = RoundToPixelGrid(box.Top, scale, false, false);
            box.RoundedWidth = RoundToPixelGrid(left + box.Width, scale, false, false) - RoundToPixelGrid(left, scale, false, false);
            box.RoundedHeight = RoundToPixelGrid(top + box.Height, scale, false, false) - RoundToPixelGrid(top, scale, false, false);
        }
    }

    /// <summary>Rounds one coordinate onto the pixel grid.</summary>
    /// <param name="value">The coordinate.</param>
    /// <param name="pointScaleFactor">How many device pixels one point is.</param>
    /// <param name="forceCeil">Round away from zero whatever the fraction is.</param>
    /// <param name="forceFloor">Round towards zero whatever the fraction is.</param>
    /// <returns>The rounded coordinate.</returns>
    internal static float RoundToPixelGrid(double value, double pointScaleFactor, bool forceCeil, bool forceFloor) {
        var scaled = value * pointScaleFactor;
        var fractional = scaled % 1.0;

        if (fractional < 0) {
            // `%` on a negative number gives a negative remainder, and what is wanted is the amount
            // that, subtracted from the value, reaches the floor — which for −2.2 is 0.8, not −0.2.
            fractional++;
        }

        if (Inexact((float) fractional, 0f)) {
            scaled -= fractional;
        } else if (Inexact((float) fractional, 1f)) {
            scaled = scaled - fractional + 1.0;
        } else if (forceCeil) {
            scaled = scaled - fractional + 1.0;
        } else if (forceFloor) {
            scaled -= fractional;
        } else {
            scaled = scaled - fractional
                + (!double.IsNaN(fractional) && (fractional > 0.5 || Inexact((float) fractional, 0.5f)) ? 1.0 : 0.0);
        }

        return double.IsNaN(scaled) || double.IsNaN(pointScaleFactor) ? float.NaN : (float) (scaled / pointScaleFactor);
    }
}
