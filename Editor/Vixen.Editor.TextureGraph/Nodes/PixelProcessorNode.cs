// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>Arbitrary per-texel arithmetic, written in Raven.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D6's escape hatch, and the shape of it is the point.</b> The setting is a
///         Raven <em>expression</em>, compiled by the real Raven compiler into a kernel of exactly
///         the shape the other forty-five have, with the complaints mapped back to this node. It is
///         not a hand-rolled evaluator, not a scripting language, and not a nested function graph of
///         forty tiny nodes — Designer's answer, which § D6 refuses by name.
///     </para>
///     <para>
///         What the expression can name: <c>a</c> and <c>b</c>, the two image inputs, as
///         <c>float4</c>; <c>x</c>, <c>y</c>, <c>z</c> and <c>w</c>, the four numbers — each of which
///         may itself be an expression over the graph's exposed parameters; <c>uv</c>, the texel
///         centre in <c>0…1</c>; and <c>coord</c> and <c>size</c>, the integer position and extent.
///         It answers with a <c>float4</c>.
///     </para>
///     <para>
///         ⚠ <b>The op it emits does not evaluate yet, and that is disclosed rather than
///         discovered.</b> A plan's op names a kernel and the evaluator resolves that name through
///         this assembly's <em>embedded</em> sources; an authored kernel is not embedded and never
///         can be. The source is on <c>TextureGraphCompiler.Kernels</c> and the last wire is
///         <a href="https://github.com/Rikarin/Vixen/issues/729">#729</a>. Everything § D6 is
///         actually about — the real compiler, the diagnostics, the mapping — is here and tested.
///     </para>
/// </remarks>
[Node(
    "Filters/Pixel Processor",
    Preview = true,
    Summary = "Per-texel arithmetic, as one Raven expression over a, b, x…w and uv."
)]
sealed partial class PixelProcessorNode : TextureNode {
    /// <summary>The expression, which answers with a <c>float4</c>.</summary>
    [Setting(Default = "a")]
    public string Expression = "a";

    /// <summary>The image the expression calls <c>a</c>.</summary>
    [Input(Name = "A")]
    public Image A;

    /// <summary>The image it calls <c>b</c>.</summary>
    [Input(Name = "B")]
    public Image B;

    /// <summary>The number it calls <c>x</c>.</summary>
    [Input(Name = "X")]
    public Scalar X = 0f;

    /// <summary>The number it calls <c>y</c>.</summary>
    [Input(Name = "Y")]
    public Scalar Y = 0f;

    /// <summary>The number it calls <c>z</c>.</summary>
    [Input(Name = "Z")]
    public Scalar Z = 0f;

    /// <summary>The number it calls <c>w</c>.</summary>
    [Input(Name = "W")]
    public Scalar W = 0f;

    /// <summary>What it computed.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var expression = emitter.Text(nameof(Expression)).Trim();

        if (TexturePixelProcessor.Refuse(expression) is { } reason) {
            // ⚠ TG0020 and TG0021 below, because this node's first pair were TG0018 and TG0017 —
            // which two other sites in this assembly already used for entirely different things, one
            // of them a warning about a resample onto its own size. An id is what a host filters and
            // suppresses on, so two meanings under one id is a filter that hides the wrong half.
            emitter.Report(TextureDiagnostics.PixelProcessorExpressionRefused, $"'{nameof(Expression)}' {reason}", nameof(Expression));

            return;
        }

        // ⚠ Whether a port is *connected* rather than whether reading it produced an image, because
        // `Read` reports a hole against an unconnected one — and here an unconnected input is not a
        // hole. Both of them are optional: a Pixel Processor with neither wired is a generator, and
        // the expression names `a` and `b` as opaque black.
        var hasA = Binding.IsConnected("A");
        var hasB = Binding.IsConnected("B");
        var a = hasA ? emitter.Read("A") : -1;
        var b = hasB ? emitter.Read("B") : -1;

        // Colour when nothing is wired, for `UniformNode`'s reason: a node with no image input
        // resolves to grey — the narrowest — and a generator whose expression writes a colour would
        // silently keep only its red.
        var target = emitter.Write("Out", hasA || hasB ? emitter.Resolved : TextureChannels.Colour);
        var kernel = TexturePixelProcessor.Write(expression, hasA, hasB);
        var problems = TexturePixelProcessor.Check(kernel);

        if (problems.Length > 0) {
            foreach (var problem in problems) {
                emitter.Report(TextureDiagnostics.PixelProcessorDoesNotCompile, problem.Message, nameof(Expression), NodeSeverity.Error, problem.Span);
            }

            return;
        }

        emitter.Declare(kernel.Kernel, kernel.Source);

        var inputs = ImmutableArray.CreateBuilder<int>(2);

        if (hasA) {
            inputs.Add(a);
        }

        if (hasB) {
            inputs.Add(b);
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = kernel.Kernel,
                Output = target,
                Inputs = inputs.ToImmutable(),

                // Every declared member, because `TexturePlanEvaluator.Uniforms` refuses an op that
                // leaves one out — a parameter nobody carried would be written as zero, which is a
                // valid-looking number for all four of these.
                Parameters = [
                    new("x", emitter.Number(nameof(X))),
                    new("y", emitter.Number(nameof(Y))),
                    new("z", emitter.Number(nameof(Z))),
                    new("w", emitter.Number(nameof(W)))
                ]
            }
        );
    }
}
