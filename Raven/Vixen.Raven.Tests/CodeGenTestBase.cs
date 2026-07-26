// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>Shared plumbing for the Phase 4 backend tests.</summary>
public static class CodeGenTestBase {
    /// <summary>
    ///     Runs the whole pipeline — parse, bind, lower, verify, generate — and
    ///     asserts every stage before code generation was clean.
    /// </summary>
    public static IReadOnlyList<GeneratedSource> Generate(
        string source,
        out IReadOnlyList<Diagnostic> diagnostics,
        string target = "glsl"
    ) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        var semantic = compilation.GetDiagnostics();
        Assert.True(
            semantic.Count == 0,
            "Expected no semantic diagnostics, got:\n" + string.Join("\n", semantic.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.True(
            bag.IsEmpty,
            "Expected clean lowering, got:\n" + string.Join("\n", bag.Select(d => d.ToString()))
        );

        var backend = TargetBackends.Create(target);
        Assert.NotNull(backend);

        var generated = backend.Generate(module, bag);
        diagnostics = bag.ToArray();
        return generated;
    }

    /// <summary>Generates and asserts the backend reported no errors.</summary>
    public static IReadOnlyList<GeneratedSource> GenerateClean(string source, string target = "glsl") {
        var generated = Generate(source, out var diagnostics, target);

        var errors = diagnostics.Where(d => d.IsError).ToArray();
        Assert.True(
            errors.Length == 0,
            "Expected no code generation errors, got:\n" + string.Join("\n", errors.Select(d => d.ToString()))
        );

        return generated;
    }

    /// <summary>The single generated unit, for shaders with one entry point.</summary>
    public static string GenerateOne(string source, string target = "glsl") =>
        Assert.Single(GenerateClean(source, target)).Code;

    /// <summary>Wraps a body in a pixel shader and returns the generated GLSL.</summary>
    public static string GeneratePixel(string body, string members = "", string signature = "func Pixel(): float4") =>
        GenerateOne(
            $$"""
              package A

              shader S {
              {{members}}
                  [PixelShader]
                  {{signature}} {
              {{body}}
                  }
              }

              """
        );
}
