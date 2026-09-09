// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.ShaderCompiler;

namespace Vixen.Editor.ShaderGraph;

/// <summary>
///     The shader-library sources a node preview is compiled beside, so that a node calling the
///     library can be previewed at all.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The limit <a href="https://github.com/Rikarin/Vixen/issues/510">#510</a> names, and
///         it is a property of the preview's compilation rather than of any node.</b>
///         <c>ShaderGraphPreviewRenderer</c> passed <c>RavenEffectCompiler.FromSources</c> one text
///         — the emitted preview — so nothing in <c>Raven/Library</c> was in scope and a node whose
///         body calls <c>ComputeColor.ValueNoise</c> failed to bind with <c>RVN2010: The name
///         'ComputeColor' does not exist in the current context</c>. The same graph compiled
///         perfectly as a <em>material</em>, because <c>EditorEffects</c> and <c>ShaderBuildRunner</c>
///         both hand Raven the library's import closure. So five shipped nodes declared no preview
///         and the next node reaching into the library would have made six.
///     </para>
///     <para>
///         <b>The cure is a list rather than a signature</b>, which is the same finding the texture
///         graph's prelude landed on: <c>FromSources</c> takes a <em>set</em> of texts and makes them
///         one compilation, and a package's declarations are visible across one compilation. Nothing
///         had to be compiled to a <c>.rvnlib</c> and nothing had to be re-written into this
///         assembly.
///     </para>
///     <para>
///         ⚠ <b>And it does not cost the preview a binding, which was the thing to establish before
///         designing anything.</b> The renderer binds one uniform block and refuses a variant whose
///         reflection asks for anything else — see its own remarks — so a prelude that dragged a
///         texture or a sampler into scope would have turned "no preview" into "every preview
///         refused". These four files declare no resources at all: they are <c>struct</c>s of pure
///         functions, and a function nothing calls is unreferenced and does not reach the emitted
///         module. ⚠ <c>ShaderGraphPreviewTests.A_node_that_calls_the_library_compiles_to_a_variant_that_binds_one_block</c>
///         asserts the reflection rather than trusting that paragraph.
///     </para>
///     <para>
///         <b>Which four, and why they are a closure and not a taste.</b>
///         <c>Material/ComputeColor.rvn</c> is the node vocabulary — its own header says it was
///         written for this graph. It imports <c>Vixen.Shaders.Core</c>, and <c>Core/Random.rvn</c>
///         spells <c>Math.SphericalToCartesian</c> and <c>Const.TwoPi</c>, so <c>Core/Math.rvn</c>
///         arrives with it; <c>Core/ColorSpaces.rvn</c> completes the package's <c>Const</c> and
///         colour helpers and imports nothing further. ⚠ Every file in a compilation is bound whether
///         the preview calls into it or not, so a set that stops one edge short fails on
///         <em>every</em> previewed node at once — which reads as the prelude not working rather than
///         as one missing file.
///     </para>
/// </remarks>
static class ShaderGraphPreviewPrelude {
    const string Prefix = "Vixen.Editor.ShaderGraph.Prelude.";

    /// <summary>The library sources, in the order a compilation is handed them.</summary>
    /// <remarks>
    ///     The name is the library file's own path under <c>Raven/Library</c> —
    ///     <c>Core.Random.rvn</c> — so a diagnostic about the library reads as one rather than as a
    ///     complaint about the preview compiled beside it.
    /// </remarks>
    public static ImmutableArray<(string Name, string Text)> Sources { get; } = Read();

    /// <summary>Compiles one preview's Raven against the library.</summary>
    /// <param name="name">What the compiler is told the preview's file is called.</param>
    /// <param name="source">The emitted preview shader.</param>
    /// <returns>A compiler the caller asks for an <see cref="Vixen.Shaders.EffectKey" />.</returns>
    /// <remarks>
    ///     ⚠ The one place that decides what a preview binds against, for the reason the texture
    ///     graph's equivalent is: a test that spelled <c>FromSources([(name, source)])</c> for itself
    ///     would prove a node compiles under rules the renderer does not use, and the direction of
    ///     that error is a node that previews in the suite and shows nothing in the editor.
    /// </remarks>
    public static RavenEffectCompiler Compile(string name, string source) =>
        RavenEffectCompiler.FromSources([.. Sources, (name, source)]);

    static ImmutableArray<(string Name, string Text)> Read() {
        var assembly = typeof(ShaderGraphPreviewPrelude).Assembly;
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
