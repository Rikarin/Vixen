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
///         ⚠ <b><see cref="Resolve" /> recomposites a rectangle, never the atlas.</b> That is the
///         other half of the same claim: the cached halves make a stamp independent of the
///         <em>number</em> of layers, and the rectangle makes it independent of their
///         <em>resolution</em>. Either one alone leaves a stamp whose cost grows with something the
///         artist did not touch.
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
    /// <exception cref="ArgumentException">A slice is not the painted layer's size.</exception>
    public PaintComposite(IPaintStack stack, PaintImage layer) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(layer);

        this.layer = layer;

        Below = Sized(stack.Evaluate(PaintStackSlice.Below), layer, PaintStackSlice.Below);
        Above = Sized(stack.Evaluate(PaintStackSlice.Above), layer, PaintStackSlice.Above);
        Evaluations = 2;
        Result = new(layer.Width, layer.Height);

        Resolve(layer.Bounds);
    }

    /// <summary>Everything under the painted layer.</summary>
    public PaintImage Below { get; }

    /// <summary>Everything over it.</summary>
    public PaintImage Above { get; }

    /// <summary>What the artist is looking at.</summary>
    public PaintImage Result { get; }

    /// <summary>How many slice evaluations this composite has asked for, over its whole life.</summary>
    public int Evaluations { get; }

    /// <summary>How many texels have been recomposited, over the composite's whole life.</summary>
    /// <remarks>
    ///     The counter exit criterion 8 is gated on, together with
    ///     <see cref="PaintStroke.WeightsEvaluated" />: a stamp's work is its footprint and the
    ///     rectangle it dirtied, and nothing about the atlas or the stack appears in either number.
    /// </remarks>
    public long TexelsResolved { get; private set; }

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
