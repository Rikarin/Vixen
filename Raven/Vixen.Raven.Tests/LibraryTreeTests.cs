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
    static readonly string[] Packages = ["Core", "Shading"];

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

        var compilation = Compilation.Create("Library", trees);
        var diagnostics = compilation.GetDiagnostics();

        Assert.True(
            diagnostics.Count == 0,
            "The library does not bind cleanly:\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );
    }

    /// <summary>
    ///     Every library file compiles to a <c>.rvnlib</c>, which is what § F ships.
    /// </summary>
    /// <remarks>
    ///     A library file holds only field-less structs of static functions, so nothing in it reads a
    ///     shader binding and everything is exportable. That is not incidental: RVN5001 refuses to
    ///     export a function that touches a binding, because a binding belongs to the shader that
    ///     declares it — so "the library exports cleanly" and "the library is written as free
    ///     functions" are the same statement.
    /// </remarks>
    [Fact]
    public void EveryLibraryFileExportsToARvnlib() {
        foreach (var file in Files()) {
            var diagnostics = new DiagnosticBag();
            var library = BuildLibrary(file, diagnostics);

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
            "DiffuseOrenNayar", "DiffuseBurley", "DistributionGgxAnisotropic",
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

        var trees = Files()
            .Select(file => SyntaxTree.ParseText(File.ReadAllText(file), path: Path.GetFileName(file)))
            .Append(SyntaxTree.ParseText(Consumer, path: "Consumer.rvn"))
            .ToArray();

        var bag = new DiagnosticBag();
        var together = Lowerer.Lower(Compilation.Create("Together", trees), bag);
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));

        foreach (var name in (string[])["SpecularGgx", "VisibilitySmithGgx", "DistributionGgx", "SafeNormalize"]) {
            var fromReference = FindFunction(referenced, name);
            var fromSource = FindFunction(together, name);
            Assert.Equal(IrPrinter.Print(fromSource), IrPrinter.Print(fromReference));
        }
    }

    // --- Plumbing ---------------------------------------------------------

    static string LibraryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library"));

    static IEnumerable<string> Files() =>
        Packages
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

                                    val NdotL = saturate(dot(n, l))
                                    val NdotV = saturate(dot(n, v))
                                    val NdotH = saturate(dot(n, h))
                                    val VdotH = saturate(dot(v, h))

                                    val spec = Brdf.SpecularGgx(f0, NdotL, NdotV, NdotH, VdotH, alpha)
                                    val diff = Brdf.DiffuseLambert(baseColor * (1f - metalness))
                                    val lit = (diff + spec) * NdotL
                                    return float4(ColorSpaces.LinearToSrgb(lit), 1f)
                                }
                            }

                            """;

    static CompiledLibrary BuildLibrary(string file, DiagnosticBag diagnostics) {
        // Every library file binds against the others, so each is built inside a compilation of the
        // whole tree and the artefact is taken for the one file's own types.
        var trees = Files()
            .Select(f => SyntaxTree.ParseText(File.ReadAllText(f), path: Path.GetFileName(f)))
            .ToArray();

        var compilation = Compilation.Create(Path.GetFileNameWithoutExtension(file), trees);
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

        foreach (var file in Files()) {
            var diagnostics = new DiagnosticBag();
            var library = BuildLibrary(file, diagnostics);
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
