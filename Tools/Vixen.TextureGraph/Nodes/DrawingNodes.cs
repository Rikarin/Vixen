// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>Where each line of a <c>Source/Text</c> sits against its anchor.</summary>
enum TextureTextAlign {
    /// <summary>The line starts at the anchor.</summary>
    Left,

    /// <summary>The line's middle is at the anchor.</summary>
    Centre,

    /// <summary>The line ends at the anchor.</summary>
    Right
}

/// <summary>How a <c>Source/Vector</c> drawing's own frame is laid onto the image.</summary>
enum TextureVectorFit {
    /// <summary>All of it shows, at its own proportions, centred. What a browser does by default.</summary>
    Contain,

    /// <summary>It fills the image at its own proportions, and whatever overhangs is cut off.</summary>
    Cover,

    /// <summary>Its frame is stretched onto the image, each axis on its own.</summary>
    Stretch
}

/// <summary>What a <c>Source/Vector</c> writes.</summary>
enum TextureVectorDraw {
    /// <summary>The drawing's own fills and strokes, with its coverage as alpha.</summary>
    Colour,

    /// <summary>White where it covers, as a mask, whatever its colours are.</summary>
    Coverage
}

/// <summary>A string set in a font — Patina #139.</summary>
/// <remarks>
///     <para>
///         <b>A picture a host draws, like <c>Source/Bitmap</c>, and for doc 48 § 4.1's reason.</b> A compilation must
///         not open a font, so the node writes what it was told into the reference of an external image
///         (<see cref="TextureDrawing" />), and <c>Vixen.TextureGraph.Drawing</c> shapes and fills it at the bake's own
///         size when a host fills the plan. Every length is a fraction of the image, so a 1K graph baked at 4K sets
///         the same words at four times the texels rather than the same texels in a quarter of the picture.
///     </para>
///     <para>
///         ⚠ <b>Lines break where the text has a line break and nowhere else.</b> A wrap width would be layout's
///         decision about where a paragraph may break, and an author who wants two lines writes two.
///     </para>
/// </remarks>
[Node("Source/Text", Preview = true, Summary = "A line or lines of text in a font, as a mask: labels, logos, stamps, serial numbers.")]
sealed partial class TextNode : TextureNode {
    /// <summary>What to write. A line break starts a new line.</summary>
    [Setting(Summary = "What to write. A line break starts a new line.")]
    public string Text = "Text";

    /// <summary>The font file, as a host finds it. Empty is the bundled Open Sans.</summary>
    [Setting(Summary = "A TrueType or OpenType font file. Empty uses the bundled Open Sans.")]
    public string Font = "";

    /// <summary>Where each line sits against the anchor.</summary>
    [Setting(AcceptedFrom = typeof(TextureTextAlign), Summary = "Whether each line starts, centres or ends at the anchor.")]
    public string Alignment = "Centre";

    /// <summary>The font size, as a fraction of the image's height.</summary>
    [Input(Minimum = 0.001f, SoftMinimum = 0.01f, SoftMaximum = 1f, Summary = "The font size, as a fraction of the image's height.")]
    public Scalar Size = 0.2f;

    /// <summary>Where the anchor is across the image, from 0 at the left to 1 at the right.</summary>
    [Input(Name = "Position X", SoftMinimum = 0f, SoftMaximum = 1f, Summary = "Where the anchor is across the image, from 0 at the left to 1 at the right.")]
    public Scalar PositionX = 0.5f;

    /// <summary>Where the middle of the block of lines is, from 0 at the top to 1 at the bottom.</summary>
    [Input(Name = "Position Y", SoftMinimum = 0f, SoftMaximum = 1f, Summary = "Where the middle of the lines is, from 0 at the top to 1 at the bottom.")]
    public Scalar PositionY = 0.5f;

    /// <summary>Extra space after every letter, as a fraction of the font size. Negative tightens.</summary>
    [Input(Name = "Letter Spacing", SoftMinimum = -0.2f, SoftMaximum = 1f, Summary = "Extra space after every letter, as a fraction of the font size. Negative tightens.")]
    public Scalar LetterSpacing = 0f;

    /// <summary>The distance between lines, as a multiple of the font's own line height.</summary>
    [Input(Name = "Line Spacing", Minimum = 0f, SoftMaximum = 3f, Summary = "The distance between lines, as a multiple of the font's own line height.")]
    public Scalar LineSpacing = 1f;

    /// <summary>White where the letters are.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var alignment = TextureSettings.Enum(emitter, nameof(Alignment), TextureTextAlign.Centre);

        var reference = TextureDrawing.Reference(
            TextureDrawing.TextScheme,
            ("text", emitter.Text(nameof(Text))),
            ("font", emitter.Text(nameof(Font)).Trim()),
            ("align", alignment.ToString()),
            ("size", TextureDrawing.Number(emitter.Number(nameof(Size)))),
            ("x", TextureDrawing.Number(emitter.Number("Position X"))),
            ("y", TextureDrawing.Number(emitter.Number("Position Y"))),
            ("tracking", TextureDrawing.Number(emitter.Number("Letter Spacing"))),
            ("leading", TextureDrawing.Number(emitter.Number("Line Spacing")))
        );

        // R8, which is what a coverage field is uploaded as and what a mask is read as.
        var source = emitter.External(TextureFormat.R8, TextureChannels.Grey, reference);
        var target = emitter.Write("Out", TextureChannels.Grey);

        emitter.Dispatch(TextureSources.Bitmap(target, source, srgb: false));
    }
}

/// <summary>An SVG drawing — Patina #139.</summary>
/// <remarks>
///     <para>
///         <b><c>Source/Text</c>'s arrangement, for a file of paths.</b> The node writes the file's name and how to lay
///         it out into an external image's reference; <c>Vixen.TextureGraph.Drawing</c> reads the SVG and fills its
///         shapes at the bake's own size, so a logo is as sharp at 4K as at 1K.
///     </para>
///     <para>
///         ⚠ <b>What it draws is a stated subset of SVG, and the rest is refused by name rather than skipped.</b> Paths
///         and the basic shapes, groups, transforms, fills, strokes, opacity and fill rules: what an icon or a logo
///         exported from a drawing tool is made of. Gradients, text, images, filters and masks are not, and a file
///         using one says which, because a drawing quietly missing a part is a wrong picture nobody can explain.
///     </para>
/// </remarks>
[Node("Source/Vector", Preview = true, Summary = "An SVG file drawn at the graph's size: logos, decals, icons and stencils, as colour or as a mask.")]
sealed partial class VectorNode : TextureNode {
    /// <summary>The SVG file, as a host finds it.</summary>
    [Setting(Summary = "The SVG file to draw.")]
    public string File = "";

    /// <summary>How the drawing's frame is laid onto the image.</summary>
    [Setting(AcceptedFrom = typeof(TextureVectorFit), Summary = "Contain shows all of it, Cover fills the image, Stretch fills it in each axis.")]
    public string Fit = "Contain";

    /// <summary>Whether it writes its own colours or a mask.</summary>
    [Setting(AcceptedFrom = typeof(TextureVectorDraw), Summary = "Colour draws the file's own colours; Coverage draws a white mask of its shapes.")]
    public string Draw = "Colour";

    /// <summary>How large it is, against the size the fit gives it.</summary>
    [Input(Minimum = 0f, SoftMaximum = 4f, Summary = "How large it is, against the size the fit gives it.")]
    public Scalar Scale = 1f;

    /// <summary>Where its middle is across the image.</summary>
    [Input(Name = "Position X", SoftMinimum = 0f, SoftMaximum = 1f, Summary = "Where its middle is across the image, from 0 at the left to 1 at the right.")]
    public Scalar PositionX = 0.5f;

    /// <summary>Where its middle is down the image.</summary>
    [Input(Name = "Position Y", SoftMinimum = 0f, SoftMaximum = 1f, Summary = "Where its middle is down the image, from 0 at the top to 1 at the bottom.")]
    public Scalar PositionY = 0.5f;

    /// <summary>The drawing.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var fit = TextureSettings.Enum(emitter, nameof(Fit), TextureVectorFit.Contain);
        var draw = TextureSettings.Enum(emitter, nameof(Draw), TextureVectorDraw.Colour);
        var file = emitter.Text(nameof(File)).Trim();

        if (file.Length == 0) {
            // Refused rather than filled with nothing, for `Source/Bitmap`'s reason: a compiler can name the node.
            emitter.Report(
                TextureDiagnostics.NoImage,
                "This node has no drawing. Its shapes come from an SVG file, so a file is what fills it.",
                nameof(File)
            );

            return;
        }

        var reference = TextureDrawing.Reference(
            TextureDrawing.VectorScheme,
            ("file", file),
            ("fit", fit.ToString()),
            ("draw", draw.ToString()),
            ("scale", TextureDrawing.Number(emitter.Number(nameof(Scale)))),
            ("x", TextureDrawing.Number(emitter.Number("Position X"))),
            ("y", TextureDrawing.Number(emitter.Number("Position Y")))
        );

        if (draw == TextureVectorDraw.Coverage) {
            var mask = emitter.External(TextureFormat.R8, TextureChannels.Grey, reference);

            emitter.Dispatch(TextureSources.Bitmap(emitter.Write("Out", TextureChannels.Grey), mask, srgb: false));

            return;
        }

        // Eight-bit sRGB with straight alpha, which is what an SVG's colours are written in; the decode is
        // `Bitmap.rvn`'s, so every image inside the graph is linear as every other source's is.
        var source = emitter.External(TextureFormat.Rgba8, TextureChannels.Colour, reference);

        emitter.Dispatch(TextureSources.Bitmap(emitter.Write("Out", TextureChannels.Colour), source, srgb: true));
    }
}
