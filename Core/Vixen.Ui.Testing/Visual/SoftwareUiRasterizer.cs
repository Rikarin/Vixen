// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Ui.Testing.Visual;

/// <summary>The interface, drawn on the CPU, from the same geometry the GPU would be given.</summary>
/// <remarks>
///     <para>
///         <b>Why a software renderer rather than the real one.</b> A visual regression suite that
///         needs a Vulkan device runs on the machines that have one, which in practice means it does
///         not run: not on a laptop without the SDK, not on a container, not on the CI leg that has
///         no GPU. A game developer's UI suite has to run everywhere their unit tests run or it will
///         be the suite nobody has ever seen pass.
///     </para>
///     <para>
///         ⚠ <b>And it buys something a GPU cannot: an exact comparison.</b>
///         <c>Vixen.Graphics.Golden.Tests</c> compares perceptually because MoltenVK and lavapipe
///         round the same sRGB conversion differently and both are conformant — its README says a
///         bitwise suite there is red from the day it is written. The arithmetic here is the same on
///         every machine, so a screenshot can be compared byte for byte and a one-pixel shift in a
///         glyph is a failure rather than something under a threshold.
///     </para>
///     <para>
///         ⚠ <b>What it does <i>not</i> replace.</b> This consumes <see cref="UiGeometry" /> — so the
///         draw list, the geometry builder, the clip resolution, the tessellator and the glyph atlas
///         are all under test — and then does the fragment shaders' arithmetic itself. It therefore
///         cannot catch a mistake that lives below that line: a wrong descriptor binding, a vertex
///         layout that disagrees with the shader, a projection that flips y. Those are exactly what
///         <c>Vixen.Graphics.Golden.Tests</c> exists for, and this does not make that suite
///         redundant. The two answer different questions and both are worth asking.
///     </para>
///     <para>
///         ⚠ <b>The port is of the shaders as written, including their approximations.</b>
///         <see cref="BoxDistance" /> turns an elliptical corner into a circle by scaling and scales
///         the distance back by the smaller semi-axis, because that is what <c>ui-box.frag</c> does;
///         "improving" it here would make this renderer disagree with the one that ships, which is
///         the one failure mode a testing renderer must not have.
///     </para>
/// </remarks>
public static class SoftwareUiRasterizer {
    /// <summary>Draws a frame of interface geometry.</summary>
    /// <param name="geometry">What to draw.</param>
    /// <param name="atlas">The glyph fields the text reads.</param>
    /// <param name="width">How wide the picture is.</param>
    /// <param name="height">How tall.</param>
    /// <param name="background">What to clear to.</param>
    /// <returns>The picture, 8-bit RGBA.</returns>
    public static Bitmap Render(
        UiGeometry geometry,
        GlyphAtlas atlas,
        int width,
        int height,
        Color4 background
    ) {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        // Premultiplied, because that is what the blend state is and what every fragment below
        // produces. Carrying straight alpha here and converting per blend would show as a dark halo
        // around every rounded corner — the classic symptom, and one this suite would then bake into
        // its references.
        var target = new float[width * height * 4];

        for (var i = 0; i < width * height; i++) {
            target[(i * 4) + 0] = background.R * background.A;
            target[(i * 4) + 1] = background.G * background.A;
            target[(i * 4) + 2] = background.B * background.A;
            target[(i * 4) + 3] = background.A;
        }

        var frame = new Frame(geometry, atlas, width, height);
        frame.Run(target, 0, geometry.Draws.Count);

        var pixels = new byte[width * height * 4];

        for (var i = 0; i < pixels.Length; i++) {
            // ⚠ No sRGB encode. The render output is Rgba8UNorm and the draw list's colours are
            // linear, so this is the same store the GPU does — and a reference written through a
            // gamma curve the engine does not apply would be a picture of something that never
            // reaches a screen.
            pixels[i] = (byte)Math.Clamp(MathF.Round(target[i] * 255f), 0f, 255f);
        }

        return new(width, height, pixels);
    }

    /// <summary>The signed distance to a box with an elliptical corner, negative inside.</summary>
    /// <param name="point">Where, as an offset from the box's centre in pixels.</param>
    /// <param name="half">Half the box's size.</param>
    /// <param name="radius">The radii of the corner this point is nearest.</param>
    /// <returns>The distance.</returns>
    /// <remarks>
    ///     A line-for-line port of <c>box_distance</c> in <c>ui-box.frag</c>. See that file for why
    ///     the corner quadrant is the only place the ellipse is an ellipse, and why scaling the
    ///     distance back by the larger semi-axis leaves a wide corner's flat side soft.
    /// </remarks>
    public static float BoxDistance(Vector2 point, Vector2 half, Vector2 radius) {
        var r = new Vector2(
            Math.Clamp(radius.X, 0f, half.X),
            Math.Clamp(radius.Y, 0f, half.Y)
        );

        var q = new Vector2(MathF.Abs(point.X) - half.X + r.X, MathF.Abs(point.Y) - half.Y + r.Y);

        if (r.X <= 0f || r.Y <= 0f) {
            var squareX = MathF.Abs(point.X) - half.X;
            var squareY = MathF.Abs(point.Y) - half.Y;

            return MathF.Sqrt(
                (MathF.Max(squareX, 0f) * MathF.Max(squareX, 0f))
                + (MathF.Max(squareY, 0f) * MathF.Max(squareY, 0f))
            ) + MathF.Min(MathF.Max(squareX, squareY), 0f);
        }

        if (q.X <= 0f && q.Y <= 0f) {
            return MathF.Max(q.X - r.X, q.Y - r.Y);
        }

        if (q.X <= 0f) {
            return q.Y - r.Y;
        }

        if (q.Y <= 0f) {
            return q.X - r.X;
        }

        var scaled = new Vector2(q.X / r.X, q.Y / r.Y);
        return (MathF.Sqrt((scaled.X * scaled.X) + (scaled.Y * scaled.Y)) - 1f) * MathF.Min(r.X, r.Y);
    }

    static void Triangle(
        float[] target,
        int width,
        int height,
        UiVertex a,
        UiVertex b,
        UiVertex c,
        BatchKind kind,
        IReadOnlyList<UiShape> shapes,
        GlyphAtlas atlas,
        Bounds clip,
        float[]? surface,
        ReadOnlySpan<UiMask> masks
    ) {
        var area = Edge(a.Position, b.Position, c.Position);

        if (MathF.Abs(area) < 1e-9f) {
            return;
        }

        // ⚠ <b>Wound consistently before the fill rule is applied, because the pipeline is
        // <c>RasterizerState.TwoSided</c> and the tessellator emits both.</b> The rule below is stated
        // in terms of which side of an edge the interior is on, which is only meaningful once every
        // triangle agrees about that. Swapping two vertices flips the sign of the area and of all three
        // edge functions at once, so the barycentrics stay attached to the vertices they came from.
        if (area < 0f) {
            (b, c) = (c, b);
            area = -area;
        }

        // ⚠ <b>Half-open along the edges the interior is <i>not</i> on, which is what stops a shared
        // edge being shaded twice.</b> A quad is two triangles that share its diagonal, and a sample
        // landing exactly on that diagonal used to satisfy `w >= 0` in both of them — so the pixel was
        // blended twice. For an opaque fill that is invisible, because blending a colour over itself
        // returns it; for anything translucent it is not, and a composited group is translucent by
        // construction. A 40×40 box at half opacity drew a 0.75 pixel down its diagonal where 0.5 was
        // wanted, which is how this was found.
        var inclusive0 = TopLeft(b.Position, c.Position);
        var inclusive1 = TopLeft(c.Position, a.Position);
        var inclusive2 = TopLeft(a.Position, b.Position);

        var minX = Math.Max(clip.MinX, (int)MathF.Floor(Min3(a.Position.X, b.Position.X, c.Position.X)));
        var maxX = Math.Min(clip.MaxX, (int)MathF.Ceiling(Max3(a.Position.X, b.Position.X, c.Position.X)));
        var minY = Math.Max(clip.MinY, (int)MathF.Floor(Min3(a.Position.Y, b.Position.Y, c.Position.Y)));
        var maxY = Math.Min(clip.MaxY, (int)MathF.Ceiling(Max3(a.Position.Y, b.Position.Y, c.Position.Y)));

        for (var y = minY; y <= maxY; y++) {
            for (var x = minX; x <= maxX; x++) {
                // The pixel centre, which is where Vulkan samples. Testing the corner instead moves
                // every edge half a pixel and every reference with it.
                var sample = new Vector2(x + 0.5f, y + 0.5f);

                var w0 = Edge(b.Position, c.Position, sample) / area;
                var w1 = Edge(c.Position, a.Position, sample) / area;
                var w2 = Edge(a.Position, b.Position, sample) / area;

                // ⚠ Both windings accepted, because the pipeline is RasterizerState.TwoSided. A
                // one-sided test here would drop whichever of the tessellator's triangles came out
                // the other way round and leave holes nobody could explain from the draw list. The
                // winding was normalised above rather than tested for here, so that the fill rule has
                // one case instead of two mirrored ones.
                if (!Covers(w0, inclusive0) || !Covers(w1, inclusive1) || !Covers(w2, inclusive2)) {
                    continue;
                }

                var texture = new Vector2(
                    (a.Texture.X * w0) + (b.Texture.X * w1) + (c.Texture.X * w2),
                    (a.Texture.Y * w0) + (b.Texture.Y * w1) + (c.Texture.Y * w2)
                );

                var colour = new Color4(
                    (a.Color.R * w0) + (b.Color.R * w1) + (c.Color.R * w2),
                    (a.Color.G * w0) + (b.Color.G * w1) + (c.Color.G * w2),
                    (a.Color.B * w0) + (b.Color.B * w1) + (c.Color.B * w2),
                    (a.Color.A * w0) + (b.Color.A * w1) + (c.Color.A * w2)
                );

                var shape = (a.Shape.X * w0) + (b.Shape.X * w1) + (c.Shape.X * w2);

                var fragment = kind switch {
                    BatchKind.Geometry => Box(texture, colour, shapes, (int)(shape + 0.5f), x, y),
                    BatchKind.Text => Text(texture, colour, shape, atlas),

                    // ⚠ Only an image whose texture is a composited group, which is the one image this
                    // renderer has the pixels for. Every other image number names a texture a host
                    // owns and this has never seen, so it keeps falling through to `Solid` — which
                    // draws nothing, because an image quad's `shape.x` is zero. That is the behaviour
                    // this renderer has always had for images and it is not being changed here.
                    BatchKind.Image when surface is not null =>
                        Composite(surface, width, height, texture, colour, masks),
                    _ => Solid(colour, shape)
                };

                Blend(target, ((y * width) + x) * 4, fragment);
            }
        }
    }

    /// <summary>A rounded box or its border, as <c>ui-box.frag</c> evaluates it.</summary>
    /// <param name="texture">The offset from the box's centre, in pixels.</param>
    /// <param name="colour">The interpolated vertex colour.</param>
    /// <param name="shapes">The frame's shape records.</param>
    /// <param name="index">Which record this fragment reads.</param>
    /// <param name="px">
    ///     The fragment's column. ⚠ Needed for its <i>parity</i> alone, and only because
    ///     <c>fwidth</c> is a property of the 2×2 quad rather than of the fragment — see the
    ///     derivative below. Nothing else in this method depends on where the pixel is.
    /// </param>
    /// <param name="py">The fragment's row, for the same reason.</param>
    static Color4 Box(Vector2 texture, Color4 colour, IReadOnlyList<UiShape> shapes, int index, int px, int py) {
        if (index < 0 || index >= shapes.Count) {
            return default;
        }

        var shape = shapes[index];
        var half = new Vector2(shape.Size.X, shape.Size.Y);
        var radius = CornerRadius(shape, texture);
        var distance = BoxDistance(texture, half, radius);

        // ⚠ <b><c>fwidth</c>, and the 2×2 quad has to be emulated rather than reasoned away.</b> The
        // texture coordinate of a box *is* the offset from its centre in pixels, so its screen-space
        // derivative is exactly one along each axis and there is no interpolation to reconstruct —
        // which is what used to justify taking a plain forward difference to `point + (1, 0)`. It
        // does not follow. A GPU does not difference against the *next* fragment; it differences
        // across the quad the fragment sits in, so the pixel on the odd side of a quad gets a
        // *backward* difference, and both fragments of the pair get a difference taken over the same
        // two samples. For a straight edge the distance is affine and every one of those is the same
        // number. Around a corner it is not: the arc's curvature is second order, the forward and
        // backward differences straddle it in opposite directions, and the two disagree by roughly
        // the second derivative — up to a twentieth of a pixel of coverage on a ten-pixel radius.
        //
        // ⚠ That was worth up to <b>seventeen levels of 255</b> on the corner arcs, on a frame with
        // nothing composited in it at all, and nothing could see it: the golden suite compares each
        // renderer against a committed *picture*, never against the other one, so the two drifted
        // inside their separate tolerances until <c>UiCompositingTests</c> put them side by side.
        // Emulating the quad takes the two-box fixture from a worst channel of nine to a worst of
        // one, which is the 8-bit store and nothing else.
        //
        // ⚠ <b>Fine and not coarse, established by measuring both.</b> <c>fwidth</c> is
        // implementation-defined between the two: <i>fine</i> differences each row and column
        // separately, <i>coarse</i> gives all four fragments the quad's top-left difference. This is
        // the fine one, because that is what the device does — the coarse variant was tried and left
        // ten pixels of the compositing fixture over the channel bound where this leaves one. A
        // driver that reported coarse derivatives would cost those ten back, which is what the
        // headroom in <c>UiCompositingTests.Agreement</c> is sized for.
        var sx = (px & 1) == 0 ? 1f : -1f;
        var sy = (py & 1) == 0 ? 1f : -1f;

        var width = MathF.Max(
            MathF.Abs(BoxDistance(texture + new Vector2(sx, 0f), half, radius) - distance)
            + MathF.Abs(BoxDistance(texture + new Vector2(0f, sy), half, radius) - distance),
            1e-4f
        );

        var coverage = Coverage(distance, width);
        var thickness = shape.Size.Z;

        if (thickness > 0f) {
            // The band between the edge and `thickness` inside it, as the difference of two
            // coverages rather than a second shape — so the two share one antialiased outer edge and
            // cannot disagree about where it is.
            coverage -= Coverage(distance + thickness, width);
        }

        var fill = colour;

        if (shape.Size.W > 0f) {
            fill = GradientColour(shape, colour, Parameter(shape, texture, half));
        }

        var alpha = fill.A * coverage;
        return new(fill.R * alpha, fill.G * alpha, fill.B * alpha, alpha);
    }

    /// <summary>Where this pixel sits along the ramp, as <c>UiBox.Parameter</c> evaluates it.</summary>
    /// <remarks>
    ///     ⚠ <b>No centre and no radius are read, because the record carries neither.</b> CSS's
    ///     defaults for both round shapes are <i>at center</i> with an extent that is a function of the
    ///     box, so the half size is all either of them needs — which is what let all four of A11's
    ///     owed pieces fit inside two more <c>Vector4</c>s.
    /// </remarks>
    static float Parameter(UiShape shape, Vector2 point, Vector2 half) {
        var kind = (int) (shape.Size.W + 0.5f);

        if (kind == (int) GradientShape.Radial) {
            // `ellipse farthest-corner at center`: the farthest-*side* ellipse is the point divided
            // by the half size, on which the corner sits at root two — so the reciprocal of root two
            // is the whole of `farthest-corner`.
            var normalised = new Vector2(
                point.X / MathF.Max(half.X, 1e-4f),
                point.Y / MathF.Max(half.Y, 1e-4f)
            );

            return MathF.Sqrt((normalised.X * normalised.X) + (normalised.Y * normalised.Y)) * 0.70710678f;
        }

        if (kind == (int) GradientShape.Conic) {
            // CSS starts at twelve o'clock and sweeps clockwise; screen space is y-down, so up is -y
            // and `Atan2(x, -y)` is already CSS's angle. The axis's own angle is the `from <angle>`.
            var angle = MathF.Atan2(point.X, -point.Y) - MathF.Atan2(shape.Axis.X, -shape.Axis.Y);
            var turns = (angle / MathF.Tau) + 1f;

            return turns - MathF.Floor(turns);
        }

        var axis = Normalize(new Vector2(shape.Axis.X, shape.Axis.Y));
        var reach = MathF.Abs(axis.X * half.X) + MathF.Abs(axis.Y * half.Y);

        return ((((point.X * axis.X) + (point.Y * axis.Y)) / MathF.Max(reach, 1e-4f)) * 0.5f) + 0.5f;
    }

    /// <summary>The colour at <c>t</c>, as <c>UiBox.GradientColour</c> evaluates it.</summary>
    static Color4 GradientColour(UiShape shape, Color4 near, float t) {
        var end = new Color4(shape.End.X, shape.End.Y, shape.End.Z, shape.End.W);

        if (shape.Stops.W > 0f) {
            var mid = new Color4(shape.Mid.X, shape.Mid.Y, shape.Mid.Z, shape.Mid.W);

            // Which side of the *middle stop*, not of one half: with `via-40%` the two halves are
            // different lengths, and splitting at 0.5 draws the right colour with the wrong slope.
            return t < shape.Stops.Y
                ? MixStops(shape, near, mid, Span(t, shape.Stops.X, shape.Stops.Y))
                : MixStops(shape, mid, end, Span(t, shape.Stops.Y, shape.Stops.Z));
        }

        return MixStops(shape, near, end, Span(t, shape.Stops.X, shape.Stops.Z));
    }

    /// <summary>Where <c>t</c> sits between two stops, flat outside them.</summary>
    /// <remarks>
    ///     A zero-width span is a hard edge rather than a division by zero — <c>from-50% to-50%</c> is
    ///     a legal declaration and a step is what it means.
    /// </remarks>
    static float Span(float t, float from, float to) {
        var width = to - from;

        return width > 1e-4f
            ? Math.Clamp((t - from) / width, 0f, 1f)
            : t < from ? 0f : 1f;
    }

    /// <summary>Mixes two stops in whichever space the record names.</summary>
    /// <remarks>
    ///     ⚠ <b>Oklab goes through <see cref="Oklab.Lerp(Color4, Color4, float)" /> rather than being
    ///     transcribed the way the rest of this file transcribes the shader</b>, and that is the one
    ///     deliberate departure from "port the shader as written". The shader cannot call the host's
    ///     implementation and spells the same matrices out; keeping <i>one</i> of the two authoritative
    ///     means a mistake in the transcription shows up as a diff here instead of agreeing with
    ///     itself. What that costs is the last bit or two: the shader's signed cube root is
    ///     <c>sign(x)·pow(|x|, ⅓)</c> where <c>MathF.Cbrt</c> is correctly rounded, so the two are not
    ///     bit-identical — which is a real limit on how tightly a device golden could ever pin this.
    /// </remarks>
    static Color4 MixStops(UiShape shape, Color4 a, Color4 b, float u) {
        var space = (GradientSpace) (int) (shape.Axis.W + 0.5f);

        if (space == GradientSpace.Oklab) {
            return Oklab.Lerp(a, b, u);
        }

        if (space == GradientSpace.Srgb) {
            return new Color4(
                ColorSpace.SrgbToLinear(Lerp(ColorSpace.LinearToSrgb(a.R), ColorSpace.LinearToSrgb(b.R), u)),
                ColorSpace.SrgbToLinear(Lerp(ColorSpace.LinearToSrgb(a.G), ColorSpace.LinearToSrgb(b.G), u)),
                ColorSpace.SrgbToLinear(Lerp(ColorSpace.LinearToSrgb(a.B), ColorSpace.LinearToSrgb(b.B), u)),
                Lerp(a.A, b.A, u)
            );
        }

        return new Color4(Lerp(a.R, b.R, u), Lerp(a.G, b.G, u), Lerp(a.B, b.B, u), Lerp(a.A, b.A, u));
    }

    /// <summary>A glyph, as <c>ui-text.frag</c> evaluates it.</summary>
    static Color4 Text(Vector2 texture, Color4 colour, float pixelRange, GlyphAtlas atlas) {
        var field = Sample(atlas, texture);

        // ⚠ The median of three channels, never their average and never a colour. One distance
        // cannot describe two edges meeting at a point, which is what a serif is; three whose median
        // reconstructs the shape keep the corner. Treating the three as RGB is the classic mistake
        // and draws text that is too thin.
        var distance = Median(field.X, field.Y, field.Z) - 0.5f;
        var coverage = Math.Clamp((distance * MathF.Max(pixelRange, 1e-4f)) + 0.5f, 0f, 1f);

        var alpha = colour.A * coverage;
        return new(colour.R * alpha, colour.G * alpha, colour.B * alpha, alpha);
    }

    /// <summary>A tessellated path, as <c>ui-solid.frag</c> evaluates it.</summary>
    /// <remarks>
    ///     A triangle carries no distance to its own boundary, so its edge cannot be resolved here
    ///     the way a box's is. What it carries instead is the coverage the tessellator interpolated —
    ///     one across the interior, running to zero over the half-pixel strip along the outline.
    /// </remarks>
    static Color4 Solid(Color4 colour, float coverage) {
        var alpha = colour.A * Math.Clamp(coverage, 0f, 1f);
        return new(colour.R * alpha, colour.G * alpha, colour.B * alpha, alpha);
    }

    /// <summary>Bilinear, which is what the atlas sampler is.</summary>
    /// <remarks>
    ///     ⚠ Clamped at the edges rather than wrapped. The atlas pads its entries so a filtered read
    ///     of one glyph cannot pick up its neighbour, and a wrap at the texture's own border would
    ///     put the far edge of the atlas into the first glyph in it.
    /// </remarks>
    static Vector4 Sample(GlyphAtlas atlas, Vector2 uv) {
        var x = (uv.X * atlas.Width) - 0.5f;
        var y = (uv.Y * atlas.Height) - 0.5f;

        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;

        var c00 = Texel(atlas, x0, y0);
        var c10 = Texel(atlas, x0 + 1, y0);
        var c01 = Texel(atlas, x0, y0 + 1);
        var c11 = Texel(atlas, x0 + 1, y0 + 1);

        return new(
            Lerp(Lerp(c00.X, c10.X, fx), Lerp(c01.X, c11.X, fx), fy),
            Lerp(Lerp(c00.Y, c10.Y, fx), Lerp(c01.Y, c11.Y, fx), fy),
            Lerp(Lerp(c00.Z, c10.Z, fx), Lerp(c01.Z, c11.Z, fx), fy),
            0f
        );
    }

    static Vector4 Texel(GlyphAtlas atlas, int x, int y) {
        x = Math.Clamp(x, 0, atlas.Width - 1);
        y = Math.Clamp(y, 0, atlas.Height - 1);

        var offset = ((y * atlas.Width) + x) * 3;
        return new(atlas.Pixels[offset], atlas.Pixels[offset + 1], atlas.Pixels[offset + 2], 0f);
    }

    /// <summary>The radius of the corner a point is nearest, clockwise from the top left.</summary>
    static Vector2 CornerRadius(UiShape shape, Vector2 point) {
        var top = point.Y < 0f;
        var left = point.X < 0f;
        var index = top ? left ? 0 : 1 : left ? 3 : 2;

        return new(Lane(shape.RadiiX, index), Lane(shape.RadiiY, index));
    }

    static float Lane(Vector4 value, int index) =>
        index switch {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            _ => value.W
        };

    /// <summary>Coverage across a one-pixel band, from the derivative of the distance itself.</summary>
    static float Coverage(float distance, float width) => Math.Clamp(0.5f - (distance / width), 0f, 1f);

    /// <summary>Branchless via min and max, as the shader has it.</summary>
    static float Median(float a, float b, float c) =>
        MathF.Max(MathF.Min(a, b), MathF.Min(MathF.Max(a, b), c));

    /// <summary>Premultiplied source over destination — the blend state the UI pipeline uses.</summary>
    static void Blend(float[] target, int offset, Color4 source) {
        var inverse = 1f - source.A;
        target[offset + 0] = source.R + (target[offset + 0] * inverse);
        target[offset + 1] = source.G + (target[offset + 1] * inverse);
        target[offset + 2] = source.B + (target[offset + 2] * inverse);
        target[offset + 3] = source.A + (target[offset + 3] * inverse);
    }

    /// <summary>Whether a sample exactly on an edge belongs to this triangle.</summary>
    /// <remarks>
    ///     ⚠ <b>Strictly inside always counts; the rule only decides the ties.</b> Two triangles sharing
    ///     an edge traverse it in opposite directions, so exactly one of them sees a downward <c>dy</c>
    ///     and the pixel is shaded once. The wording is the usual top-left rule stated for this file's
    ///     edge function — <see cref="Edge" /> is positive on the interior once the winding is
    ///     normalised, which makes a horizontal edge the <i>top</i> one when it runs left to right, and
    ///     any edge running upwards a <i>left</i> one.
    /// </remarks>
    static bool TopLeft(Vector2 from, Vector2 to) {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;

        // ⚠ <b>Axis-aligned edges keep the closed test they have always had, and that is a deliberate
        // departure from the textbook rule.</b> The full rule also opens a quad's right and bottom
        // edges, which is correct — it is what stops two abutting quads both shading the column they
        // share — but every UI primitive here is an axis-aligned quad, so switching it on re-resolves
        // the antialiasing of *every* edge in the suite whose coordinate happens to land on a sample
        // centre. Measured: eleven committed screenshots moved, and the ones worth looking at were the
        // outliner's one-pixel tree connectors losing half their coverage. That is a real improvement
        // to argue for on its own day; it is not this change, and bundling it would hide the diagonal
        // fix inside a diff nobody could read.
        if (dx == 0f || dy == 0f) {
            return true;
        }

        // The diagonal, which is the only edge a quad's two triangles actually share. They traverse it
        // in opposite directions, so exactly one of them sees a negative `dy` and the sample is shaded
        // once. A tessellated path's shared edges fall here too, unless they are exactly horizontal.
        return dy < 0f;
    }

    /// <summary>Whether a barycentric puts the sample inside, ties broken by the fill rule.</summary>
    static bool Covers(float weight, bool inclusive) => weight > 0f || (weight == 0f && inclusive);

    static float Edge(Vector2 a, Vector2 b, Vector2 point) =>
        ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));

    static Vector2 Normalize(Vector2 value) {
        var length = MathF.Sqrt((value.X * value.X) + (value.Y * value.Y));
        return length < 1e-6f ? new(0f, 0f) : new(value.X / length, value.Y / length);
    }

    static float Lerp(float from, float to, float t) => from + ((to - from) * t);

    static float Min3(float a, float b, float c) => MathF.Min(a, MathF.Min(b, c));

    static float Max3(float a, float b, float c) => MathF.Max(a, MathF.Max(b, c));

    static Bounds Intersect(Rectangle clip, int width, int height) =>
        new(
            Math.Max(0, (int)MathF.Floor(clip.Left)),
            Math.Max(0, (int)MathF.Floor(clip.Top)),
            Math.Min(width - 1, (int)MathF.Ceiling(clip.Right) - 1),
            Math.Min(height - 1, (int)MathF.Ceiling(clip.Bottom) - 1)
        );

    /// <summary>A composited group's surface, as <c>UiImage.Fragment</c> evaluates it.</summary>
    /// <remarks>
    ///     ⚠ <b>The sample is <i>already</i> premultiplied and is not multiplied again, which is the
    ///     whole difference between this and an ordinary image.</b> Every UI pipeline writes
    ///     premultiplied colour because that is what the blend state consumes, so a group's surface
    ///     holds premultiplied colour too — where a texture a host uploaded holds straight alpha and has
    ///     to be premultiplied on the way out. Doing it twice darkens every partly covered pixel, which
    ///     shows as a dark fringe around everything inside the group and is the classic symptom.
    ///     <para>
    ///         The shader is told which it has been handed by <c>shape.x</c>; here the two cases are
    ///         different call sites, so the flag is not read. That is a deliberate asymmetry and the one
    ///         place this file departs from transcribing the shader — see the class remarks on why the
    ///         transcription is otherwise literal.
    ///     </para>
    /// </remarks>
    static Color4 Composite(
        float[] surface,
        int width,
        int height,
        Vector2 uv,
        Color4 tint,
        ReadOnlySpan<UiMask> masks
    ) {
        var sampled = SampleSurface(surface, width, height, uv);

        // ⚠ <b>Here, at the composite, and <i>not</i> at the seam the blur and the colour matrix are
        // applied at — which is the one place this file deliberately does not follow next door's
        // arrangement.</b> `UiLayer.Filter` is folded into the finished surface a few lines above
        // because a colour matrix is the same affine map at every pixel and therefore commutes with
        // both the Gaussian and the bilinear tap below. A mask does not: it is a scalar that varies
        // with position, so `m(p)·Σ wᵢsᵢ` and `Σ wᵢ·m(pᵢ)·sᵢ` are different numbers wherever the ramp
        // is not flat across the kernel — which is exactly over a blurred edge. Applying it here is
        // what makes this agree with `ui-mask.frag`, which has no choice about where it applies it.
        //
        // ⚠ <b>The point comes from the same <c>uv</c> the sample did</b>, times the surface size,
        // because every layer surface is the viewport's size and so that product *is* the document
        // pixel — the identical expression the shader evaluates. Taking the loop's `x` and `y`
        // instead would be right today and would stop being right the first time a composite quad
        // was not laid out one-to-one.
        //
        // ⚠ <b>And the fold over the list is <c>UiMask</c>'s own, not a loop written here.</b> The
        // operators are not commutative, so two transcriptions of the same list could agree entry by
        // entry and still disagree about the order they combine in — which is a class of divergence
        // `UiCompositingTests` would report as a diff over the whole group with no obvious cause. One
        // method, called by both executors, is what removes the possibility.
        var coverage = UiMask.Coverage(masks, new Vector2(uv.X * width, uv.Y * height));

        // ⚠ <b>All four channels, because the sample is premultiplied.</b> Scaling coverage on
        // premultiplied colour is `(rgb·m, a·m)`; the `(rgb, a·m)` an ordinary straight-alpha image
        // would want is the premultiply mistake `ui-image.frag`'s `varying_shape.x` exists to
        // prevent. Leaving `rgb` alone here brightens every masked texel towards full strength as the
        // mask closes, which reads as a glow along the fading edge.
        var scale = tint.A * coverage;

        return new Color4(
            sampled.X * scale,
            sampled.Y * scale,
            sampled.Z * scale,
            sampled.W * scale
        );
    }

    /// <summary>Bilinear over a group's surface, which is what the shared sampler is.</summary>
    /// <remarks>
    ///     ⚠ <b>Bilinear, and it resolves to a single texel every time.</b> A composite quad covers the
    ///     group's bounds, which <see cref="UiGeometryBuilder" /> has already rounded out to whole
    ///     pixels, over a surface that is the size of the viewport — so the mapping is one to one and a
    ///     pixel centre lands exactly on a texel centre, where both weights are zero. Written as a
    ///     filter anyway rather than as a texel fetch, because the GPU's sampler <i>is</i> a filter: if
    ///     the two ever stopped being aligned, a fetch here would quietly disagree with the device
    ///     instead of blurring the same way it does.
    /// </remarks>
    static Vector4 SampleSurface(float[] surface, int width, int height, Vector2 uv) {
        var x = (uv.X * width) - 0.5f;
        var y = (uv.Y * height) - 0.5f;

        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;

        var c00 = Pixel(surface, width, height, x0, y0);
        var c10 = Pixel(surface, width, height, x0 + 1, y0);
        var c01 = Pixel(surface, width, height, x0, y0 + 1);
        var c11 = Pixel(surface, width, height, x0 + 1, y0 + 1);

        return new(
            Lerp(Lerp(c00.X, c10.X, fx), Lerp(c01.X, c11.X, fx), fy),
            Lerp(Lerp(c00.Y, c10.Y, fx), Lerp(c01.Y, c11.Y, fx), fy),
            Lerp(Lerp(c00.Z, c10.Z, fx), Lerp(c01.Z, c11.Z, fx), fy),
            Lerp(Lerp(c00.W, c10.W, fx), Lerp(c01.W, c11.W, fx), fy)
        );
    }

    static Vector4 Pixel(float[] surface, int width, int height, int x, int y) {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);

        var offset = ((y * width) + x) * 4;
        return new(surface[offset], surface[offset + 1], surface[offset + 2], surface[offset + 3]);
    }

    /// <summary>One frame's walk, with a surface of its own for each composited group.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same execution the GPU renderer performs, in the same order, and that is the
    ///         point of it being written this way.</b> A group is rendered into a surface first and the
    ///         result is drawn back as an ordinary image afterwards, on both paths — rather than this
    ///         one taking the shortcut it could easily take of blending a subtree's fragments with the
    ///         alpha folded in. A shortcut would agree with the device on the cases anybody thought to
    ///         test and diverge on the ones nobody did, and the only thing that would notice is a
    ///         committed screenshot.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every surface is the size of the whole viewport.</b> See <see cref="UiLayer" />:
    ///         it means neither path has an origin to subtract, so neither can subtract it differently.
    ///     </para>
    /// </remarks>
    sealed class Frame(UiGeometry geometry, GlyphAtlas atlas, int width, int height) {
        readonly Dictionary<ulong, float[]> surfaces = [];

        /// <summary>Each composited group's mask list, as a range of <see cref="UiGeometry.Masks" />.</summary>
        /// <remarks>
        ///     ⚠ <b>A second dictionary beside <see cref="surfaces" /> rather than a pass beside
        ///     <c>Blurred</c> and <c>Filtered</c>, and the shape is <c>UiRenderer.layerMasks</c>'
        ///     on purpose.</b> A mask cannot be folded into a surface — see <see cref="UiMask" /> —
        ///     so it has to survive until the composite draw reads it, which is precisely what
        ///     keying it by <c>UiLayer.Image</c> buys. The device does the same lookup in
        ///     <c>SubmitDraw</c>, so the two paths find the mask at the same moment as well as apply
        ///     it at the same moment.
        ///     ⚠ A range and not the entries, because the entries are already contiguous in the
        ///     geometry and copying them here would be a second copy for the two paths to disagree
        ///     about.
        /// </remarks>
        readonly Dictionary<ulong, (int First, int Count)> masks = [];

        /// <summary>Every mask of the frame, flattened once so a composite can take a span of it.</summary>
        /// <remarks>
        ///     ⚠ <c>UiGeometry.Masks</c> is an <c>IReadOnlyList</c> and a fragment needs a
        ///     <c>ReadOnlySpan</c>, so the copy happens once per frame here rather than once per
        ///     <i>pixel</i> at the composite.
        /// </remarks>
        readonly UiMask[] entries = [.. geometry.Masks];

        int next;

        /// <summary>Executes a range of draws into one surface.</summary>
        internal void Run(float[] target, int from, int to) {
            var draw = from;

            while (draw < to) {
                // ⚠ Matched on the draw index rather than searched for, which is what
                // `UiGeometry.Layers` being in pre-order buys. Two groups can legitimately open at the
                // same index — a translucent element that paints nothing of its own, wrapping a
                // translucent child — and the outer one has to be entered first; pre-order is exactly
                // the statement that it comes first in the list.
                if (next < geometry.Layers.Count && geometry.Layers[next].First == draw) {
                    var layer = geometry.Layers[next++];

                    // Transparent black, not the background: a group composites *over* what is already
                    // there, so starting it from the destination would blend the destination in twice.
                    var surface = new float[width * height * 4];

                    Run(surface, layer.First, layer.First + layer.Count);

                    // ⚠ <b>Here and nowhere else: the one moment a group's surface is finished and
                    // nothing has sampled it.</b> Everything downstream — the composite draw,
                    // `Composite`, `SampleSurface` — is indifferent to how the surface got its
                    // contents, which is exactly why the filter belongs at the seam rather than in
                    // any of them.
                    if (layer.Blur > 0f) {
                        surface = Blurred(surface, layer.Blur);
                    }

                    // ⚠ <b>Between the two, and the position is forced from both sides.</b> After the
                    // blur, because <c>blur(σ) drop-shadow(0 0 τ)</c> casts the shadow of the
                    // <i>blurred</i> element and the two Gaussians do not commute — see
                    // <see cref="UiLayer.Shadow" />. Before the matrix, because that is where the
                    // device takes it: `UiRenderer` never writes `UiLayer.Filter` into a surface at
                    // all, so its shadow is cast from the blurred, untinted one, and taking it from a
                    // different source here is the divergence `UiCompositingTests` exists to catch.
                    //
                    // ⚠ It happens to be the same picture either way — <c>UiDropShadow.Tint</c> has
                    // zero coefficients, so whatever the group's matrix did to the colour is
                    // multiplied by nothing and only the alpha survives, which no colour matrix
                    // touches. Written in the order that is right for a reason rather than the order
                    // that is right by accident, because the accident stops holding the first time a
                    // filter function reads alpha.
                    if (layer.Shadow is { } cast) {
                        surfaces[layer.ShadowImage] = Filtered(Blurred(surface, cast.Blur), cast.Tint);
                    }

                    // ⚠ <b>At the same seam, in place, and <i>not</i> at the same place the device
                    // does it — which is the one deliberate structural divergence in this file and
                    // the reason it is safe is arithmetic rather than testing.</b> `UiRenderer`
                    // applies the matrix in the composite's fragment stage, because that costs it no
                    // pass and no second surface; a convolution has no such option and a per-pixel
                    // transform does. Both give the same picture because the transform is affine in
                    // premultiplied colour: `M(Σ wᵢ sᵢ) = Σ wᵢ M(sᵢ)`, so it commutes exactly with
                    // both the Gaussian above and the bilinear sampler below, and the tint the
                    // composite multiplies in afterwards is a scalar that commutes with everything.
                    // ⚠ The clamp in `UiColorMatrix.Apply` is the part that does *not* commute, which
                    // is why it happens once, last, on both paths rather than per tap.
                    //
                    // ⚠ Over the whole viewport-sized surface and not over `layer.Bounds`, because
                    // the two have to be the same picture and the device's fragment stage cannot
                    // choose. It is free of consequence: the matrix maps transparent black to
                    // transparent black, so every texel outside the group's ink is unchanged.
                    if (layer.Filter is { } matrix && !matrix.IsIdentity) {
                        surface = Filtered(surface, matrix);
                    }

                    surfaces[layer.Image] = surface;

                    // ⚠ Recorded rather than applied, which is the whole of the difference between a
                    // mask and the two transforms above it. Nothing here may touch the surface with
                    // it: the composite draw is the seam, on this path and on the device's.
                    if (layer.MaskCount > 0) {
                        masks[layer.Image] = (layer.MaskFirst, layer.MaskCount);

                        // ⚠ The shadow is masked on the same terms and in the same frame — see
                        // `UiRenderer.Compose`, which states the divergence from CSS this shares:
                        // the mask travels with the silhouette, because both executors recover its
                        // point from the quad's own `uv × size` and the shadow quad's UV is the
                        // un-displaced one. Recorded and not applied, for the reason two lines up.
                        if (layer.Shadow is not null) {
                            masks[layer.ShadowImage] = (layer.MaskFirst, layer.MaskCount);
                        }
                    }

                    // The composite draw sits immediately after the group's own draws and is an
                    // ordinary image naming this surface, so the loop picks it up on the next turn.
                    draw = layer.First + layer.Count;
                    continue;
                }

                Execute(target, geometry.Draws[draw]);
                draw++;
            }
        }

        /// <summary>A separable Gaussian over a finished layer surface.</summary>
        /// <param name="surface">The group's contents, premultiplied.</param>
        /// <param name="blur">The standard deviation, in pixels — this path draws at 1:1.</param>
        /// <returns>A new surface. The one handed in is left alone.</returns>
        /// <remarks>
        ///     <para>
        ///         ⚠ <b>Two one-dimensional passes and not one two-dimensional kernel, and the
        ///         reason is agreement rather than speed.</b> A Gaussian is separable exactly, so the
        ///         two give the same answer in real arithmetic — but not in <c>float</c>, and the
        ///         device has no choice: it cannot read and write one attachment, so it is separable
        ///         there whatever this does. Summing the square here would put a rounding difference
        ///         between the two executors that grows with the kernel, on the one test whose whole
        ///         job is that they agree.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>Premultiplied straight through, with no un-premultiply.</b> The surface holds
        ///         premultiplied colour — see <see cref="Composite" /> — and a weighted sum of
        ///         premultiplied samples is the premultiplied weighted sum, which is the whole reason
        ///         compositing works in that encoding. Dividing out the alpha to blur "the colour"
        ///         and multiplying it back is the classic version of this that produces a halo of the
        ///         wrong hue wherever the group's edge meets transparent black, because the colour
        ///         under a zero alpha is not a colour.
        ///     </para>
        ///     <para>
        ///         ⚠ <b>The weights are normalised over the <i>whole</i> kernel and not over the taps
        ///         that landed inside the surface, and the difference is a bright rim.</b>
        ///         Renormalising per pixel is what a clamp-to-edge sampler amounts to — it makes the
        ///         edge row stand in for everything past it — and near a group that runs to the
        ///         viewport edge that lifts the result towards the edge's own colour instead of
        ///         letting it fade. Dividing by the full sum makes a tap outside the surface
        ///         contribute exactly the transparent black that is there, which is also what the
        ///         shader does, by the same test in UV space. The normalisation *does* absorb the
        ///         truncation at <see cref="UiLayer.MaximumKernel" />, because that tail is inside the
        ///         surface and simply not summed.
        ///     </para>
        /// </remarks>
        float[] Blurred(float[] surface, float blur) {
            // The same number the shader arrives at, from the same method, because a kernel that is
            // one tap wider on one path is a difference `UiCompositingTests` would report as a
            // disagreement about compositing.
            var reach = UiLayer.KernelRadius(blur, 1f);

            if (reach <= 0) {
                return surface;
            }

            var weights = new float[reach + 1];
            var total = 0f;

            for (var i = 0; i <= reach; i++) {
                weights[i] = MathF.Exp(-(i * i) / (2f * blur * blur));
                total += i == 0 ? weights[i] : 2f * weights[i];
            }

            // ⚠ Folded into the weights rather than divided out per pixel, and the shader does the
            // same. Two executors that normalise at different points in the sum are two executors
            // rounding differently, which is the only kind of disagreement this whole file exists to
            // rule out.
            for (var i = 0; i <= reach; i++) {
                weights[i] /= total;
            }

            var scratch = new float[surface.Length];

            Sweep(surface, scratch, reach, weights, horizontal: true);

            var result = new float[surface.Length];

            Sweep(scratch, result, reach, weights, horizontal: false);

            return result;
        }

        /// <summary>A colour matrix over a finished layer surface, as <c>ui-colour.frag</c> does it.</summary>
        /// <param name="surface">The group's contents, premultiplied.</param>
        /// <param name="matrix">The transform.</param>
        /// <returns>A new surface. The one handed in is left alone.</returns>
        /// <remarks>
        ///     ⚠ <b>A new buffer rather than a rewrite in place, and it costs an allocation to buy the
        ///     same shape <see cref="Blurred" /> has.</b> The blur cannot be in place — a convolution
        ///     reads neighbours — so it returns a new array, and a filter that mutated its argument
        ///     would leave the caller with two functions that look interchangeable and are not. The
        ///     device's cost is not this: its transform is nine multiplies in a fragment shader that
        ///     was already running, and this file's job is the picture rather than the cost.
        /// </remarks>
        static float[] Filtered(float[] surface, in UiColorMatrix matrix) {
            var result = new float[surface.Length];

            for (var offset = 0; offset < surface.Length; offset += 4) {
                var filtered = matrix.Apply(
                    new Color4(surface[offset], surface[offset + 1], surface[offset + 2], surface[offset + 3])
                );

                result[offset] = filtered.R;
                result[offset + 1] = filtered.G;
                result[offset + 2] = filtered.B;
                result[offset + 3] = filtered.A;
            }

            return result;
        }

        /// <summary>One axis of the separable pass.</summary>
        void Sweep(float[] from, float[] into, int reach, float[] weights, bool horizontal) {
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    var sum = Vector4.Zero;

                    for (var i = -reach; i <= reach; i++) {
                        var sampleX = horizontal ? x + i : x;
                        var sampleY = horizontal ? y : y + i;

                        // ⚠ Outside the surface contributes nothing rather than clamping to the edge,
                        // which is where this parts company with `Pixel` a few methods down.
                        // Clamp-to-edge is right for an atlas, whose texels all hold ink; a layer
                        // surface's outside is transparent black, and smearing its edge row outwards
                        // would invent coverage the group never drew — visible only on a group that
                        // runs to the viewport edge, which is most panels.
                        if (sampleX < 0 || sampleX >= width || sampleY < 0 || sampleY >= height) {
                            continue;
                        }

                        var weight = weights[Math.Abs(i)];
                        var offset = ((sampleY * width) + sampleX) * 4;

                        sum += new Vector4(from[offset], from[offset + 1], from[offset + 2], from[offset + 3]) * weight;
                    }

                    var destination = ((y * width) + x) * 4;

                    into[destination] = sum.X;
                    into[destination + 1] = sum.Y;
                    into[destination + 2] = sum.Z;
                    into[destination + 3] = sum.W;
                }
            }
        }

        void Execute(float[] target, UiDraw draw) {
            // A clip is a state change the builder has already resolved onto every draw, so there is
            // nothing to execute for one. Skipped rather than assumed absent: the batch list
            // partitions the commands by design, so a Clip batch reaching a consumer is normal.
            if (draw.Kind == BatchKind.Clip || draw.Count == 0) {
                return;
            }

            var clip = Intersect(draw.Clip, width, height);

            if (clip.Width <= 0 || clip.Height <= 0) {
                return;
            }

            var surface = draw.Kind == BatchKind.Image && surfaces.TryGetValue(draw.Image, out var found)
                ? found
                : null;

            var mask = draw.Kind == BatchKind.Image && masks.TryGetValue(draw.Image, out var range)
                ? entries.AsSpan(range.First, range.Count)
                : default;

            for (var i = draw.First; i + 2 < draw.First + draw.Count; i += 3) {
                Triangle(
                    target,
                    width,
                    height,
                    geometry.Vertices[(int)geometry.Indices[i]],
                    geometry.Vertices[(int)geometry.Indices[i + 1]],
                    geometry.Vertices[(int)geometry.Indices[i + 2]],
                    draw.Kind,
                    geometry.Shapes,
                    atlas,
                    clip,
                    surface,
                    mask
                );
            }
        }
    }

    readonly record struct Bounds(int MinX, int MinY, int MaxX, int MaxY) {
        public int Width => MaxX - MinX + 1;

        public int Height => MaxY - MinY + 1;
    }
}
