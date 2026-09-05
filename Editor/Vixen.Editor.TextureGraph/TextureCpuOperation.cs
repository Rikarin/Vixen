// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.TextureGraph;

/// <summary>One image as a CPU operation sees it: the raw texels, and what they are.</summary>
/// <param name="Format">What the bytes are — the same <see cref="TextureFormat" /> the plan declared.</param>
/// <param name="Width">Its width in texels, at this bake.</param>
/// <param name="Height">Its height in texels, at this bake.</param>
/// <param name="Bytes">
///     The texels, tightly packed, top row first, <see cref="TextureFormats.BytesPerTexel" /> each.
///     An input's are what came off the device; the output's are the buffer to fill, and whatever is
///     in it when <see cref="ITextureCpuOperation.Run" /> returns is what is uploaded.
/// </param>
/// <remarks>
///     ⚠ <b>Raw texels rather than a decoded picture, because the one operation this exists for
///     cannot use a picture.</b> Doc 48 § 4.6's <c>Normal → Height</c> integrates a gradient field:
///     it needs the half-floats it was handed, and <c>TexturePixels</c> — which is an encoder on the
///     way to a PNG — would have already rounded them to bytes.
/// </remarks>
public readonly record struct TextureCpuImage(TextureFormat Format, int Width, int Height, byte[] Bytes);

/// <summary>Everything one CPU op is given when the evaluator reaches it.</summary>
/// <param name="Plan">The plan being baked, so the operation can resolve its own parameters.</param>
/// <param name="Op">Its index in <see cref="TexturePlan.Ops" />.</param>
/// <param name="Inputs">Its input images, in the order <see cref="TextureOp.Inputs" /> lists them.</param>
/// <param name="Output">The image to fill.</param>
/// <remarks>
///     <b>The plan and the index rather than the resolved numbers</b>, so that a CPU op reads its
///     parameters through <see cref="TexturePlan.Resolve" /> exactly as a kernel's uniform block does
///     — which is the only way doc 48 § D8's scaling reaches both kinds of op through one piece of
///     arithmetic.
/// </remarks>
public readonly record struct TextureCpuInvocation(
    TexturePlan Plan,
    int Op,
    ImmutableArray<TextureCpuImage> Inputs,
    TextureCpuImage Output
);

/// <summary>An operation in a plan that runs on the CPU rather than as a compute dispatch.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A deliberate exception to doc 48 § D3, and it exists for exactly one node.</b> § D3
///         says every atomic operation is a Raven compute shader and that there is no CPU
///         implementation of any node — because a CPU twin of a kernel turns every parity test into a
///         claim that two transcriptions agree rather than that either is right. § 4.6 names the one
///         entry in the catalogue that is <b>not</b> a kernel: <c>Normal → Height</c> is a Poisson
///         solve, and doc 42 § B1's <c>Vixen.Geometry.Uv/Solving/ConjugateGradient.cs</c> is the
///         solver. <a href="https://github.com/Rikarin/Vixen/issues/688">#688</a> is the finding that
///         a plan could not express it at all.
///     </para>
///     <para>
///         ⚠ <b>What stops this becoming the norm is that it is not an escape hatch from writing a
///         kernel — it is a much worse way to compute a picture, and the cost is structural.</b> A
///         dispatch is recorded into the list already in flight; a CPU op ends that list, waits for
///         the device, copies every input into host memory, runs single-threaded, copies the answer
///         back and starts a new list. That is two full pipeline drains in the middle of a bake, plus
///         the bandwidth, and a chain of them serialises the entire evaluation. <b>The test of
///         whether a node belongs here is not "would this be easier in C#" — it is "is there a GPU
///         formulation at all".</b> For a Poisson solve there is not one worth having: low
///         frequencies converge in O(n²) Jacobi sweeps on a grid this size, so the GPU version is
///         thousands of dispatches and a different convergence story from the one § 4.6 specified.
///         Anything that <em>can</em> be a kernel is a kernel, and § D5's "anything that has to be
///         written in C# to be fast is a bug in the atomic set" is the standing test.
///     </para>
///     <para>
///         <b>It is not a place for a CPU twin either.</b> An implementation here that reproduces
///         what some <c>.rvn</c> already does is the thing § D3 bans, whatever this interface makes
///         possible — and it would be found by the same parity test being meaningless.
///     </para>
/// </remarks>
public interface ITextureCpuOperation {
    /// <summary>What to call this in a message, when the op's own kernel name is not enough.</summary>
    string Name { get; }

    /// <summary>Fills the output image from the inputs.</summary>
    /// <param name="invocation">The plan, the op, its inputs and the buffer to fill.</param>
    /// <remarks>
    ///     ⚠ <b>The output buffer arrives zeroed, and zero is a valid-looking number for almost
    ///     everything a plan computes</b> — so an implementation that returns early leaves a black
    ///     image rather than an error. Write every texel.
    /// </remarks>
    void Run(in TextureCpuInvocation invocation);
}
