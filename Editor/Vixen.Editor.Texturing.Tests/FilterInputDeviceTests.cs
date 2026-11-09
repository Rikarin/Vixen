// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Vulkan;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Xunit;
using static Vixen.Editor.Texturing.Tests.FilterInputTests;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Patina #113 and #114 on a device: a filter's images are the pictures the file says.</summary>
public class FilterInputDeviceTests(ITestOutputHelper output) {
    /// <summary>
    ///     A filter writing roughness and reading height beneath it hands roughness the height: 0.7, not the 0.2 the
    ///     roughness beneath was. In both orders of the set's channels, so the input is wired whether the height's walk
    ///     comes before the roughness's or after it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_filter_reads_another_channel_beneath_it(bool heightFirst) {
        using var device = TexturingDevice.Open();
        var stack = Stack(
            heightFirst ? ["baseColor", "height", "roughness"] : ["baseColor", "roughness", "height"],
            Base(),
            new LayerAsset {
                Id = "f",
                Kind = LayerKind.Filter,
                FilterNode = "Colour/Levels",
                Channels = ["roughness"],
                Inputs = { ["Input"] = LayerInputAsset.BeneathIn("height") }
            }
        );

        var rough = BakeRed(device, stack, "roughness");
        var height = BakeRed(device, stack, "height");

        output.WriteLine($"{TexturingDevice.Adapter(device)}: roughness {rough}, height {height}");

        Assert.InRange(rough, 176, 181);
        Assert.InRange(height, 176, 181);
    }

    /// <summary>A second image read from a generator: a shuffle taking its red from a uniform of a quarter.</summary>
    [Fact]
    public void A_filter_reads_a_generator() {
        using var device = TexturingDevice.Open();
        var stack = Stack(
            Base(),
            new LayerAsset {
                Id = "f",
                Kind = LayerKind.Filter,
                FilterNode = "Colour/Channel Shuffle",
                Channels = ["baseColor"],
                Texts = { ["Red From"] = "SecondRed" },
                Inputs = {
                    ["Second"] = new() {
                        Source = LayerInputSource.Generator,
                        Generator = "Source/Uniform",
                        Values = { ["Colour"] = [0.25f, 0.25f, 0.25f, 1f] }
                    }
                }
            }
        );

        Assert.InRange(BakeRed(device, stack, "baseColor"), 62, 66);
    }

    /// <summary>
    ///     An instance of a filter reading a generator as its second image bakes what its source bakes, with the source
    ///     switched off so that the picture is the instance's alone (Patina #108 with #113).
    /// </summary>
    [Fact]
    public void An_instance_of_a_filter_reads_its_sources_second_image() {
        using var device = TexturingDevice.Open();
        var filter = new LayerAsset {
            Id = "f",
            Kind = LayerKind.Filter,
            FilterNode = "Colour/Channel Shuffle",
            Channels = ["baseColor"],
            Texts = { ["Red From"] = "SecondRed" },
            Inputs = {
                ["Second"] = new() {
                    Source = LayerInputSource.Generator,
                    Generator = "Source/Uniform",
                    Values = { ["Colour"] = [0.25f, 0.25f, 0.25f, 1f] }
                }
            }
        };
        var instance = new LayerAsset { Id = "copy", Kind = LayerKind.Instance, Instance = new() { Layer = "f" } };

        var source = BakeRed(device, Stack(Base(), filter), "baseColor");
        var copied = BakeRed(device, Stack(Base(), filter with { Enabled = false }, instance), "baseColor");

        Assert.InRange(source, 62, 66);
        Assert.Equal(source, copied);
    }

    /// <summary>A mask step reads another channel too: a mask of a quarter shuffled to take its red from height beneath.</summary>
    [Fact]
    public void A_mask_step_reads_a_channel_beneath_its_layer() {
        using var device = TexturingDevice.Open();
        var stack = Stack(
            Base(),
            new LayerAsset {
                Id = "white",
                Channels = ["baseColor"],
                Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                Mask = new() {
                    Source = LayerMaskSource.Constant,
                    Value = 0.25f,
                    Layers = [new() { Source = LayerMaskSource.Constant, Value = 0.25f }],
                    Effects = [
                        new() {
                            Node = "Colour/Channel Shuffle",
                            Texts = { ["Red From"] = "SecondRed" },
                            Inputs = { ["Second"] = LayerInputAsset.BeneathIn("height") }
                        }
                    ]
                }
            }
        );

        // 0.2 under white at the height's 0.7: 0.2 + 0.8 · 0.7 = 0.76.
        Assert.InRange(BakeRed(device, stack, "baseColor"), 192, 196);
    }

    static byte BakeRed(VulkanDevice device, LayerStackAsset stack, string usage) {
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Plan);

        using TexturePlanEvaluator evaluator = new(device);
        using TextureUploads uploads = new(device);
        using var bake = evaluator.Evaluate(compilation.Plan, uploads.Externals);

        return bake.Read(LayerStackDifferential.ImageOf(compilation, usage)).Pixels[0];
    }
}
