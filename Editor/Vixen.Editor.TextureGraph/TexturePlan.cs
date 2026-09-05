// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;

namespace Vixen.Editor.TextureGraph;

/// <summary>One entry in a plan's image table.</summary>
/// <param name="Format">What it stores.</param>
/// <param name="LevelOffset">
///     How its size relates to the plan's base resolution: <c>0</c> is the base, <c>1</c> is half,
///     <c>-1</c> is double. Doc 48 § D8 — <b>every node is relative and only a bitmap is absolute</b>.
/// </param>
/// <param name="External">
///     Whether the caller supplies it. An external image is not allocated, not pooled and never
///     written; it is what a bitmap input is, and it is the only place an absolute size enters a
///     plan.
/// </param>
/// <remarks>
///     ⚠ <b>A mip offset rather than a fraction, and the difference is the whole of D8.</b> A radius
///     stored as a fraction of the image has the mirror-image bug at a non-square resolution; a size
///     stored absolutely makes a graph authored at 1K a different material at 4K. One base written in
///     the plan, with everything else a power of two away from it, is the only form in which both
///     questions have one answer.
/// </remarks>
public readonly record struct TextureImage(TextureFormat Format, int LevelOffset = 0, bool External = false);

/// <summary>How a parameter's number is read.</summary>
public enum TextureParameterUnit : byte {
    /// <summary>A plain number, written to the kernel as authored.</summary>
    Scalar = 0,

    /// <summary>
    ///     A length in texels <em>at the plan's base resolution</em>, scaled by the evaluator to the
    ///     resolution of the image the op writes.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Doc 48 § D8's bug with a two-year fuse.</b> A blur radius stored as absolute texels
    ///     looks right at the resolution it was tuned at and is half as wide at 4K, and nobody
    ///     associates the change with the resolution field. Every radius, width and length in a plan
    ///     is one of these.
    /// </remarks>
    TexelsAtBase = 1
}

/// <summary>One number an op hands its kernel, under the name the kernel declares.</summary>
/// <param name="Name">The uniform's name in the <c>.rvn</c>.</param>
/// <param name="Value">Its value, in the unit below.</param>
/// <param name="Unit">How the evaluator reads the value.</param>
public readonly record struct TextureParameter(
    string Name,
    float Value,
    TextureParameterUnit Unit = TextureParameterUnit.Scalar
);

/// <summary>One kernel dispatch: what it runs, what it reads, what it writes, and with what.</summary>
/// <remarks>
///     <para>
///         <b>An op has no resolution of its own, and that is deliberate.</b> Its resolution is the
///         resolution of the image it writes — so two ops writing one image cannot disagree about how
///         big it is, and a plan cannot be built in which they do. Doc 48 § M1 lists the resolution
///         as a field of the op; carrying it twice is a second place for it to be wrong.
///     </para>
///     <para>
///         <b>Inputs are indices into <see cref="TexturePlan.Images" />, bound in order</b> to the
///         sampled textures the kernel declares, in binding order. A kernel therefore names its
///         inputs positionally and an op never spells a binding.
///     </para>
/// </remarks>
public sealed record TextureOp {
    /// <summary>The kernel's shader name — <c>Blend</c>, <c>Blur</c>, <c>Levels</c>.</summary>
    public required string Kernel { get; init; }

    /// <summary>The image it writes, as an index into <see cref="TexturePlan.Images" />.</summary>
    public required int Output { get; init; }

    /// <summary>The images it reads, in the order the kernel declares its textures.</summary>
    public ImmutableArray<int> Inputs { get; init; } = [];

    /// <summary>The numbers it hands the kernel.</summary>
    public ImmutableArray<TextureParameter> Parameters { get; init; } = [];

    /// <summary>The value of one parameter, or <see langword="null" /> when the op does not carry it.</summary>
    /// <param name="name">The uniform's name.</param>
    /// <returns>The authored parameter.</returns>
    public TextureParameter? Find(string name) {
        foreach (var parameter in Parameters) {
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal)) {
                return parameter;
            }
        }

        return null;
    }
}

/// <summary>
///     A flat, ordered list of kernel dispatches over a table of images: the artefact both front ends
///     compile to and the only thing the evaluator runs.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D1.</b> A graph compiles to one of these and so does a layer stack, which is
///         what stops the two front ends from acquiring two evaluators and then two opinions about
///         what "overlay" means. Nothing here knows about a node, a wire, a document or a panel.
///     </para>
///     <para>
///         <b>Hand-buildable, and that is a requirement rather than a convenience.</b> M0 is a plan
///         with no graph behind it, and every test in this assembly builds one the same way — so a
///         defect in the evaluator is never confused with a defect in a compiler that does not exist
///         yet.
///     </para>
///     <para>
///         <b>The order is the liveness.</b> Ops run in the order they are listed, which is what lets
///         <see cref="TexturePoolSchedule" /> free an intermediate the moment its last reader has run
///         without any analysis of its own. A compiler emitting this list emits it in topological
///         order; a stack emits it bottom-up.
///     </para>
/// </remarks>
public sealed class TexturePlan {
    /// <summary>The largest extent any image in a plan may reach, in texels.</summary>
    /// <remarks>
    ///     Well past what any adapter will create — <c>maxImageDimension2D</c> is 16 384 on the
    ///     hardware this runs on — and it is here to make a nonsensical level a message rather than
    ///     an allocation. ⚠ It also keeps <c>baseExtent &lt;&lt; -level</c> away from a shift of 32 or
    ///     more, which in C# is a shift by <c>-level &amp; 31</c> and would silently make a 1024-texel
    ///     base come back as 1024.
    /// </remarks>
    public const int MaxExtent = 65536;

    /// <summary>
    ///     The width every relative image is measured against, in texels: the resolution the graph
    ///     was <b>authored</b> at.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Authored, not baked, and <see cref="BakeLevelOffset" /> is the difference.</b> This is
    ///     the unit <see cref="TextureParameterUnit.TexelsAtBase" /> is counted in, so it is a
    ///     property of the graph and not of the run — a radius of 8 means 8 texels of a 1024-wide
    ///     image forever, whatever resolution the same plan is later baked at.
    /// </remarks>
    public required int BaseWidth { get; init; }

    /// <summary>
    ///     The height every relative image is measured against, in texels, at the authoring
    ///     resolution.
    /// </summary>
    public required int BaseHeight { get; init; }

    /// <summary>How much bigger this bake is than the resolution the graph was authored at.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 48 § D8's actual criterion, and until
    ///         <a href="https://github.com/Rikarin/Vixen/issues/619">#619</a> the plan had no field
    ///         that could express it.</b> <see cref="BaseWidth" /> alone answers "how big is a
    ///         half-resolution image inside this graph"; it cannot answer "what is this graph at 4K",
    ///         because moving it moves the unit a radius is counted in by exactly as much and the
    ///         radius comes out unchanged. A plan with a base of 1024 and one with a base of 4096
    ///         both resolve <c>8</c> texels-at-base to <c>8</c> — which is the two-year fuse § D8 was
    ///         written to prevent, lit inside the type meant to prevent it.
    ///     </para>
    ///     <para>
    ///         <b>Same currency and same sign as <see cref="TextureImage.LevelOffset" />, and it adds
    ///         to it:</b> <c>0</c> bakes at the authoring resolution, <c>-2</c> bakes a 1K graph at
    ///         4K, <c>1</c> bakes it at 512 for a preview. <see cref="BakeLevelFor" /> turns a pair of
    ///         resolutions into one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One number rather than a bake width and a bake height, deliberately.</b> Two would
    ///         let a caller bake 1024×1024 as 4096×2048, and then a radius would have to be either
    ///         four times wider horizontally and twice vertically — a filter that is no longer round —
    ///         or wrong in one axis. That is the mirror-image failure § D8 names for a radius stored
    ///         as a fraction of the image, and refusing to represent it is cheaper than deciding it.
    ///     </para>
    /// </remarks>
    public int BakeLevelOffset { get; init; }

    /// <summary>The level offset that bakes a graph authored at one width at another.</summary>
    /// <param name="authoredWidth">The plan's <see cref="BaseWidth" />.</param>
    /// <param name="bakeWidth">The width to bake at.</param>
    /// <returns>The value to put in <see cref="BakeLevelOffset" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either width is not positive.</exception>
    /// <exception cref="ArgumentException">One is not a power-of-two multiple of the other.</exception>
    /// <remarks>
    ///     ⚠ <b>It refuses a ratio that is not a power of two rather than rounding one.</b> Baking a
    ///     1024 graph at 1536 would put every image at a size no level names, and the plan's whole
    ///     relative model — "every node is relative and only a bitmap is absolute" — would stop
    ///     meaning anything. A front end that wants 1536 bakes at 2048 and resamples the file.
    /// </remarks>
    public static int BakeLevelFor(int authoredWidth, int bakeWidth) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(authoredWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bakeWidth);

        if (bakeWidth % authoredWidth == 0 && int.IsPow2(bakeWidth / authoredWidth)) {
            return -int.Log2(bakeWidth / authoredWidth);
        }

        if (authoredWidth % bakeWidth == 0 && int.IsPow2(authoredWidth / bakeWidth)) {
            return int.Log2(authoredWidth / bakeWidth);
        }

        throw new ArgumentException(
            $"A graph authored at {authoredWidth} cannot be baked at {bakeWidth}: the two are not a "
            + "power of two apart, and every image in a plan is a level offset from the base. Bake at the "
            + "next power of two and resample the file.",
            nameof(bakeWidth)
        );
    }

    /// <summary>The image table. Ops address these by index.</summary>
    public required ImmutableArray<TextureImage> Images { get; init; }

    /// <summary>The dispatches, in evaluation order.</summary>
    public required ImmutableArray<TextureOp> Ops { get; init; }

    /// <summary>Which images survive the evaluation, as indices into <see cref="Images" />.</summary>
    /// <remarks>
    ///     An image nothing else reads and nothing here names is freed as soon as its last reader has
    ///     run — which for the last op in a plan with no outputs means the plan computes nothing
    ///     anybody can look at. <see cref="Validate" /> says so.
    /// </remarks>
    public ImmutableArray<int> Outputs { get; init; } = [];

    /// <summary>The plan's seed, from which every op's is derived.</summary>
    /// <remarks>
    ///     ⚠ <b>Per op, not per plan, and that is what makes a bake reproducible under editing.</b> A
    ///     single seed shared by every noise in a graph means inserting an op upstream changes the
    ///     numbers every op downstream draws — so the artist moves a node and the whole material
    ///     shimmers. <see cref="SeedFor" /> mixes the plan's seed with the op's own identity instead.
    /// </remarks>
    public uint Seed { get; init; }

    /// <summary>How big one image is in this bake, in texels.</summary>
    /// <param name="image">Its index in <see cref="Images" />.</param>
    /// <returns>Its width and height.</returns>
    /// <remarks>
    ///     <para>
    ///         The image's own <see cref="TextureImage.LevelOffset" /> and the plan's
    ///         <see cref="BakeLevelOffset" /> added: one says where this image sits inside the graph,
    ///         the other says how big the whole graph is being made this time.
    ///     </para>
    ///     <para>
    ///         Never smaller than one texel in either axis: a base of 1024 with a level offset of 12
    ///         would otherwise be a zero-sized image, which is a dispatch of no groups and a texture
    ///         no backend will create.
    ///     </para>
    /// </remarks>
    public Int2 SizeOf(int image) {
        var level = LevelOf(image);

        return new(Extent(BaseWidth, level), Extent(BaseHeight, level));
    }

    /// <summary>Where one image sits, counted in mip levels from the authoring base.</summary>
    /// <param name="image">Its index in <see cref="Images" />.</param>
    /// <returns>The image's own offset plus this bake's.</returns>
    public int LevelOf(int image) => Images[image].LevelOffset + BakeLevelOffset;

    /// <summary>How many texels of this image there are per texel of the <em>authoring</em> base.</summary>
    /// <param name="image">Its index in <see cref="Images" />.</param>
    /// <returns><c>1</c> at the base, <c>0.5</c> one level down, <c>4</c> in a bake two levels up.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Per texel of the base the graph was <em>authored</em> at, which is what makes
    ///         this the whole of § D8.</b> A level-0 image is <c>1</c> in a bake at the authoring
    ///         resolution and <c>4</c> in a bake two levels above it, so the same authored radius is
    ///         four times as many texels there — the same physical width, which is the property that
    ///         makes a 1K graph and its 4K bake one material.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured from the width the plan actually gets rather than from the level.</b>
    ///         They agree until an image is clamped to one texel, and past that point
    ///         <c>1 / 2^level</c> would scale a radius to a fraction of a texel on an image that is
    ///         not that small.
    ///     </para>
    /// </remarks>
    public float ScaleOf(int image) => Extent(BaseWidth, LevelOf(image)) / (float)BaseWidth;

    /// <summary>What one op's parameter is worth at the resolution that op writes.</summary>
    /// <param name="op">The op's index in <see cref="Ops" />.</param>
    /// <param name="parameter">The parameter.</param>
    /// <returns>The number to write into the kernel's uniform block.</returns>
    /// <remarks>
    ///     The one place doc 48 § D8's scaling happens. A kernel is written as though every length
    ///     were already in its own texels, because by the time the number reaches it, it is — and
    ///     that covers both halves of § D8 at once, because <see cref="ScaleOf" /> folds the image's
    ///     level and the bake's together.
    /// </remarks>
    public float Resolve(int op, TextureParameter parameter) =>
        parameter.Unit == TextureParameterUnit.TexelsAtBase
            ? parameter.Value * ScaleOf(Ops[op].Output)
            : parameter.Value;

    /// <summary>The seed one op draws from.</summary>
    /// <param name="op">Its index in <see cref="Ops" />.</param>
    /// <returns>A number that is the same on every machine and every run.</returns>
    /// <remarks>
    ///     A 32-bit avalanche mix of the plan's seed and the op's index — the finalizer from
    ///     MurmurHash3, chosen because it is four lines, has no table behind it, and gives every bit
    ///     of the index an equal effect on every bit of the result. That last property is the one
    ///     that matters: op 6 and op 7 must not draw neighbouring noise.
    /// </remarks>
    public uint SeedFor(int op) {
        var value = unchecked(Seed + (0x9E3779B9u * (uint)(op + 1)));

        value ^= value >> 16;
        value = unchecked(value * 0x85EBCA6Bu);
        value ^= value >> 13;
        value = unchecked(value * 0xC2B2AE35u);
        value ^= value >> 16;

        return value;
    }

    /// <summary>Everything about this plan that would make an evaluation meaningless.</summary>
    /// <returns>One message per problem; empty when the plan is sound.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An evaluator that refuses is worth much more than one that copes.</b> Every
    ///         problem below produces a picture on a real device — an out-of-range index throws
    ///         somewhere unrelated, an unwritten input reads as whatever the allocator left, an R8
    ///         output fails at pipeline creation with a message about a format nobody chose by hand.
    ///         Naming them here means the failure is about the plan.
    ///     </para>
    /// </remarks>
    public ImmutableArray<string> Validate() {
        var problems = ImmutableArray.CreateBuilder<string>();

        if (BaseWidth <= 0 || BaseHeight <= 0) {
            problems.Add($"The base resolution is {BaseWidth}×{BaseHeight}, and both axes have to be positive.");
        } else {
            for (var image = 0; image < Images.Length; image++) {
                // An external image's size is the caller's — the plan's level for it is nominal and
                // nothing here allocates it, so an absurd one is not this plan's problem.
                if (Images[image].External) {
                    continue;
                }

                var level = LevelOf(image);

                // ⚠ Reported rather than clamped, because the arithmetic below it stops being
                // arithmetic: `1024 << 32` in C# shifts by `32 & 31`, which is zero, so an absurd
                // level comes back as the base resolution and the plan bakes something that looks
                // plausible. Positive levels clamp to a texel and are a legitimate, if odd, thing to
                // ask for; only the doubling direction can run away.
                if (level < 0 && (Math.Max(BaseWidth, BaseHeight) > MaxExtent >> -Math.Max(level, -31))) {
                    problems.Add(
                        $"Image {image} is at level {level} of a {BaseWidth}×{BaseHeight} base — "
                        + $"{Images[image].LevelOffset} of its own plus a bake offset of {BakeLevelOffset} — "
                        + $"which is past the {MaxExtent}-texel ceiling."
                    );
                }
            }
        }

        var written = new int[Images.Length];

        Array.Fill(written, -1);

        for (var index = 0; index < Ops.Length; index++) {
            var op = Ops[index];

            if (string.IsNullOrEmpty(op.Kernel)) {
                problems.Add($"Op {index} names no kernel.");
            }

            if (op.Output < 0 || op.Output >= Images.Length) {
                problems.Add($"Op {index} writes image {op.Output}, and the table holds {Images.Length}.");

                continue;
            }

            var target = Images[op.Output];

            if (target.External) {
                problems.Add(
                    $"Op {index} writes image {op.Output}, which the caller supplies. An external image is an "
                    + "input and is never written."
                );
            }

            if (!TextureFormats.IsStorable(target.Format)) {
                problems.Add(
                    $"Op {index} writes image {op.Output}, which is {target.Format}. Raven declares no storage "
                    + "image of that format and Vulkan requires none, so no kernel can write it — compute in "
                    + "Rgba8, R16Float or Rgba16Float and narrow at the encode."
                );
            }

            if (written[op.Output] >= 0) {
                problems.Add(
                    $"Op {index} writes image {op.Output}, which op {written[op.Output]} already wrote. An image "
                    + "is written once, which is what makes its liveness the op order."
                );
            } else {
                written[op.Output] = index;
            }

            foreach (var input in op.Inputs) {
                if (input < 0 || input >= Images.Length) {
                    problems.Add($"Op {index} reads image {input}, and the table holds {Images.Length}.");

                    continue;
                }

                if (input == op.Output) {
                    problems.Add(
                        $"Op {index} reads and writes image {input}. A dispatch has no ordering between its own "
                        + "invocations, so a kernel reading the image it is writing reads whichever half of it "
                        + "has already run."
                    );
                }

                if (!Images[input].External && written[input] < 0) {
                    problems.Add(
                        $"Op {index} reads image {input}, which nothing has written yet. An intermediate is "
                        + "written by an earlier op or supplied by the caller."
                    );
                }
            }
        }

        foreach (var output in Outputs) {
            if (output < 0 || output >= Images.Length) {
                problems.Add($"Output {output} is not in a table of {Images.Length}.");
            } else if (!Images[output].External && written[output] < 0) {
                problems.Add($"Image {output} is an output and nothing writes it.");
            }
        }

        if (Outputs.IsDefaultOrEmpty && Ops.Length > 0) {
            problems.Add("The plan names no outputs, so everything it computes is freed before it is read.");
        }

        return problems.ToImmutable();
    }

    /// <summary>One axis at a level, saturating rather than wrapping at either end.</summary>
    /// <remarks>
    ///     ⚠ <b>Both shifts are clamped to 31, because C# shifts by <c>count &amp; 31</c> for an
    ///     <see cref="int" />.</b> Without the clamp a level of 32 would be a level of 0 and a level
    ///     of −32 would be one too: an absurd plan would come back reporting exactly the base
    ///     resolution, which is the most plausible-looking wrong answer available.
    ///     <see cref="Validate" /> refuses those plans; this makes the number honest even for a
    ///     caller that never asked.
    /// </remarks>
    static int Extent(int baseExtent, int level) =>
        level >= 0
            ? Math.Max(1, baseExtent >> Math.Min(level, 31))
            : (int)Math.Min(MaxExtent, (long)baseExtent << Math.Min(-level, 31));
}
