// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>Everything the box shader needs about one box, in the layout it reads.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Per box, not per vertex, and that is the whole reason this type exists.</b> Four
///         corners with an elliptical radius each is eight floats, a three-stop gradient is another
///         fifteen, and a box is four vertices — so carrying them on the vertex would take it from
///         forty-eight bytes to well past a hundred, and every glyph and every path triangle in the
///         frame would pay for fields no shader reads on them. One record per box costs a hundred and
///         sixty bytes against the sixty-four the four vertices already spend on <c>Shape</c>, and
///         the vertex layout does not move at all: a box's <c>Shape.X</c> becomes the index of its
///         record.
///     </para>
///     <para>
///         ⚠ <b>Ten <c>Vector4</c>s, written out rather than left to a struct layout to work out.</b>
///         This is copied into a storage buffer with <c>MemoryMarshal</c> and read back by a shader
///         whose own alignment rules are not C#'s — a <c>float</c> beside a <c>Vector2</c> lays out
///         differently under std430 than under sequential, and the failure is a box drawn with
///         another box's radii, which looks like a bug in the geometry.
///     </para>
///     <para>
///         ⚠ <b>Several files have to agree about this layout, and half of them are invisible to a
///         search for this type's name.</b> The ones that name it: this record,
///         <c>Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn</c>, the committed <c>UiBox.frag.spv</c> and
///         <c>UiBox.reflect.json</c> beside it, and <c>SoftwareUiRasterizer</c>. The ones that do not:
///         <c>UiRenderer</c>, which needs only the <i>size</i> and once spelled it <c>80</c> in three
///         places; and one hand-maintained GLSL copy of the box shader, under
///         <c>Vixen.Graphics.Golden.Tests</c>, which calls the struct <c>Shape</c>. There were three
///         GLSL copies until the sample and the <c>vixen-app</c> template stopped carrying their own
///         frame loops, and two Raven ones until <c>Vixen.Editor.Host</c>'s duplicate of
///         <c>Ui.rvn</c> was deleted — so what is left is one source, named and checked rather than
///         transcribed.
///     </para>
///     <para>
///         ⚠ <b>Which test covers which half is worth knowing before changing this.</b>
///         <c>UiShapeLayoutTests</c> pins the record's <i>shape</i> against the committed
///         reflection beside <c>Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn</c> and has nothing to say
///         about how a host sizes a buffer around it; <c>./build.sh CheckShaders</c> pins the Raven
///         source against its modules and does not see GLSL. The stride and the GLSL copy are caught
///         only by <c>Vixen.Graphics.Golden.Tests</c>, on a real device — which is exactly what
///         caught them when this record grew. <c>UiRenderer</c> now derives its stride from
///         <c>Marshal.SizeOf</c>, so that one cannot drift again; the GLSL still can.
///     </para>
///     <para>
///         ⚠ <b>Every lane this record has ever grown was <i>appended</i>, and the two repurposed ones
///         were both zero.</b> Growing from eighty bytes to a hundred and twelve, then to a
///         hundred and forty-four and then to a hundred and sixty, left every existing offset exactly where the shader already read
///         it: <c>Axis.w</c> was declared padding and is now the interpolation
///         space, whose zero is <see cref="GradientSpace.Linear" /> — which is what the shader did
///         before there was a choice — and <c>Size.w</c> was a zero-or-one gradient flag and is now
///         the shape, whose one is <see cref="GradientShape.Linear" />, which is what a flag set to
///         one meant. That is not a coincidence to admire; it is a hazard to name. A stale shader
///         reading this record still draws two-stop linear gradients correctly and silently ignores
///         everything below offset eighty, so drift here does <i>not</i> announce itself as garbage on
///         screen. The test is the only thing that finds it.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct UiShape {
    /// <summary>Creates a shape record for a flat fill or a plain two-stop linear gradient.</summary>
    /// <param name="half">Half the box's width and height.</param>
    /// <param name="thickness">A border's width, or zero for a fill.</param>
    /// <param name="corners">The four corners' radii.</param>
    /// <param name="gradientEnd">The colour at the far end of the gradient.</param>
    /// <param name="gradientAxis">Which way the gradient runs. Zero for a flat fill.</param>
    /// <param name="blur">
    ///     How far a shadow's edge is spread, in pixels. Zero for everything that is not one.
    /// </param>
    public UiShape(
        Vector2 half,
        float thickness,
        CornerRadii corners,
        Color4 gradientEnd,
        Vector2 gradientAxis,
        float blur = 0f
    ) : this(
        half,
        thickness,
        corners,
        gradientAxis == Vector2.Zero ? GradientShape.None : GradientShape.Linear,
        GradientSpace.Linear,
        gradientAxis,
        gradientEnd,
        default,
        hasVia: false,
        GradientStops.Default,
        blur
    ) { }

    /// <summary>Creates a shape record for any gradient this shader draws.</summary>
    /// <param name="half">Half the box's width and height.</param>
    /// <param name="thickness">A border's width, or zero for a fill.</param>
    /// <param name="corners">The four corners' radii.</param>
    /// <param name="shape">Which family of gradient, or <see cref="GradientShape.None" />.</param>
    /// <param name="space">Which space the stops are interpolated in.</param>
    /// <param name="gradientAxis">
    ///     Which way the gradient runs, in the box's own space. Linear reads it as the axis, conic as
    ///     the direction its zero angle points, and radial does not read it at all.
    /// </param>
    /// <param name="gradientEnd">The colour at the last stop.</param>
    /// <param name="gradientVia">The colour at the middle stop, when there is one.</param>
    /// <param name="hasVia">Whether the middle stop exists.</param>
    /// <param name="stops">Where the three stops sit along the ramp.</param>
    /// <param name="blur">A shadow's spread in pixels, or zero.</param>
    /// <param name="paintCentre">
    ///     Where the ramp's own centre sits, in pixels from the centre of the tile it fills. Read only
    ///     when <paramref name="paintExtent" /> is non-zero.
    /// </param>
    /// <param name="paintExtent">
    ///     How far the ramp reaches from its centre, in pixels. Zero means the box, which is what every
    ///     gradient written before this lane existed meant.
    /// </param>
    /// <param name="areaCentre">Where the first tile's centre sits, in pixels from the box's centre.</param>
    /// <param name="areaHalf">
    ///     Half the tile, signed — positive tiles along that axis and negative clips. Zero means the
    ///     tile is the box and there is neither.
    /// </param>
    /// <param name="inset">
    ///     Whether this record is an <c>inset</c> shadow, which is drawn inside the box rather than
    ///     around it.
    /// </param>
    /// <param name="insetOffset">An inset shadow's offset, in pixels. Read only when it is one.</param>
    /// <param name="insetSpread">
    ///     How far an inset shadow's rectangle is shrunk from the box, in pixels. Read only when it is
    ///     one.
    /// </param>
    public UiShape(
        Vector2 half,
        float thickness,
        CornerRadii corners,
        GradientShape shape,
        GradientSpace space,
        Vector2 gradientAxis,
        Color4 gradientEnd,
        Color4 gradientVia,
        bool hasVia,
        GradientStops stops,
        float blur = 0f,
        Vector2 paintCentre = default,
        Vector2 paintExtent = default,
        Vector2 areaCentre = default,
        Vector2 areaHalf = default,
        bool inset = false,
        Vector2 insetOffset = default,
        float insetSpread = 0f
    ) {
        // ⚠ The shape goes in the lane that was a zero-or-one gradient flag, and `Linear` is one, so
        // the sentinel the shader already tests — `size.w > 0` — keeps meaning exactly what it meant.
        Size = new Vector4(half.X, half.Y, thickness, (float) shape);

        RadiiX = new Vector4(corners.TopLeft.X, corners.TopRight.X, corners.BottomRight.X, corners.BottomLeft.X);
        RadiiY = new Vector4(corners.TopLeft.Y, corners.TopRight.Y, corners.BottomRight.Y, corners.BottomLeft.Y);

        // ⚠ The blur goes in a lane that was padding, which is the reason it could be added at all
        // without moving anything: the record's existing offsets stay exactly where the shader
        // already reads them. The interpolation space is the second half of that same trick — it
        // takes the last declared-padding lane, and its zero is the linear-RGB lerp the shader did
        // when there was nothing to choose.
        Axis = new Vector4(gradientAxis.X, gradientAxis.Y, blur, (float) space);
        End = new Vector4(gradientEnd.R, gradientEnd.G, gradientEnd.B, gradientEnd.A);
        Mid = new Vector4(gradientVia.R, gradientVia.G, gradientVia.B, gradientVia.A);

        // ⚠ `hasVia` is a lane of its own rather than a negative middle position, because a middle
        // stop genuinely may sit at zero — `via-red via-0%` is a hard edge at the start, which CSS
        // draws and a sentinel would erase.
        Stops = new Vector4(stops.From, stops.Via, stops.To, hasVia ? 1f : 0f);

        // ⚠ Appended again, and again with a zero that is what the shader did before the lane
        // existed: a zero `Paint.zw` is "the ramp is the box", a zero `Area.zw` is "the tile is the
        // box, do not tile and do not clip". A stale shader that reads neither draws exactly the
        // picture it drew, and a stale *host* that writes neither asks for exactly that picture —
        // which is the property the eighty-to-a-hundred-and-twelve growth had and is the only reason
        // a nine-lane record can be added to eight files one at a time.
        Paint = new Vector4(paintCentre.X, paintCentre.Y, paintExtent.X, paintExtent.Y);
        Area = new Vector4(areaCentre.X, areaCentre.Y, areaHalf.X, areaHalf.Y);

        // ⚠ <b>Appended, which is the answer #279 could not find while it was looking for a lane to
        // reuse.</b> Both sentinels it argued out are genuinely unsound — a negative blur cannot
        // express `inset-shadow-2xs`, which is `inset 0 1px` with no blur at all, and `Stops.x` is the
        // lane reuse this repository keeps finding bugs in. The third option is the one this record
        // has taken every time it grew: a new lane whose all-zero is what every writer before it
        // meant. An outer shadow writes `(0, 0, 0, 0)` and a stale shader reading it takes the branch
        // it already took.
        // ⚠ The flag is a lane of its own rather than "the offset or the spread is non-zero", for the
        // reason `hasVia` is: `inset 0 0 4px` is a legal centred inner shadow, and a sentinel would
        // erase it.
        Inset = new Vector4(insetOffset.X, insetOffset.Y, insetSpread, inset ? 1f : 0f);
    }

    /// <summary>Half width, half height, border thickness, and which family of gradient.</summary>
    public Vector4 Size { get; }

    /// <summary>The four horizontal radii, clockwise from the top left.</summary>
    public Vector4 RadiiX { get; }

    /// <summary>The four vertical radii, in the same order.</summary>
    public Vector4 RadiiY { get; }

    /// <summary>The gradient's direction, then a shadow's blur radius, then the interpolation space.</summary>
    public Vector4 Axis { get; }

    /// <summary>The colour at the last stop.</summary>
    public Vector4 End { get; }

    /// <summary>The colour at the middle stop, read only when <see cref="Stops" />'s last lane is set.</summary>
    public Vector4 Mid { get; }

    /// <summary>Where the three stops sit, then whether the middle one exists.</summary>
    public Vector4 Stops { get; }

    /// <summary>The ramp's own centre, then how far it reaches. A zero reach means the box.</summary>
    public Vector4 Paint { get; }

    /// <summary>
    ///     The first tile's centre, then half its size with <c>background-repeat</c> in the sign. All
    ///     zero means the tile is the box.
    /// </summary>
    public Vector4 Area { get; }

    /// <summary>
    ///     An inset shadow's offset and spread, then whether it is one at all. All zero is every box
    ///     and every outer shadow.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An inner shadow is two rectangles in one draw, which is why an offset that is already
    ///     in the geometry has to be in the record as well.</b> An outer shadow puts its offset in the
    ///     quad's position and needs no second rectangle; an inner one is the region between the box
    ///     and a rectangle offset and shrunk inside it, so the quad is the box — for the clip — and
    ///     these three numbers are the other rectangle.
    /// </remarks>
    public Vector4 Inset { get; }
}
