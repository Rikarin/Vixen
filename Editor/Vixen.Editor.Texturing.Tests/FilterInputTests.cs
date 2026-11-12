// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Patina #113 and #114: where a filter's images come from, as the file says and as the build wires it.</summary>
public class FilterInputTests {
    /// <summary>A warp whose field nobody chose is a slot nobody filled: refused for a bake, left out of a draft.</summary>
    /// <remarks>
    ///     ⚠ <b>Left out whole in a draft</b>, so the graph holds no warp with an unwired field for the compile to trip
    ///     over: every slot is decided before the node is added.
    /// </remarks>
    [Fact]
    public void A_second_image_nobody_chose_is_refused_and_a_draft_leaves_the_filter_out() {
        var stack = Stack(Base(), new LayerAsset { Id = "warp", Kind = LayerKind.Filter, FilterNode = "Filters/Warp" });

        var baked = LayerStackGraph.Build(stack, stack.Sets[0]);

        Assert.Contains(
            baked.Problems,
            static problem => problem is { Severity: NodeSeverity.Error, Layer: "warp" }
                && problem.Message.Contains("reads 'Warp' from nothing yet", StringComparison.Ordinal)
        );

        var drafted = LayerStackGraph.Build(stack, stack.Sets[0], draft: true);

        Assert.False(drafted.HasErrors, string.Join("; ", drafted.Problems));
        Assert.DoesNotContain(drafted.Graph.Nodes, static node => node.Type == "Filters/Warp");
    }

    /// <summary>An input the node does without is left unwired, and the filter compiles as it is.</summary>
    [Fact]
    public void An_input_the_node_does_without_needs_no_source() {
        var stack = Stack(
            Base(),
            new LayerAsset {
                Id = "swap",
                Kind = LayerKind.Filter,
                FilterNode = "Colour/Channel Shuffle",
                Texts = { ["Red From"] = "FirstGreen" }
            }
        );

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);
    }

    /// <summary>What a host offers a slot for: the subject first, and whether each other input can be left empty.</summary>
    [Fact]
    public void A_filters_inputs_say_which_is_the_subject_and_which_can_be_left_empty() {
        var library = LayerStackCompiler.Library(out var compounds);

        library.TryGet("Filters/Warp", out var warp);
        library.TryGet("Colour/Channel Shuffle", out var shuffle);

        Assert.Equal(
            [
                new("Input", warp!.Port("Input", PortDirection.Input)!.Summary, true, false, string.Empty),
                new LayerFilterInput(
                    "Warp",
                    warp.Port("Warp", PortDirection.Input)!.Summary,
                    false,
                    false,
                    string.Empty
                )
            ],
            LayerStackGraph.Inputs(warp, library, compounds)
        );
        Assert.True(LayerStackGraph.Inputs(shuffle!, library, compounds)[1].Optional);
        Assert.True(TextureImageInputs.Measures(library, compounds, "Filters/Warp", "Warp"));
        Assert.False(TextureImageInputs.Measures(library, compounds, "Filters/Warp", "Input"));
    }

    /// <summary>Every source the file can name comes back from it as it went in.</summary>
    [Fact]
    public void Every_source_survives_the_file() {
        var stack = Stack(
            Base(),
            new LayerAsset {
                Id = "f",
                AnchorPoint = "Here",
                Kind = LayerKind.Filter,
                FilterNode = "Colour/Dissolve",
                Inputs = {
                    ["Background"] = LayerInputAsset.BeneathIn("height"),
                    ["Foreground"] = new() {
                        Source = LayerInputSource.Generator,
                        Generator = "Source/Noise",
                        Values = { ["Scale"] = [6f] },
                        Texts = { ["Basis"] = "Worley" }
                    },
                    ["Mask"] = new() { Source = LayerInputSource.Texture, Asset = "images/mask.png" }
                },
                Mask = new() {
                    Source = LayerMaskSource.Constant,
                    Effects = [
                        new() {
                            Node = "Filters/Warp",
                            Inputs = { ["Warp"] = new() { Source = LayerInputSource.Bake, Map = "curvature" } }
                        },
                        new() {
                            Node = "Filters/Slope Blur",
                            Inputs = { ["Slope"] = LayerInputAsset.Of(new() { Anchor = "Here", Channel = "height" }) }
                        }
                    ]
                }
            }
        );

        var read = LayerStackYaml.Read(LayerStackYaml.Write(stack)).Sets[0].Layers[1];

        Assert.Equal(LayerInputSource.Beneath, read.Inputs["Background"].Source);
        Assert.Equal("height", read.Inputs["Background"].Channel);
        Assert.Equal(LayerInputSource.Generator, read.Inputs["Foreground"].Source);
        Assert.Equal("Source/Noise", read.Inputs["Foreground"].Generator);
        Assert.Equal([6f], read.Inputs["Foreground"].Values["Scale"]);
        Assert.Equal("Worley", read.Inputs["Foreground"].Texts["Basis"]);
        Assert.Equal("images/mask.png", read.Inputs["Mask"].Asset);
        Assert.Equal(LayerInputSource.Bake, read.Mask.Effects[0].Inputs["Warp"].Source);
        Assert.Equal("curvature", read.Mask.Effects[0].Inputs["Warp"].Map);
        Assert.Equal(
            new() { Anchor = "Here", Channel = "height" },
            read.Mask.Effects[1].Inputs["Slope"].Anchor
        );
    }

    /// <summary>An image and a mesh map are read through the nodes a texture fill and a bake mask use.</summary>
    [Theory]
    [InlineData(LayerInputSource.Texture, "Source/Bitmap")]
    [InlineData(LayerInputSource.Bake, "Source/Mesh Map")]
    [InlineData(LayerInputSource.Generator, "Noise/Clouds")]
    public void Each_source_is_the_node_that_reads_it(LayerInputSource source, string node) {
        var stack = Stack(
            Base(),
            new LayerAsset {
                Id = "warp",
                Kind = LayerKind.Filter,
                FilterNode = "Filters/Warp",
                Channels = ["baseColor"],
                Inputs = {
                    ["Warp"] = new() {
                        Source = source, Asset = "images/field.png", Map = "curvature", Generator = "Noise/Clouds"
                    }
                }
            }
        );

        var build = LayerStackGraph.Build(stack, stack.Sets[0]);

        Assert.False(build.HasErrors, string.Join("; ", build.Problems));

        var warp = Assert.Single(build.Graph.Nodes, static candidate => candidate.Type == "Filters/Warp");
        var field = Assert.Single(build.Graph.Edges, edge => edge.To.Node == warp.Id && edge.To.Port == "Warp");

        // The field measures, so it arrives through a grey of what it reads.
        var grey = build.Graph.Nodes.Single(candidate => candidate.Id == field.From.Node);

        Assert.Equal("Colour/Grayscale", grey.Type);

        // ⚠ A generator or a mesh map is mostly one channel, which `Grayscale` reads as (r, 0, 0) and would weigh down to
        // a fifth under the luminance weights: it is read for its red. An image is a picture, read for its luminance.
        Assert.Equal(
            source == LayerInputSource.Texture ? (float?)null : 0f,
            grey.Values.TryGetValue("Weight G", out var green) ? green[0] : (float?)null
        );

        var read = Assert.Single(build.Graph.Edges, edge => edge.To.Node == grey.Id);

        Assert.Equal(node, build.Graph.Nodes.Single(candidate => candidate.Id == read.From.Node).Type);
    }

    /// <summary>A generator is a node that reads no image, and one that does is refused by name.</summary>
    [Fact]
    public void A_generator_that_reads_an_image_is_refused() {
        var stack = Stack(
            Base(),
            new LayerAsset {
                Id = "warp",
                Kind = LayerKind.Filter,
                FilterNode = "Filters/Warp",
                Inputs = { ["Warp"] = new() { Source = LayerInputSource.Generator, Generator = "Filters/Blur" } }
            }
        );

        var build = LayerStackGraph.Build(stack, stack.Sets[0], draft: true);

        Assert.Contains(
            build.Problems,
            static problem => problem.Message.Contains("is not a generator", StringComparison.Ordinal)
        );
    }

    /// <summary>A mask is one picture, so a step reading the layers beneath names the channel it reads.</summary>
    [Fact]
    public void A_mask_step_reading_the_layers_beneath_names_its_channel() {
        var stack = Stack(
            Base(),
            new LayerAsset {
                Id = "m",
                Values = { ["roughness"] = [1f, 1f, 1f, 1f] },
                Mask = new() {
                    Source = LayerMaskSource.Constant,
                    Value = 0.5f,
                    Effects = [
                        new() { Node = "Filters/Warp", Inputs = { ["Warp"] = LayerInputAsset.BeneathIn(string.Empty) } }
                    ]
                }
            }
        );

        var build = LayerStackGraph.Build(stack, stack.Sets[0]);

        Assert.Contains(
            build.Problems,
            static problem => problem.Message.Contains("in no channel", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ A filter reading the layers beneath it in a channel whose layers read this one is a loop, and the graph
    ///     model refuses it as it refuses an anchor's.
    /// </summary>
    /// <remarks>
    ///     The filter writes height and reads roughness beneath it; the roughness beneath it is masked by the anchor
    ///     point the filter publishes, read in height. Neither half is a loop alone.
    /// </remarks>
    [Fact]
    public void A_filter_reading_layers_that_read_it_is_refused_as_a_loop() {
        var stack = Stack(
            Base(),
            new LayerAsset {
                Id = "rough",
                Channels = ["roughness"],
                Values = { ["roughness"] = [1f, 1f, 1f, 1f] },
                Mask = new() {
                    Source = LayerMaskSource.Anchor, Anchor = new() { Anchor = "Raised", Channel = "height" }
                }
            },
            new LayerAsset {
                Id = "f",
                AnchorPoint = "Raised",
                Kind = LayerKind.Filter,
                FilterNode = "Colour/Levels",
                Channels = ["height"],
                Inputs = { ["Input"] = LayerInputAsset.BeneathIn("roughness") }
            }
        );

        var build = LayerStackGraph.Build(stack, stack.Sets[0]);

        Assert.Contains(
            build.Problems,
            static problem => problem.Severity == NodeSeverity.Error
                && (problem.Message.Contains("reads the layers beneath it in 'roughness'", StringComparison.Ordinal)
                    || problem.Message.Contains("anchor point 'Raised' is refused", StringComparison.Ordinal))
        );
    }

    /// <summary>A channel the set does not have is not one to read beneath.</summary>
    [Fact]
    public void Reading_beneath_in_a_channel_the_set_lacks_is_refused() {
        var stack = Stack(
            Base(),
            new LayerAsset {
                Id = "f",
                Kind = LayerKind.Filter,
                FilterNode = "Colour/Levels",
                Inputs = { ["Input"] = LayerInputAsset.BeneathIn("emissive") }
            }
        );

        var build = LayerStackGraph.Build(stack, stack.Sets[0]);

        Assert.Contains(
            build.Problems,
            static problem => problem.Message.Contains("'emissive', which is not a channel")
        );
    }

    internal static LayerAsset Base() =>
        new() {
            Id = "base",
            Values = {
                ["baseColor"] = [0.2f, 0.4f, 0.6f, 1f],
                ["roughness"] = [0.2f, 0.2f, 0.2f, 1f],
                ["height"] = [0.7f, 0.7f, 0.7f, 1f]
            }
        };

    internal static LayerStackAsset Stack(params LayerAsset[] layers) =>
        Stack(["baseColor", "roughness", "height"], layers);

    internal static LayerStackAsset Stack(string[] channels, params LayerAsset[] layers) =>
        new() {
            Name = "Inputs",
            BaseWidth = 16,
            BaseHeight = 16,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [
                        ..channels.Select(usage => new ChannelAsset { Usage = usage, Default = [0.5f, 0.5f, 0.5f, 1f] })
                    ],
                    Layers = [..layers]
                }
            ]
        };
}
