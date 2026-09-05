// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Vixen.Editor.NodeGraph;
using Vixen.Raven;
using Vixen.Raven.Syntax;

namespace Vixen.Editor.TextureGraph;

/// <summary>One kernel written from an author's expression.</summary>
/// <param name="Kernel">The shader's name, derived from the expression so two identical ones are one.</param>
/// <param name="Source">The whole <c>.rvn</c>, ready for <c>TextureKernels.Variant</c>'s format rewrite.</param>
/// <param name="Line">Which line of it the author's expression is on, counted from zero.</param>
readonly record struct TexturePixelKernel(string Kernel, string Source, int Line);

/// <summary>One thing Raven said about a generated kernel.</summary>
/// <param name="Message">What it said, phrased for somebody looking at the expression field.</param>
/// <param name="Span">Which line of the generated kernel, so a "show generated code" pane can mark it.</param>
readonly record struct TexturePixelProblem(string Message, NodeSpan Span);

/// <summary>
///     Doc 48 § D6: the escape hatch is a Raven expression compiled into the plan's own kernel, and
///     the real compiler is what compiles it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The whole node is a string and a compilation.</b> What an author types is dropped into
///         a generated kernel of exactly the shape the other forty-five have — one storage image,
///         its taps clamped to the <em>source's</em> dimensions, an <c>8×8</c> workgroup and a bounds
///         guard — and the result goes through <see cref="Compilation" />. Every type error, every
///         unknown name and every arity mistake is Raven's own, phrased Raven's way, and mapped back
///         to the node by the line the expression is on. There is no second language, no second type
///         checker and no second set of diagnostics to keep in step with the first, which is the
///         entire argument of § D6.
///     </para>
///     <para>
///         ⚠ <b>The mapping is a line and not a column, and one line is all it can honestly be.</b>
///         The expression is written into one <c>target.Store</c> statement, so a complaint anywhere
///         in it points at that statement — <see cref="NodeSpan" />'s own remarks say a line is the
///         finest resolution this kind of emission can claim. What the author is handed is the node,
///         which is the thing they can select.
///     </para>
///     <para>
///         ⚠ <b>The shader's name is derived from the expression, not from the node.</b> Two nodes
///         whose expressions are identical produce one kernel and one compilation — and the same
///         graph produces the same kernel name on every machine and every run, which is what makes a
///         plan comparable between two saved versions of a graph.
///     </para>
///     <para>
///         ⚠ <b>What is visible to the expression is a closed list, and it is short on purpose.</b>
///         <c>a</c> and <c>b</c> are the two image inputs as <c>float4</c>; <c>x</c>, <c>y</c>,
///         <c>z</c> and <c>w</c> are the four numbers (each of which may itself be an expression over
///         the graph's parameters, § D6's other half); <c>uv</c> is the texel centre in <c>0…1</c>;
///         <c>coord</c> and <c>size</c> are the integer position and extent. Everything else — a
///         sampler, a second image, the library — is refused by there being nothing to name, which is
///         the same refusal <c>TextureKernels</c> makes of every kernel in this assembly.
///     </para>
/// </remarks>
static class TexturePixelProcessor {
    /// <summary>What every generated kernel's name starts with.</summary>
    public const string Prefix = "PixelProcessor_";

    /// <summary>The four numbers an expression may read, in the order the node declares them.</summary>
    public static ImmutableArray<string> Numbers { get; } = ["x", "y", "z", "w"];

    /// <summary>Writes the kernel one expression stands for.</summary>
    /// <param name="expression">What the author typed, trimmed.</param>
    /// <param name="hasA">Whether the first image input is connected.</param>
    /// <param name="hasB">Whether the second is.</param>
    /// <returns>The kernel.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression" /> is null.</exception>
    public static TexturePixelKernel Write(string expression, bool hasA, bool hasB) {
        ArgumentNullException.ThrowIfNull(expression);

        var name = Prefix + Digest(expression, hasA, hasB);
        var source = new StringBuilder();
        var line = 0;
        var body = -1;

        void Say(string text) {
            source.Append(text).Append('\n');
            line++;
        }

        Say("package Vixen.Editor.TextureGraph.Shaders");
        Say("");
        Say("// Generated from a Pixel Processor node's expression — doc 48 § D6. Not a file.");
        Say("");
        Say($"shader {name} {{");

        if (hasA) {
            Say("    var sourceA: Texture2D");
        }

        if (hasB) {
            Say("    var sourceB: Texture2D");
        }

        foreach (var number in Numbers) {
            Say($"    var {number}: float = 0f");
        }

        Say("");
        Say("    [Format(\"rgba16f\")] var target: RWTexture2D<float4>");

        if (hasA) {
            Say("");
            Tap(Say, "TapA", "sourceA");
        }

        if (hasB) {
            Say("");
            Tap(Say, "TapB", "sourceB");
        }

        Say("");
        Say("    [ComputeShader(8, 8, 1)]");
        Say("    func Main([Semantic(\"SV_DispatchThreadID\")] id: uint3) {");
        Say("        val coord = int2(int(id.x), int(id.y))");
        Say("        val size = target.GetDimensions()");
        Say("");
        Say("        if (coord.x >= size.x || coord.y >= size.y) {");
        Say("            return");
        Say("        }");
        Say("");

        // ⚠ A `float4` and not a `float` even for an unconnected input, and the alpha is one. A
        // splatted zero would make `a` invisible to every blend an expression could write, which is
        // the same trap the compiler's grey-to-colour promotion documents.
        Say(hasA ? "        val a = TapA(coord)" : "        val a = float4(0f, 0f, 0f, 1f)");
        Say(hasB ? "        val b = TapB(coord)" : "        val b = float4(0f, 0f, 0f, 1f)");

        Say("        val uv = float2((float(coord.x) + 0.5f) / float(size.x), (float(coord.y) + 0.5f) / float(size.y))");
        Say("");

        body = line;

        Say($"        target.Store(coord, {expression})");
        Say("    }");
        Say("}");

        return new(name, source.ToString(), body);
    }

    /// <summary>What Raven says about a generated kernel, ready to be said about a node.</summary>
    /// <param name="kernel">The kernel.</param>
    /// <returns>One problem per error, in the order Raven reported them.</returns>
    /// <remarks>
    ///     ⚠ <b>Parse and bind, and stop there — <c>ShaderGraphDocument.Check</c>'s rule.</b> Those
    ///     are the complaints that are about the <em>shader</em>; a backend's are about a target the
    ///     author did not choose, and a bake is where they belong. It also means this costs no
    ///     SPIR-V, so it can run on every edit.
    /// </remarks>
    public static ImmutableArray<TexturePixelProblem> Check(TexturePixelKernel kernel) {
        var tree = SyntaxTree.ParseText(kernel.Source, path: kernel.Kernel + ".rvn");
        var compilation = Compilation.Create(kernel.Kernel, tree);
        var problems = ImmutableArray.CreateBuilder<TexturePixelProblem>();

        foreach (var diagnostic in compilation.GetDiagnostics()) {
            if (!diagnostic.IsError) {
                continue;
            }

            var at = diagnostic.Location.IsNone ? -1 : diagnostic.Location.GetLineSpan().Start.Line;

            problems.Add(new(
                // ⚠ The line is *not* in the message when it is the expression's own, because the
                // author is looking at a field with one line in it and being told "line 27" of a file
                // they have never seen is worse than being told nothing.
                at == kernel.Line
                    ? $"{diagnostic.Id}: {diagnostic.GetMessage()}"
                    : $"The generated kernel does not compile at line {at + 1}: {diagnostic.Id}: "
                      + diagnostic.GetMessage(),
                at < 0 ? NodeSpan.None : new NodeSpan(at, 1)
            ));
        }

        return problems.ToImmutable();
    }

    /// <summary>Why an expression cannot be emitted at all, or null when it can.</summary>
    /// <param name="expression">The trimmed text.</param>
    /// <returns>The tail of a sentence, or null.</returns>
    public static string? Refuse(string expression) {
        if (string.IsNullOrWhiteSpace(expression)) {
            return "is empty. A Pixel Processor's expression is what it computes; there is no default "
                   + "picture it could produce instead.";
        }

        // ⚠ The same refusal `TextureGraphExpressions` makes, for the same reason and with more at
        // stake: a newline ends a statement in Raven, so a two-line expression would end the
        // `target.Store` early *and* move every line of the generated kernel after it — which is what
        // the span maps through.
        if (expression.Contains('\n', StringComparison.Ordinal)
            || expression.Contains('\r', StringComparison.Ordinal)) {
            return "runs over more than one line. A newline ends a statement in Raven, so only the first "
                   + "line would be part of it — write it as one expression.";
        }

        if (expression.Contains('{', StringComparison.Ordinal)
            || expression.Contains('}', StringComparison.Ordinal)) {
            return "contains a brace. This is one expression rather than a function body: a block would "
                   + "close the one it is written into.";
        }

        return null;
    }

    static void Tap(Action<string> say, string name, string source) {
        say($"    func {name}(coord: int2): float4 {{");
        say($"        val size = {source}.GetDimensions(0)");
        say("");

        // ⚠ Clamped to the *source's* extent and not the target's, which is every kernel in this
        // assembly's rule: an op may write an image larger than the one it reads, and clamping to the
        // storage image would be a no-op after Main's guard — a bounds check that is not one, and an
        // out-of-bounds Load reads as zero on most drivers rather than failing.
        say($"        return {source}.Load(int3(clamp(coord.x, 0, size.x - 1), "
            + "clamp(coord.y, 0, size.y - 1), 0))");

        say("    }");
    }

    /// <summary>A stable short name for one expression and its shape.</summary>
    /// <remarks>
    ///     ⚠ <b>SHA-256 and not <c>string.GetHashCode</c>.</b> That one is randomised per process, so
    ///     a kernel named from it would have a different name on every run — and a plan is compared
    ///     between two saved versions of a graph.
    /// </remarks>
    static string Digest(string expression, bool hasA, bool hasB) {
        var bytes = Encoding.UTF8.GetBytes($"{(hasA ? 'a' : '-')}{(hasB ? 'b' : '-')}\n{expression}");

        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..8];
    }
}
