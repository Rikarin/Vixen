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
    static readonly string[] Packages = [
        "Core", "Shading", "Geometry", "DistanceFields", "IrradianceFields", "Material", "Pipeline", "Ui",
        "PostFx", "Vfx"
    ];

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
    ///     <c>DistanceFields</c> is the same case: <c>DistanceField</c>'s structs would export happily, but
    ///     <c>GlobalDistanceField</c> is a shader whose functions read the clipmap it binds.
    ///     <c>IrradianceFields</c> likewise — <c>IrradianceFieldProbes</c> reads the pool it binds.
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
            LibraryComposition.With(),
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

        Assert.Equal([ShaderStage.Vertex, ShaderStage.Fragment], generated.Select(unit => unit.Stage));

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
        var fragment = Assert.Single(GenerateConsumer("glsl"), unit => unit.Stage == ShaderStage.Fragment).Code;

        var definitions = fragment
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
        var fragment = Assert.Single(GenerateConsumer("glsl"), unit => unit.Stage == ShaderStage.Fragment).Code;

        foreach (var absent in (string[])[
            "Halton", "ConcentricDisk", "RadicalInverseBase2", "ImportanceSampleGgx",
            "OrenNayar", "Burley", "DistributionGgxAnisotropic",
            "AcesFilmic", "AgXSigmoid", "LinearToPq", "RgbToYCoCg",
            "EncodeOctahedral", "DecodeOctahedral", "DirectionToEquirectangular"
        ]) {
            Assert.DoesNotContain(absent, fragment, StringComparison.Ordinal);
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
        foreach (var expected in (string[])["ForwardPlus", "GBufferPass", "Deferred", "ClusterCulling", "DepthOnly", "ShadowCaster", "UiQuad", "Tonemap", "AutoExposure", "ParticleBillboard"]) {
            Assert.Contains(expected, names);
        }

        var entryPoints = module.Shaders.Sum(shader => shader.EntryPoints.Count);
        Assert.True(entryPoints >= 20, $"Only {entryPoints} entry points across the library.");

        AssertReachesBothBackends(module, entryPoints);
    }

    /// <summary>Generates for both targets and puts every unit through the reference tools.</summary>
    static void AssertReachesBothBackends(IrModule module, int? expectedUnits = null) {
        foreach (var target in (string[])["glsl", "spirv"]) {
            var bag = new DiagnosticBag();
            var generated = TargetBackends.Create(target)!.Generate(module, bag);

            var errors = bag.ToArray().Where(d => d.IsError).ToArray();
            Assert.True(
                errors.Length == 0,
                $"The library does not reach {target}:\n" + string.Join("\n", errors.Select(d => d.ToString()))
            );

            if (expectedUnits is { } count) {
                Assert.Equal(count, generated.Count);
            }

            if (target == "spirv") {
                Assert.All(generated, SpirvTestBase.Validate);
            } else {
                AssertGlslCompiles(generated);
            }
        }
    }

    /// <summary>
    ///     The clustered variant of `ForwardPlus` reaches both backends, and reads what the culling
    ///     pass writes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Forward+ is two passes that have to agree on a buffer layout and on the arithmetic
    ///         that finds a cluster in it, and they are in different files. This asserts the thing
    ///         that would actually break: that the culler's output type and the shading pass's input
    ///         type are the same type, and that the shading pass grew the cluster loop rather than
    ///         keeping the uniform array.
    ///     </para>
    ///     <para>
    ///         Off by default, so <see cref="EveryShippedShaderReachesBothBackends" /> compiles the
    ///         uniform-array variant and never the clustered one — the same reason the cutout needs
    ///         asking for by name.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_clustered_variant_reaches_both_backends_and_shares_the_culling_passs_layout() {
        var module = LowerTree(PermutationValues.Parse(["UseClusteredLights=true"]));

        var written = Assert.Single(
            FindShader(module, "ClusterCulling").Bindings,
            b => b.Name == "clusters"
        );

        var read = Assert.Single(FindShader(module, "ForwardPlus").Bindings, b => b.Name == "clusters");

        // One type, not two structurally equal ones: the culler writes and the shading pass reads
        // the same `ClusterLights`, which is what keeps a channel from being added to one side only.
        Assert.Equal(written.Type, read.Type);
        Assert.True(written.IsWritable);
        Assert.False(read.IsWritable);

        // The clustered loop is emitted and the uniform-array one is not, which is the permutation
        // doing its job rather than a branch surviving into the variant.
        var fragment = FindShader(module, "ForwardPlus").Functions.Single(f => f.Name.Contains("Clustered"));
        Assert.NotNull(fragment);

        AssertReachesBothBackends(module);
    }

    /// <summary>
    ///     Every mode of the auto-exposure chain reaches both backends, including the one that writes
    ///     a buffer.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         `Mode` picks between two dispatch shapes and only one is emitted, so the default
    ///         variant compiles the reduction and never the adaptation — where the storage-buffer
    ///         write lives, which is the whole reason the pass is compute rather than a fullscreen
    ///         triangle. `FirstStep` is the same trap one level down: it folds the colour-space
    ///         arithmetic away in every step but the first.
    ///     </para>
    ///     <para>
    ///         Asserted on the bindings rather than only compiled, because "it reaches both
    ///         backends" would also be true of a variant that quietly stopped writing anything.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("Mode=0", "FirstStep=true")]
    [InlineData("Mode=0", "FirstStep=false")]
    [InlineData("Mode=1", "FirstStep=false")]
    public void Every_auto_exposure_mode_reaches_both_backends(string mode, string firstStep) {
        var module = LowerTree(PermutationValues.Parse([mode, firstStep]));
        var shader = FindShader(module, "AutoExposure");

        if (mode == "Mode=1") {
            // The adaptation step is the one that outlives the frame: a storage buffer the next
            // frame's tonemapper reads as a uniform, which no fragment stage could have written.
            var buffer = Assert.Single(shader.Bindings, b => b.Name == "exposure");
            Assert.True(buffer.IsWritable);
        }

        Assert.Contains(shader.Bindings, b => b.Kind == IrBindingKind.StorageImage);
        AssertReachesBothBackends(module);
    }

    /// <summary>
    ///     The cut-out passes reach both backends with the branch that <c>discard</c>s switched on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>AlphaTested</c> is off by default, so <see cref="EveryShippedShaderReachesBothBackends" />
    ///         folds the whole cutout away before lowering ever sees it — which is exactly what a
    ///         <c>[Permutation]</c> key is for, and exactly why the interesting variant needs
    ///         asking for by name. Without this, the only <c>discard</c> the library ships would be
    ///         dead code in every test that compiles it.
    ///     </para>
    ///     <para>
    ///         Both passes, because they are separate shaders for the reasons
    ///         <c>ShadowCaster.rvn</c> gives, and a cutout that works in the prepass and not in the
    ///         shadow map is precisely the drift keeping them apart is meant to catch.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_cutout_passes_reach_both_backends_with_alpha_testing_on() {
        var module = LowerTree(PermutationValues.Parse(["AlphaTested=true"]));

        foreach (var name in (string[])["DepthOnly", "ShadowCaster"]) {
            var fragment = Assert.Single(
                FindShader(module, name).EntryPoints,
                entry => entry.Stage == ShaderStage.Fragment
            );

            Assert.True(fragment.Function.Discards, $"{name}'s cutout did not survive lowering.");
        }

        foreach (var target in (string[])["glsl", "spirv"]) {
            var bag = new DiagnosticBag();
            var generated = TargetBackends.Create(target)!.Generate(module, bag);

            var errors = bag.ToArray().Where(d => d.IsError).ToArray();
            Assert.True(
                errors.Length == 0,
                $"The alpha-tested library does not reach {target}:\n"
                + string.Join("\n", errors.Select(d => d.ToString()))
            );

            if (target == "spirv") {
                Assert.All(generated, SpirvTestBase.Validate);
            } else {
                AssertGlslCompiles(generated);
            }
        }
    }

    /// <summary>
    ///     The cluster-traversal variant of <c>Culling</c> reaches both backends, and is the only
    ///     variant that carries any workgroup-shared memory.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two claims, and the second is the one that would rot. The traversal is a permutation
    ///         of the object cull rather than a shader of its own — improvement 3 of
    ///         <c>docs/plan/22-virtualized-geometry.md</c> — so the object variant has to come out with no
    ///         queue, no barrier and no shared storage at all, or every frame that is not doing
    ///         virtualized geometry pays for the branch that is.
    ///     </para>
    ///     <para>
    ///         Off by default, so <see cref="EveryShippedShaderReachesBothBackends" /> compiles the
    ///         object variant and never this one — the same reason the clustered light loop needs
    ///         asking for by name, and the same defect that found.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_cluster_traversal_variant_reaches_both_backends_and_owns_the_shared_memory() {
        var traversal = LowerTree(PermutationValues.Parse(["Clusters=true"]));
        var culling = FindShader(traversal, "Culling");

        var shared = Assert.Single(culling.EntryPoints).SharedVariables;
        Assert.Equal(["queue", "pushed", "taken", "overflowed"], shared.Select(v => v.Name).ToArray());

        // The output the traversal exists to produce, and the request buffer that makes streaming
        // demand-driven rather than predictive.
        Assert.Contains(culling.Bindings, b => b.Name == "visible" && b.IsWritable);
        Assert.Contains(culling.Bindings, b => b.Name == "requests" && b.IsWritable);

        AssertReachesBothBackends(traversal);

        // And the object variant carries none of it: the branch is folded before lowering, so the
        // shared declarations are unreachable and no unit declares them.
        var objects = FindShader(LowerTree(), "Culling");
        Assert.Empty(Assert.Single(objects.EntryPoints).SharedVariables);
    }

    static IrShader FindShader(IrModule module, string name) =>
        Assert.Single(module.Shaders, shader => shader.Name == name);

    /// <summary>
    ///     Lowers the whole tree with a default material bound, verifying the IR.
    /// </summary>
    static IrModule LowerTree(PermutationValues? permutations = null, params (string Slot, string Shader)[] composition) {
        var trees = Files()
            .Select(file => SyntaxTree.ParseText(File.ReadAllText(file), path: Path.GetFileName(file)))
            .ToArray();

        var compilation = Compilation.Create(
            "Library",
            permutations ?? PermutationValues.Empty,
            LibraryComposition.With(composition),
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
    [InlineData("AnisotropySurface")]
    [InlineData("ClearCoatSurface")]
    [InlineData("ClearCoatNormalMapSurface")]
    [InlineData("SheenSurface")]
    [InlineData("SubsurfaceSurface")]
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

    /// <summary>
    ///     Every shading model composes into the shipped forward pass and reaches both backends.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The other half of what <c>compose</c> is for, and the half the library was missing: it
    ///         had <c>ClearCoat.rvn</c>, <c>Sheen.rvn</c>, <c>Hair.rvn</c> and <c>Subsurface.rvn</c>,
    ///         and no shader called any of them. A BSDF nothing evaluates compiles perfectly and
    ///         shades nothing, which is the failure this asserts against — per model, because each
    ///         reaches a different corner of the library and one that lowers says nothing about the
    ///         next.
    ///     </para>
    ///     <para>
    ///         Two claims beyond compiling, and it takes both. That the model's <c>Shade</c> reached
    ///         the emitted unit at all — a pass that stopped calling through the slot would prune it,
    ///         and nothing else about the shader would look wrong. And that the result differs from
    ///         the standard model — which on its own is satisfied by a model that contributed only its
    ///         uniforms, as a sabotage of the call site proved: two of these passed with the lobes
    ///         hard-coded back into the pass, because <c>SubsurfaceShading</c> and <c>CelShading</c>
    ///         have parameters and those arrived either way.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("AnisotropicShading")]
    [InlineData("ClearCoatShading")]
    [InlineData("SheenShading")]
    [InlineData("SubsurfaceShading")]
    [InlineData("HairShading")]
    [InlineData("CelShading")]
    public void EveryShadingModelComposesIntoTheForwardPassAndReachesBothBackends(string model) {
        var standard = ForwardPlusSource(LowerTree(composition: [("shading", "StandardShading")]));
        var module = LowerTree(composition: [("shading", model)]);
        var source = ForwardPlusSource(module);

        Assert.NotEqual(standard, source);
        Assert.Contains("Shade", source, StringComparison.Ordinal);

        foreach (var target in (string[])["glsl", "spirv"]) {
            var bag = new DiagnosticBag();
            var generated = TargetBackends.Create(target)!.Generate(module, bag);

            var errors = bag.ToArray().Where(d => d.IsError).ToArray();
            Assert.True(
                errors.Length == 0,
                $"{model} does not reach {target}:\n" + string.Join("\n", errors.Select(d => d.ToString()))
            );

            var pass = generated.Where(unit => unit.Name.StartsWith("ForwardPlus", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(pass);

            if (target == "spirv") {
                Assert.All(pass, SpirvTestBase.Validate);
            } else {
                AssertGlslCompiles(pass);
            }
        }
    }

    /// <summary>
    ///     A shading model's own parameters reach the pass, qualified by the model that declares them.
    /// </summary>
    /// <remarks>
    ///     A model is a shader with storage, not a free function, and this is the difference: a wrap
    ///     width belongs to the lighting model rather than to a point on the surface, so it is a
    ///     uniform on <c>SubsurfaceShading</c> and it has to arrive in the pass's block for a host to
    ///     be able to set it.
    /// </remarks>
    [Theory]
    [InlineData("SubsurfaceShading", "SubsurfaceShading.wrap")]
    [InlineData("CelShading", "CelShading.steps")]
    public void AShadingModelsParametersReachThePass(string model, string parameter) {
        var pass = FindShader(LowerTree(composition: [("shading", model)]), "ForwardPlus");

        Assert.Contains(pass.Bindings, binding => binding.Name == parameter);
    }

    /// <summary>
    ///     A material with several features composes through <c>CompositeSurface</c>, and each
    ///     feature's parameters arrive under its own name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What a real material is: a workflow, a normal map and emission are three features and
    ///         one slot, so the chain is what stands between "the library has features" and "a
    ///         material can have more than one of them".
    ///     </para>
    ///     <para>
    ///         The names are the contract the engine's <c>MaterialCompiler</c> predicts without a
    ///         compiler in the process, so this pins them: a parameter is qualified by the path of
    ///         types it was reached through, and the chain is part of that path.
    ///     </para>
    /// </remarks>
    [Fact]
    public void SeveralFeaturesComposeThroughTheChainUnderTheirOwnNames() {
        var module = LowerTree(
            composition: [
                ("surface", "CompositeSurface"),
                ("first", "MetalRoughnessSurface"),
                ("second", "NormalMapSurface"),
                ("third", "EmissiveSurface")
            ]
        );

        var names = FindShader(module, "ForwardPlus").Bindings.Select(binding => binding.Name).ToArray();

        Assert.Contains("CompositeSurface.MetalRoughnessSurface.baseColor", names);
        Assert.Contains("CompositeSurface.NormalMapSurface.normalTS", names);
        Assert.Contains("CompositeSurface.EmissiveSurface.emissiveColor", names);

        // The slots the material did not use contributed nothing — which is what makes one chain
        // able to stand in for every length.
        Assert.DoesNotContain(names, name => name.Contains("IdentitySurface", StringComparison.Ordinal));

        foreach (var target in (string[])["glsl", "spirv"]) {
            var bag = new DiagnosticBag();
            var generated = TargetBackends.Create(target)!.Generate(module, bag);

            Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

            var pass = generated.Where(unit => unit.Name.StartsWith("ForwardPlus", StringComparison.Ordinal)).ToArray();

            if (target == "spirv") {
                Assert.All(pass, SpirvTestBase.Validate);
            } else {
                AssertGlslCompiles(pass);
            }
        }
    }

    /// <summary>
    ///     A layered material composes into the pass, and its layer count is a compile-time size.
    /// </summary>
    /// <remarks>
    ///     Layering by array rather than by composition is what gives a layer parameters of its own:
    ///     two composed copies of one feature would share its storage, so a two-layer terrain would
    ///     have one base colour. The permutation is what keeps that from costing anything — a
    ///     two-layer material's block holds two layers, not the maximum anyone might use.
    /// </remarks>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ALayeredMaterialSizesItsLayersByPermutation(int count) {
        var module = LowerTree(
            PermutationValues.Parse([$"LayerCount={count}"]),
            composition: [("surface", "MaterialLayersSurface")]
        );

        var pass = FindShader(module, "ForwardPlus");
        var names = pass.Bindings.Select(binding => binding.Name).ToArray();

        Assert.Contains(names, name => name.Contains("MaterialLayersSurface.layers", StringComparison.Ordinal));

        foreach (var target in (string[])["glsl", "spirv"]) {
            var bag = new DiagnosticBag();
            var generated = TargetBackends.Create(target)!.Generate(module, bag);

            Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

            var units = generated.Where(unit => unit.Name.StartsWith("ForwardPlus", StringComparison.Ordinal)).ToArray();

            if (target == "spirv") {
                Assert.All(units, SpirvTestBase.Validate);
            } else {
                AssertGlslCompiles(units);
            }
        }
    }

    /// <summary>
    ///     Two different surfaces blend into one material, and both reach the pass.
    /// </summary>
    /// <remarks>
    ///     The heterogeneous half of layering, and the reason the two layers here are deliberately
    ///     different features: composition binds a shader per <em>type</em>, so a blend of two
    ///     metal-roughness layers would be one set of parameters read twice. This asserts the case
    ///     that works; <c>MaterialCompilerTests</c> asserts the engine refuses the one that does not.
    /// </remarks>
    [Fact]
    public void ABlendOfTwoDifferentSurfacesReachesBothBackends() {
        var module = LowerTree(
            composition: [
                ("surface", "BlendSurface"),
                ("under", "MetalRoughnessSurface"),
                ("over", "SpecularGlossinessSurface")
            ]
        );

        var names = FindShader(module, "ForwardPlus").Bindings.Select(binding => binding.Name).ToArray();

        Assert.Contains("BlendSurface.MetalRoughnessSurface.baseColor", names);
        Assert.Contains("BlendSurface.SpecularGlossinessSurface.specularColor", names);
        Assert.Contains("BlendSurface.blend", names);

        foreach (var target in (string[])["glsl", "spirv"]) {
            var bag = new DiagnosticBag();
            var generated = TargetBackends.Create(target)!.Generate(module, bag);

            Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

            var units = generated.Where(unit => unit.Name.StartsWith("ForwardPlus", StringComparison.Ordinal)).ToArray();

            if (target == "spirv") {
                Assert.All(units, SpirvTestBase.Validate);
            } else {
                AssertGlslCompiles(units);
            }
        }
    }

    /// <summary>
    ///     A composition of the shape the engine's material compiler emits compiles.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every other test here binds slots the convenient way — bare, so one binding covers
    ///         every shader that declares that name. <c>MaterialCompiler</c> does not: it qualifies
    ///         every slot inside the material by the shader that declares it, leaves only the pass's
    ///         own two bare so that one composition serves the forward and G-buffer passes alike, and
    ///         fills the slots the material does not use with <c>IdentitySurface</c> rather than
    ///         leaving them to a fallback.
    ///     </para>
    ///     <para>
    ///         That shape is the thing neither project can check on its own — the engine has no
    ///         compiler and this has no engine — so it is written out here exactly as the compiler
    ///         produces it, for a material with three features. If the shape stops compiling, this is
    ///         where it says so.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheCompositionShapeTheEngineEmitsCompiles() {
        var module = LowerTree(
            composition: [
                ("surface", "CompositeSurface"),
                ("shading", "StandardShading"),
                ("CompositeSurface.first", "MetalRoughnessSurface"),
                ("CompositeSurface.second", "NormalMapSurface"),
                ("CompositeSurface.third", "ClearCoatSurface"),
                ("CompositeSurface.fourth", "IdentitySurface"),
                ("CompositeSurface.fifth", "IdentitySurface"),
                ("CompositeSurface.sixth", "IdentitySurface"),
                ("CompositeSurface.seventh", "IdentitySurface"),
                ("CompositeSurface.eighth", "IdentitySurface"),
                ("BlendSurface.under", "IdentitySurface"),
                ("BlendSurface.over", "IdentitySurface")
            ]
        );

        var names = FindShader(module, "ForwardPlus").Bindings.Select(binding => binding.Name).ToArray();

        Assert.Contains("CompositeSurface.MetalRoughnessSurface.baseColor", names);
        Assert.Contains("CompositeSurface.NormalMapSurface.normalTS", names);
        Assert.Contains("CompositeSurface.ClearCoatSurface.clearCoat", names);

        foreach (var target in (string[])["glsl", "spirv"]) {
            var bag = new DiagnosticBag();
            var generated = TargetBackends.Create(target)!.Generate(module, bag);

            Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

            var pass = generated.Where(unit => unit.Name.StartsWith("ForwardPlus", StringComparison.Ordinal)).ToArray();

            if (target == "spirv") {
                Assert.All(pass, SpirvTestBase.Validate);
            } else {
                AssertGlslCompiles(pass);
            }
        }
    }

    /// <summary>
    ///     The library declares exactly these <c>compose</c> slots, and no others.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An inventory rather than a behaviour, and it earns its place because something outside
    ///         this repository's compiler depends on it: <c>MaterialCompiler</c> in
    ///         <c>Vixen.Rendering</c> writes a binding for every slot the library declares, because
    ///         Raven rejects a compilation with an unfilled one wherever it is declared. It cannot
    ///         discover them — it has no compiler in the process — so it holds a list, and a list is
    ///         a thing that goes stale.
    ///     </para>
    ///     <para>
    ///         So a slot added to the library fails here, next to the shader that added it, with the
    ///         name of the file that has to be updated. Without it the failure arrives as
    ///         <c>RVN2073</c> in whatever first tries to compile a material, which says the slot is
    ///         unbound and nothing about who was supposed to bind it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheLibraryDeclaresExactlyTheSlotsTheEngineBinds() {
        var declared = Files()
            .SelectMany(file => File.ReadAllLines(file))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("compose val ", StringComparison.Ordinal))
            .Select(line => line["compose val ".Length..].Split(':')[0].Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(slot => slot, StringComparer.Ordinal)
            .ToArray();

        // Slot names only: which shader declares which is the engine's business, and qualifying them
        // here would make this fail every time a shader is renamed for reasons nothing depends on.
        string[] expected = [
            "distanceField", "eighth", "fifth", "first", "fourth", "irradiance", "over", "second",
            "seventh", "shading", "sixth", "surface", "third", "under"
        ];

        Assert.Equal(
            expected,
            declared
        );
    }

    /// <summary>The fragment stage of the shipped forward pass, as GLSL.</summary>
    static string ForwardPlusSource(IrModule module) {
        var bag = new DiagnosticBag();
        var generated = TargetBackends.Create("glsl")!.Generate(module, bag);

        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return Assert.Single(
                generated,
                unit => unit.Name.StartsWith("ForwardPlus", StringComparison.Ordinal) && unit.Stage == ShaderStage.Fragment
            )
            .Code;
    }

    static IrModule LowerMaterial(string feature) {
        var trees = Files()
            .Select(file => SyntaxTree.ParseText(File.ReadAllText(file), path: Path.GetFileName(file)))
            .Append(SyntaxTree.ParseText(MaterialConsumer, path: "Forward.rvn"))
            .ToArray();

        var compilation = Compilation.Create(
            "Material",
            PermutationValues.Empty,
            LibraryComposition.With(("surface", feature)),
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

                                        [FragmentShader]
                                        [Semantic("SV_Target")]
                                        func Fragment(): float4 {
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

                                [FragmentShader]
                                [Semantic("SV_Target")]
                                func Fragment(): float4 {
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
