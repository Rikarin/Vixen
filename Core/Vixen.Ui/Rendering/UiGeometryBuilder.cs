// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Ui.Rendering;

/// <summary>
///     Turns a frame's draw list into the vertices a renderer submits.
/// </summary>
/// <remarks>
///     <para>
///         The last step that is still the interface's own. Everything below here is a pipeline, a
///         buffer and a scissor; everything above is elements and styles. Being a pure function of a
///         draw list is what lets the whole of it be tested without a device — the same argument the
///         draw list itself makes about being testable without a window.
///     </para>
///     <para>
///         ⚠ <b>Boxes are one quad each, not a tessellated outline.</b> A rounded rectangle and its
///         border are both a signed distance the shader evaluates per pixel, so a corner is exact at
///         any radius and costs four vertices — where tessellating one costs a vertex count that
///         grows with the radius and is still faceted. That is also why the two share a batch kind:
///         one shader draws both, with the thickness deciding whether the inside is filled.
///     </para>
///     <para>
///         ⚠ <b>Clips are resolved here rather than replayed.</b> A draw list pushes and pops; a
///         renderer sets a scissor. Carrying the resolved rectangle on each draw means the renderer
///         never holds a stack, and cannot be caught out by a batch it skipped having left one
///         behind.
///     </para>
/// </remarks>
public sealed class UiGeometryBuilder {
    readonly List<UiVertex> vertices = [];
    readonly List<uint> indices = [];
    readonly List<UiDraw> draws = [];
    readonly List<UiShape> shapes = [];
    readonly List<Rectangle> clips = [];
    readonly List<Vector2> points = [];
    readonly List<Contour> contours = [];
    readonly List<PathVertex> triangles = [];

    /// <summary>How many glyphs were dropped because the atlas could not hold them.</summary>
    /// <remarks>
    ///     ⚠ Counted rather than thrown on. A glyph too large for the atlas is a configuration
    ///     mistake and not a reason for a frame to fail; what it must not be is silent, because the
    ///     symptom is a word with a hole in it.
    /// </remarks>
    public int DroppedGlyphs { get; private set; }

    /// <summary>How far a flattened curve may sit from the curve it replaces, in document pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>Document pixels, which are device pixels only at scale one.</b> A surface drawn at
    ///     twice the scale wants half of this, or its curves are visibly faceted — the flattening
    ///     error is in the geometry and the projection magnifies it along with everything else.
    ///     Settable rather than derived, because the builder is handed a viewport and not a scale,
    ///     and inventing one would be guessing.
    /// </remarks>
    public float Tolerance { get; set; } = 0.2f;

    /// <summary>How far a path's antialiasing fringe reaches past its outline, in document pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>Half a pixel, and in document pixels like <see cref="Tolerance" />.</b> A surface drawn
    ///     at twice the scale wants half of this, or the fringe is a whole device pixel wide and the
    ///     edge reads as soft rather than smooth. Zero switches it off, which is what a caller
    ///     multisampling the pass should do: two antialiasing schemes over one edge do not make it
    ///     twice as smooth, they make a seam.
    /// </remarks>
    public float Fringe { get; set; } = 0.5f;


    /// <summary>Builds the geometry for a frame.</summary>
    /// <param name="list">The frame's draw list, already batched.</param>
    /// <param name="glyphs">Where glyph fields come from.</param>
    /// <param name="viewport">The whole surface, which is the clip when nothing has pushed one.</param>
    /// <returns>The geometry. Its lists are the builder's own and are rewritten by the next call.</returns>
    public UiGeometry Build(DrawList list, GlyphFieldCache glyphs, Rectangle viewport) {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(glyphs);

        vertices.Clear();
        indices.Clear();
        draws.Clear();
        shapes.Clear();
        clips.Clear();
        DroppedGlyphs = 0;

        var clip = viewport;

        foreach (var batch in list.Batches) {
            if (batch.Kind == BatchKind.Clip) {
                clip = Clip(list.Commands[batch.First], clip, viewport);
                continue;
            }


            var first = indices.Count;

            for (var i = 0; i < batch.Count; i++) {
                var command = list.Commands[batch.First + i];

                switch (command.Kind) {
                    case DrawCommandKind.Rectangle:
                    case DrawCommandKind.Border:
                        Box(list, command);
                        break;

                    case DrawCommandKind.Shadow:
                        Shadow(list, command);
                        break;

                    case DrawCommandKind.Image:
                        Image(command);
                        break;

                    case DrawCommandKind.Text:
                        Text(list, command, glyphs);
                        break;

                    case DrawCommandKind.Path:
                    case DrawCommandKind.PathStroke:
                        Path(list, command);
                        break;

                    default:
                        break;
                }
            }

            if (indices.Count > first) {
                draws.Add(
                    new UiDraw(batch.Kind, first, indices.Count - first, batch.Font, clip) {
                        Image = batch.Image
                    }
                );
            }
        }

        return new UiGeometry(vertices, indices, draws, shapes);
    }

    /// <summary>Applies a clip push or pop, keeping the stack here so the renderer has none.</summary>
    Rectangle Clip(DrawCommand command, Rectangle current, Rectangle viewport) {
        if (command.Kind == DrawCommandKind.ClipPop) {
            if (clips.Count > 0) {
                clips.RemoveAt(clips.Count - 1);
            }

            return clips.Count > 0 ? clips[^1] : viewport;
        }

        // ⚠ Intersected with what is already in force, never replacing it. A push inside a push that
        // set the scissor outright would let a child draw outside the panel that contains it, which
        // is the one thing a clip exists to prevent.
        var pushed = Intersect(new Rectangle(command.X, command.Y, command.Width, command.Height), current);
        clips.Add(pushed);
        return pushed;
    }

    static Rectangle Intersect(Rectangle a, Rectangle b) {
        var left = MathF.Max(a.X, b.X);
        var top = MathF.Max(a.Y, b.Y);
        var right = MathF.Min(a.X + a.Width, b.X + b.Width);
        var bottom = MathF.Min(a.Y + a.Height, b.Y + b.Height);

        return new Rectangle(left, top, MathF.Max(0, right - left), MathF.Max(0, bottom - top));
    }

    /// <summary>A rectangle or a border, as one quad the shader resolves.</summary>
    /// <remarks>
    ///     ⚠ <b>The parameters go in a record and the vertex carries its index.</b> Four elliptical
    ///     corners and a gradient are fourteen floats; on the vertex they would take it from
    ///     forty-eight bytes to a hundred and four, and every glyph in the frame would carry fields
    ///     no shader reads on them. Per box it is eighty bytes against the sixty-four its four
    ///     vertices already spend, and the layout does not move.
    /// </remarks>
    void Box(DrawList list, DrawCommand command) {
        if (command.Width <= 0 || command.Height <= 0) {
            return;
        }

        var half = new Vector2(command.Width / 2, command.Height / 2);

        // A plain box's uniform radius is written out as four equal corners rather than kept as a
        // separate path through the shader. One shape of parameters means one branch fewer per pixel
        // and one fewer thing that can disagree with itself.
        var style = command.HasStyle && (uint) command.Offset < (uint) list.Boxes.Count
            ? list.Boxes[command.Offset]
            : BoxStyle.Rounded(CornerRadii.Uniform(command.Radius));

        shapes.Add(
            new UiShape(half, command.Thickness, style.Corners, style.GradientEnd, style.GradientAxis)
        );

        // The texture coordinate is the offset from the centre, which is what a signed distance to a
        // rounded box is written in terms of — so the shader needs no uniform per box.
        Quad(
            command.X,
            command.Y,
            command.X + command.Width,
            command.Y + command.Height,
            new Vector2(-half.X, -half.Y),
            new Vector2(half.X, half.Y),
            command.Color,
            new Vector4(shapes.Count - 1, 0, 0, 0)
        );
    }

    /// <summary>A blurred box, as one quad grown far enough for the blur to land inside it.</summary>
    /// <remarks>
    ///     <para>
    ///         The same distance field and the same shader as <see cref="Box" />: a shadow is a box
    ///         whose one-pixel edge has been widened to the blur radius. What differs is the
    ///         <i>quad</i> — a soft edge reaches outwards, and drawing it on the box's own quad would
    ///         cut the blur off square at the boundary, which reads as a shadow with a crease in it.
    ///     </para>
    ///     <para>
    ///         ⚠ Grown by twice the blur rather than once. The distance at which coverage reaches
    ///         zero is the blur radius, but the field is evaluated from the box's edge and the
    ///         falloff is symmetric about it, so the visible tail runs a blur *beyond* the point the
    ///         edge itself has moved to. One blur of margin leaves a faint straight line where the
    ///         quad ends.
    ///     </para>
    /// </remarks>
    void Shadow(DrawList list, DrawCommand command) {
        if (command.Width <= 0 || command.Height <= 0) {
            return;
        }

        var half = new Vector2(command.Width / 2, command.Height / 2);
        var blur = MathF.Max(command.Thickness, 0f);
        var margin = blur * 2f;

        var style = command.HasStyle && (uint) command.Offset < (uint) list.Boxes.Count
            ? list.Boxes[command.Offset]
            : BoxStyle.Rounded(CornerRadii.Uniform(command.Radius));

        // Thickness zero: a shadow is a fill, and a border's band would hollow it out.
        shapes.Add(new UiShape(half, 0f, style.Corners, style.GradientEnd, style.GradientAxis, blur));

        Quad(
            command.X - margin,
            command.Y - margin,
            command.X + command.Width + margin,
            command.Y + command.Height + margin,
            new Vector2(-half.X - margin, -half.Y - margin),
            new Vector2(half.X + margin, half.Y + margin),
            command.Color,
            new Vector4(shapes.Count - 1, 0, 0, 0)
        );
    }

    /// <summary>One textured quad.</summary>
    /// <remarks>
    ///     ⚠ <b>No shape entry and no distance field.</b> An image is the one thing the interface
    ///     draws that is already a picture: there is nothing to round, no border to inset and no
    ///     coverage to compute, so it is four vertices and the UVs the command asked for. Rounding
    ///     an image's corners would need the box shader's field and the image shader's sample at
    ///     once, which is a fourth pipeline and not a fourth branch.
    /// </remarks>
    void Image(DrawCommand command) {
        if (command.Image == 0) {
            // Nothing registered. Drawing it with whatever texture happens to be bound would put the
            // font atlas in the hole, which reads as a rendering fault rather than as a missing image.
            return;
        }

        Quad(
            command.X,
            command.Y,
            command.X + command.Width,
            command.Y + command.Height,
            new Vector2(command.Source.X, command.Source.Y),
            new Vector2(command.Source.X + command.Source.Width, command.Source.Y + command.Source.Height),
            command.Color,
            Vector4.Zero
        );
    }

    /// <summary>A run of glyphs, one quad each, from the atlas.</summary>
    void Text(DrawList list, DrawCommand command, GlyphFieldCache glyphs) {
        if (command.Font < 0 || command.Font >= list.Fonts.Count) {
            return;
        }

        var font = list.Fonts[command.Font];
        var size = command.FontSize;
        var atlas = glyphs.Atlas;

        for (var i = 0; i < command.Length; i++) {
            var glyph = list.Glyphs[command.Offset + i];

            if (!glyphs.TryGet(font, command.Font, glyph.GlyphId, out var entry)) {
                // A glyph that draws nothing is not a drop; one the atlas refused is.
                if (font.GetOutline(glyph.GlyphId).IsEmpty) {
                    continue;
                }

                DroppedGlyphs++;
                continue;
            }

            // ⚠ <b>A glyph's position is relative to the run, not to the surface.</b> The command
            // carries where the line starts and each glyph carries its offset along it, so that two
            // identical labels in different places hold identical glyph runs — which is what lets
            // the batcher and the frame diff notice they are the same. Reading the offset as
            // absolute puts every label in the top-left corner.
            var penX = command.X + glyph.X;
            var penY = command.Y + glyph.Y;

            // ⚠ The placement is in ems and the pen is in pixels, so the size multiplies one and not
            // the other. And y runs down the surface while a font's runs up, which is why the top
            // edge is a subtraction.
            var left = penX + (entry.Left * size);
            var right = penX + (entry.Right * size);
            var top = penY - (entry.Top * size);
            var bottom = penY - (entry.Bottom * size);

            var u0 = (float)entry.Region.X / atlas.Width;
            var v0 = (float)entry.Region.Y / atlas.Height;
            var u1 = (float)(entry.Region.X + entry.Region.Width) / atlas.Width;
            var v1 = (float)(entry.Region.Y + entry.Region.Height) / atlas.Height;

            Quad(
                left,
                top,
                right,
                bottom,
                new Vector2(u0, v0),
                new Vector2(u1, v1),
                command.Color,
                new Vector4(entry.ScreenPixelRange * size, 0, 0, 0)
            );
        }
    }

    /// <summary>A path, filled or stroked, as loose triangles.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The only kind that is real geometry rather than a quad the shader resolves.</b> A
    ///         box and a glyph are both a distance function evaluated per pixel, so they cost four
    ///         vertices whatever their shape; an arbitrary path has no such function, so it is
    ///         tessellated.
    ///     </para>
    ///     <para>
    ///         ⚠ Which is why it is also the only kind whose edge has to be <i>drawn</i>. The interior
    ///         comes out at full coverage and a strip along the outline carries the ramp from one to
    ///         zero, and the coverage travels in the vertex where the other two kinds put a distance.
    ///     </para>
    /// </remarks>
    void Path(DrawList list, DrawCommand command) {
        points.Clear();
        contours.Clear();
        triangles.Clear();

        PathFlattener.Flatten(list.Segments, command.Offset, command.Length, Tolerance, points, contours);

        if (contours.Count == 0) {
            return;
        }

        if (command.Kind == DrawCommandKind.Path) {
            PathTessellator.Fill(points, contours, command.FillRule, triangles);
            PathTessellator.FillFringe(points, contours, command.FillRule, Fringe, triangles);
        } else {
            PathTessellator.Stroke(
                points,
                contours,
                command.Thickness,
                command.Join,
                command.Cap,
                Tolerance,
                triangles,
                command.MiterLimit > 0 ? command.MiterLimit : PathTessellator.DefaultMiterLimit,
                Fringe
            );
        }

        for (var i = 0; i + 2 < triangles.Count; i += 3) {
            var start = (uint)vertices.Count;

            // ⚠ No vertex is shared, and there is nothing to share. The fill decomposes into
            // trapezoids that meet only along band boundaries, where the two sides have different x;
            // the stroke overlaps rather than seams. An index buffer over this is the identity, and
            // building one would cost a hash per vertex to discover that.
            for (var corner = 0; corner < 3; corner++) {
                var vertex = triangles[i + corner];

                // The coverage rides where the other two kinds put a distance, so the solid shader
                // reads `Shape.x` for the same reason the text shader does. Nothing else is read: a
                // path has no field to sample and no shape to evaluate.
                vertices.Add(
                    new UiVertex(
                        vertex.Position,
                        Vector2.Zero,
                        command.Color,
                        new Vector4(vertex.Coverage, 0, 0, 0)
                    )
                );

                indices.Add(start + (uint)corner);
            }
        }
    }

    /// <summary>Four vertices and six indices, wound the same way every time.</summary>
    void Quad(
        float left,
        float top,
        float right,
        float bottom,
        Vector2 textureMin,
        Vector2 textureMax,
        Color4 color,
        Vector4 shape
    ) {
        var start = (uint)vertices.Count;

        vertices.Add(new UiVertex(new Vector2(left, top), textureMin, color, shape));
        vertices.Add(new UiVertex(new Vector2(right, top), new Vector2(textureMax.X, textureMin.Y), color, shape));
        vertices.Add(new UiVertex(new Vector2(right, bottom), textureMax, color, shape));
        vertices.Add(new UiVertex(new Vector2(left, bottom), new Vector2(textureMin.X, textureMax.Y), color, shape));

        indices.Add(start);
        indices.Add(start + 1);
        indices.Add(start + 2);
        indices.Add(start);
        indices.Add(start + 2);
        indices.Add(start + 3);
    }
}
