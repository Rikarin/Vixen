// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Artefacts;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     The shipped shader library under <c>Raven/Library</c> — docs/plan/07 § F.
/// </summary>
/// <remarks>
///     <para>
///         Three claims, each of which failing means something different. Per file: it parses and
///         round-trips, which is what the golden corpus guarantees for the fixtures and now for the
///         library. Per package: it binds as one compilation, so the library is internally
///         consistent rather than a set of files that each happen to compile. And end to end: a
///         shader compiles against the library through <c>.rvnlib</c> references and both reference
///         tools accept the result.
///     </para>
///     <para>
///         The third is the one worth having. It is the first exercise of cross-package
///         <c>.rvnlib</c> reference resolution on real content rather than on a fixture, and it
///         covers two properties that only show up at this scale: a function reached through
///         several independent references keeps one identity, and a library the shader barely
///         touches does not enlarge it.
///     </para>
/// </remarks>
public class LibraryTreeTests {
    /// <summary>The library's own packages, excluding the two example files at the root.</summary>
    /// <remarks>
    ///     The examples have their own contracts in <see cref="LibraryExampleTests" /> — one binds
    ///     but deliberately does not lower — so folding them in here would mean weakening what this
    ///     asserts about the library proper.
    /// </remarks>
    static readonly string[] Packages = ["Core", "Shading", "Geometry", "Material", "Pipeline", "Ui", "PostFx", "Vfx"];

    /// <summary>
    ///     The packages that ship as <c>.rvnlib</c> references.
    /// </summary>
    /// <remarks>
    ///     Not the whole tree, and the split is structural rather than a policy choice.
    ///     <c>Core</c> and <c>Shading</c> are field-less structs of static functions, so nothing in
    ///     them touches a binding and everything exports. <c>Material</c> is <em>shaders</em> — a
    ///     feature has material parameters, which are bindings — and <c>RVN5001</c> correctly refuses
    ///     to export a function that reads one, because a binding belongs to the shader that declares
    ///     it. A material feature is consumed by being compiled alongside its consumer and resolved
    ///     through a <c>compose</c> slot, which is what makes the binding the consumer's.
    /// </remarks>
    static readonly string[] ExportedPackages = ["Core", "Shading", "Geometry"];

    public static TheoryData<string> LibraryFiles() {
        var data = new TheoryData<string>();
        foreach (var file in Files()) {
            data.Add(Path.GetFileName(Path.GetDirectoryName(file)) + "/" + Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LibraryFiles))]
    public void EveryLibraryFileParsesAndRoundTrips(string relative) {
        var path = Path.Combine(LibraryRoot, relative);
        var source = File.ReadAllText(path);

        var tree = SyntaxTree.ParseText(source, path: relative);
        Assert.True(
            tree.Diagnostics.Count == 0,
            $"{relative} does not parse:\n" + string.Join("\n", tree.Diagnostics.Select(d => d.ToString()))
        );

        Assert.Equal(source, tree.GetRoot().ToFullString());
    }

    /// <summary>
    ///     The whole library binds as one compilation.
    /// </summary>
    /// <remarks>
    ///     One compilation over every file rather than one per file, because the files depend on each
    ///     other: <c>Shading/Brdf.rvn</c> imports <c>Vixen.Shaders.Core</c>, so binding it alone
    ///     would fail on a name that is not missing. This is also the stronger claim — that the
    ///     library agrees with itself.
    /// </remarks>
    [Fact]
    public void TheWholeLibraryBindsAsOneCompilation() {
        var trees = Files()
            .Select(file => SyntaxTree.ParseText(File.ReadAllText(file), path: Path.GetFileName(file)))
            .ToArray();

        // A pipeline shader is a template, so its compose slot must be filled for the tree to bind:
        // an unbound slot is RVN2073 by design, not a defect in the library.
        var compilation = Compilation.Create(
            "Library",
            PermutationValues.Empty,
            ComposeBindings.Create([new("surface", "MetalRoughnessSurface")]),
            trees
        );

        var diagnostics = compilation.GetDiagnostics();

        Assert.True(
            diagnostics.Count == 0,
            "The library does not bind cleanly:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );
    }

    /// <summary>
    ///     Every free-function package compiles to a <c>.rvnlib</c>, which is what § F ships.
    /// </summary>
    /// <remarks>
    ///     See <see cref="ExportedPackages" /> for why this is not the whole tree. "The package
    ///     exports cleanly" and "the package is written as free functions" are the same statement,
    ///     which is why the split is worth naming rather than working around.
    /// </remarks>
    [Fact]
    public void EveryExportedLibraryFileExportsToARvnlib() {
        foreach (var file in ExportedFiles()) {
            var diagnostics = new DiagnosticBag();
            var library = BuildLibrary(Path.GetFileNameWithoutExtension(file), diagnostics);

            var errors = diagnostics.ToArray().Where(d => d.IsError).ToArray();
            Assert.True(
                errors.Length == 0,
                $"{Path.GetFileName(file)} does not export:\n" + string.Join("\n", errors.Select(d => d.ToString()))
            );

            Assert.NotEmpty(library.Types);
        }
    }

    /// <summary>
    ///     A shader compiles against the library through references, and both reference tools accept
    ///     what comes out.
    /// </summary>
    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void AShaderCompilesAgainstTheLibraryThroughReferences(string target) {
        var generated = GenerateConsumer(target);

        Assert.Equal([ShaderStage.Vertex, ShaderStage.Pixel], generated.Select(unit => unit.Stage));

        if (target == "spirv") {
            Assert.All(generated, SpirvTestBase.Validate);
        }
    }

    /// <summary>
    ///     A function reached through several independent references is emitted once.
    /// </summary>
    /// <remarks>
    ///     <c>Math.SafeNormalize</c> arrives three ways here: directly from <c>Math.rvnlib</c>, and
    ///     inside both <c>Brdf.rvnlib</c> and <c>ColorSpaces.rvnlib</c>, each of which was compiled
    ///     against its own copy of Math. One shared IR decoder across all references is what makes
    ///     those the same entity; a decoder per library would have produced three private copies
    ///     that the verifier accepts and that inflate every module.
    /// </remarks>
    [Fact]
    public void AFunctionReachedThroughSeveralReferencesIsEmittedOnce() {
        var pixel = Assert.Single(GenerateConsumer("glsl"), unit => unit.Stage == ShaderStage.Pixel).Code;

        var definitions = pixel
            .Split('\n')
            .Count(line => line.StartsWith("vec3 SafeNormalize(", StringComparison.Ordinal) && line.EndsWith('{'));

        Assert.Equal(1, definitions);
    }

    /// <summary>
    ///     Referencing a library does not enlarge the shader that uses part of it.
    /// </summary>
    /// <remarks>
    ///     The three referenced libraries hold well over sixty functions between them and the shader
    ///     reaches about a dozen. Asserted by naming functions that must be absent rather than by
    ///     counting, so the test says which claim it is making and does not need editing every time
    ///     the library grows.
    /// </remarks>
    [Fact]
    public void UnreachedLibraryFunctionsAreNotEmitted() {
        var pixel = Assert.Single(GenerateConsumer("glsl"), unit => unit.Stage == ShaderStage.Pixel).Code;

        foreach (var absent in (string[])[
            "Halton", "ConcentricDisk", "RadicalInverseBase2", "ImportanceSampleGgx",
            "OrenNayar", "Burley", "DistributionGgxAnisotropic",
            "AcesFilmic", "AgXSigmoid", "LinearToPq", "RgbToYCoCg",
            "EncodeOctahedral", "DecodeOctahedral", "DirectionToEquirectangular"
        ]) {
            Assert.DoesNotContain(absent, pixel, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     A referenced library's function lowers to what compiling its source alongside would have
    ///     produced.
    /// </summary>
    /// <remarks>
    ///     The property that makes a reference a reference rather than a second compiler: reading
    ///     <c>Brdf.SpecularGgx</c> out of a <c>.rvnlib</c> and compiling <c>Brdf.rvn</c> as an input
    ///     have to agree, or a library is a source of divergence between a developer build and a
    ///     shipped one. Compared per function, because pruning legitimately makes the referenced
    ///     module the smaller of the two.
    /// </remarks>
    [Fact]
    public void AReferencedLibraryFunctionLowersToWhatItsSourceDid() {
        var referenced = LowerConsumer(References());

        // The exported packages only, which is the scope the reference path covers. Pulling Pipeline
        // in would mean binding its compose slot, and a template shader is not what this compares.
        var trees = ExportedFiles()
            .Select(file => SyntaxTree.ParseText(File.ReadAllText(file), path: Path.GetFileName(file)))
            .Append(SyntaxTree.ParseText(Consumer, path: "Consumer.rvn"))
            .ToArray();

        var bag = new DiagnosticBag();
        var together = Lowerer.Lower(Compilation.Create("Together", trees), bag);
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));

        foreach (var name in (string[])["VisibilitySmithGgx", "DistributionGgx", "SafeNormalize"]) {
            var fromReference = FindFunction(referenced, name);
            var fromSource = FindFunction(together, name);
            Assert.Equal(IrPrinter.Print(fromSource), IrPrinter.Print(fromReference));
        }
    }

    /// <summary>
    ///     Every shipped shader with an entry point reaches both backends, and the reference tools
    ///     accept all of it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The strongest claim in this file, and the one § F exists to be able to make: every
    ///         pipeline pass, UI shader, post-process effect and particle shader in the library
    ///         compiles the whole way down, and <c>glslc</c> and <c>spirv-val</c> agree it is valid.
    ///         Anything less is a library that parses.
    ///     </para>
    ///     <para>
    ///         One compilation over the whole tree rather than per file, because that is how it ships
    ///         and because it means a shader cannot pass by being compiled without the packages it
    ///         imports. The default variant of each permutation is what gets generated — every
    ///         combination would be thousands of modules, which is the effect cache's job, not a
    ///         unit test's.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryShippedShaderReachesBothBackends() {
        var module = LowerTree();

        // Sanity: the tree really does contain the passes, so a refactor that stopped finding files
        // cannot make this pass by generating nothing.
        var names = module.Shaders.Select(shader => shader.Name).ToArray();
        foreach (var expected in (string[])["ForwardPlus", "Deferred", "DepthOnly", "ShadowCaster", "UiQuad", "Tonemap", "ParticleBillboard"]) {
            Assert.Contains(expected, names);
        }

        var entryPoints = module.Shaders.Sum(shader => shader.EntryPoints.Count);
        Assert.True(entryPoints >= 20, $"Only {entryPoints} entry points across the library.");

        foreach (var target in (string[])["glsl", "spirv"]) {
            var bag = new DiagnosticBag();
            var generated = TargetBackends.Create(target)!.Generate(module, bag);

            var errors = bag.ToArray().Where(d => d.IsError).ToArray();
            Assert.True(
                errors.Length == 0,
                $"The library does not reach {target}:\n" + string.Join("\n", errors.Select(d => d.ToString()))
            );

            Assert.Equal(entryPoints, generated.Count);

            if (target == "spirv") {
                Assert.All(generated, SpirvTestBase.Validate);
            } else {
                AssertGlslCompiles(generated);
            }
        }
    }

    /// <summary>
    ///     Lowers the whole tree with a default material bound, verifying the IR.
    /// </summary>
    static IrModule LowerTree() {
        var trees = Files()
            .Select(file => SyntaxTree.ParseText(File.ReadAllText(file), path: Path.GetFileName(file)))
            .ToArray();

        var compilation = Compilation.Create(
            "Library",
            PermutationValues.Empty,
            ComposeBindings.Create([new("surface", "MetalRoughnessSurface")]),
            trees
        );

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(d => d.ToString())));

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.True(
            IrVerifier.Verify(module, bag),
            "The library's IR does not verify:\n" + string.Join("\n", bag.Select(d => d.ToString()))
        );

        var errors = bag.ToArray().Where(d => d.IsError).ToArray();
        Assert.True(
            errors.Length == 0,
            "The library does not lower cleanly:\n" + string.Join("\n", errors.Select(d => d.ToString()))
        );

        return module;
    }

    /// <summary>Runs every emitted GLSL unit through <c>glslc</c>.</summary>
    static void AssertGlslCompiles(IReadOnlyList<GeneratedSource> generated) {
        Assert.SkipUnless(ReferenceCompiler.Available, "glslc is not on PATH (brew install shaderc).");

        foreach (var unit in generated) {
            Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
        }
    }

    /// <summary>
    ///     Every material feature composes into a forward shader and reaches both backends.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The end-to-end claim § F is for: a pipeline shader written once against
    ///         <c>IMaterialSurface</c> and instantiated per material, with the feature's own
    ///         parameters becoming the effect's descriptors. Run per feature rather than once,
    ///         because each contributes a different interface and a merge that worked for one shape
    ///         could fail for another — <c>NormalMapSurface</c> writes only the normal,
    ///         <c>OcclusionSurface</c> reads what a previous feature left.
    ///     </para>
    ///     <para>
    ///         This is what could not be emitted before <c>compose</c> learned to carry a feature's
    ///         bindings; see <see cref="ComposeInterfaceTests" /> for the defect itself.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("MetalRoughnessSurface")]
    [InlineData("SpecularGlossinessSurface")]
    [InlineData("NormalMapSurface")]
    [InlineData("EmissiveSurface")]
    [InlineData("OcclusionSurface")]
    public void EveryMaterialFeatureComposesAndReachesBothBackends(string feature) {
        var module = LowerMaterial(feature);

        // The contract really is by-reference all the way to the IR, which is why `inout` exists.
        var compute = module.Shaders
            .SelectMany(shader => shader.Functions)
            .First(function => function.Name.EndsWith("Compute", StringComparison.Ordinal));

        Assert.True(compute.Parameters[^1].IsByReference);

        // The feature's own parameters are now the effect's, qualified by the feature that declares
        // them so two features with a `strength` stay distinguishable.
        var forward = module.Shaders.Single(shader => shader.Name == "Forward");
        Assert.Contains(forward.Bindings, binding => binding.Name.StartsWith(feature + ".", StringComparison.Ordinal));

        foreach (var target in (string[])["glsl", "spirv"]) {
            var bag = new DiagnosticBag();
            var generated = TargetBackends.Create(target)!.Generate(module, bag);

            var errors = bag.ToArray().Where(d => d.IsError).ToArray();
            Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

            if (target == "spirv") {
                Assert.All(generated.Where(unit => unit.Name.StartsWith("Forward", StringComparison.Ordinal)), SpirvTestBase.Validate);
            }
        }
    }

    static IrModule LowerMaterial(string feature) {
        var trees = Files()
            .Select(file => SyntaxTree.ParseText(File.ReadAllText(file), path: Path.GetFileName(file)))
            .Append(SyntaxTree.ParseText(MaterialConsumer, path: "Forward.rvn"))
            .ToArray();

        var compilation = Compilation.Create(
            "Material",
            PermutationValues.Empty,
            ComposeBindings.Create([new("surface", feature)]),
            trees
        );

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(
            diagnostics.Count == 0,
            "The material contract does not bind:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.True(
            IrVerifier.Verify(module, bag),
            "The composed module does not verify:\n" + string.Join("\n", bag.Select(d => d.ToString()))
        );

        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));
        return module;
    }

    /// <summary>A forward shader written once against the material contract.</summary>
    const string MaterialConsumer = """
                                    package Vixen.Shaders.Test

                                    import Vixen.Shaders.Core
                                    import Vixen.Shaders.Shading
                                    import Vixen.Shaders.Material

                                    shader Forward {
                                        compose val surface: IMaterialSurface

                                        var lightDirection: float3

                                        [PixelShader]
                                        [Semantic("SV_Target")]
                                        func Pixel(): float4 {
                                            var d: MaterialData
                                            MaterialDefaults.Reset(d)
                                            surface.Compute(d)

                                            val n = Math.SafeNormalize(d.normalWS)
                                            val l = Math.SafeNormalize(-lightDirection)
                                            val NdotL = saturate(dot(n, l))
                                            val diff = DiffuseModels.Lambert(d.diffuseColor) * d.occlusion
                                            return float4(diff * NdotL + d.emissive, d.alpha)
                                        }
                                    }

                                    """;

    // --- Plumbing ---------------------------------------------------------

    static string LibraryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library"));

    static IEnumerable<string> Files() => In(Packages);

    static IEnumerable<string> ExportedFiles() => In(ExportedPackages);

    static IEnumerable<string> In(string[] packages) =>
        packages
            .SelectMany(package => Directory.EnumerateFiles(Path.Combine(LibraryRoot, package), "*.rvn"))
            .OrderBy(file => file, StringComparer.Ordinal);

    /// <summary>
    ///     A shader that reaches into all three referenced libraries, including through a stream so
    ///     the interstage path is exercised alongside the reference path.
    /// </summary>
    const string Consumer = """
                            package Vixen.Shaders.Test

                            import Vixen.Shaders.Core
                            import Vixen.Shaders.Shading

                            shader Lit {
                                var world: mat4
                                var baseColor: float3
                                var roughness: float
                                var metalness: float
                                var lightDirection: float3

                                stream var normalWS: float3

                                [VertexShader]
                                [Semantic("SV_Position")]
                                func Vertex(position: float3, normal: float3): float4 {
                                    normalWS = Math.TransformNormalRigid(world, normal)
                                    return world * float4(position, 1f)
                                }

                                [PixelShader]
                                [Semantic("SV_Target")]
                                func Pixel(): float4 {
                                    val n = Math.SafeNormalize(normalWS)
                                    val l = Math.SafeNormalize(-lightDirection)
                                    val v = float3(0f, 0f, 1f)
                                    val h = Math.SafeNormalize(l + v)

                                    val alpha = Brdf.Alpha(roughness)
                                    val f0 = Brdf.F0FromMetalness(baseColor, metalness, 0.04f)
                                    val angles = ShadingAngles.Of(n, v, l)

                                    val spec = SpecularModels.Ggx(f0, angles, alpha)
                                    val diff = DiffuseModels.Lambert(baseColor * (1f - metalness))
                                    val lit = (diff + spec) * angles.NdotL
                                    return float4(ColorSpaces.LinearToSrgb(lit), 1f)
                                }
                            }

                            """;

    /// <summary>
    ///     Builds a <c>.rvnlib</c> over the exported packages, named for one of them.
    /// </summary>
    /// <param name="name">
    ///     The artefact's library name. Distinct per artefact, or a consumer referencing several
    ///     reports RVN5005 and keeps only the first — which is the warning doing its job.
    /// </param>
    static CompiledLibrary BuildLibrary(string name, DiagnosticBag diagnostics) {
        // The exported packages only: LibraryBuilder exports everything in the compilation, and
        // including Material — which is shaders with bindings — would be RVN5001 for its functions
        // even while building Core's artefact.
        var trees = ExportedFiles()
            .Select(f => SyntaxTree.ParseText(File.ReadAllText(f), path: Path.GetFileName(f)))
            .ToArray();

        var compilation = Compilation.Create(name, trees);
        Assert.Empty(compilation.GetDiagnostics());

        var lowered = Lowerer.LowerWithLinks(compilation, diagnostics);
        return LibraryBuilder.Build(compilation, lowered, diagnostics);
    }

    /// <summary>
    ///     The library as three separate <c>.rvnlib</c> references, written to a temporary
    ///     directory.
    /// </summary>
    /// <remarks>
    ///     One artefact per package rather than one for the whole library, because that is how § F
    ///     ships them and because it is what makes the shared-identity claim above meaningful: Math
    ///     is inside all three.
    /// </remarks>
    static RavenReference[] References() {
        var directory = Directory.CreateTempSubdirectory("raven-library-tests").FullName;
        List<RavenReference> references = [];

        foreach (var file in ExportedFiles()) {
            var diagnostics = new DiagnosticBag();
            var library = BuildLibrary(Path.GetFileNameWithoutExtension(file), diagnostics);
            Assert.DoesNotContain(diagnostics.ToArray(), d => d.IsError);

            var path = Path.Combine(directory, Path.GetFileNameWithoutExtension(file) + CompiledLibraryFormat.Extension);
            CompiledLibraryWriter.WriteFile(path, library);
            references.Add(RavenReference.FromFile(path));
        }

        return [.. references];
    }

    static IrModule LowerConsumer(RavenReference[] references) {
        var tree = SyntaxTree.ParseText(Consumer, path: "Consumer.rvn");
        var compilation = Compilation.Create("Consumer", references, [tree]);

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(
            diagnostics.Count == 0,
            "The consumer does not bind against the library:\n"
            + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.True(
            IrVerifier.Verify(module, bag),
            "The linked module does not verify:\n" + string.Join("\n", bag.Select(d => d.ToString()))
        );

        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));
        return module;
    }

    static IReadOnlyList<GeneratedSource> GenerateConsumer(string target) {
        var module = LowerConsumer(References());
        var bag = new DiagnosticBag();
        var generated = TargetBackends.Create(target)!.Generate(module, bag);

        var errors = bag.ToArray().Where(d => d.IsError).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

        return generated;
    }

    static IrFunction FindFunction(IrModule module, string name) {
        var matches = module.Shaders
            .SelectMany(shader => shader.Functions)
            .Concat(module.Functions)
            .Where(function => function.Name.EndsWith(name, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(matches);
        return matches[0];
    }
}
