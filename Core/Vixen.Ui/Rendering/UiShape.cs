// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>Everything the box shader needs about one box, in the layout it reads.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Per box, not per vertex, and that is the whole reason this type exists.</b> Four
///         corners with an elliptical radius each is eight floats, a gradient is another six, and a
///         box is four vertices — so carrying them on the vertex would take it from forty-eight bytes
///         to a hundred and four, and every glyph and every path triangle in the frame would pay for
///         fields no shader reads on them. One record per box costs eighty bytes against the
///         sixty-four the four vertices already spend on <c>Shape</c>, and the vertex layout does not
///         move at all: a box's <c>Shape.X</c> becomes the index of its record.
///     </para>
///     <para>
///         ⚠ <b>Five <c>Vector4</c>s, written out rather than left to a struct layout to work out.</b>
///         This is copied into a storage buffer with <c>MemoryMarshal</c> and read back by a shader
///         whose own alignment rules are not C#'s — a <c>float</c> beside a <c>Vector2</c> lays out
///         differently under std430 than under sequential, and the failure is a box drawn with
///         another box's radii, which looks like a bug in the geometry.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct UiShape {
    /// <summary>Creates a shape record.</summary>
    /// <param name="half">Half the box's width and height.</param>
    /// <param name="thickness">A border's width, or zero for a fill.</param>
    /// <param name="corners">The four corners' radii.</param>
    /// <param name="gradientEnd">The colour at the far end of the gradient.</param>
    /// <param name="gradientAxis">Which way the gradient runs. Zero for a flat fill.</param>
    public UiShape(
        Vector2 half,
        float thickness,
        CornerRadii corners,
        Color4 gradientEnd,
        Vector2 gradientAxis
    ) {
        Size = new Vector4(half.X, half.Y, thickness, gradientAxis == Vector2.Zero ? 0f : 1f);

        RadiiX = new Vector4(corners.TopLeft.X, corners.TopRight.X, corners.BottomRight.X, corners.BottomLeft.X);
        RadiiY = new Vector4(corners.TopLeft.Y, corners.TopRight.Y, corners.BottomRight.Y, corners.BottomLeft.Y);

        Axis = new Vector4(gradientAxis.X, gradientAxis.Y, 0f, 0f);
        End = new Vector4(gradientEnd.R, gradientEnd.G, gradientEnd.B, gradientEnd.A);
    }

    /// <summary>Half width, half height, border thickness, and whether there is a gradient.</summary>
    public Vector4 Size { get; }

    /// <summary>The four horizontal radii, clockwise from the top left.</summary>
    public Vector4 RadiiX { get; }

    /// <summary>The four vertical radii, in the same order.</summary>
    public Vector4 RadiiY { get; }

    /// <summary>Which way the gradient runs, in the box's own space. The last two lanes are padding.</summary>
    public Vector4 Axis { get; }

    /// <summary>The colour at the far end of the gradient.</summary>
    public Vector4 End { get; }
}
