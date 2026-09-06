// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.TextureGraph;

/// <summary>The third category: an op in the catalogue that is not a compute kernel.</summary>
/// <remarks>
///     <para>
///         <b>Every other <c>TextureKernels.*</c> file declares shaders; this one declares the
///         exceptions to that.</b> Doc 48 § 4.11 counts them separately — "Not a kernel: one" — and
///         until this file the roll calls in <c>TextureNodeLibraryTests</c> knew about exactly two
///         kinds of thing, so an op with a <see cref="TextureOp.Cpu" /> would have been read as a
///         kernel whose <c>.rvn</c> had gone missing. A category nothing enumerates is the shape this
///         repository's gates fail in, so it is enumerated.
///     </para>
///     <para>
///         ⚠ <b>The one property a roll call can actually check about this list is that nothing on it
///         is also a kernel</b>, and it is the mechanical form of doc 48 § D3's ban on CPU twins. An
///         implementation of <see cref="ITextureCpuOperation" /> that reproduced what some
///         <c>.rvn</c> already does would make every parity test a claim that two transcriptions
///         agree; naming it after that kernel is how it would arrive, and
///         <c>TextureNodeLibraryTests</c> refuses the name collision outright.
///     </para>
/// </remarks>
[TextureKernelSurface]
internal static class TextureCpuKernels {
    /// <summary>Doc 48 § 4.6's <c>Normal → Height</c>. A Poisson solve, and the only entry.</summary>
    public const string NormalToHeight = NormalToHeightOperation.OpKernel;

    /// <summary>Every CPU operation this assembly ships, which is what the roll call enumerates.</summary>
    public static IReadOnlyList<string> All { get; } = [NormalToHeight];
}

/// <summary>The ops doc 48's CPU nodes are.</summary>
/// <remarks>
///     <b>Builders, for <see cref="TextureSurfaces" />'s reason and one more.</b> A CPU op carries an
///     <see cref="ITextureCpuOperation" /> instance as well as its parameters, and an op built by
///     hand that forgot it is a plan naming a kernel that does not exist — an exception at bake time
///     about an embedded resource, which says nothing about the mistake. There is one place that
///     instance is attached.
/// </remarks>
[TextureKernelSurface]
internal static class TextureCpuOperations {
    /// <summary>Doc 48 § 4.6's <c>Normal → Height</c>.</summary>
    /// <param name="output">The height field to write. Grey, and signed — see the operation.</param>
    /// <param name="normals">The normal map to integrate.</param>
    /// <param name="iterations">The solver's budget, in conjugate-gradient steps.</param>
    /// <param name="intensity">
    ///     <c>HeightToNormal</c>'s <c>intensity</c>, undone. A map that was authored at 1 — which is
    ///     every map that came out of a file rather than out of that kernel — wants 1 here.
    /// </param>
    /// <returns>The op.</returns>
    /// <remarks>
    ///     ⚠ <b>Neither parameter is <see cref="TextureParameterUnit.TexelsAtBase" />, and both were
    ///     considered.</b> Doc 48 § D8 scales a length so that a graph bakes the same picture at any
    ///     resolution; an iteration count is not a length, and scaling it would make a 4K bake spend
    ///     four times the budget on a system four times as large — which is closer to right than
    ///     leaving it alone, and is still wrong, because the number of steps a Poisson system needs
    ///     grows with the grid's <em>diameter</em> rather than with its area. The honest answer is
    ///     that this parameter is the author's and the resolution independence § D8 promises is
    ///     approximate here; it is stated rather than papered over with a factor.
    /// </remarks>
    public static TextureOp NormalToHeight(
        int output,
        int normals,
        float iterations = NormalToHeightOperation.DefaultIterations,
        float intensity = 1f
    ) =>
        new() {
            Kernel = TextureCpuKernels.NormalToHeight,
            Output = output,
            Inputs = [normals],
            Cpu = new NormalToHeightOperation(),
            Parameters = [
                new(NormalToHeightOperation.Iterations, iterations),
                new(NormalToHeightOperation.Intensity, intensity)
            ]
        };

    /// <summary>Every op this class can build, for a test that wants to walk them.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the property the roll calls reflect for, and its element type is what tells
    ///     them apart.</b> <c>TextureNodeLibraryTests.Declared</c> walks every static <c>All</c> in
    ///     the assembly; an entry whose <see cref="TextureOp.Cpu" /> is set is a CPU operation and
    ///     every other entry is a kernel, which is derived from the ops themselves rather than from a
    ///     list somebody has to remember to extend.
    /// </remarks>
    public static ImmutableArray<TextureOp> All { get; } = [NormalToHeight(0, 1)];
}
