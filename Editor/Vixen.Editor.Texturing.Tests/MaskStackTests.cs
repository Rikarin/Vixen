// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § D10's mask stack: a base, entries over it, effects, and anchors.</summary>
public class MaskStackTests(ITestOutputHelper output) {
    /// <summary>The shipped generators are node types a mask can actually name.</summary>
    /// <remarks>
    ///     ⚠ <b>The precondition nothing had.</b> <c>TextureCompoundLibrary.Publish</c> had no caller
    ///     outside its own tests (#799), so the compounds this assembly ships were unreachable from a
    ///     stack — and a generator mask could not be authored, let alone baked. This asserts that the
    ///     library a stack compiles against is the one with them in it.
    /// </remarks>
    [Fact]
    public void The_shipped_generators_are_reachable_from_a_stack() {
        var registry = LayerStackCompiler.Library(out var subGraphs);

        Assert.NotNull(subGraphs);

        foreach (var path in TextureCompoundLibrary.Shipped) {
            output.WriteLine(path);

            Assert.True(
                registry.TryGet(path, out _),
                $"'{path}' ships in this assembly and is not a node type a stack can name. Doc 48 § D5's "
                + "'the several hundred nodes are content' is only true if the content is published."
            );
        }

        Assert.NotEmpty(TextureCompoundLibrary.Shipped);
    }

    /// <summary>A generator mask compiles, and asks for the mesh maps it reads.</summary>
    /// <remarks>
    ///     ⚠ <b>The externals are the whole of why one generator works on every mesh.</b> A generator
    ///     names no file: it asks for <c>meshmap:curvature</c> and <c>meshmap:ao</c>, which a host
    ///     resolves against whichever mesh is being baked. So "no rewiring" is not a claim about the
    ///     compound — it is a consequence of the compound naming a measurement rather than an asset.
    /// </remarks>
    [Fact]
    public void A_generator_mask_asks_for_the_mesh_maps_it_reads() {
        var stack = Dirt();
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        foreach (var problem in compilation.Problems) {
            output.WriteLine(problem.ToString());
        }

        foreach (var diagnostic in compilation.Diagnostics) {
            output.WriteLine(diagnostic.ToString());
        }

        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);

        List<string> asked = [];

        foreach (var external in compilation.Externals) {
            asked.Add(external.Asset);
        }

        asked.Sort(StringComparer.Ordinal);
        output.WriteLine(string.Join(", ", asked));

        Assert.Contains("meshmap:curvature", asked);
        Assert.Contains("meshmap:ao", asked);
    }

    /// <summary>
    ///     Doc 48 § D10 — a generator authored once produces a plausible mask on two different
    ///     meshes with no rewiring.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The criterion that had never been measured.</b> Three generator compounds shipped
    ///         and none had produced a picture, because nothing published them (#799) — so this is
    ///         the first time <c>Generators/Dirt</c> has been evaluated at all.
    ///     </para>
    ///     <para>
    ///         <b>"No rewiring" is asserted by construction: there is one stack and one
    ///         compilation.</b> Only the uploaded mesh maps differ between the two bakes, which is
    ///         exactly what changes when the same material is applied to a different mesh. A test
    ///         that compiled twice could not tell that apart from a stack edited in between.
    ///     </para>
    ///     <para>
    ///         <b>And "plausible" is three properties rather than an eyeball</b>: the mask varies
    ///         across the surface, the two meshes disagree, and the response to occlusion is
    ///         monotone — <c>Dirt</c> is a levelled curvature multiplied by a levelled occlusion, and
    ///         multiplication by a monotone function of AO must move every texel the same way when AO
    ///         rises everywhere. The third is the one that would catch a mask that varied for the
    ///         wrong reason.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_generator_authored_once_masks_two_different_meshes() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        var stack = Dirt();
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);

        // One compilation, three meshes. Only the bakes differ.
        var first = Bake(device, compilation, Mesh(Horizontal, Vertical));
        var second = Bake(device, compilation, Mesh(Vertical, Horizontal));
        var brighter = Bake(device, compilation, Mesh(Horizontal, Raised));

        output.WriteLine($"{adapter}: mesh A {Describe(first)}");
        output.WriteLine($"{adapter}: mesh B {Describe(second)}");
        output.WriteLine($"{adapter}: mesh A with more AO {Describe(brighter)}");

        // 1. It is a picture rather than a flat colour.
        Assert.True(
            Spread(first) >= 16,
            $"{adapter}: the dirt mask on mesh A runs {Describe(first)}, which is very nearly flat. A "
            + "generator that produced a constant would satisfy every structural assertion in this file "
            + "and mean nothing — doc 48 § D10's word is 'plausible'."
        );

        // 2. It is a picture *of the mesh*.
        Assert.False(
            Same(first, second),
            $"{adapter}: the same compound baked identical masks on two meshes whose curvature and "
            + "occlusion were exchanged. A generator reads the mesh maps by usage, so a mask that does "
            + "not change when they do is reading something else — or nothing."
        );

        // 3. And it responds to occlusion in one direction, which a mask varying for the wrong
        //    reason would not. `Dirt` is levels(curvature) · levels(ao): monotone in each.
        int up = 0, down = 0;

        for (var index = 0; index < first.Pixels.Length; index += 4) {
            if (brighter.Pixels[index] > first.Pixels[index]) {
                up++;
            } else if (brighter.Pixels[index] < first.Pixels[index]) {
                down++;
            }
        }

        output.WriteLine($"{adapter}: raising AO moved {up} texels up and {down} down");

        Assert.True(
            up + down > 0 && (up == 0 || down == 0),
            $"{adapter}: raising the occlusion map everywhere moved {up} texels up and {down} down. "
            + "Dirt is a levelled curvature multiplied by a levelled occlusion, so it is monotone in "
            + "AO and every texel has to move the same way — or not at all, which would mean the "
            + "occlusion branch reaches nothing."
        );
    }

    /// <summary>
    ///     #573's exit criterion — a cycle through an anchor is refused as it is made, by the graph
    ///     model, from inside a mask stack.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The anchor is an <em>entry</em> of the mask rather than its base, which is the path a
    ///     mask stack added.</b> Doc 48 § D10 says the stack compiles <em>through</em>
    ///     <c>NodeGraphModel</c>, which already refuses a cycle at construction, rather than growing a
    ///     second check of its own — so the proof that it is that check and not a lookup which
    ///     happened to fail is the wording: <c>GraphInvariants.Describe</c>'s words for
    ///     <c>GraphConnectionError.Cycle</c>, which nothing in <c>Vixen.Editor.Texturing</c> writes.
    /// </remarks>
    [Fact]
    public void An_anchor_inside_a_mask_stack_onto_a_layer_above_is_a_cycle_the_graph_refuses() {
        LayerStackAsset stack = new() {
            Name = "Cycle",
            BaseWidth = 32,
            BaseHeight = 32,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [
                        new() {
                            Id = "under",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                            Mask = new() {
                                Source = LayerMaskSource.Constant,
                                Value = 1f,
                                Layers = [
                                    new() {
                                        Source = LayerMaskSource.Anchor,
                                        Anchor = "over",
                                        Blend = LayerBlendMode.Multiply
                                    }
                                ]
                            }
                        },
                        new() {
                            Id = "over",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [1f, 0f, 0f, 1f] }
                        }
                    ]
                }
            ]
        };

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        foreach (var problem in compilation.Problems) {
            output.WriteLine(problem.ToString());
        }

        Assert.Null(compilation.Plan);

        var refusal = Assert.Single(compilation.Problems);

        Assert.Equal("under", refusal.Layer);
        Assert.Contains("loop", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An anchor onto a layer beneath is an ordinary edge, and the mask stack compiles.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half of the refusal above, and the reason it is here.</b> A check that
    ///     refused every anchor would pass the cycle test and be useless; this is the case that has to
    ///     stay legal for that one to mean anything.
    /// </remarks>
    [Fact]
    public void An_anchor_inside_a_mask_stack_onto_a_layer_beneath_compiles() {
        LayerStackAsset stack = new() {
            Name = "Anchor",
            BaseWidth = 32,
            BaseHeight = 32,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [
                        new() {
                            Id = "under",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [1f, 0f, 0f, 1f] }
                        },
                        new() {
                            Id = "over",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                            Mask = new() {
                                Source = LayerMaskSource.Constant,
                                Value = 1f,
                                Layers = [
                                    new() {
                                        Source = LayerMaskSource.Anchor,
                                        Anchor = "under",
                                        Blend = LayerBlendMode.Multiply
                                    }
                                ]
                            }
                        }
                    ]
                }
            ]
        };

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);
    }

    /// <summary>A mask stack composites its entries, with a value arithmetic decides.</summary>
    /// <remarks>
    ///     ⚠ <b>A half multiplied by a half is a quarter, and a mask that ignored its second entry
    ///     would be a half.</b> 64 against 128 — no tolerance to widen, and the two numbers are far
    ///     enough apart that no rounding reaches from one to the other.
    /// </remarks>
    [Fact]
    public void A_mask_stack_composites_its_entries() {
        using var device = TexturingDevice.Open();

        var stack = Masked(new() {
            Source = LayerMaskSource.Constant,
            Value = 0.5f,
            Layers = [
                new() { Source = LayerMaskSource.Constant, Value = 0.5f, Blend = LayerBlendMode.Multiply }
            ]
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.NotNull(compilation.Plan);

        var red = BakeFlat(device, compilation);

        output.WriteLine($"{TexturingDevice.Adapter(device)}: a half multiplied by a half baked {red}");

        Assert.True(
            red is >= 62 and <= 66,
            $"{TexturingDevice.Adapter(device)}: the mask is a constant half with a second constant half "
            + $"multiplied over it, so the layer is revealed at a quarter — 64. It baked {red}. 128 "
            + "would mean the second entry never reached the composite."
        );
    }

    /// <summary>A mask effect is any node with one image in and one out.</summary>
    [Fact]
    public void A_mask_effect_is_any_single_input_graph() {
        var stack = Masked(new() {
            Source = LayerMaskSource.Constant,
            Value = 1f,
            Effects = [new() { Node = "Colour/Levels", Values = { ["Input Black"] = [0.25f] } }]
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);

        var levels = Assert.Single(Ops(compilation.Plan, "Levels"));

        Assert.Equal(0.25f, levels.Find("inputBlack")!.Value.Value, 5);
    }

    /// <summary>A published compound is a mask effect too, with no C# type of its own.</summary>
    /// <remarks>
    ///     ⚠ <b>The claim doc 48 § 4.10 makes and a <c>MaskEffectKind</c> enum could not keep.</b>
    ///     <c>Utility/Histogram Scan</c> is content — a <c>.vxtexgraph</c> in a folder — and it is a
    ///     mask effect for exactly the reason <c>Colour/Levels</c> is: one image in, one image out.
    /// </remarks>
    [Fact]
    public void A_published_compound_is_a_mask_effect() {
        var stack = Masked(new() {
            Source = LayerMaskSource.Constant,
            Value = 0.5f,
            Effects = [new() { Node = "Utility/Histogram Scan" }]
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        foreach (var problem in compilation.Problems) {
            output.WriteLine(problem.ToString());
        }

        Assert.Empty(compilation.Problems);
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);
    }

    /// <summary>A node with two images is a composite rather than an effect, and is refused.</summary>
    [Fact]
    public void A_two_image_node_is_not_a_mask_effect() {
        var stack = Masked(new() {
            Source = LayerMaskSource.Constant,
            Value = 1f,
            Effects = [new() { Node = "Colour/Blend" }]
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Null(compilation.Plan);
        Assert.Contains(
            compilation.Problems,
            problem => problem.Message.Contains("single-input", StringComparison.Ordinal)
        );
    }

    /// <summary>A value named after the image port is dropped, never written over the wire.</summary>
    /// <remarks>
    ///     ⚠ <b>The port it would otherwise have reached carries the mask itself</b>, so the picture
    ///     would be the effect over a constant — an adjustment of nothing, at full strength, with no
    ///     diagnostic anywhere.
    /// </remarks>
    [Fact]
    public void A_mask_effect_value_cannot_overwrite_the_image_it_adjusts() {
        var stack = Masked(new() {
            Source = LayerMaskSource.Constant,
            Value = 1f,
            Effects = [new() { Node = "Colour/Levels", Values = { ["Input"] = [0f] } }]
        });

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);

        var dropped = Assert.Single(compilation.Problems);

        Assert.Equal(NodeGraph.NodeSeverity.Warning, dropped.Severity);
        Assert.Contains("Input", dropped.Message, StringComparison.Ordinal);

        // And the levels still reads the mask rather than a constant.
        Assert.Empty(compilation.Diagnostics);
    }

    /// <summary>A bake mask is one mesh map, by what it measures.</summary>
    [Fact]
    public void A_bake_mask_reads_one_mesh_map_by_usage() {
        var stack = Masked(new() { Source = LayerMaskSource.Bake, Map = "curvature" });
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);

        var external = Assert.Single(compilation.Externals);

        Assert.Equal("meshmap:curvature", external.Asset);
    }

    /// <summary>A map nothing bakes is refused by the node, rather than by a second list here.</summary>
    /// <remarks>
    ///     ⚠ <b>The refusal is a <em>node</em> diagnostic and not a layer problem</b>, which is the
    ///     evidence that <c>LayerStackGraph</c> writes the usage through instead of checking it. A
    ///     list of the nine in the layer stack could disagree with <c>TextureMeshMaps.Known</c>, and
    ///     the way it would disagree is by silently accepting a name the bake never writes.
    /// </remarks>
    [Fact]
    public void A_bake_mask_naming_nothing_is_refused_by_the_node() {
        var stack = Masked(new() { Source = LayerMaskSource.Bake, Map = "sparkle" });
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.NotEmpty(compilation.Diagnostics);
    }

    /// <summary>Doc 48 exit criterion 6 still holds with masks in the stack.</summary>
    /// <remarks>
    ///     ⚠ <b>A mask stack is where the round trip is most likely to lose something</b>, because it
    ///     is the newest shape in the file: an entry's blend mode, an effect's node path and its
    ///     numbers all have to survive being written as YAML and read back, and every one of them
    ///     changes an op if it does not. <c>TexturePlan.SeedFor</c> mixes an op's index into its seed,
    ///     so a single dropped setting moves every procedural op after it.
    /// </remarks>
    [Fact]
    public void A_stack_with_masks_and_its_explosion_compile_to_the_same_plan() {
        var stack = Masked(new() {
            Source = LayerMaskSource.Constant,
            Value = 0.5f,
            Layers = [
                new() { Source = LayerMaskSource.Bake, Map = "curvature", Blend = LayerBlendMode.Multiply },
                new() {
                    Source = LayerMaskSource.Constant,
                    Value = 0.75f,
                    Blend = LayerBlendMode.Screen,
                    Opacity = 0.5f
                }
            ],
            Effects = [new() { Node = "Colour/Levels", Values = { ["Input White"] = [0.8f] } }]
        });

        var (direct, exploded) = LayerStackDifferential.Both(stack);

        // ⚠ The instrument first. Two plans that both lost the mask would compare equal and this
        // test would be the claim that nothing equals nothing — the shape a comparator that called
        // three empty manifests identical already had once in this repository. So the ops the mask
        // stack is *made of* are counted before the two are compared: the bake entry's Bitmap, the
        // effect's Levels, and one Blend per mask entry on top of the layer's own.
        Assert.Single(Ops(direct.Plan!, "Levels"));
        Assert.Equal(3, Ops(direct.Plan!, "Blend").Count);
        Assert.NotEmpty(direct.Externals);

        LayerStackDifferential.AssertSamePlan(direct.Plan!, exploded.Plan!);
    }

    /// <summary>A stack of one white layer under the given mask.</summary>
    static LayerStackAsset Masked(MaskAsset mask) =>
        new() {
            Name = "Masked",
            BaseWidth = 16,
            BaseHeight = 16,
            Seed = 5u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [
                        new() {
                            Id = "l",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                            Mask = mask
                        }
                    ]
                }
            ]
        };

    static List<TextureOp> Ops(TexturePlan plan, string kernel) {
        List<TextureOp> found = [];

        foreach (var op in plan.Ops) {
            if (string.Equals(op.Kernel, kernel, StringComparison.Ordinal)) {
                found.Add(op);
            }
        }

        return found;
    }

    /// <summary>Bakes a plan that needs no externals, and returns the first texel's red.</summary>
    static byte BakeFlat(VulkanDevice device, LayerStackCompilation compilation) {
        using TexturePlanEvaluator evaluator = new(device);
        using TextureUploads uploads = new(device);
        using var bake = evaluator.Evaluate(compilation.Plan!, uploads.Externals);

        return bake.Read(LayerStackDifferential.ImageOf(compilation, "baseColor")).Pixels[0];
    }

    /// <summary>A stack whose one layer is masked by <c>Generators/Dirt</c>.</summary>
    internal static LayerStackAsset Dirt() =>
        new() {
            Name = "Generator",
            BaseWidth = 32,
            BaseHeight = 32,
            Seed = 11u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [
                        new() {
                            Id = "dirt",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                            Mask = new() { Source = LayerMaskSource.Generator, Generator = "Generators/Dirt" }
                        }
                    ]
                }
            ]
        };

    /// <summary>How many texels across a supplied mesh map is.</summary>
    const int Side = 16;

    /// <summary>A horizontal ramp — a plausible curvature, and one with a direction.</summary>
    static byte Horizontal(int x, int y) => (byte)(x * 255 / (Side - 1));

    /// <summary>A vertical ramp.</summary>
    static byte Vertical(int x, int y) => (byte)(y * 255 / (Side - 1));

    /// <summary>The vertical ramp lifted, so that occlusion rises at every texel at once.</summary>
    static byte Raised(int x, int y) => (byte)Math.Min(255, (y * 255 / (Side - 1)) + 40);

    /// <summary>One mesh, as the two maps <c>Generators/Dirt</c> asks for.</summary>
    static Dictionary<string, Func<int, int, byte>> Mesh(
        Func<int, int, byte> curvature,
        Func<int, int, byte> occlusion
    ) =>
        new(StringComparer.Ordinal) {
            ["meshmap:curvature"] = curvature,
            ["meshmap:ao"] = occlusion
        };

    /// <summary>Bakes the one channel, with each mesh map supplied under the name it asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>A map the generator asked for and this table does not have fails rather than being
    ///     filled with black.</b> A silently black occlusion map would make the mask a plausible
    ///     picture of nothing, which is the failure this whole test exists to rule out.
    /// </remarks>
    static Bitmap Bake(
        VulkanDevice device,
        LayerStackCompilation compilation,
        Dictionary<string, Func<int, int, byte>> maps
    ) {
        var plan = compilation.Plan!;

        using TextureUploads uploads = new(device);

        foreach (var external in compilation.Externals) {
            Assert.True(
                maps.TryGetValue(external.Asset, out var map),
                $"the plan asks for the external '{external.Asset}' and this test has no picture for it."
            );

            uploads.Add(plan, external.Image, Side, Side, Texels(map!));
        }

        using TexturePlanEvaluator evaluator = new(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        return bake.Read(LayerStackDifferential.ImageOf(compilation, "baseColor"));
    }

    /// <summary>One map, RGBA8 and grey.</summary>
    static byte[] Texels(Func<int, int, byte> map) {
        var texels = new byte[Side * Side * 4];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var value = map(x, y);
                var offset = ((y * Side) + x) * 4;

                texels[offset] = value;
                texels[offset + 1] = value;
                texels[offset + 2] = value;
                texels[offset + 3] = 255;
            }
        }

        return texels;
    }

    static int Spread(Bitmap picture) {
        byte low = 255;
        byte high = 0;

        for (var index = 0; index < picture.Pixels.Length; index += 4) {
            low = Math.Min(low, picture.Pixels[index]);
            high = Math.Max(high, picture.Pixels[index]);
        }

        return high - low;
    }

    static string Describe(Bitmap picture) {
        byte low = 255;
        byte high = 0;

        for (var index = 0; index < picture.Pixels.Length; index += 4) {
            low = Math.Min(low, picture.Pixels[index]);
            high = Math.Max(high, picture.Pixels[index]);
        }

        return $"red {low}…{high} over {picture.Width}×{picture.Height}";
    }

    static bool Same(Bitmap first, Bitmap second) => first.Pixels.AsSpan().SequenceEqual(second.Pixels);
}
