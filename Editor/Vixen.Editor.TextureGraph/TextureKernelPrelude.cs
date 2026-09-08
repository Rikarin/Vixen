// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.ShaderCompiler;

namespace Vixen.Editor.TextureGraph;

/// <summary>
///     The shader-library sources every kernel is compiled beside, so that a kernel can
///     <c>import</c> rather than transcribe.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A kernel could always have imported; what it could not do was be compiled alone.</b>
///         <a href="https://github.com/Rikarin/Vixen/issues/635">#635</a> reads as though an
///         <c>import</c> in a texture-graph kernel is refused by the compiler and offers two
///         answers — a compiled <c>.rvnlib</c> handed to <c>referencePaths</c>, or a prelude this
///         assembly writes and prepends. Both are wrong about the cause. <c>FromSources</c> takes a
///         <em>set</em> of texts and makes them one compilation, and a package's declarations are
///         visible across one compilation; the evaluator was passing one text. So the fix is
///         neither a build artefact nor a second copy of the arithmetic: it is the library's own
///         <c>.rvn</c> files, embedded verbatim, in the same list.
///     </para>
///     <para>
///         <b>Which sources, and why exactly these three.</b> <c>Material/ComputeColor.rvn</c> is
///         what doc 48 § 4.2's colour kernels want — the YIQ hue rotation, the Rec. 709 luminance,
///         the blend modes. It <c>import</c>s <c>Vixen.Shaders.Core</c>, and <c>Core/Random.rvn</c>
///         in turn spells <c>Math.SphericalToCartesian</c> and <c>Const.TwoPi</c>, so
///         <c>Core/Math.rvn</c> comes with it. ⚠ That last edge is the one worth writing down: a
///         set that stops at <c>Random.rvn</c> fails with <c>RVN2010: The name 'Math' does not
///         exist</c> on <em>every</em> kernel at once, because the library file is bound whether the
///         kernel calls into that part of it or not.
///     </para>
///     <para>
///         ⚠ <b>Embedded from the library's own path, not copied into this assembly.</b> The
///         <c>EmbeddedResource</c> entries in the <c>.csproj</c> point at
///         <c>Raven/Library/**</c> and give the resource a <c>LogicalName</c> under a prefix
///         <see cref="TextureKernels" /> does not read — so there is exactly one copy of these
///         functions in the repository, editing the library edits what a kernel compiles against,
///         and the folder that <em>is</em> the kernel list stays the folder it was.
///     </para>
///     <para>
///         <b>What it costs.</b> Every variant parses three more files. That is three parses against
///         a lowering, a code generation and a pipeline creation, and it is paid once per
///         <c>(kernel, format)</c> because <c>TexturePlanEvaluator</c> caches the variant. What it
///         does not cost is module size: a library function no kernel calls is unreferenced and does
///         not reach the emitted module.
///     </para>
/// </remarks>
public static class TextureKernelPrelude {
    const string Prefix = "Vixen.Editor.TextureGraph.Prelude.";

    /// <summary>The library sources, in the order a compilation is handed them.</summary>
    /// <remarks>
    ///     The name is what a diagnostic points at, and it is the library file's own path under
    ///     <c>Raven/Library</c> — <c>Core.Random.rvn</c> — so that a complaint about the library
    ///     reads as one rather than as a complaint about the kernel compiled beside it.
    /// </remarks>
    public static ImmutableArray<(string Name, string Text)> Sources { get; } = Read();

    /// <summary>Compiles one kernel source against the library.</summary>
    /// <param name="name">What the compiler is told the kernel's file is called.</param>
    /// <param name="source">The kernel's Raven text, already in the variant's output format.</param>
    /// <returns>A compiler the caller asks for an <c>EffectKey</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>The one place that decides what a kernel binds against, and it is public because the
    ///     tests are the other half of the answer.</b> Fourteen test call sites used to spell
    ///     <c>FromSources([(name, source)])</c> for themselves, which is fourteen chances for a suite
    ///     to prove a kernel compiles under rules the evaluator does not use. A kernel that imports
    ///     would have passed <c>TexturePlanEvaluator</c> and failed every one of them, which is the
    ///     right direction for that error to point but the wrong number of places to fix it.
    /// </remarks>
    public static RavenEffectCompiler Compile(string name, string source) =>
        RavenEffectCompiler.FromSources([.. Sources, (name, source)]);

    static ImmutableArray<(string Name, string Text)> Read() {
        var assembly = typeof(TextureKernelPrelude).Assembly;
        var builder = ImmutableArray.CreateBuilder<(string, string)>();

        // Ordered by resource name so that two runs hand the compiler the same list, and a
        // diagnostic's file therefore reads the same on every machine.
        foreach (var resource in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal)) {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal)) {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource);

            if (stream is null) {
                continue;
            }

            using var reader = new StreamReader(stream);

            builder.Add((resource[Prefix.Length..], reader.ReadToEnd()));
        }

        return builder.ToImmutable();
    }
}
