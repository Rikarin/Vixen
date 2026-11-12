// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>What a Pixel Processor keeps of the <c>float4</c> its expression answers with.</summary>
enum TexturePixelFormat {
    /// <summary>All four lanes.</summary>
    Colour = 0,

    /// <summary>Red alone, for a port that measures.</summary>
    Grey = 1
}

/// <summary>Arbitrary per-texel arithmetic, written in Raven.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D6's escape hatch, and the shape of it is the point.</b> The setting is a
///         Raven <em>expression</em>, compiled by the real Raven compiler into a kernel of exactly
///         the shape every other kernel in <c>Shaders/</c> has, with the complaints mapped back to
///         this node. It is
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
///         ⚠
///         <b>
///             Both inputs arrive as colours, always, and the output is a colour unless
///             <see cref="Format" /> says grey.
///         </b>
///         It took the shape of whatever arrived, which is the
///         rule for a node whose kernel is a tonal operator and the wrong one here: the expression
///         decides what the lanes mean, so over a grey noise <c>float4(a.x, 1f - a.x, 0f, 1f)</c>
///         lost its green with no message. And <c>a</c> read <c>(r, 0, 0, 1)</c> from a grey view
///         and <c>(r, r, r, 1)</c> once a colour at <c>b</c> splatted it, so one expression drew two
///         pictures. A colour into a port that measures is refused by name, so the cost of the
///         default is a sentence rather than a missing channel.
///     </para>
///     <para>
///         <b>The op it emits bakes, through the plan and not through this assembly's resources.</b>
///         <c>TextureGraphCompiler.Finish</c> copies every authored kernel onto
///         <c>TexturePlan.Kernels</c>, <c>TexturePlan.Source</c> prefers that entry over
///         <see cref="TextureKernels.Source" />, and <c>TexturePlanEvaluator</c> compiles
///         <c>TextureKernels.Variant(kernel, plan.Source(kernel), output)</c> — the same format
///         rewrite every embedded kernel gets, over text the plan carries.
///     </para>
///     <para>
///         ⚠
///         <b>
///             This paragraph used to say the op "does not evaluate yet" and that an authored
///             kernel "is not embedded and never can be", and the second half was wrong about the design
///             rather than about the schedule
///         </b>
///         —
///         <a href="https://github.com/Rikarin/Vixen/issues/1061">#1061</a>. It survived
///         <a href="https://github.com/Rikarin/Vixen/issues/729">#729</a>'s close because it reads
///         as a settled disclosure, and it is the only sentence in the tree that says whether a
///         Pixel Processor bakes: a compound author building doc 48 § 4.9's library wrote a
///         five-node mask on the strength of it when one expression would have done.
///     </para>
///     <para>
///         ⚠
///         <b>
///             That no shipped compound uses this node is a claim about the library, not about the
///             node.
///         </b>
///         § 4.9's thirty-one are the measurement of whether the atomic set is complete, and
///         an escape hatch in one of them would have hidden the two gaps they found —
///         <a href="https://github.com/Rikarin/Vixen/issues/1059">#1059</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/1060">#1060</a>. Counted rather than
///         repeated: the sentence said twelve while thirty-one shipped. ⚠ No compound of § 4.9's
///         measured rows names this node; <c>Compounds/Adjust</c> and <c>Compounds/Finishes</c> do, on
///         purpose — they are Patina's layer filters, not a measurement of the atomic set, and the
///         folder's README says why.
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

    /// <summary>What is kept of the answer: <c>Colour</c>, or <c>Grey</c> for its red.</summary>
    [Setting(AcceptedFrom = typeof(TexturePixelFormat))]
    public string Format = "Colour";

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
            emitter.Report(
                TextureDiagnostics.PixelProcessorExpressionRefused,
                $"'{nameof(Expression)}' {reason}",
                nameof(Expression)
            );

            return;
        }

        // ⚠ Whether a port is *connected* rather than whether reading it produced an image, because
        // `Read` reports a hole against an unconnected one — and here an unconnected input is not a
        // hole. Both of them are optional: a Pixel Processor with neither wired is a generator, and
        // the expression names `a` and `b` as opaque black.
        var hasA = Binding.IsConnected("A");
        var hasB = Binding.IsConnected("B");
        var a = hasA ? emitter.Read("A", TextureChannels.Colour) : -1;
        var b = hasB ? emitter.Read("B", TextureChannels.Colour) : -1;

        var format = TextureSettings.Enum(emitter, nameof(Format), TexturePixelFormat.Colour);
        var target = emitter.Write(
            "Out",
            format == TexturePixelFormat.Grey ? TextureChannels.Grey : TextureChannels.Colour
        );

        var kernel = TexturePixelProcessor.Write(expression, hasA, hasB);
        var problems = TexturePixelProcessor.Check(kernel);

        if (problems.Length > 0) {
            foreach (var problem in problems) {
                emitter.Report(
                    TextureDiagnostics.PixelProcessorDoesNotCompile,
                    problem.Message,
                    nameof(Expression),
                    NodeSeverity.Error,
                    problem.Span
                );
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
