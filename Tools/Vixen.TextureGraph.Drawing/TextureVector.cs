// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Vixen.Ui;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Outlines;
using Vixen.Ui.Text.Rasterizing;
using MathVector = Vixen.Core.Mathematics.Vector2;
using NumericsVector = System.Numerics.Vector2;

namespace Vixen.TextureGraph.Drawing;

/// <summary>How an SVG's frame is laid onto the picture.</summary>
internal enum TextureVectorFit {
    /// <summary>All of it shows, at its own proportions, centred.</summary>
    Contain,

    /// <summary>It fills the picture at its own proportions, and what overhangs is cut.</summary>
    Cover,

    /// <summary>Its frame is stretched onto the picture, each axis on its own.</summary>
    Stretch
}

/// <summary>Where an SVG lands on the picture.</summary>
/// <param name="Fit">How its frame is laid on.</param>
/// <param name="Scale">A size against what the fit gives it.</param>
/// <param name="CentreX">Where the frame's middle goes, as a fraction of the picture's width.</param>
/// <param name="CentreY">The same, of its height.</param>
internal readonly record struct TextureVectorPlacement(
    TextureVectorFit Fit = TextureVectorFit.Contain,
    float Scale = 1f,
    float CentreX = 0.5f,
    float CentreY = 0.5f
);

/// <summary>One painted shape: its outline in the SVG's user space, and how it is filled and stroked.</summary>
internal sealed record TextureVectorShape(
    IReadOnlyList<PathSegment> Segments,
    (float R, float G, float B, float A)? Fill,
    FillRule FillRule,
    (float R, float G, float B, float A)? Stroke,
    float StrokeWidth,
    LineJoin Join,
    LineCap Cap,
    float MiterLimit
);

/// <summary>An SVG read into shapes, and the frame they are drawn in.</summary>
/// <param name="FrameX">The frame's left edge in user units — <c>viewBox</c>'s first number.</param>
/// <param name="FrameY">Its top edge.</param>
/// <param name="FrameWidth">Its width.</param>
/// <param name="FrameHeight">Its height.</param>
/// <param name="Shapes">What is painted, in painting order, transforms already applied.</param>
internal sealed record TextureVectorDrawing(
    float FrameX,
    float FrameY,
    float FrameWidth,
    float FrameHeight,
    IReadOnlyList<TextureVectorShape> Shapes
);

/// <summary>Doc 48 § 4.1's <c>Svg Path</c>, grown to a file: an SVG read and filled — Patina #139.</summary>
/// <remarks>
///     <para>
///         <b>What it reads is a stated subset, and everything outside it is refused by name.</b> Paths and the basic
///         shapes (<c>rect</c>, <c>circle</c>, <c>ellipse</c>, <c>line</c>, <c>polyline</c>, <c>polygon</c>), groups,
///         <c>use</c>, nested <c>svg</c>, transforms, solid fills and strokes with their opacity, joins, caps and fill
///         rule, <c>opacity</c>, <c>display</c> and <c>visibility</c>, presentation attributes, inline <c>style</c> and
///         a <c>style</c> sheet's class rules. That is what an icon or a logo exported from a drawing tool is made of.
///         Gradients, patterns, text, images, filters and masks are not, and a file that paints with one is an
///         <see cref="InvalidDataException" /> naming it: a drawing quietly missing a part is a wrong picture nobody
///         can explain.
///     </para>
///     <para>
///         ⚠ <b>Two approximations, stated.</b> A group's <c>opacity</c> multiplies into each shape inside it, so two
///         overlapping shapes in a half-transparent group show their overlap where a browser would not. And
///         <c>clip-path</c> is not applied: exporters wrap a whole drawing in a clip to its own frame, which the
///         picture's edge does anyway, and refusing it would refuse most files a drawing tool writes.
///     </para>
///     <para>
///         ⚠ <b>Colours are composited as the file writes them</b>, in sRGB and premultiplied, which is what a
///         browser does; the node decodes the result to linear on the way in, as every bitmap is.
///     </para>
///     <para>
///         <b>No new rasteriser.</b> <c>SvgPath</c> reads path data, <c>PathTessellator.Stroke</c> turns a stroke into
///         triangles, and <c>GlyphRasterizer</c> — the font's scanline fill, which takes an even-odd rule for exactly
///         this — fills both.
///     </para>
/// </remarks>
internal static partial class TextureVector {
    const float Kappa = 0.5522847498f;
    const string Svg = "http://www.w3.org/2000/svg";

    static readonly Dictionary<string, (float R, float G, float B)> Named = new(StringComparer.OrdinalIgnoreCase) {
        ["black"] = (0f, 0f, 0f),
        ["white"] = (1f, 1f, 1f),
        ["red"] = (1f, 0f, 0f),
        ["lime"] = (0f, 1f, 0f),
        ["green"] = (0f, 128 / 255f, 0f),
        ["blue"] = (0f, 0f, 1f),
        ["yellow"] = (1f, 1f, 0f),
        ["cyan"] = (0f, 1f, 1f),
        ["aqua"] = (0f, 1f, 1f),
        ["magenta"] = (1f, 0f, 1f),
        ["fuchsia"] = (1f, 0f, 1f),
        ["gray"] = (128 / 255f, 128 / 255f, 128 / 255f),
        ["grey"] = (128 / 255f, 128 / 255f, 128 / 255f),
        ["silver"] = (192 / 255f, 192 / 255f, 192 / 255f),
        ["maroon"] = (128 / 255f, 0f, 0f),
        ["olive"] = (128 / 255f, 128 / 255f, 0f),
        ["purple"] = (128 / 255f, 0f, 128 / 255f),
        ["teal"] = (0f, 128 / 255f, 128 / 255f),
        ["navy"] = (0f, 0f, 128 / 255f),
        ["orange"] = (1f, 165 / 255f, 0f),
        ["brown"] = (165 / 255f, 42 / 255f, 42 / 255f),
        ["pink"] = (1f, 192 / 255f, 203 / 255f),
        ["gold"] = (1f, 215 / 255f, 0f)
    };

    /// <summary>What makes a selector more than one class name, so a rule under it is not a class rule.</summary>
    static readonly SearchValues<char> Compound = SearchValues.Create(" >+~:[#.");

    /// <summary>Elements that paint and are not drawn here, so a file using one is refused.</summary>
    static readonly HashSet<string> Refused = ["text", "image", "foreignObject", "pattern", "switch"];

    /// <summary>Elements that hold definitions and paint nothing where they stand.</summary>
    static readonly HashSet<string> Definitions = [
        "defs", "symbol", "clipPath", "mask", "marker", "linearGradient", "radialGradient", "filter", "style",
        "title", "desc", "metadata"
    ];

    /// <summary>Reads an SVG document.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>Its shapes and frame.</returns>
    /// <exception cref="InvalidDataException">It is not an SVG, or it paints with something not drawn here.</exception>
    public static TextureVectorDrawing Read(string text) {
        ArgumentNullException.ThrowIfNull(text);

        XDocument document;

        try {
            // ⚠ No DTD processing and no resolver: a file is data, and an entity is how one reads another file.
            using var reader = XmlReader.Create(
                new StringReader(text),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }
            );

            document = XDocument.Load(reader);
        } catch (XmlException failure) {
            throw new InvalidDataException($"it is not well-formed XML: {failure.Message}", failure);
        }

        var root = document.Root;

        if (root is null || root.Name.LocalName != "svg") {
            throw new InvalidDataException("its root element is not <svg>");
        }

        var classes = ClassRules(document);
        var ids = document.Descendants()
            .Where(static element => element.Attribute("id") is not null)
            .GroupBy(static element => element.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        var (frameX, frameY, frameWidth, frameHeight) = Frame(root);
        Reader reading = new(classes, ids, frameWidth, frameHeight);

        reading.Children(root, Style.Initial, Affine.Identity, 0);

        return new(frameX, frameY, frameWidth, frameHeight, reading.Shapes);
    }

    /// <summary>Fills a drawing into a premultiplied RGBA picture, or into coverage.</summary>
    /// <param name="drawing">What to draw.</param>
    /// <param name="canvas">The picture's size and wrap.</param>
    /// <param name="placement">Where the drawing's frame lands.</param>
    /// <param name="coverageOnly">True for a mask: every paint is white and opaque but for its coverage.</param>
    /// <returns>
    ///     Four floats per texel, premultiplied, in the file's own sRGB encoding — or one per texel, the coverage,
    ///     when <paramref name="coverageOnly" /> is set.
    /// </returns>
    public static float[] Rasterize(
        TextureVectorDrawing drawing,
        TextureCanvas canvas,
        TextureVectorPlacement placement,
        bool coverageOnly
    ) {
        ArgumentNullException.ThrowIfNull(drawing);

        var picture = new float[canvas.Width * canvas.Height * (coverageOnly ? 1 : 4)];
        var place = Placement(drawing, canvas, placement);
        var unit = MathF.Sqrt(MathF.Abs((place.A * place.D) - (place.B * place.C)));

        foreach (var shape in drawing.Shapes) {
            var segments = shape.Segments.Select(place.Apply).ToArray();

            if (shape.Fill is { } fill) {
                Paint(picture, canvas, Outline(segments), shape.FillRule, fill, coverageOnly);
            }

            if (shape.Stroke is { } stroke && shape.StrokeWidth * unit > 0f) {
                Paint(
                    picture,
                    canvas,
                    Stroked(segments, shape.StrokeWidth * unit, shape.Join, shape.Cap, shape.MiterLimit),
                    FillRule.NonZero,
                    stroke,
                    coverageOnly
                );
            }
        }

        return picture;
    }

    /// <summary>Lays a premultiplied picture down as straight eight-bit RGBA, the bytes an upload takes.</summary>
    /// <param name="picture">Four floats per texel, premultiplied.</param>
    /// <returns>Four bytes per texel, straight.</returns>
    public static byte[] Straight(float[] picture) {
        ArgumentNullException.ThrowIfNull(picture);

        var bytes = new byte[picture.Length];

        for (var at = 0; at < picture.Length; at += 4) {
            var alpha = picture[at + 3];
            var inverse = alpha > 0f ? 1f / alpha : 0f;

            bytes[at] = Byte(picture[at] * inverse);
            bytes[at + 1] = Byte(picture[at + 1] * inverse);
            bytes[at + 2] = Byte(picture[at + 2] * inverse);
            bytes[at + 3] = Byte(alpha);
        }

        return bytes;
    }

    static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    /// <summary>The user-space-to-texel map: the frame's middle on the centre, sized by the fit and the scale.</summary>
    static Affine Placement(TextureVectorDrawing drawing, TextureCanvas canvas, TextureVectorPlacement placement) {
        var across = canvas.Width / drawing.FrameWidth;
        var down = canvas.Height / drawing.FrameHeight;

        var (sx, sy) = placement.Fit switch {
            TextureVectorFit.Cover => (Math.Max(across, down), Math.Max(across, down)),
            TextureVectorFit.Stretch => (across, down),
            _ => (Math.Min(across, down), Math.Min(across, down))
        };

        sx *= placement.Scale;
        sy *= placement.Scale;

        var middleX = drawing.FrameX + (drawing.FrameWidth * 0.5f);
        var middleY = drawing.FrameY + (drawing.FrameHeight * 0.5f);

        return new(sx, 0f, 0f, sy, (placement.CentreX * canvas.Width) - (middleX * sx), (placement.CentreY * canvas.Height) - (middleY * sy));
    }

    /// <summary>Fills one outline, given in texels with y down, onto the picture.</summary>
    static void Paint(
        float[] picture,
        TextureCanvas canvas,
        List<OutlineSegment> outline,
        FillRule rule,
        (float R, float G, float B, float A) paint,
        bool coverageOnly
    ) {
        if (outline.Count == 0 || paint.A <= 0f) {
            return;
        }

        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;

        foreach (var segment in outline) {
            foreach (var (x, y) in Points(segment)) {
                left = Math.Min(left, x);
                right = Math.Max(right, x);

                // The outline is held with y negated, so its top is the smallest of the negated values.
                top = Math.Min(top, -y);
                bottom = Math.Max(bottom, -y);
            }
        }

        if (canvas.Box(left, top, right, bottom) is not { } box) {
            return;
        }

        var filled = GlyphRasterizer.Rasterize(
            GlyphOutline.FromSegments(outline),
            box.Width,
            box.Height,
            1f,
            new NumericsVector(box.Left, -(box.Top + box.Height)),
            rule
        );

        if (coverageOnly) {
            var weighed = paint.A >= 1f ? filled.Coverage : [..filled.Coverage.Select(value => value * paint.A)];

            canvas.Union(picture, box, weighed);
        } else {
            canvas.Over(picture, box, filled.Coverage, paint.R, paint.G, paint.B, paint.A);
        }
    }

    static IEnumerable<(float X, float Y)> Points(OutlineSegment segment) {
        yield return (segment.X0, segment.Y0);

        if (segment.Verb is OutlineVerb.Quadratic or OutlineVerb.Cubic) {
            yield return (segment.X1, segment.Y1);
        }

        if (segment.Verb == OutlineVerb.Cubic) {
            yield return (segment.X2, segment.Y2);
        }
    }

    /// <summary>
    ///     A path in texels as a glyph outline, with y negated: an outline's y grows upwards, and the rasteriser's
    ///     origin is the bottom-left corner of the box.
    /// </summary>
    static List<OutlineSegment> Outline(IReadOnlyList<PathSegment> segments) {
        List<OutlineSegment> outline = new(segments.Count);

        foreach (var segment in segments) {
            outline.Add(
                segment.Verb switch {
                    PathVerb.Move => new(OutlineVerb.Move, segment.P2.X, -segment.P2.Y),
                    PathVerb.Line => new(OutlineVerb.Line, segment.P2.X, -segment.P2.Y),
                    PathVerb.Quadratic => new(OutlineVerb.Quadratic, segment.P0.X, -segment.P0.Y, segment.P2.X, -segment.P2.Y),
                    PathVerb.Cubic => new(
                        OutlineVerb.Cubic,
                        segment.P0.X,
                        -segment.P0.Y,
                        segment.P1.X,
                        -segment.P1.Y,
                        segment.P2.X,
                        -segment.P2.Y
                    ),
                    _ => new(OutlineVerb.Close, segment.P2.X, -segment.P2.Y)
                }
            );
        }

        return outline;
    }

    /// <summary>
    ///     A stroke as an outline: the tessellator's triangles, each turned the same way so that non-zero winding fills
    ///     exactly their union.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Turned the same way is what makes this a union by construction, and it is defensive rather than
    ///     measured.</b> The tessellator winds the two halves of a segment opposite ways, and two triangles of opposite
    ///     winding would cancel under non-zero wherever they overlapped. Measured, they only ever meet along an edge:
    ///     <c>A_zigzag_stroke_covers_every_one_of_its_pieces</c> stays green with the turn removed, at all three joins.
    ///     Wound alike, the winding is the count of triangles over a texel whatever the tessellator does next, which is
    ///     non-zero exactly where the stroke is. The fringe the tessellator can add for antialiasing is asked for at
    ///     nought: the scanline fill antialiases on its own.
    /// </remarks>
    static List<OutlineSegment> Stroked(PathSegment[] segments, float width, LineJoin join, LineCap cap, float miterLimit) {
        List<MathVector> points = [];
        List<Contour> contours = [];
        List<PathVertex> triangles = [];

        PathFlattener.Flatten(segments, 0, segments.Length, 0.1f, points, contours);
        PathTessellator.Stroke(points, contours, width, join, cap, 0.1f, triangles, miterLimit);

        List<OutlineSegment> outline = new(triangles.Count / 3 * 4);

        for (var at = 0; at + 2 < triangles.Count; at += 3) {
            var a = triangles[at].Position;
            var b = triangles[at + 1].Position;
            var c = triangles[at + 2].Position;
            var turn = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

            if (turn < 0f) {
                (b, c) = (c, b);
            }

            outline.Add(new(OutlineVerb.Move, a.X, -a.Y));
            outline.Add(new(OutlineVerb.Line, b.X, -b.Y));
            outline.Add(new(OutlineVerb.Line, c.X, -c.Y));
            outline.Add(new(OutlineVerb.Close, a.X, -a.Y));
        }

        return outline;
    }

    /// <summary>The frame: <c>viewBox</c>, or <c>width</c> and <c>height</c> from the origin.</summary>
    static (float X, float Y, float Width, float Height) Frame(XElement root) {
        if (root.Attribute("viewBox")?.Value is { } box) {
            var numbers = Numbers(box);

            if (numbers.Count == 4 && numbers[2] > 0f && numbers[3] > 0f) {
                return (numbers[0], numbers[1], numbers[2], numbers[3]);
            }

            throw new InvalidDataException($"its viewBox '{box}' is not four numbers with a positive width and height");
        }

        var width = Length(root.Attribute("width")?.Value, 0f);
        var height = Length(root.Attribute("height")?.Value, 0f);

        if (width > 0f && height > 0f) {
            return (0f, 0f, width, height);
        }

        throw new InvalidDataException("it has neither a viewBox nor a width and height, so it has no frame to fit");
    }

    /// <summary>The class rules of every <c>style</c> sheet: <c>.name { property: value }</c>, and nothing else.</summary>
    static Dictionary<string, Dictionary<string, string>> ClassRules(XDocument document) {
        Dictionary<string, Dictionary<string, string>> rules = new(StringComparer.Ordinal);

        foreach (var sheet in document.Descendants().Where(static element => element.Name.LocalName == "style")) {
            var css = CssComment().Replace(sheet.Value, " ");

            foreach (Match rule in CssRule().Matches(css)) {
                var declarations = Declarations(rule.Groups[2].Value);

                foreach (var selector in rule.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries)) {
                    if (selector.Length > 1 && selector[0] == '.' && !selector.AsSpan(1).ContainsAny(Compound)) {
                        var into = rules.TryGetValue(selector[1..], out var known) ? known : rules[selector[1..]] = new(StringComparer.Ordinal);

                        foreach (var (name, value) in declarations) {
                            into[name] = value;
                        }
                    }
                }
            }
        }

        return rules;
    }

    static Dictionary<string, string> Declarations(string text) {
        Dictionary<string, string> declarations = new(StringComparer.Ordinal);

        foreach (var declaration in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var colon = declaration.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0) {
                declarations[declaration[..colon].Trim()] = declaration[(colon + 1)..].Replace("!important", "", StringComparison.Ordinal).Trim();
            }
        }

        return declarations;
    }

    static List<float> Numbers(string text) {
        List<float> numbers = [];

        foreach (Match match in Number().Matches(text)) {
            numbers.Add(float.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        return numbers;
    }

    /// <summary>A length in user units: a number with an absolute unit, or a percentage of <paramref name="whole" />.</summary>
    static float Length(string? text, float whole) {
        if (string.IsNullOrWhiteSpace(text)) {
            return 0f;
        }

        var match = Number().Match(text);

        if (!match.Success) {
            throw new InvalidDataException($"'{text}' is not a length");
        }

        var value = float.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        var unit = text[(match.Index + match.Length)..].Trim();

        return unit switch {
            "" or "px" => value,
            "%" => value * whole / 100f,
            "pt" => value * 96f / 72f,
            "pc" => value * 16f,
            "mm" => value * 96f / 25.4f,
            "cm" => value * 96f / 2.54f,
            "in" => value * 96f,
            _ => throw new InvalidDataException($"'{text}' is a length in '{unit}', which a drawing here does not measure")
        };
    }

    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}")]
    private static partial Regex CssRule();

    [GeneratedRegex(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)")]
    private static partial Regex TransformCall();

    /// <summary>A 2D affine map, <c>(a c e; b d f)</c> as SVG writes it.</summary>
    readonly record struct Affine(float A, float B, float C, float D, float E, float F) {
        public static Affine Identity { get; } = new(1f, 0f, 0f, 1f, 0f, 0f);

        /// <summary>This map after <paramref name="inner" />: a point goes through the inner map first.</summary>
        public Affine Then(Affine inner) =>
            new(
                (A * inner.A) + (C * inner.B),
                (B * inner.A) + (D * inner.B),
                (A * inner.C) + (C * inner.D),
                (B * inner.C) + (D * inner.D),
                (A * inner.E) + (C * inner.F) + E,
                (B * inner.E) + (D * inner.F) + F
            );

        public MathVector Map(MathVector point) =>
            new((A * point.X) + (C * point.Y) + E, (B * point.X) + (D * point.Y) + F);

        public PathSegment Apply(PathSegment segment) => segment with { P0 = Map(segment.P0), P1 = Map(segment.P1), P2 = Map(segment.P2) };
    }

    /// <summary>The inherited properties at one element.</summary>
    sealed record Style(
        string Fill,
        float FillOpacity,
        FillRule FillRule,
        string Stroke,
        float StrokeOpacity,
        float StrokeWidth,
        LineJoin Join,
        LineCap Cap,
        float MiterLimit,
        float Opacity,
        string Colour,
        bool Visible
    ) {
        public static Style Initial { get; } =
            new("black", 1f, FillRule.NonZero, "none", 1f, 1f, LineJoin.Miter, LineCap.Butt, 4f, 1f, "black", true);
    }

    /// <summary>One walk over the document.</summary>
    sealed class Reader(
        Dictionary<string, Dictionary<string, string>> classes,
        Dictionary<string, XElement> ids,
        float frameWidth,
        float frameHeight
    ) {
        /// <summary>How deep <c>use</c> may nest before a file is taken to be referring to itself.</summary>
        const int Deepest = 32;

        public List<TextureVectorShape> Shapes { get; } = [];

        public void Children(XElement parent, Style style, Affine transform, int depth) {
            foreach (var child in parent.Elements()) {
                Element(child, style, transform, depth);
            }
        }

        void Element(XElement element, Style inherited, Affine transform, int depth) {
            if (element.Name.NamespaceName.Length > 0 && element.Name.NamespaceName != Svg) {
                return;
            }

            var name = element.Name.LocalName;

            if (Definitions.Contains(name)) {
                return;
            }

            if (Refused.Contains(name)) {
                throw new InvalidDataException(
                    $"it paints a <{name}>, which a drawing here does not draw — paths, shapes, groups and solid fills "
                    + "and strokes are what it reads. Convert it to paths in the drawing tool."
                );
            }

            var properties = Properties(element);

            if (properties.ContainsKey("filter") || properties.ContainsKey("mask")) {
                throw new InvalidDataException(
                    $"its <{name}> is drawn through a {(properties.ContainsKey("filter") ? "filter" : "mask")}, which a "
                    + "drawing here does not apply"
                );
            }

            var style = Cascade(inherited, properties);

            if (properties.TryGetValue("display", out var display) && display == "none") {
                return;
            }

            var local = transform.Then(Transform(element.Attribute("transform")?.Value));

            switch (name) {
                case "svg" when depth > 0 || element.Parent is not null:
                    Children(element, style, local.Then(new(1f, 0f, 0f, 1f, Number(element, "x"), Number(element, "y"))), depth);

                    return;
                case "svg":
                case "g":
                case "a":
                    Children(element, style, local, depth);

                    return;
                case "use":
                    Use(element, style, local, depth);

                    return;
            }

            var path = Geometry(element, name);

            if (path is null || path.Count == 0 || !style.Visible) {
                return;
            }

            var fill = name is "line" ? null : Paint(style.Fill, style.FillOpacity * style.Opacity, style);
            var stroke = Paint(style.Stroke, style.StrokeOpacity * style.Opacity, style);

            if (fill is null && stroke is null) {
                return;
            }

            Shapes.Add(
                new(
                    [..path.Segments.Select(local.Apply)],
                    fill,
                    style.FillRule,
                    stroke,
                    style.StrokeWidth * MathF.Sqrt(MathF.Abs((local.A * local.D) - (local.B * local.C))),
                    style.Join,
                    style.Cap,
                    style.MiterLimit
                )
            );
        }

        void Use(XElement element, Style style, Affine transform, int depth) {
            var href = (element.Attribute("href") ?? element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")))?.Value;

            if (href is null || !href.StartsWith('#')) {
                throw new InvalidDataException("it has a <use> that does not refer to an element of the same file");
            }

            if (depth >= Deepest || !ids.TryGetValue(href[1..], out var target)) {
                throw new InvalidDataException(
                    depth >= Deepest
                        ? $"its <use> of '{href}' refers to itself through {Deepest} others"
                        : $"its <use> refers to '{href}', which is not in the file"
                );
            }

            var placed = transform.Then(new(1f, 0f, 0f, 1f, Number(element, "x"), Number(element, "y")));

            if (target.Name.LocalName == "symbol") {
                Children(target, style, placed, depth + 1);
            } else {
                Element(target, style, placed, depth + 1);
            }
        }

        float Number(XElement element, string attribute) => Length(element.Attribute(attribute)?.Value, attribute is "x" or "width" or "cx" or "rx" or "x1" or "x2" ? frameWidth : frameHeight);

        PathBuilder? Geometry(XElement element, string name) {
            switch (name) {
                case "path": {
                    var data = element.Attribute("d")?.Value;

                    try {
                        return string.IsNullOrWhiteSpace(data) ? null : SvgPath.Parse(data);
                    } catch (SvgPathException failure) {
                        throw new InvalidDataException(
                            $"a <path>'s data cannot be read at character {failure.Position}: {failure.Message}",
                            failure
                        );
                    }
                }
                case "rect": {
                    var x = Number(element, "x");
                    var y = Number(element, "y");
                    var w = Number(element, "width");
                    var h = Number(element, "height");

                    if (w <= 0f || h <= 0f) {
                        return null;
                    }

                    var rx = element.Attribute("rx") is null ? -1f : Number(element, "rx");
                    var ry = element.Attribute("ry") is null ? -1f : Number(element, "ry");

                    rx = rx < 0f ? Math.Max(ry, 0f) : rx;
                    ry = ry < 0f ? rx : ry;

                    return Rectangle(x, y, w, h, Math.Min(rx, w / 2f), Math.Min(ry, h / 2f));
                }
                case "circle": {
                    var r = Length(element.Attribute("r")?.Value, MathF.Sqrt(((frameWidth * frameWidth) + (frameHeight * frameHeight)) / 2f));

                    return r > 0f ? Ellipse(Number(element, "cx"), Number(element, "cy"), r, r) : null;
                }
                case "ellipse": {
                    var rx = Number(element, "rx");
                    var ry = Number(element, "ry");

                    return rx > 0f && ry > 0f ? Ellipse(Number(element, "cx"), Number(element, "cy"), rx, ry) : null;
                }
                case "line":
                    return new PathBuilder()
                        .MoveTo(new(Number(element, "x1"), Number(element, "y1")))
                        .LineTo(new(Number(element, "x2"), Number(element, "y2")));
                case "polyline":
                case "polygon": {
                    var numbers = Numbers(element.Attribute("points")?.Value ?? string.Empty);

                    if (numbers.Count < 4) {
                        return null;
                    }

                    var path = new PathBuilder().MoveTo(new(numbers[0], numbers[1]));

                    for (var at = 2; at + 1 < numbers.Count; at += 2) {
                        path.LineTo(new(numbers[at], numbers[at + 1]));
                    }

                    return name == "polygon" ? path.Close() : path;
                }
                default:
                    // An element this does not know, such as an editor's own annotation, paints nothing.
                    return null;
            }
        }

        Dictionary<string, string> Properties(XElement element) {
            Dictionary<string, string> properties = new(StringComparer.Ordinal);

            foreach (var attribute in element.Attributes()) {
                if (attribute.Name.NamespaceName.Length == 0 && attribute.Name.LocalName is not ("style" or "class" or "transform" or "d" or "id")) {
                    properties[attribute.Name.LocalName] = attribute.Value.Trim();
                }
            }

            foreach (var name in (element.Attribute("class")?.Value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                if (classes.TryGetValue(name, out var rule)) {
                    foreach (var (key, value) in rule) {
                        properties[key] = value;
                    }
                }
            }

            if (element.Attribute("style")?.Value is { } inline) {
                foreach (var (key, value) in Declarations(inline)) {
                    properties[key] = value;
                }
            }

            return properties;
        }

        static Style Cascade(Style inherited, Dictionary<string, string> properties) {
            var style = inherited with { Opacity = inherited.Opacity };

            foreach (var (key, value) in properties) {
                if (value == "inherit") {
                    continue;
                }

                style = key switch {
                    "fill" => style with { Fill = value },
                    "fill-opacity" => style with { FillOpacity = Fraction(value) },
                    "fill-rule" => style with { FillRule = value == "evenodd" ? FillRule.EvenOdd : FillRule.NonZero },
                    "stroke" => style with { Stroke = value },
                    "stroke-opacity" => style with { StrokeOpacity = Fraction(value) },
                    "stroke-width" => style with { StrokeWidth = Length(value, 0f) },
                    "stroke-linejoin" => style with {
                        Join = value switch { "round" => LineJoin.Round, "bevel" => LineJoin.Bevel, _ => LineJoin.Miter }
                    },
                    "stroke-linecap" => style with {
                        Cap = value switch { "round" => LineCap.Round, "square" => LineCap.Square, _ => LineCap.Butt }
                    },
                    "stroke-miterlimit" => style with { MiterLimit = Math.Max(Fraction(value, 4f), 1f) },
                    "opacity" => style with { Opacity = style.Opacity * Fraction(value) },
                    "color" => style with { Colour = value },
                    "visibility" => style with { Visible = value is "visible" },
                    _ => style
                };
            }

            return style;
        }

        static float Fraction(string value, float fallback = 1f) {
            var match = TextureVector.Number().Match(value);

            if (!match.Success) {
                return fallback;
            }

            var number = float.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture);

            return Math.Clamp(value.TrimEnd().EndsWith('%') ? number / 100f : number, 0f, fallback > 1f ? float.MaxValue : 1f);
        }

        static (float R, float G, float B, float A)? Paint(string paint, float opacity, Style style) {
            var text = paint.Trim();

            if (text is "none" or "transparent" || opacity <= 0f) {
                return null;
            }

            if (text.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException(
                    $"it paints with '{text}', a gradient or a pattern, which a drawing here does not draw — only "
                    + "solid colours"
                );
            }

            if (text == "currentColor") {
                text = style.Colour;
            }

            var (r, g, b, a) = Colour(text);

            return (r, g, b, a * opacity);
        }

        static (float R, float G, float B, float A) Colour(string text) {
            if (text.StartsWith('#')) {
                var hex = text[1..];

                if (hex.Length is 3 or 4) {
                    hex = string.Concat(hex.Select(static digit => $"{digit}{digit}"));
                }

                if ((hex.Length is 6 or 8) && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed)) {
                    if (hex.Length == 6) {
                        packed = (packed << 8) | 0xFF;
                    }

                    return ((packed >> 24) / 255f, ((packed >> 16) & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, (packed & 0xFF) / 255f);
                }
            } else if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) {
                var parts = text[(text.IndexOf('(', StringComparison.Ordinal) + 1)..].TrimEnd(')').Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 3) {
                    float Channel(string part) =>
                        part.EndsWith('%') ? Fraction(part) : Math.Clamp(float.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture) / 255f, 0f, 1f);

                    return (Channel(parts[0]), Channel(parts[1]), Channel(parts[2]), parts.Length > 3 ? Fraction(parts[3]) : 1f);
                }
            } else if (Named.TryGetValue(text, out var named)) {
                return (named.R, named.G, named.B, 1f);
            }

            throw new InvalidDataException($"it paints with '{text}', which is not a colour a drawing here reads");
        }

        static Affine Transform(string? text) {
            var transform = Affine.Identity;

            if (string.IsNullOrWhiteSpace(text)) {
                return transform;
            }

            foreach (Match call in TransformCall().Matches(text)) {
                var n = Numbers(call.Groups[2].Value);

                Affine step = (call.Groups[1].Value, n.Count) switch {
                    ("matrix", 6) => new(n[0], n[1], n[2], n[3], n[4], n[5]),
                    ("translate", 1) => new(1f, 0f, 0f, 1f, n[0], 0f),
                    ("translate", 2) => new(1f, 0f, 0f, 1f, n[0], n[1]),
                    ("scale", 1) => new(n[0], 0f, 0f, n[0], 0f, 0f),
                    ("scale", 2) => new(n[0], 0f, 0f, n[1], 0f, 0f),
                    ("rotate", 1) => Rotation(n[0], 0f, 0f),
                    ("rotate", 3) => Rotation(n[0], n[1], n[2]),
                    ("skewX", 1) => new(1f, 0f, MathF.Tan(n[0] * MathF.PI / 180f), 1f, 0f, 0f),
                    ("skewY", 1) => new(1f, MathF.Tan(n[0] * MathF.PI / 180f), 0f, 1f, 0f, 0f),
                    _ => throw new InvalidDataException($"its transform '{call.Value}' takes a count of numbers it cannot")
                };

                transform = transform.Then(step);
            }

            return transform;
        }

        static Affine Rotation(float degrees, float cx, float cy) {
            var angle = degrees * MathF.PI / 180f;
            var c = MathF.Cos(angle);
            var s = MathF.Sin(angle);

            return new Affine(1f, 0f, 0f, 1f, cx, cy).Then(new(c, s, -s, c, 0f, 0f)).Then(new(1f, 0f, 0f, 1f, -cx, -cy));
        }

        static PathBuilder Ellipse(float cx, float cy, float rx, float ry) {
            var kx = rx * Kappa;
            var ky = ry * Kappa;

            return new PathBuilder()
                .MoveTo(new(cx + rx, cy))
                .CubicTo(new(cx + rx, cy + ky), new(cx + kx, cy + ry), new(cx, cy + ry))
                .CubicTo(new(cx - kx, cy + ry), new(cx - rx, cy + ky), new(cx - rx, cy))
                .CubicTo(new(cx - rx, cy - ky), new(cx - kx, cy - ry), new(cx, cy - ry))
                .CubicTo(new(cx + kx, cy - ry), new(cx + rx, cy - ky), new(cx + rx, cy))
                .Close();
        }

        static PathBuilder Rectangle(float x, float y, float w, float h, float rx, float ry) {
            if (rx <= 0f || ry <= 0f) {
                return new PathBuilder()
                    .MoveTo(new(x, y))
                    .LineTo(new(x + w, y))
                    .LineTo(new(x + w, y + h))
                    .LineTo(new(x, y + h))
                    .Close();
            }

            var kx = rx * Kappa;
            var ky = ry * Kappa;

            return new PathBuilder()
                .MoveTo(new(x + rx, y))
                .LineTo(new(x + w - rx, y))
                .CubicTo(new(x + w - rx + kx, y), new(x + w, y + ry - ky), new(x + w, y + ry))
                .LineTo(new(x + w, y + h - ry))
                .CubicTo(new(x + w, y + h - ry + ky), new(x + w - rx + kx, y + h), new(x + w - rx, y + h))
                .LineTo(new(x + rx, y + h))
                .CubicTo(new(x + rx - kx, y + h), new(x, y + h - ry + ky), new(x, y + h - ry))
                .LineTo(new(x, y + ry))
                .CubicTo(new(x, y + ry - ky), new(x + rx - kx, y), new(x + rx, y))
                .Close();
        }
    }
}
