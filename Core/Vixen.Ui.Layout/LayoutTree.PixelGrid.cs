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

        if (scale != 0d) {
            var textRounding = (flags[index] & LayoutNodeState.HasMeasureFunction) != 0;

            results[index].RawDimensions[(int) Dimension.Width] = (float) nodeWidth;
            results[index].RawDimensions[(int) Dimension.Height] = (float) nodeHeight;

            results[index].Position[(int) Edge.Left] = RoundToPixelGrid(nodeLeft, scale, false, textRounding);
            results[index].Position[(int) Edge.Top] = RoundToPixelGrid(nodeTop, scale, false, textRounding);

            var scaledWidth = nodeWidth * scale;
            var hasFractionalWidth = !Inexact((float) Math.Round(scaledWidth), (float) scaledWidth);
            var scaledHeight = nodeHeight * scale;
            var hasFractionalHeight = !Inexact((float) Math.Round(scaledHeight), (float) scaledHeight);

            results[index].Dimensions[(int) Dimension.Width] =
                RoundToPixelGrid(absoluteNodeRight, scale, textRounding && hasFractionalWidth, textRounding && !hasFractionalWidth)
                - RoundToPixelGrid(absoluteNodeLeft, scale, false, textRounding);

            results[index].Dimensions[(int) Dimension.Height] =
                RoundToPixelGrid(absoluteNodeBottom, scale, textRounding && hasFractionalHeight, textRounding && !hasFractionalHeight)
                - RoundToPixelGrid(absoluteNodeTop, scale, false, textRounding);
        }

        foreach (var child in ChildIds(index)) {
            RoundToPixelGrid(child, absoluteNodeLeft, absoluteNodeTop);
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
