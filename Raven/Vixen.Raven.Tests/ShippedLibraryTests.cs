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
    [Fact]
    public void TheDistanceFieldModuleCompilesAgainstCore() {
        var core = Build("Math", Read("Math.rvn"));
        var field = BuildWithDiagnostics(
            "DistanceField",
            Read("DistanceField.rvn"),
            out var diagnostics,
            RavenReference.FromLibrary(core)
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.IsError);
        Assert.NotNull(field);
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
