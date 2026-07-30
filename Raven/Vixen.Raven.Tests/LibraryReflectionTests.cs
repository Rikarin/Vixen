// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     The checked-in reflection for the library shaders the engine binds against.
/// </summary>
/// <remarks>
///     <para>
///         <c>Vixen.Shaders.Generators</c> turns a <c>.reflect.json</c> into typed keys, and until
///         something produced one for <c>Raven/Library</c> the engine's own post-process nodes had to
///         intern their keys from strings — which works and gives up every guarantee interning exists
///         for. This is what produces them.
///     </para>
///     <para>
///         <strong>Checked in rather than generated during the build</strong>, and deliberately: the
///         alternative is <c>Vixen.Rendering</c> depending on the compiler being built first, in a
///         repository where the compiler is the larger project of the two. The same argument as
///         <c>Generated/Lighting.Bindings.g.cs</c> in <c>Vixen.Shaders.Tests</c>, and the same
///         safeguard — this test regenerates and compares, so the files cannot drift from the shaders
///         without a test saying so. Set <c>VIXEN_REGENERATE=1</c> to rewrite them, then read the
///         diff.
///     </para>
///     <para>
///         <strong>The default variant, and that is a real limitation.</strong> Reflection describes
///         one variant — a binding behind a false permutation is already gone by lowering, which is
///         what makes the reported interface honest — so a resource only a non-default variant reads
///         gets no generated key. Every shader here is written so that its bindings survive its
///         default, and <see cref="Every_binding_the_engine_needs_survives_the_default_variant" /> is
///         what keeps that true.
///     </para>
/// </remarks>
public class LibraryReflectionTests {
    /// <summary>The shaders the engine binds by name, and therefore wants keys for.</summary>
    /// <remarks>
    ///     Not the whole library. A shader nothing in the engine names needs no keys, and every entry
    ///     here is a file somebody has to keep compiling — so the list grows when a node starts
    ///     binding one rather than in anticipation.
    /// </remarks>
    static readonly (string Package, string Shader)[] Published = [
        ("PostFx", "Bloom"),
        ("PostFx", "Tonemap"),
        ("PostFx", "Fxaa"),
        ("PostFx", "Sharpen"),
        ("PostFx", "Vignette"),
        ("PostFx", "Fog"),
        ("PostFx", "Outline"),
        ("PostFx", "Ssao"),
        ("PostFx", "Taa"),
        ("PostFx", "DistanceFieldAo"),
        ("PostFx", "IndirectDiffuse"),
        ("IrradianceFields", "IrradianceFill"),
        ("IrradianceFields", "IrradianceRepair"),
        ("Pipeline", "ForwardPlus"),

        // The GPU culling passes, whose host binds every one of their buffers by name — see
        // Vixen.Rendering's GpuVisibilityGroup, HiZPyramid and GpuDrawArguments. Published for the
        // reason the others are: a binding index comes from declaration order within a set, so
        // adding a texture above another renumbers it, and a name that is a literal in C# survives a
        // rename in the .rvn as a silent fall back to the CPU rather than as an error.
        ("Pipeline", "Culling"),
        ("Pipeline", "HiZReduce"),
        ("Pipeline", "DrawArguments"),

        // And the raster that draws what the traversal chose — GpuClusterRaster binds all six of its
        // buffers by name, for the same reason and with the same consequence if a rename slips past.
        ("Pipeline", "ClusterRaster"),

        // Phase 5's binning pass. The resolve is deliberately *not* here: it composes IMaterialSurface,
        // so it has no single interface to publish — its parameters are whichever features a material
        // chose, which is exactly why ForwardPlus's entry above is described under one named composition
        // rather than in general.
        ("Pipeline", "VisibilityTiles")
    ];

    /// <summary>
    ///     The composition the published reflection is described under.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A composed shader has no single interface — its parameters are the ones its features
    ///         brought, so <c>ForwardPlus</c> reflects differently for every material. This is the one
    ///         the engine's own default descriptor produces: the feature chain, with a
    ///         metal-roughness surface in its first slot and the standard shading model.
    ///     </para>
    ///     <para>
    ///         <strong>Which makes this file an oracle rather than only a convenience.</strong>
    ///         <c>MaterialCompiler</c> predicts these names without a compiler in the process — it has
    ///         to, because a material is authored and serialised on machines that never compile a
    ///         shader — and prediction is a rule written down twice. <c>MaterialReflectionTests</c> in
    ///         <c>Vixen.Rendering.Tests</c> reads this file and holds the engine's prediction against
    ///         it, so the two cannot drift without a test saying so.
    ///     </para>
    ///     <para>
    ///         Qualified rather than bare for the chain's slot, because that is what the engine emits
    ///         and the point is to describe what the engine asks for.
    ///     </para>
    /// </remarks>
    static readonly (string Slot, string Shader)[] PublishedComposition = [
        ("surface", "CompositeSurface"),
        ("CompositeSurface.first", "MetalRoughnessSurface"),
        ("shading", "StandardShading")
    ];

    static readonly JsonSerializerOptions Json = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    static string LibraryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library"));

    /// <summary>
    ///     The whole library, lowered once. Every shader here is described out of the same module.
    /// </summary>
    /// <remarks>
    ///     One compilation over every file, for the reason
    ///     <c>LibraryTreeTests.TheWholeLibraryBindsAsOneCompilation</c> gives: the files depend on
    ///     each other, so binding one alone fails on a name that is not missing.
    /// </remarks>
    static IrModule Library(out IEnumerable<string> usedPermutationKeys) {
        var trees = new[] {
                "Core", "Shading", "Geometry", "DistanceFields", "IrradianceFields", "Material", "Pipeline", "Ui",
                "PostFx", "Vfx"
            }
            .SelectMany(package => Directory.EnumerateFiles(Path.Combine(LibraryRoot, package), "*.rvn"))
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(file => SyntaxTree.ParseText(File.ReadAllText(file), path: Path.GetFileName(file)))
            .ToArray();

        var compilation = Compilation.Create(
            "Library",
            PermutationValues.Empty,
            LibraryComposition.With(PublishedComposition),
            trees
        );

        var diagnostics = compilation.GetDiagnostics();

        Assert.True(
            diagnostics.Count == 0,
            "The library does not bind cleanly:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        usedPermutationKeys = compilation.UsedPermutationKeys;
        return module;
    }

    static string PathOf(string package, string shader) =>
        Path.Combine(LibraryRoot, package, shader + ".reflect.json");

    /// <summary>
    ///     The checked-in reflection is what the compiler produces for the library as it stands.
    /// </summary>
    [Fact]
    public void The_checked_in_reflection_is_what_the_compiler_produces() {
        var module = Library(out var used);
        var described = ReflectionBuilder.Describe(module, used);

        foreach (var (package, shader) in Published) {
            Assert.True(described.ContainsKey(shader), $"the library has no shader named '{shader}'");

            var emitted = JsonSerializer.Serialize(described[shader], Json);
            var path = PathOf(package, shader);

            if (Environment.GetEnvironmentVariable("VIXEN_REGENERATE") == "1") {
                File.WriteAllText(path, emitted + Environment.NewLine);
            }

            Assert.True(File.Exists(path), $"{package}/{shader}.reflect.json is missing — regenerate it");

            Assert.Equal(
                emitted.ReplaceLineEndings().TrimEnd(),
                File.ReadAllText(path).ReplaceLineEndings().TrimEnd()
            );
        }
    }

    /// <summary>
    ///     Every binding the engine binds by name is in the default variant's interface.
    /// </summary>
    /// <remarks>
    ///     The limitation this file exists inside. Reflection describes one variant, and a resource
    ///     behind a false permutation is gone before it is described — so a shader whose texture is
    ///     only read in a non-default mode would silently generate no key for it, and the node
    ///     binding it would fail to compile with no hint as to why. Bloom is exactly that shape:
    ///     <c>previous</c> is read only by the upsample mode, and this is what says so out loud.
    /// </remarks>
    [Fact]
    public void Every_binding_the_engine_needs_survives_the_default_variant() {
        var described = ReflectionBuilder.Describe(Library(out var used), used);

        var bloom = described["Bloom"];
        var names = bloom.Sets.SelectMany(set => set.Bindings).Select(binding => binding.Name).ToArray();

        Assert.Contains("source", names);
        Assert.Contains("sourceSampler", names);
        Assert.Contains("previous", names);

        Assert.Contains("texelSize", bloom.Parameters.Select(p => p.Name));
        Assert.Contains("threshold", bloom.Parameters.Select(p => p.Name));
    }

    /// <summary>
    ///     A shader's used-key list holds its own keys and nobody else's.
    /// </summary>
    /// <remarks>
    ///     The list handed to the builder is the <em>compilation's</em>, and describing the whole
    ///     library at once makes that a compilation of forty-odd permutations. Reporting them all
    ///     against a two-permutation post effect would be wrong by the field's own definition — "the
    ///     keys that actually affected this output" — and it is the list an effect cache key hashes,
    ///     so the cost of getting it wrong is a cache that splits on keys the shader never read.
    /// </remarks>
    [Fact]
    public void A_shaders_used_keys_are_its_own() {
        var described = ReflectionBuilder.Describe(Library(out var used), used);

        Assert.True(used.Count() > 10, "the library should have many permutations, or this proves nothing");
        Assert.Equal(["KarisAverage", "Mode"], described["Bloom"].UsedPermutationKeys);

        Assert.All(
            described["Tonemap"].UsedPermutationKeys,
            key => Assert.Contains(key, described["Tonemap"].Permutations.Select(p => p.Name))
        );
    }

    /// <summary>The declared defaults reach the reflection, which is where a generated key gets them.</summary>
    [Fact]
    public void The_shaders_declared_defaults_are_in_the_reflection() {
        var described = ReflectionBuilder.Describe(Library(out var used), used);

        Assert.Equal("1", Assert.Single(described["Bloom"].Parameters, p => p.Name == "threshold").DefaultValue);
        Assert.Equal("0.5", Assert.Single(described["Bloom"].Parameters, p => p.Name == "knee").DefaultValue);
        Assert.Equal("1", Assert.Single(described["Tonemap"].Parameters, p => p.Name == "exposure").DefaultValue);
        Assert.Equal("4", Assert.Single(described["Tonemap"].Parameters, p => p.Name == "whitePoint").DefaultValue);
    }
}
