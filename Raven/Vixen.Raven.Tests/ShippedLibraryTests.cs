// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Artefacts;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     The library files Vixen ships, compiled from the copies on disk rather than from a string.
/// </summary>
/// <remarks>
///     <para>
///         Nothing else in the tree compiles <c>Raven/Library</c>. Every other test here builds its
///         subject out of an inline source string, which is exactly right for testing the compiler and
///         exactly wrong for testing the library: a shipped shader that stopped parsing would be found
///         by whoever next ran the CLI by hand, which is not a gate.
///     </para>
///     <para>
///         The files are linked into the test output rather than found by walking up from
///         <see cref="AppContext.BaseDirectory" />, so the test breaks loudly when a file is renamed
///         instead of quietly passing over nothing.
///     </para>
/// </remarks>
public class ShippedLibraryTests {
    /// <summary>
    ///     The distance-field module of <c>docs/plan/19</c>, which is the shader half of
    ///     <c>DistanceFieldTracer</c> and had no gate at all before this.
    /// </summary>
    /// <remarks>
    ///     Bound and lowered rather than built into a library, and that is not a weaker check — it is
    ///     the only one available. <c>GlobalDistanceField</c> reads its own bindings, and the compiler
    ///     refuses to export such a function (RVN5001) because a binding belongs to the shader that
    ///     declares it. The shader is composed into a pass through <c>IDistanceFieldSource</c>, so it
    ///     has no entry point of its own and nothing to generate until something fills the slot.
    /// </remarks>
    [Fact]
    public void TheDistanceFieldModuleBindsAndLowersAgainstCore() {
        var core = Build("Math", Read("Math.rvn"));
        var tree = SyntaxTree.ParseText(Read("DistanceField.rvn"), path: "DistanceField.rvn");

        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("DistanceField", [RavenReference.FromLibrary(core)], [tree]);

        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.IsError);

        var bag = new DiagnosticBag();
        Lowerer.LowerWithLinks(compilation, bag);

        Assert.DoesNotContain(bag.ToArray(), diagnostic => diagnostic.IsError);
    }

    /// <summary>
    ///     What the module is <i>for</i>, named so a rename has to notice. These are the entry points
    ///     <c>GlobalDistanceFieldTexture</c>'s parameter names are a contract with.
    /// </summary>
    [Fact]
    public void TheModuleExportsTheRoutinesTheRendererExpects() {
        var source = Read("DistanceField.rvn");

        Assert.Contains("struct DistanceFieldVolume", source, StringComparison.Ordinal);
        Assert.Contains("var inverseCellSize: float", source, StringComparison.Ordinal);
        Assert.Contains("var maxDistance: float", source, StringComparison.Ordinal);

        foreach (var routine in (string[]) ["Sample", "Gradient", "Trace", "Shadow", "Occlusion"]) {
            Assert.Contains($"static func {routine}(", source, StringComparison.Ordinal);
        }

        // And the clipmap side, whose level count is a permutation rather than a uniform: that is
        // what unrolls the search so every texture index is a literal, which is what keeps
        // multi-level tracing off descriptor indexing and therefore on every target.
        Assert.Contains("protocol IDistanceFieldSource", source, StringComparison.Ordinal);
        Assert.Contains("shader GlobalDistanceField : IDistanceFieldSource", source, StringComparison.Ordinal);
        Assert.Contains("[Permutation] val LevelCount: int", source, StringComparison.Ordinal);
        Assert.Contains("var distanceFieldLevels: Texture3D[LevelCount]", source, StringComparison.Ordinal);

        foreach (var routine in (string[]) ["SampleField", "GradientField", "TraceField", "ShadowField", "OcclusionField"]) {
            Assert.Contains($"func {routine}(", source, StringComparison.Ordinal);
        }
    }

    static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Library", name));

    static CompiledLibrary Build(string name, string source, params RavenReference[] references) {
        var library = BuildWithDiagnostics(name, source, out var diagnostics, references);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.IsError);

        return library;
    }

    static CompiledLibrary BuildWithDiagnostics(
        string name,
        string source,
        out IReadOnlyList<Diagnostic> diagnostics,
        params RavenReference[] references
    ) {
        var tree = SyntaxTree.ParseText(source, path: name + ".rvn");

        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create(name, references, [tree]);

        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.IsError);

        var bag = new DiagnosticBag();
        var lowered = Lowerer.LowerWithLinks(compilation, bag);
        var library = LibraryBuilder.Build(compilation, lowered, bag);

        diagnostics = bag.ToArray();

        return library;
    }
}
