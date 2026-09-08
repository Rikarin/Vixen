// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.TextureGraph;

/// <summary>Which of doc 48 § 4.2's sixteen operators a <c>Blend</c> op composites with.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The numbers are the contract with <c>Shaders/Blend.rvn</c>'s <c>mode</c>, which
///         compares against them literally, and they are <em>appended</em> rather than ordered the way
///         § 4.2's prose lists them.</b> The eight of § M1 were numbered 0–7 before the other eight
///         existed, and a plan is a file: renumbering to match the prose would have changed what every
///         plan already written means, silently, into another perfectly plausible picture.
///     </para>
///     <para>
///         <b>Each has a <em>neutral</em> foreground and a <em>distinguishing</em> value, and
///         <c>TextureBlendDeviceTests</c> asserts both of every one.</b> Neither alone is enough: a
///         mode written with its operands swapped usually keeps one of the two. Difference against
///         black is the backdrop whichever way round it is written, and hard light copied from overlay
///         without moving the selector is exactly overlay on every image there is.
///     </para>
/// </remarks>
internal enum TextureBlendMode {
    /// <summary>The foreground, under the opacity and its own alpha. The mode with no neutral.</summary>
    Copy = 0,

    /// <summary><c>a · b</c>. Neutral at white.</summary>
    Multiply = 1,

    /// <summary><c>1 − (1 − a)(1 − b)</c>. Neutral at black.</summary>
    Screen = 2,

    /// <summary>Multiply where the backdrop is dark, screen where it is light. Neutral at mid-grey.</summary>
    Overlay = 3,

    /// <summary><c>a + b</c>. Neutral at black.</summary>
    Add = 4,

    /// <summary><c>a − b</c>. Neutral at black.</summary>
    Subtract = 5,

    /// <summary><c>min(a, b)</c>. Neutral at white.</summary>
    Darken = 6,

    /// <summary><c>max(a, b)</c>. Neutral at black.</summary>
    Lighten = 7,

    /// <summary><c>a / b</c>, capped at white. Neutral at white.</summary>
    Divide = 8,

    /// <summary>Overlay with the operands swapped — the selector reads the foreground. Neutral at mid-grey.</summary>
    HardLight = 9,

    /// <summary><c>ComputeColor.SoftLight</c>'s form, with the <c>sqrt</c> half. Neutral at mid-grey.</summary>
    SoftLight = 10,

    /// <summary><c>|a − b|</c>. Neutral at black.</summary>
    Difference = 11,

    /// <summary><c>a + b − 2ab</c>: difference's smooth twin. Neutral at black.</summary>
    Exclusion = 12,

    /// <summary><c>a / (1 − b)</c>, capped. Neutral at black.</summary>
    ColourDodge = 13,

    /// <summary><c>1 − (1 − a) / b</c>, capped. Neutral at white.</summary>
    ColourBurn = 14,

    /// <summary><c>a + (b − ½) · 2</c>: the foreground read as a signed unorm. Neutral at mid-grey.</summary>
    SignedAdd = 15
}

/// <summary>Whether a <c>Blend</c>'s foreground arrives on top of its backdrop or reinterprets it.</summary>
/// <remarks>
///     <para>
///         <b>Orthogonal to <see cref="TextureBlendMode" /> and asking a different question.</b> The
///         mode says what the operator does where the two overlap; this says what the foreground
///         <em>is</em>. <see cref="Over" /> brings coverage of its own and the composite covers
///         <c>αb + (1 − αb)·αs</c>. <see cref="Atop" /> covers exactly what the backdrop covered and
///         reads the foreground's alpha as the fraction of the backdrop the adjustment speaks for.
///     </para>
///     <para>
///         ⚠ <b>It exists because <see cref="Over" />'s alpha rule is monotonic</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/845">#845</a>. A filter layer's content is
///         the layers beneath it, adjusted, so compositing it back <em>over</em> them raises the
///         coverage it was handed: <c>K + (1 − K)·K</c>, three quarters where a group covered a half.
///         No mode and no opacity can express "the coverage that leaves is the coverage that
///         arrived", so this is a second uniform rather than a seventeenth operator.
///     </para>
/// </remarks>
internal enum TextureBlendCoverage {
    /// <summary>Source-over: the foreground arrives on top and brings its own coverage.</summary>
    Over = 0,

    /// <summary>The foreground is a reinterpretation of the backdrop, and the coverage is unchanged.</summary>
    Atop = 1
}

/// <summary>The <c>Blend</c> kernel, as ops a plan can hold.</summary>
/// <remarks>
///     <para>
///         <b>A builder rather than a <c>TextureOp</c> written at each call site</b>, for
///         <c>TextureSources</c>' reason: <c>TexturePlanEvaluator.Uniforms</c> throws when an op omits
///         a parameter the kernel declares and silently ignores one it does not, so the complete set
///         is emitted in one place and a test walks it.
///     </para>
///     <para>
///         <b>Internal, because nothing outside this assembly has a caller yet.</b> § M4's node
///         classes are what will want it public.
///     </para>
/// </remarks>
[TextureKernelSurface]
internal static class TextureBlend {
    /// <summary>Two images composited into one.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="background">What is underneath.</param>
    /// <param name="foreground">What is on top.</param>
    /// <param name="mode">Which operator.</param>
    /// <param name="opacity">How much of the result is the foreground's, before its own alpha.</param>
    /// <param name="coverage">Whether the foreground arrives on top of the backdrop or reinterprets it.</param>
    /// <returns>The op.</returns>
    public static TextureOp Mix(
        int output,
        int background,
        int foreground,
        TextureBlendMode mode = TextureBlendMode.Copy,
        float opacity = 1f,
        TextureBlendCoverage coverage = TextureBlendCoverage.Over
    ) =>
        new() {
            Kernel = "Blend",
            Output = output,
            Inputs = [background, foreground],
            Parameters = [
                new("mode", (float)(int)mode),
                new("opacity", opacity),
                new("atop", (float)(int)coverage)
            ]
        };

    /// <summary>The same composite, with the opacity read per texel out of a third image.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="background">What is underneath.</param>
    /// <param name="foreground">What is on top.</param>
    /// <param name="mask">How much of the foreground each texel gets, in its red channel.</param>
    /// <param name="mode">Which operator.</param>
    /// <param name="opacity">A scale over the whole mask, before the foreground's own alpha.</param>
    /// <param name="coverage">Whether the foreground arrives on top of the backdrop or reinterprets it.</param>
    /// <returns>The op.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second kernel rather than a fourth image on <see cref="Mix" />, and the reason
    ///         is <c>Blend.rvn</c>'s own refusal</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1059">#1059</a>. That refusal is
    ///         about a <em>layer</em>, which owns a mask stack, and doc 48 § M7 is where it belongs;
    ///         a graph's mask is whatever image the author wired, which is a different question. So
    ///         the kernel is a sibling and § M7's shape is untouched.
    ///     </para>
    ///     <para>
    ///         <b>Named <c>Masked</c> and not <c>Mix</c> because <see cref="Mix" /> is taken</b> — by
    ///         the <c>Blend</c> op, which this class has built since M1. The kernel it names is
    ///         <c>Mix</c>, which is the node's name and the file's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mask multiplies <paramref name="opacity" />, it does not replace it.</b> A
    ///         white mask is exactly <see cref="Mix" /> at the same opacity — asserted on a device,
    ///         because "the two kernels agree where the mask says nothing" is the only thing that
    ///         makes a second transcription of sixteen operators safe to have.
    ///     </para>
    /// </remarks>
    public static TextureOp Masked(
        int output,
        int background,
        int foreground,
        int mask,
        TextureBlendMode mode = TextureBlendMode.Copy,
        float opacity = 1f,
        TextureBlendCoverage coverage = TextureBlendCoverage.Over
    ) =>
        new() {
            Kernel = "Mix",
            Output = output,

            // ⚠ Positional, in the order `Mix.rvn` declares its three `Texture2D`s — the evaluator
            // binds a sampled texture per declaration and never by name, so a pair swapped here is a
            // picture rather than an error.
            Inputs = [background, foreground, mask],
            Parameters = [
                new("mode", (float)(int)mode),
                new("opacity", opacity),
                new("atop", (float)(int)coverage)
            ]
        };

    /// <summary>Every op this class can build, for a test that wants to walk them.</summary>
    public static ImmutableArray<TextureOp> All { get; } = [Mix(0, 1, 2), Masked(0, 1, 2, 3)];
}
