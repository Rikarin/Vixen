// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>What a layer stack compiles to, and what it refuses to compile.</summary>
public class LayerStackCompileTests {
    /// <summary>Every blend mode a layer can name is a mode the kernel has, with the same number.</summary>
    /// <remarks>
    ///     ⚠ <b>Reflected out of the evaluator assembly rather than listed here, and that is the
    ///     whole point.</b> <c>TextureBlendMode</c> is <c>internal</c> to
    ///     <c>Vixen.Editor.TextureGraph</c>, so <c>LayerBlendMode</c> is a second declaration of one
    ///     list — and nothing in the compilation would notice them drifting: a layer hands
    ///     <c>Colour/Blend</c> the mode's <em>name</em>, and a name that assembly does not know falls
    ///     back to its default, <c>Copy</c>. A stack whose every overlay silently became a copy is a
    ///     picture, not an error.
    ///     <para>
    ///         The direction that matters for correctness is "mine ⊆ theirs, with the same values":
    ///         a stack must never emit a mode that does not exist or that means something else.
    ///         <c>An_operator_the_kernel_gained_is_offered_to_a_layer</c> is the other direction and
    ///         is a drift tripwire rather than a correctness claim.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_blend_mode_a_layer_names_is_the_kernels_own() {
        var kernel = KernelBlendModes();

        foreach (var mode in Enum.GetValues<LayerBlendMode>()) {
            var name = mode.ToString();

            Assert.True(
                kernel.TryGetValue(name, out var value),
                $"LayerBlendMode.{name} is not a TextureBlendMode. Colour/Blend parses the mode by name and "
                + "falls back to Copy on one it does not know, so this layer would silently composite as a copy."
            );

            Assert.Equal((int)mode, value);
        }
    }

    /// <summary>A mode the kernel gained and the stack has not.</summary>
    /// <remarks>
    ///     ⚠ <b>A tripwire, and the fix when it fires is one line.</b> <c>TextureBlendMode</c> is the
    ///     authority; a value added there and not to <c>LayerBlendMode</c> is an operator an artist
    ///     can reach from a wire and not from a layer, which doc 48 § 4.10 says is the same sixteen.
    ///     If this goes red, add the named member to <c>LayerBlendMode</c> with the same number.
    /// </remarks>
    [Fact]
    public void An_operator_the_kernel_gained_is_offered_to_a_layer() {
        List<string> missing = [];

        foreach (var (name, _) in KernelBlendModes()) {
            if (!Enum.TryParse<LayerBlendMode>(name, out _)) {
                missing.Add(name);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"TextureBlendMode has {string.Join(", ", missing)} and LayerBlendMode has not. Doc 48 § 4.10 says a "
            + "layer composites with the same sixteen a wire does; add the member with the kernel's own number."
        );
    }

    /// <summary>And the name reaches the node, which reflection cannot show.</summary>
    /// <remarks>
    ///     ⚠ <b>Reflection proves the two enums agree; only a compilation proves the string a layer
    ///     writes is the string <c>BlendNode</c> reads.</b> A rename of the setting, a
    ///     <c>Mode</c> spelled with a space, or a parse that is case-sensitive the other way all
    ///     leave the reflection test green — and <c>TextureSettings.Enum</c> reports a diagnostic
    ///     when it cannot parse, which is what this reads.
    /// </remarks>
    [Fact]
    public void Compiling_one_layer_per_mode_reports_nothing() {
        foreach (var mode in Enum.GetValues<LayerBlendMode>()) {
            var stack = One(new() { Id = "l", Kind = LayerKind.Fill, Blend = mode, Values = { ["baseColor"] = Opaque } });
            var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

            Assert.Empty(compilation.Problems);
            Assert.Empty(compilation.Diagnostics);
            Assert.NotNull(compilation.Plan);

            var blend = Find(compilation.Plan, "Blend");

            Assert.Equal((float)(int)mode, blend.Find("mode")!.Value.Value);
        }
    }

    /// <summary>A layer restricted to one channel appears in that channel's chain and no other.</summary>
    [Fact]
    public void A_per_channel_enable_keeps_a_layer_out_of_the_other_channels() {
        var stack = new LayerStackAsset {
            Name = "Enables",
            BaseWidth = 32,
            BaseHeight = 32,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [
                        new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] },
                        new() { Usage = "roughness", Default = [0.5f, 0.5f, 0.5f, 1f] }
                    ],
                    Layers = [
                        new() {
                            Id = "rough",
                            Kind = LayerKind.Fill,
                            Channels = ["roughness"],
                            Values = { ["roughness"] = [0.9f, 0.9f, 0.9f, 1f] }
                        }
                    ]
                }
            ]
        };

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);

        // Two channels, one layer in one of them: two base colours, one layer colour, one blend.
        Assert.Equal(1, Count(compilation.Plan, "Blend"));
        Assert.Equal(3, Count(compilation.Plan, "Uniform"));

        // ⚠ And the untouched channel still produces a map. A per-channel enable that dropped the
        // channel rather than the layer would compile to a plan with one output and read as an
        // artist's mistake rather than as this one.
        Assert.Equal(2, compilation.Plan.Outputs.Length);
        Assert.Equal(2, compilation.Outputs.Length);
    }

    /// <summary>A disabled layer compiles to nothing at all.</summary>
    [Fact]
    public void A_disabled_layer_emits_no_op() {
        var enabled = One(new() { Id = "l", Kind = LayerKind.Fill, Values = { ["baseColor"] = Opaque } });
        var disabled = One(new() { Id = "l", Kind = LayerKind.Fill, Enabled = false, Values = { ["baseColor"] = Opaque } });

        var with = LayerStackCompiler.Compile(enabled, enabled.Sets[0]);
        var without = LayerStackCompiler.Compile(disabled, disabled.Sets[0]);

        Assert.NotNull(with.Plan);
        Assert.NotNull(without.Plan);
        Assert.Equal(with.Plan.Ops.Length - 2, without.Plan.Ops.Length);
    }

    /// <summary>A group's children composite among themselves, then the group blends once.</summary>
    [Fact]
    public void A_group_blends_once_over_what_is_under_it() {
        var stack = One(new() {
            Id = "g",
            Kind = LayerKind.Group,
            Blend = LayerBlendMode.Screen,
            Opacity = 0.5f,
            Children = [
                new() { Id = "a", Kind = LayerKind.Fill, Blend = LayerBlendMode.Multiply, Values = { ["baseColor"] = Opaque } },
                new() { Id = "b", Kind = LayerKind.Fill, Blend = LayerBlendMode.Add, Values = { ["baseColor"] = Opaque } }
            ]
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);

        // Two children and the group itself.
        Assert.Equal(3, Count(compilation.Plan, "Blend"));

        var group = compilation.Plan.Ops[^1];

        Assert.Equal("Blend", group.Kernel);
        Assert.Equal((float)(int)LayerBlendMode.Screen, group.Find("mode")!.Value.Value);
        Assert.Equal(0.5f, group.Find("opacity")!.Value.Value);
    }

    /// <summary>An empty group is not a dispatch that changes nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>A blend of the cursor over itself would be harmless arithmetic and is not harmless
    ///     in a plan</b>: <c>TexturePlan.SeedFor</c> mixes an op's <em>index</em> into its seed, so
    ///     an op that draws nothing still moves every procedural op after it.
    /// </remarks>
    [Fact]
    public void A_group_with_nothing_in_it_emits_nothing() {
        var stack = One(new() { Id = "g", Kind = LayerKind.Group });
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);
        Assert.Equal(0, Count(compilation.Plan, "Blend"));
    }

    /// <summary>A constant mask is a real image and real shuffles, not a folded number.</summary>
    /// <remarks>
    ///     ⚠ <b>The mask reaches the blend as the foreground's alpha <em>times</em> what it already
    ///     was</b> — <a href="https://github.com/Rikarin/Vixen/issues/832">#832</a> · 1. Two coverages
    ///     compose by multiplication, and the product has to be computed in a colour lane because
    ///     <c>Blend</c>'s alpha rule only ever raises alpha. So a masked layer is a shuffle that lifts
    ///     the content's alpha into grey, a shuffle that lifts the mask's red into grey, a
    ///     <c>Multiply</c> between them, and the shuffle that puts the answer back into alpha.
    /// </remarks>
    [Fact]
    public void A_mask_multiplies_into_the_foregrounds_alpha() {
        var stack = One(new() {
            Id = "l",
            Kind = LayerKind.Fill,
            Values = { ["baseColor"] = Opaque },
            Mask = new() { Source = LayerMaskSource.Constant, Value = 0.25f }
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);
        Assert.Equal(3, Count(compilation.Plan, "ChannelShuffle"));

        var shuffle = Last(compilation.Plan, "ChannelShuffle");

        // 0…2 are the first input's red, green and blue and 4 is the second's red: the product, and
        // only the product, moves into alpha.
        Assert.Equal(4f, shuffle.Find("sourceA")!.Value.Value);
        Assert.Equal(0f, shuffle.Find("sourceR")!.Value.Value);

        // The Multiply of the two greys is what the last shuffle reads, and the layer's own blend is
        // what reads the shuffle.
        var product = Find(compilation.Plan, "Blend");

        Assert.Equal((float)(int)LayerBlendMode.Multiply, product.Find("mode")!.Value.Value);
        Assert.Equal(product.Output, shuffle.Inputs[1]);

        // 3 is FirstAlpha and 9 is a constant one: the content's own coverage, as an opaque grey.
        // ⚠ Pinned by what reads it rather than by where it sits. The content's grey and the mask's
        // grey are graph siblings and nothing orders one before the other, so `Find` — first in op
        // order — would have named whichever the topological walk happened to emit first.
        var carried = Only(compilation.Plan, "ChannelShuffle", op => op.Output == product.Inputs[0]);

        Assert.Equal(3f, carried.Find("sourceR")!.Value.Value);
        Assert.Equal(9f, carried.Find("sourceA")!.Value.Value);

        var blend = Last(compilation.Plan, "Blend");

        Assert.Equal(shuffle.Output, blend.Inputs[1]);
    }

    /// <summary>A constant fill's own alpha survives a mask, because it folds into the opacity.</summary>
    [Fact]
    public void A_constant_fills_alpha_folds_into_the_opacity() {
        var stack = One(new() {
            Id = "l",
            Kind = LayerKind.Fill,
            Opacity = 0.5f,
            Values = { ["baseColor"] = [1f, 1f, 1f, 0.4f] },
            Mask = new() { Source = LayerMaskSource.Constant, Value = 1f }
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);

        // ⚠ The *last* blend, because a masked layer's first one is the Multiply that composes the
        // two coverages and its opacity is 1 by construction.
        Assert.Equal(0.2f, Last(compilation.Plan, "Blend").Find("opacity")!.Value.Value, 6);
    }

    /// <summary>An anchor onto a layer beneath is an edge, and it compiles.</summary>
    [Fact]
    public void An_anchor_reads_a_layer_beneath_it() {
        var stack = Stack(
            new() { Id = "under", Kind = LayerKind.Fill, Values = { ["baseColor"] = Opaque } },
            new() {
                Id = "over",
                Kind = LayerKind.Fill,
                Values = { ["baseColor"] = Opaque },
                Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "under" }
            }
        );

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.NotNull(compilation.Plan);

        // The anchor reads the lower layer's *composite*, which is the first blend, and it arrives at
        // the shuffle that lifts a mask's red into an opaque grey — selector 0 into red, 9 into
        // alpha. ⚠ Found by what it does rather than by its position: a masked layer emits three
        // shuffles and only this one reads the mask.
        var under = Find(compilation.Plan, "Blend");
        var opaque = Only(
            compilation.Plan,
            "ChannelShuffle",
            op => op.Find("sourceR")!.Value.Value == 0f && op.Find("sourceA")!.Value.Value == 9f
        );

        Assert.Equal(under.Output, opaque.Inputs[0]);
    }

    /// <summary>An anchor onto a layer above it is refused, by the graph model.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 48 § D10: the stack compiles <em>through</em> <c>NodeGraphModel</c>'s cycle
    ///     check rather than growing a second one.</b> The proof that it is that check and not a
    ///     lookup that happened to fail is the message: <c>GraphInvariants.Describe</c>'s words for
    ///     <c>GraphConnectionError.Cycle</c>, which nothing in this assembly writes.
    /// </remarks>
    [Fact]
    public void An_anchor_onto_a_layer_above_it_is_a_cycle_the_graph_refuses() {
        var stack = Stack(
            new() {
                Id = "under",
                Kind = LayerKind.Fill,
                Values = { ["baseColor"] = Opaque },
                Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "over" }
            },
            new() { Id = "over", Kind = LayerKind.Fill, Values = { ["baseColor"] = Opaque } }
        );

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Null(compilation.Plan);

        var problem = Assert.Single(compilation.Problems);

        Assert.Equal("under", problem.Layer);
        Assert.Contains("loop", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A layer anchored to itself is the same refusal.</summary>
    [Fact]
    public void A_layer_anchored_to_itself_is_refused() {
        var stack = One(new() {
            Id = "self",
            Kind = LayerKind.Fill,
            Values = { ["baseColor"] = Opaque },
            Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "self" }
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Null(compilation.Plan);
        Assert.Contains(compilation.Problems, problem => problem.Layer == "self");
    }

    /// <summary>A paint layer is held, and refused, and the message names M9.</summary>
    /// <remarks>
    ///     ⚠ <b>A tripwire whose message names the issue that removes it</b> — #574. When the brush
    ///     lands, this test is what says so, and the layer it is written against keeps its blend
    ///     mode, its opacity and its channel enables through the refusal.
    /// </remarks>
    [Fact]
    public void A_paint_layer_is_refused_and_says_which_issue_builds_it() {
        var stack = One(new() {
            Id = "paint",
            Kind = LayerKind.Paint,
            Paint = "Body.paint.vxpaint",
            Blend = LayerBlendMode.Overlay,
            Opacity = 0.7f
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Null(compilation.Plan);

        var problem = Assert.Single(compilation.Problems);

        Assert.Contains("#574", problem.Message, StringComparison.Ordinal);

        // The refusal is a compile-time one and the document keeps everything it was given.
        var layer = stack.Sets[0].Layers[0];

        Assert.Equal(LayerBlendMode.Overlay, layer.Blend);
        Assert.Equal("Body.paint.vxpaint", layer.Paint);
    }

    /// <summary>The one shape M8 modelled and did not build is refused, by name and by issue.</summary>
    /// <remarks>
    ///     ⚠ <b>A tripwire, and it has already fired twice.</b> It covered three shapes — a generator
    ///     mask, a graph fill and a projection — all refused with "which is M8 (#573)". M8 built the
    ///     first two, so the test went red on the change that answered it and the two cases came out.
    ///     What is left is projection, which needs a node nothing has written; the message names
    ///     <a href="https://github.com/Rikarin/Vixen/issues/815">#815</a> rather than the issue that
    ///     is about to close, and when that one lands this test goes red again and should be deleted.
    /// </remarks>
    [Fact]
    public void What_M8_modelled_and_did_not_build_is_refused_and_says_so() {
        var stack = One(new() {
            Id = "l",
            Kind = LayerKind.Fill,
            Values = { ["baseColor"] = Opaque },
            Projection = LayerProjection.Triplanar
        });
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Null(compilation.Plan);
        Assert.Contains(compilation.Problems, problem => problem.Message.Contains("#815", StringComparison.Ordinal));
    }

    /// <summary>A texture fill becomes an external the caller supplies.</summary>
    [Fact]
    public void A_texture_fill_compiles_into_an_external() {
        var stack = One(new() {
            Id = "l",
            Kind = LayerKind.Fill,
            Fill = LayerFillSource.Texture,
            Textures = { ["baseColor"] = "Assets/Rust.png" }
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.NotNull(compilation.Plan);

        var external = Assert.Single(compilation.Externals);

        Assert.Equal("Assets/Rust.png", external.Asset);
    }

    /// <summary>A texture fill that names no image for a channel it writes is refused.</summary>
    [Fact]
    public void A_texture_fill_with_no_image_is_refused_rather_than_filled_with_black() {
        var stack = One(new() { Id = "l", Kind = LayerKind.Fill, Fill = LayerFillSource.Texture });
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Null(compilation.Plan);
        Assert.Contains(compilation.Problems, problem => problem.Severity == NodeGraph.NodeSeverity.Error);
    }

    /// <summary>A number a filter does not take is dropped and reported, never written to a port.</summary>
    /// <remarks>
    ///     ⚠ <b>The port it would otherwise have reached is the image input.</b> A stack naming
    ///     <c>Input</c> in a filter's settings would replace the wire carrying every layer beneath it
    ///     with a constant, and the picture would be the filter over nothing.
    /// </remarks>
    [Fact]
    public void A_setting_a_filter_does_not_take_is_dropped_with_a_warning() {
        var stack = One(new() {
            Id = "l",
            Kind = LayerKind.Filter,
            Filter = LayerFilterKind.Blur,
            Settings = { ["Radius"] = [2f], ["Input"] = [0f] }
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);

        var problem = Assert.Single(compilation.Problems);

        Assert.Equal(NodeGraph.NodeSeverity.Warning, problem.Severity);
        Assert.Contains("Input", problem.Message, StringComparison.Ordinal);

        // And the blur still reads the layers beneath it rather than a constant.
        Assert.Empty(compilation.Diagnostics);
    }

    /// <summary>Two layers with one id are refused, because an anchor names one of them.</summary>
    [Fact]
    public void Two_layers_may_not_share_an_id() {
        var stack = Stack(
            new() { Id = "same", Kind = LayerKind.Fill },
            new() { Id = "same", Kind = LayerKind.Fill }
        );

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Null(compilation.Plan);
        Assert.Contains(compilation.Problems, problem => problem.Message.Contains("share the id", StringComparison.Ordinal));
    }

    /// <summary>Every channel of the default set is a usage the Output node accepts.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived rather than compared against a copy of the list.</b> <c>TextureUsages.Known</c>
    ///     is <c>internal</c> to the evaluator, so an equality here would be a second declaration of
    ///     the nine — the shape five roll calls in this workstream have gone red on. Compiling one
    ///     stack per channel and asserting no diagnostic asks the node itself, which is the only
    ///     thing whose opinion decides a bake.
    /// </remarks>
    [Fact]
    public void Every_default_channel_is_a_usage_the_output_node_accepts() {
        foreach (var channel in LayerStackDocument.DefaultChannels()) {
            var stack = new LayerStackAsset {
                Name = "Usages",
                BaseWidth = 32,
                BaseHeight = 32,
                Sets = [new() { Name = "S", Channels = [channel] }]
            };

            var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

            Assert.Empty(compilation.Diagnostics);
            Assert.NotNull(compilation.Plan);
        }
    }

    /// <summary>A set with no channels is refused rather than compiled into a plan with no outputs.</summary>
    [Fact]
    public void A_set_with_no_channels_is_refused() {
        var stack = new LayerStackAsset { Name = "Empty", Sets = [new() { Name = "S" }] };
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Null(compilation.Plan);
        Assert.NotEmpty(compilation.Problems);
    }

    /// <summary>The plan is authored at the stack's resolution and carries its seed.</summary>
    [Fact]
    public void The_plan_takes_the_stacks_resolution_and_seed() {
        var stack = One(new() { Id = "l", Kind = LayerKind.Fill });
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0], bakeLevelOffset: -2);

        Assert.NotNull(compilation.Plan);
        Assert.Equal(stack.BaseWidth, compilation.Plan.BaseWidth);
        Assert.Equal(stack.BaseHeight, compilation.Plan.BaseHeight);
        Assert.Equal(stack.Seed, compilation.Plan.Seed);
        Assert.Equal(-2, compilation.Plan.BakeLevelOffset);
    }

    static Dictionary<string, int> KernelBlendModes() {
        var type = typeof(TexturePlan).Assembly.GetType("Vixen.Editor.TextureGraph.TextureBlendMode");

        Assert.NotNull(type);

        Dictionary<string, int> modes = new(StringComparer.Ordinal);

        foreach (var value in Enum.GetValues(type)) {
            modes[value.ToString()!] = (int)value;
        }

        Assert.NotEmpty(modes);

        return modes;
    }

    /// <summary>A colour to author, so that a fill actually covers the channel it writes.</summary>
    /// <remarks>
    ///     ⚠ <b>Written out rather than left absent, since #807 · 2.</b> A constant fill with no
    ///     entry for a channel composites nothing there — an absent entry is how a layer says it has
    ///     nothing to say about that channel. A test that left <c>Values</c> empty was compiling a
    ///     layer that only ever composited the channel's own base default, so asserting on its blend
    ///     was asserting on a colour nobody had authored.
    /// </remarks>
    static readonly float[] Opaque = [1f, 1f, 1f, 1f];

    static LayerStackAsset One(LayerAsset layer) => Stack(layer);

    static LayerStackAsset Stack(params LayerAsset[] layers) =>
        new() {
            Name = "Test",
            BaseWidth = 32,
            BaseHeight = 32,
            Seed = 7u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [.. layers]
                }
            ]
        };

    static int Count(TexturePlan plan, string kernel) {
        var count = 0;

        foreach (var op in plan.Ops) {
            if (string.Equals(op.Kernel, kernel, StringComparison.Ordinal)) {
                count++;
            }
        }

        return count;
    }

    static TextureOp Find(TexturePlan plan, string kernel) {
        foreach (var op in plan.Ops) {
            if (string.Equals(op.Kernel, kernel, StringComparison.Ordinal)) {
                return op;
            }
        }

        Assert.Fail($"no '{kernel}' op in this plan");

        throw new InvalidOperationException("unreachable");
    }

    static TextureOp Last(TexturePlan plan, string kernel) {
        for (var index = plan.Ops.Length - 1; index >= 0; index--) {
            if (string.Equals(plan.Ops[index].Kernel, kernel, StringComparison.Ordinal)) {
                return plan.Ops[index];
            }
        }

        Assert.Fail($"no '{kernel}' op in this plan");

        throw new InvalidOperationException("unreachable");
    }

    /// <summary>The one op of a kernel that answers a predicate, and a failure if there are two.</summary>
    static TextureOp Only(TexturePlan plan, string kernel, Func<TextureOp, bool> predicate) {
        TextureOp? found = null;

        foreach (var op in plan.Ops) {
            if (!string.Equals(op.Kernel, kernel, StringComparison.Ordinal) || !predicate(op)) {
                continue;
            }

            Assert.Null(found);

            found = op;
        }

        Assert.NotNull(found);

        return found;
    }
}
