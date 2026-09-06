// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>Two images somebody else evaluated, as the stack a composite caches.</summary>
/// <remarks>
///     <para>
///         <b>The trivial <see cref="IPaintStack" />, and it is what makes the seam a seam.</b>
///         <see cref="PaintComposite" /> asks for two pictures once per stroke; where they come from
///         is the caller's problem, and this is the shape they arrive in whichever answer wins.
///     </para>
///     <para>
///         ⚠ <b><see cref="Empty" /> is honest and it is not free of consequence.</b> With both
///         halves transparent, <c>PaintComposite.Result</c> is exactly the painted layer — so the
///         pane shows the layer alone rather than the stack, which is a smaller promise than
///         "what the artist watches is what the bake produces" and is stated where a reader of the
///         pane will find it. Making it the stack's real halves is
///         <a href="https://github.com/Rikarin/Vixen/issues/849">#849</a>, and what that needs is
///         named there: a seam that evaluates an arbitrary sliced <c>TextureSetAsset</c> and reads
///         the texels back. ⚠ The read-back itself is <em>not</em> the missing piece —
///         <c>TextureBake.Read</c> already returns a bitmap and <c>LayerStackPreview</c> already
///         calls it.
///     </para>
/// </remarks>
sealed class PaintStackImages : IPaintStack {
    readonly PaintImage below;
    readonly PaintImage above;

    /// <summary>Holds two evaluated halves.</summary>
    /// <param name="below">Everything under the painted layer.</param>
    /// <param name="above">Everything over it.</param>
    /// <exception cref="ArgumentNullException">Either is null.</exception>
    public PaintStackImages(PaintImage below, PaintImage above) {
        ArgumentNullException.ThrowIfNull(below);
        ArgumentNullException.ThrowIfNull(above);

        this.below = below;
        this.above = above;
    }

    /// <summary>A stack of nothing at a resolution: the painted layer over and under transparency.</summary>
    /// <param name="width">The atlas width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The stack.</returns>
    /// <remarks>
    ///     ⚠ <b>Transparent both ways, and the upper half being transparent is the half that
    ///     matters.</b> <c>PaintStackSlices</c> found the same thing one level up: a channel's
    ///     authored default has alpha 1, so an upper half built from the defaults composites as
    ///     fully opaque and <c>Over(anything, Above)</c> is <c>Above</c> — a stroke that could never
    ///     be visible, which fifty-two tests did not see.
    /// </remarks>
    public static PaintStackImages Empty(int width, int height) => new(new(width, height), new(width, height));

    /// <inheritdoc />
    public PaintImage Evaluate(PaintStackSlice slice) => slice == PaintStackSlice.Below ? below : above;
}

/// <summary>
///     One paint layer of an open stack, its <c>.vxpaint</c>, and the target a session paints into.
/// </summary>
/// <remarks>
///     <para>
///         <b>What the surface needed and no file held: the join between a row in a stack and the
///         pixels behind it.</b> <c>LayerStackPreview</c> reads a <c>.vxpaint</c> on the way to the
///         plan and never writes one; <c>PaintCanvas</c> reads and writes and knows nothing about a
///         stack. This is the piece in between, and it is what makes a stroke reach a file.
///     </para>
///     <para>
///         ⚠ <b>The canvas is still written at pointer-up, and the reason it had to be is gone</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/948">#948</a>. This paragraph used to say
///         the write was forced: the painted layer reaches the plan as a <em>file path</em>
///         (<c>PaintReference</c>), which <c>LayerStackPreview</c> opened off the disk on every
///         evaluation, so a stroke held in memory was a stroke the map could not show. It no longer
///         is — <see cref="PaintCanvasStore" /> is the canvas the session and both panes share, and
///         the map redraws mid-drag from texels no file holds. The save survives because it is what
///         makes a stroke outlast the session, which is a different obligation and one this method
///         still owes.
///     </para>
///     <para>
///         ⚠ <b>A canvas whose size disagrees with the stack's is refused rather than resampled.</b>
///         An artist who changed the set's resolution after painting has pixels at the old one; a
///         silent resample would be a stroke landing somewhere other than where the pointer was, and
///         a silent crop would delete art. Neither is a thing to do without being asked.
///     </para>
/// </remarks>
sealed class PaintSurface {
    readonly PaintCanvasStore canvases;

    PaintSurface(
        TextureSetAsset set,
        LayerAsset layer,
        PaintCanvas canvas,
        string file,
        string relative,
        PaintCanvasStore canvases
    ) {
        Set = set;
        Layer = layer;
        Canvas = canvas;
        Absolute = file;
        Relative = relative;

        this.canvases = canvases;
    }

    /// <summary>The texture set the layer is in.</summary>
    public TextureSetAsset Set { get; }

    /// <summary>The paint layer.</summary>
    public LayerAsset Layer { get; }

    /// <summary>Its pixels, one image per channel.</summary>
    public PaintCanvas Canvas { get; }

    /// <summary>Where the canvas is, absolute.</summary>
    public string Absolute { get; }

    /// <summary>What <see cref="LayerAsset.Paint" /> should name, relative to the stack.</summary>
    public string Relative { get; }

    /// <summary>Whether the layer does not yet name the canvas this resolved to.</summary>
    /// <remarks>
    ///     ⚠ <b>A new paint layer names no file, and the name is derived rather than stored — but
    ///     only as a default.</b> <c>LayerPaint.NameFor</c>'s own remarks say why: a stack that
    ///     renames its set would orphan every painted layer if the name were recomputed on every
    ///     read. So the first stroke is what writes the name down, and it is a separate edit because
    ///     it changes the <c>.vxlayers</c> and the stroke does not.
    /// </remarks>
    public bool NeedsNaming => Layer.Paint.Trim().Length == 0;

    /// <summary>The first paint layer of a stack's first set, or nothing with a reason.</summary>
    /// <param name="document">The open stack.</param>
    /// <param name="layerId">Which layer, or empty for the first paint layer there is.</param>
    /// <param name="canvases">The session's open canvases, which this consults before the disk.</param>
    /// <param name="refusal">Why there is none, or empty.</param>
    /// <returns>The surface, or <see langword="null" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> or the store is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every refusal is a returned sentence and none is an exception</b>, for
    ///         <c>LayerStackPreview.Evaluate</c>'s reason: this runs from a pointer-down, and a throw
    ///         out of one takes the editor's frame with it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The canvas comes from the store rather than from the disk, and the store is a
    ///         required argument rather than an option</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/948">#948</a>. This is called twice
    ///         per stroke — at pointer-down for the target and at pointer-up to put the layer's own
    ///         pixels back in the pane — and each call used to read the whole file, which at 4K is
    ///         67 MB a channel each way. A defaulted store would be the shape this workstream keeps
    ///         producing: a mechanism whose every production caller passes the default.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the resolved canvas is pinned.</b> A drag holds this object for as long as
    ///         the pointer is down; a store that evicted it under budget pressure would hand the next
    ///         open a second canvas for the same layer, and the stroke and the pane would be looking
    ///         at different ones.
    ///     </para>
    /// </remarks>
    public static PaintSurface? Open(
        LayerStackDocument document,
        string layerId,
        PaintCanvasStore canvases,
        out string refusal
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(layerId);
        ArgumentNullException.ThrowIfNull(canvases);

        refusal = "";

        var stack = document.Document;

        if (stack.Sets.Count == 0) {
            refusal = "This stack has no texture set, so there is no layer to paint into.";

            return null;
        }

        var set = stack.Sets[0];

        if (Find(set, layerId) is not { } layer) {
            refusal = layerId.Length > 0
                ? $"The set '{set.Name}' has no paint layer with the id '{layerId}'."
                : $"The set '{set.Name}' has no paint layer. Add one, and the brush has somewhere to go.";

            return null;
        }

        var folder = Path.GetDirectoryName(document.AssetPath);

        if (string.IsNullOrEmpty(folder)) {
            refusal = "This stack has no folder, so there is nothing to resolve its paint files against.";

            return null;
        }

        var relative = layer.Paint.Trim();

        if (relative.Length == 0) {
            relative = LayerPaint.NameFor(
                Path.GetFileNameWithoutExtension(document.AssetPath),
                set.Name,
                layer.Id
            );
        }

        var file = Path.GetFullPath(Path.Combine(folder, relative));

        PaintCanvas? canvas;

        try {
            canvas = canvases.Open(file);
        } catch (Exception failure) when (failure is IOException
            or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) {
            refusal = $"'{relative}' would not read: {failure.Message}";

            return null;
        }

        if (canvas is null) {
            // No such file and nothing open for it: a paint layer whose first stroke has not
            // happened. It goes into the store now, so the second `Open` of this same drag —
            // `RefreshPaint` at pointer-up — finds the canvas the stroke went into rather than
            // making a second empty one.
            canvas = new PaintCanvas(stack.BaseWidth, stack.BaseHeight);

            canvases.Adopt(file, canvas);
        } else if (canvas.Width != stack.BaseWidth || canvas.Height != stack.BaseHeight) {
            refusal = $"'{relative}' holds {canvas.Width}×{canvas.Height} pixels and this stack is now "
                + $"{stack.BaseWidth}×{stack.BaseHeight}. Painting into it would put the stroke somewhere "
                + "other than under the pointer, so it is refused rather than resampled.";

            return null;
        }

        canvases.Pin(file);

        return new(set, layer, canvas, file, relative, canvases);
    }

    /// <summary>What a session paints into, for one channel of this layer.</summary>
    /// <param name="usage">Which channel — <c>baseColor</c>, <c>roughness</c>.</param>
    /// <param name="coverage">
    ///     Which texels a UV island covers, or <see langword="null" /> for
    ///     <see cref="PaintCoverage.Everywhere" />.
    /// </param>
    /// <param name="stack">
    ///     Where the two cached halves come from, or <see langword="null" /> for
    ///     <see cref="PaintStackImages.Empty" />.
    /// </param>
    /// <param name="gutter">How far a stamp is dilated past an island's edge, in texels.</param>
    /// <returns>The target.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The coverage and the halves are parameters because the surface cannot compute
    ///         either, and for a batch the caller replaced one of them afterwards</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/942">#942</a>. A coverage map comes
    ///         from a mesh, and resolving one reads a model file whose answer is cached across
    ///         strokes, so it is the module's; the halves come from compiling the stack minus this
    ///         layer and evaluating it on a device, which is <c>LayerStackPreview</c>'s. This method
    ///         holds a canvas and a layer and knows neither. What it must not do is <em>look</em>
    ///         like it decided them: the previous shape returned <c>Everywhere</c> unconditionally
    ///         while <c>TexturingModule.BeginStroke</c> rewrote the record on the way out, so the
    ///         remarks here described a behaviour no caller had.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both defaults are honest answers rather than stand-ins, and that is why they are
    ///         defaults rather than required arguments.</b> A 2D view over a stack that names no mesh
    ///         has no islands, so every texel is paintable and the dilation finds nothing to do —
    ///         which is what <c>PaintCoverage.Everywhere</c>'s own remarks say it is for. And two
    ///         transparent halves make the composite of the layer <em>be</em> the layer, which is a
    ///         smaller promise than doc 48 § D13's and is stated where the pane's reader is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The seed is the layer's own pixels, and it is only correct while the halves are
    ///         empty.</b> <see cref="PaintComposite.Seed" /> exists so the untouched atlas is a
    ///         picture rather than transparency; with <see cref="PaintStackImages.Empty" /> the
    ///         composite of the layer between two transparent halves <em>is</em> the layer, so the
    ///         seeded region and a resolved one agree exactly. A caller that passes a real
    ///         <paramref name="stack" /> has to seed from the picture the pane is showing instead, or
    ///         the edge of what the drag has touched is a visible discontinuity — which is why
    ///         <c>Shown</c> is set from the composite's own <c>Below</c>/<c>Above</c> join there
    ///         rather than from this image.
    ///     </para>
    /// </remarks>
    public PaintTarget Target(
        string usage,
        PaintCoverage? coverage = null,
        IPaintStack? stack = null,
        int gutter = 4
    ) {
        var image = Canvas.Channel(usage);

        return new(
            image,
            coverage ?? PaintCoverage.Everywhere(Canvas.Width, Canvas.Height),
            stack ?? PaintStackImages.Empty(Canvas.Width, Canvas.Height),
            gutter,
            image
        );
    }

    /// <summary>Writes the canvas beside the stack.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Through a temporary file and a move, because a half-written <c>.vxpaint</c> is
    ///         indistinguishable from a truncated one.</b> <c>PaintCanvas.Read</c> refuses a channel
    ///         shorter than its header says — correctly — so a crash during the write of a 67 MB
    ///         channel would leave a stack that refuses to open its own paint layer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the store is told, or the save invalidates the thing it saved.</b> The write
    ///         moves the file's <c>LastWriteTimeUtc</c> and <c>Length</c>, which is exactly what
    ///         <c>PaintCanvasStore</c> keys its staleness on — so an open canvas that was <em>the
    ///         source of those bytes</em> would fail its own stamp and be read back off the disk on
    ///         the next evaluation. That is <a href="https://github.com/Rikarin/Vixen/issues/948">#948</a>'s
    ///         third read reintroduced by the fix for its first two.
    ///     </para>
    /// </remarks>
    public void Save() {
        var folder = Path.GetDirectoryName(Absolute);

        if (!string.IsNullOrEmpty(folder)) {
            Directory.CreateDirectory(folder);
        }

        var temporary = Absolute + ".tmp";

        using (var stream = File.Create(temporary)) {
            Canvas.Write(stream);
        }

        File.Move(temporary, Absolute, overwrite: true);

        canvases.Saved(Absolute);
    }

    /// <summary>The layer with its canvas named, for the edit that writes the name down.</summary>
    /// <returns>The replacement.</returns>
    public LayerAsset Named() => Layer with { Paint = Relative };

    static LayerAsset? Find(TextureSetAsset set, string layerId) {
        foreach (var layer in Flatten(set.Layers)) {
            if (layer.Kind != LayerKind.Paint) {
                continue;
            }

            if (layerId.Length == 0 || string.Equals(layer.Id, layerId, StringComparison.Ordinal)) {
                return layer;
            }
        }

        return null;
    }

    /// <summary>Every layer of a stack, groups walked into.</summary>
    /// <remarks>
    ///     ⚠ <b>Groups are walked into, because that is where artists put layers.</b> A search that
    ///     looked only at the set's own list would tell an artist who organised their stack that it
    ///     has no paint layer in it.
    /// </remarks>
    static IEnumerable<LayerAsset> Flatten(List<LayerAsset> layers) {
        foreach (var layer in layers) {
            yield return layer;

            foreach (var child in Flatten(layer.Children)) {
                yield return child;
            }
        }
    }
}
