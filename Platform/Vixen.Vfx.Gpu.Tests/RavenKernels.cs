// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;

namespace Vixen.Vfx.Gpu.Tests;

/// <summary>Emitted Raven source, compiled to the SPIR-V a pipeline is created from.</summary>
/// <remarks>
///     The whole front half of the pipeline in one call, because every failure in it is the same
///     failure from a test's point of view — the shader did not compile, and here is what objected.
///     The diagnostics are carried into the assertion message rather than swallowed: a
///     <c>KeyNotFoundException</c> on the kernel name is a much worse way to learn that binding
///     failed.
/// </remarks>
static class RavenKernels {
    /// <summary>Compiles a source file and returns its compute modules by declaration name.</summary>
    /// <param name="source">The emitted Raven.</param>
    /// <returns>The SPIR-V for each kernel, keyed by the shader declaration it came from.</returns>
    public static Dictionary<string, byte[]> Compile(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Effect.rvn");

        Assert.True(tree.Diagnostics.Count == 0, Report("Parsing", tree.Diagnostics, source));

        var compilation = Compilation.Create("Vfx", tree);
        var semantic = compilation.GetDiagnostics();

        Assert.True(semantic.Count == 0, Report("Binding", semantic, source));

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        IrVerifier.Verify(module, bag);
        Assert.True(bag.IsEmpty, Report("Lowering", bag.ToArray(), source));

        var backend = TargetBackends.Create("spirv");

        Assert.NotNull(backend);

        var generated = backend.Generate(module, bag);

        Assert.True(bag.IsEmpty, Report("Generating", bag.ToArray(), source));

        Dictionary<string, byte[]> kernels = [];

        foreach (var unit in generated) {
            if (unit is { Stage: ShaderStage.Compute, Binary: { } binary }) {
                // The unit's name carries the declaration it came from and may carry more — a
                // permutation suffix, for a shader that has any. The declaration is the prefix.
                kernels[unit.Name] = binary;
            }
        }

        return kernels;
    }

    /// <summary>The module for one shader declaration.</summary>
    /// <param name="kernels">What <see cref="Compile" /> returned.</param>
    /// <param name="declaration">The shader declaration's name.</param>
    public static byte[] Of(Dictionary<string, byte[]> kernels, string declaration) {
        foreach (var (name, binary) in kernels) {
            if (name.StartsWith(declaration, StringComparison.Ordinal)) {
                return binary;
            }
        }

        Assert.Fail($"No compute module was generated for '{declaration}'. Got: {string.Join(", ", kernels.Keys)}.");

        return [];
    }

    static string Report(string phase, IReadOnlyList<Diagnostic> diagnostics, string source) =>
        $"{phase} the emitted shader failed:\n{string.Join("\n", diagnostics.Select(d => d.ToString()))}\n\n{source}";
}
