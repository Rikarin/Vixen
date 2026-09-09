// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
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
        var generated = Generate(source, out var lowering, out diagnostics, target);

        Assert.True(
            lowering.Count == 0,
            "Expected clean lowering, got:\n" + string.Join("\n", lowering.Select(d => d.ToString()))
        );

        return generated;
    }

    /// <summary>
    ///     The same pipeline, with what lowering said handed back rather than asserted away.
    /// </summary>
    /// <param name="source">The shader.</param>
    /// <param name="lowering">What lowering and verification reported.</param>
    /// <param name="diagnostics">What the backend reported on top of that.</param>
    /// <param name="target">Which backend to run.</param>
    /// <remarks>
    ///     For the handful of tests whose subject <em>is</em> a construct lowering warns about — a
    ///     derivative-implied sample outside a fragment stage is the case, RVN3013 — where asserting
    ///     the code and asserting the warning are the same test rather than two.
    /// </remarks>
    public static IReadOnlyList<GeneratedSource> Generate(
        string source,
        out IReadOnlyList<Diagnostic> lowering,
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
        lowering = bag.ToArray();

        var backend = TargetBackends.Create(target);
        Assert.NotNull(backend);

        var generated = backend.Generate(module, bag);
        diagnostics = bag.Skip(lowering.Count).ToArray();
        return generated;
    }

    /// <summary>
    ///     Runs a backend over a module holding an unsized array, which no source can now produce.
    /// </summary>
    /// <remarks>
    ///     <c>RVN2126</c> refuses the declaration, so the only route to an unsized array left is a
    ///     <c>.rvnlib</c> decoded straight into the IR. Both backends still have to refuse it rather
    ///     than emit something neither language has, and this is how that stays tested: the module
    ///     is built by hand, exactly as such a library would arrive.
    /// </remarks>
    public static IReadOnlyList<Diagnostic> UnsizedArrayDiagnostics(string target) {
        var shader = new IrShader("S");
        var lookup = new IrVariable("lookup", new IrArrayType(IrScalarType.Int), IrVariableKind.Global);
        shader.Add(new IrBinding(lookup, IrBindingKind.Uniform, 0, null));

        var function = new IrFunction("Fragment", new IrVectorType(IrScalarType.Float, 4));
        shader.Add(function);
        shader.Add(new IrEntryPoint(ShaderStage.Fragment, function, [], [new("result", function.ReturnType, null)]));

        var module = new IrModule("Test");
        module.Add(shader);

        var bag = new DiagnosticBag();
        TargetBackends.Create(target)!.Generate(module, bag);
        return bag.ToArray();
    }

    /// <summary>Generates, asserts the backend reported no errors, and refuses an empty result.</summary>
    /// <param name="source">The shader.</param>
    /// <param name="target">Which backend to run.</param>
    /// <returns>At least one <see cref="GeneratedSource" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The count is asserted here because a dozen callers only assert inside a
    ///         <c>foreach</c> over what comes back.</b> Every such loop reports a pass on the day the
    ///         backend emitted nothing at all — it compiled nothing, validated nothing, and said a
    ///         reference front end had accepted it. That is the shape <c>#828</c> fixed in the golden
    ///         suites and this is the same shape in the rest of the code-gen tests.
    ///     </para>
    ///     <para>
    ///         Deliberately <em>not</em> in <see cref="Generate(string, out IReadOnlyList{Diagnostic}, string)" />,
    ///         for two reasons. A caller of <c>Generate</c> may be asserting a <em>refusal</em>, where
    ///         zero units is the correct answer; and keeping the refusal one layer above the compile
    ///         is what lets a sabotage of the backend prove that the refusal fires rather than
    ///         tripping over itself.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<GeneratedSource> GenerateClean(string source, string target = "glsl") {
        var generated = Generate(source, out var diagnostics, target);

        var errors = diagnostics.Where(d => d.IsError).ToArray();
        Assert.True(
            errors.Length == 0,
            "Expected no code generation errors, got:\n" + string.Join("\n", errors.Select(d => d.ToString()))
        );

        Assert.True(generated.Count > 0, EmptyResult(target));

        return generated;
    }

    /// <summary>The message a generation that produced nothing fails with.</summary>
    /// <param name="target">Which backend was asked.</param>
    /// <returns>Why an empty result is the failure rather than a vacuous pass.</returns>
    public static string EmptyResult(string target) =>
        $"The '{target}' backend reported no errors and generated no units. A loop over the result "
        + "asserts nothing and the test reports a pass, so an empty result is the failure this "
        + "assertion exists to name.";

    /// <summary>The single generated unit, for shaders with one entry point.</summary>
    public static string GenerateOne(string source, string target = "glsl") =>
        Assert.Single(GenerateClean(source, target)).Code;

    /// <summary>Wraps a body in a fragment shader and returns the generated GLSL.</summary>
    public static string GeneratePixel(string body, string members = "", string signature = "func Fragment(): float4") =>
        GenerateOne(
            $$"""
              package A

              shader S {
              {{members}}
                  [FragmentShader]
                  {{signature}} {
              {{body}}
                  }
              }

              """
        );
}
