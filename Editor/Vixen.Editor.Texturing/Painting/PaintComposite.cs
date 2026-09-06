// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Texturing.Painting;

/// <summary>Which half of the stack a composite slice is.</summary>
enum PaintStackSlice {
    /// <summary>Everything under the painted layer, already composited.</summary>
    Below = 0,

    /// <summary>Everything over it.</summary>
    Above = 1
}

/// <summary>Where a painting composite gets the rest of the stack from.</summary>
/// <remarks>
///     ⚠ <b>The seam that keeps this folder free of the compiler, the plan and the device.</b>
///     Evaluating a slice means compiling the stack minus one layer into a <c>TexturePlan</c> and
///     running it on the editor's <c>IEditorGraphics</c> — which is <c>LayerStackPreview</c>'s job and
///     is a file this slice does not own. What the brush needs is two images and a promise about how
///     often they are asked for, and that is an interface with one method.
/// </remarks>
interface IPaintStack {
    /// <summary>Evaluates one half of the stack.</summary>
    /// <param name="slice">Which half.</param>
    /// <returns>The image, at the stack's base resolution.</returns>
    PaintImage Evaluate(PaintStackSlice slice);
}

/// <summary>
///     The stack, evaluated once at pointer-down, with the painted layer composited between its two
///     halves.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D13's latency answer, and the thing exit criterion 8 is about.</b> "The risk
///         in it is latency rather than correctness: a stamp that costs a full-atlas evaluation of
///         the stack above it will feel broken at 4K." So the stack is evaluated <em>once per stroke
///         start</em> into a composite below and above the painted layer, and every stamp of the
///         drag composites into that.
///     </para>
///     <para>
///         ⚠ <b>Once is asserted rather than intended.</b> <see cref="Evaluations" /> counts the
///         calls this made, and <c>PaintCompositeTests</c> drives a two-hundred-stamp stroke and
///         asserts the number is two — one slice each way. A composite that re-evaluated per stamp
///         would still produce the right picture, so the picture cannot be the test.
///     </para>
///     <para>
///         ⚠ <b><see cref="Resolve(PaintRect)" /> recomposites a rectangle, never the atlas.</b>
///         That is the other half of the same claim: the cached halves make a stamp independent of
///         the <em>number</em> of layers, and the rectangle makes it independent of their
///         <em>resolution</em>. Either one alone leaves a stamp whose cost grows with something the
///         artist did not touch.
///     </para>
///     <para>
///         ⚠ <b>And a caller must hand over the rectangles rather than their union, which is a
///         third thing and was missing</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/871">#871</a>. A union of rectangles is
///         a rectangle, so two mirrored paths on opposite sides of the atlas produced a bounding box
///         spanning it and symmetry — the feature the plural was built for — was the worst case
///         rather than an edge case. <see cref="Resolve(IReadOnlyList{PaintRect})" /> is the shape
///         that keeps the claim true.
///     </para>
///     <para>
///         ⚠ <b>Nothing here composites at construction, and that was 1.9 s before the first stamp</b>
///         — <a href="https://github.com/Rikarin/Vixen/issues/853">#853</a>. See the constructor: the
///         whole-atlas pass it used to make produced a picture that its only possible reader — the
///         view — already had.
///     </para>
///     <para>
///         ⚠ <b>Source-over, and not the sixteen operators a compiled stack has.</b> A live paint
///         composite that reimplemented <c>Colour/Blend</c> in C# would be a second opinion about the
///         arithmetic — the shape five exact-equality roll calls in this workstream have gone red on,
///         and worse here because the two would be compared by eye rather than by a test. The
///         authoritative composite is the plan; this is what the artist watches while the pointer is
///         down, and the bake is what they get. Making the two agree means evaluating the slices
///         through the plan, which is #849.
///     </para>
/// </remarks>
sealed class PaintComposite {
    readonly PaintImage layer;

    /// <summary>Evaluates both halves of the stack, now, and never again.</summary>
    /// <param name="stack">Where the halves come from.</param>
    /// <param name="layer">The painted layer's own pixels, read live.</param>
    /// <param name="shown">
    ///     What the view is already displaying, copied into <see cref="Result" /> so that the
    ///     untouched parts of the atlas are a picture rather than transparency — or
    ///     <see langword="null" /> when the caller only ever reads the rectangles it dirtied.
    /// </param>
    /// <exception cref="ArgumentException">A slice, or the seed, is not the painted layer's size.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It no longer composites the whole atlas here, and that was 1.9 seconds before the
    ///         first stamp</b> — <a href="https://github.com/Rikarin/Vixen/issues/853">#853</a>.
    ///         Measured in Debug at 4096² with twelve layers: 1878 ms of stroke start against 2.9 ms
    ///         per stamp, so the number that failed exit criterion 8's spirit was the one before the
    ///         stamps rather than any of them.
    ///     </para>
    ///     <para>
    ///         <b>The reason it could go is that nothing needed it.</b> A full-atlas source-over per
    ///         texel in managed code existed so that <see cref="Result" /> was a complete picture
    ///         from the first frame of the drag — and the only caller that could want one is a view,
    ///         which is already displaying that picture and re-uploads the rectangles a move
    ///         dirtied. So the whole-atlas pass produced a copy of something the caller had.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="Result" /> is therefore valid where <see cref="Resolve(PaintRect)" />
    ///         has been asked, plus whatever <paramref name="shown" /> seeded — and that is a
    ///         narrower promise than before.</b> <see cref="ResolveAll" /> restores the old
    ///         behaviour for a caller that genuinely needs the whole picture and has no seed;
    ///         seeding from the view is the cheaper answer and an <c>Array.Copy</c>, at the price of
    ///         making the seam with whatever produced that picture load-bearing, which is #849's
    ///         territory.
    ///     </para>
    /// </remarks>
    public PaintComposite(IPaintStack stack, PaintImage layer, PaintImage? shown = null) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(layer);

        this.layer = layer;

        Below = Evaluated(stack, layer, PaintStackSlice.Below);
        Above = Evaluated(stack, layer, PaintStackSlice.Above);
        Result = new(layer.Width, layer.Height);

        if (shown is not null) {
            Seed(shown);
        }
    }

    /// <summary>Everything under the painted layer.</summary>
    public PaintImage Below { get; }

    /// <summary>Everything over it.</summary>
    public PaintImage Above { get; }

    /// <summary>What the artist is looking at, where the drag has touched it.</summary>
    /// <remarks>
    ///     ⚠ <b>Valid where <see cref="Resolve(PaintRect)" /> has run, plus whatever
    ///     <see cref="Seed" /> copied in — and not before</b> (#853). It is not a whole picture on
    ///     its own; <see cref="ResolveAll" /> makes it one, at the cost the constructor used to pay
    ///     unconditionally.
    /// </remarks>
    public PaintImage Result { get; }

    /// <summary>How many slice evaluations this composite has asked for, over its whole life.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted where the call is made rather than assigned the number the constructor
    ///     intends to reach.</b> It was a literal <c>2</c>, which made the test asserting it two an
    ///     assertion that could not fail — a third evaluation added anywhere below would have left
    ///     it reading two.
    /// </remarks>
    public int Evaluations { get; private set; }

    /// <summary>How many texels have been recomposited, over the composite's whole life.</summary>
    /// <remarks>
    ///     The counter exit criterion 8 is gated on, together with
    ///     <see cref="PaintStroke.WeightsEvaluated" />: a stamp's work is its footprint and the
    ///     rectangle it dirtied, and nothing about the atlas or the stack appears in either number.
    /// </remarks>
    public long TexelsResolved { get; private set; }

    /// <summary>Composites the whole atlas, once.</summary>
    /// <returns>Everything, which is what was rewritten.</returns>
    /// <remarks>
    ///     ⚠ <b>What the constructor used to do, kept as a method a caller has to ask for.</b> It is
    ///     O(atlas) source-over per texel in managed code — 1.9 seconds at 4096² in Debug — so the
    ///     one thing that must not happen is for it to be on a path a pointer-down takes. #853.
    /// </remarks>
    public PaintRect ResolveAll() => Resolve(layer.Bounds);

    /// <summary>Copies a picture the caller already has into the result.</summary>
    /// <param name="shown">The picture. Must be the painted layer's size.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shown" /> is null.</exception>
    /// <exception cref="ArgumentException">It is not the painted layer's size.</exception>
    /// <remarks>
    ///     ⚠ <b>An <c>Array.Copy</c> where <see cref="ResolveAll" /> is a per-texel blend, and the
    ///     difference is two orders of magnitude.</b> The catch is that it makes the seam
    ///     load-bearing: what a view is displaying came out of the <em>plan</em>, and this composite
    ///     is straight source-over in C#, so a seeded region and a resolved region can disagree
    ///     wherever the two arithmetics do. That disagreement is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/849">#849</a> and it exists with or
    ///     without the seed — the seed only makes it visible as a discontinuity at the edge of what
    ///     the drag has touched instead of as a difference between the drag and the bake.
    /// </remarks>
    public void Seed(PaintImage shown) {
        ArgumentNullException.ThrowIfNull(shown);

        if (shown.Width != layer.Width || shown.Height != layer.Height) {
            throw new ArgumentException(
                $"The seed picture is {shown.Width}×{shown.Height} and the painted layer is "
                + $"{layer.Width}×{layer.Height}. A seed at another resolution would have to be resampled, "
                + "and a resample is exactly the whole-atlas pass the seed exists to avoid.",
                nameof(shown)
            );
        }

        Array.Copy(shown.Texels, Result.Texels, Result.Texels.Length);
    }

    /// <summary>Recomposites several rectangles of the result, each on its own.</summary>
    /// <param name="regions">What changed. Each is clipped to the image; an empty list does nothing.</param>
    /// <returns>The union of them, which is what a view re-uploads.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A union of rectangles is a rectangle, and that is the whole of
    ///         <a href="https://github.com/Rikarin/Vixen/issues/871">#871</a>.</b> Two mirrored paths
    ///         on opposite sides of the atlas have a bounding box spanning the atlas, so a
    ///         <c>Resolve</c> given their union recomposites nearly all of it — and symmetry is the
    ///         feature <see cref="PaintSession.MoveAll" /> grew a plural for. The same is true one
    ///         level down of a pointer that jumped between frames.
    ///     </para>
    ///     <para>
    ///         <b>Overlapping regions are recomposited twice and that is correct rather than
    ///         merely tolerable.</b> <see cref="Resolve(PaintRect)" /> writes each texel from
    ///         <see cref="Below" />, the layer and <see cref="Above" /> — never from what
    ///         <see cref="Result" /> already held — so it is idempotent, and the second pass over an
    ///         intersection produces the same bytes. What it costs is counted honestly in
    ///         <see cref="TexelsResolved" />; subtracting the overlap would need a rectangle
    ///         decomposition whose own cost is not obviously smaller than the texels it saves.
    ///     </para>
    /// </remarks>
    public PaintRect Resolve(IReadOnlyList<PaintRect> regions) {
        ArgumentNullException.ThrowIfNull(regions);

        var union = PaintRect.Empty;

        for (var index = 0; index < regions.Count; index++) {
            union = union.Union(Resolve(regions[index]));
        }

        return union;
    }

    /// <summary>Recomposites one rectangle of the result.</summary>
    /// <param name="rect">What changed. Clipped to the image.</param>
    /// <returns>What was actually rewritten.</returns>
    public PaintRect Resolve(PaintRect rect) {
        var clipped = rect.Clip(layer.Width, layer.Height);

        if (clipped.IsEmpty) {
            return clipped;
        }

        for (var y = clipped.Y; y < clipped.EndY; y++) {
            for (var x = clipped.X; x < clipped.EndX; x++) {
                var index = (y * layer.Width) + x;

                Result[index] = Over(Over(Below[index], layer[index]), Above[index]);
                TexelsResolved++;
            }
        }

        return clipped;
    }

    /// <summary>Straight-alpha source-over.</summary>
    /// <param name="under">The backdrop.</param>
    /// <param name="over">What goes on top.</param>
    /// <returns>The composite.</returns>
    public static uint Over(uint under, uint over) {
        var alpha = PaintImage.Channel(over, 3);

        if (alpha <= 0f) {
            return under;
        }

        var backdrop = PaintImage.Channel(under, 3);
        var result = alpha + (backdrop * (1f - alpha));

        if (result <= 0f) {
            return 0u;
        }

        return PaintImage.Pack(
            Mix(under, over, 0, alpha, backdrop, result),
            Mix(under, over, 1, alpha, backdrop, result),
            Mix(under, over, 2, alpha, backdrop, result),
            result
        );
    }

    static float Mix(uint under, uint over, int channel, float alpha, float backdrop, float result) =>
        ((PaintImage.Channel(over, channel) * alpha)
            + (PaintImage.Channel(under, channel) * backdrop * (1f - alpha)))
        / result;

    /// <summary>Asks the stack for one slice, and counts having asked.</summary>
    /// <param name="stack">Whom to ask.</param>
    /// <param name="layer">The painted layer, whose size the slice must match.</param>
    /// <param name="which">Which half.</param>
    /// <returns>The slice.</returns>
    PaintImage Evaluated(IPaintStack stack, PaintImage layer, PaintStackSlice which) {
        var slice = Sized(stack.Evaluate(which), layer, which);

        Evaluations++;

        return slice;
    }

    static PaintImage Sized(PaintImage slice, PaintImage layer, PaintStackSlice which) {
        ArgumentNullException.ThrowIfNull(slice);

        if (slice.Width != layer.Width || slice.Height != layer.Height) {
            throw new ArgumentException(
                $"The {which} slice is {slice.Width}×{slice.Height} and the painted layer is "
                + $"{layer.Width}×{layer.Height}. A slice at another resolution would have to be "
                + "resampled once per stamp, which is the cost this whole cache exists to remove.",
                nameof(slice)
            );
        }

        return slice;
    }
}
