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
    readonly List<ushort> indices = [];
    readonly List<UiDraw> draws = [];
    readonly List<Rectangle> clips = [];

    /// <summary>How many glyphs were dropped because the atlas could not hold them.</summary>
    /// <remarks>
    ///     ⚠ Counted rather than thrown on. A glyph too large for the atlas is a configuration
    ///     mistake and not a reason for a frame to fail; what it must not be is silent, because the
    ///     symptom is a word with a hole in it.
    /// </remarks>
    public int DroppedGlyphs { get; private set; }

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
                        Box(command);
                        break;

                    case DrawCommandKind.Text:
                        Text(list, command, glyphs);
                        break;

                    default:
                        // Paths are owed: filling one needs a tessellator, and stroking one needs
                        // that plus a join and cap model. Skipped rather than approximated, and
                        // skipped visibly — a batch with no indices produces no draw.
                        break;
                }
            }

            if (indices.Count > first) {
                draws.Add(new UiDraw(batch.Kind, first, indices.Count - first, batch.Font, clip));
            }
        }

        return new UiGeometry(vertices, indices, draws);
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
    void Box(DrawCommand command) {
        if (command.Width <= 0 || command.Height <= 0) {
            return;
        }

        var half = new Vector2(command.Width / 2, command.Height / 2);
        var shape = new Vector4(half.X, half.Y, command.Radius, command.Thickness);

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
            shape
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
        // ⚠ Nothing is emitted past what a ushort index can reach. A frame with more than sixteen
        // thousand quads in it is a real possibility for a dense editor, and the failure of running
        // over is silent and looks like geometry from the top of the frame appearing in the middle
        // of it. Widening the index is the fix and it is owed; refusing is what is honest until then.
        if (vertices.Count + 4 > ushort.MaxValue) {
            return;
        }

        var start = (ushort)vertices.Count;

        vertices.Add(new UiVertex(new Vector2(left, top), textureMin, color, shape));
        vertices.Add(new UiVertex(new Vector2(right, top), new Vector2(textureMax.X, textureMin.Y), color, shape));
        vertices.Add(new UiVertex(new Vector2(right, bottom), textureMax, color, shape));
        vertices.Add(new UiVertex(new Vector2(left, bottom), new Vector2(textureMin.X, textureMax.Y), color, shape));

        indices.Add(start);
        indices.Add((ushort)(start + 1));
        indices.Add((ushort)(start + 2));
        indices.Add(start);
        indices.Add((ushort)(start + 2));
        indices.Add((ushort)(start + 3));
    }
}
