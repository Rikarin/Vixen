// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Vulkan;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>
///     A set's bake reaches its shading: the baked normal lies under the set's normal and the baked occlusion multiplies
///     its occlusion, whether or not the set has those channels. Before, a bake reached masks and nothing else, so a
///     low mesh baked well still showed and exported flat (Patina #86).
/// </summary>
public sealed class LayerBakeMixDeviceTests : IDisposable {
    const int Size = 64;

    static readonly TextureLibrary Library = TextureNodeLibrary.Publish();

    static readonly HashSet<string> Both = new(StringComparer.Ordinal) { "normal", "ao" };

    /// <summary>A baked normal leaning right and down the image as glTF reads it, which is up as a set reads it.</summary>
    static readonly byte[] BakedNormal = [200, 90, 220, 255];

    static readonly byte[] BakedOcclusion = [128, 128, 128, 255];

    readonly string folder = Path.Combine(Path.GetTempPath(), $"vixen-bakemix-{Guid.NewGuid():N}");

    public LayerBakeMixDeviceTests() {
        Directory.CreateDirectory(folder);
    }

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void With_no_bake_a_set_writes_only_its_channels() {
        var stack = Stack(["baseColor"]);

        Assert.Equal(["baseColor"], Outputs(LayerStackGraph.Build(stack, stack.Sets[0])));
    }

    [Fact]
    public void A_bake_gives_a_set_with_no_such_channels_a_normal_and_an_occlusion() {
        var stack = Stack(["baseColor"]);

        Assert.Equal(
            ["baseColor", "normal", "occlusion"],
            Outputs(LayerStackGraph.Build(stack, stack.Sets[0], baked: Both)).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void A_bake_switched_off_or_a_map_the_bake_lacks_writes_nothing() {
        var stack = Stack(["baseColor"], LayerBakeMix.Off);
        HashSet<string> occlusionOnly = new(StringComparer.Ordinal) { "ao" };

        Assert.Equal(["baseColor", "occlusion"], Outputs(LayerStackGraph.Build(stack, stack.Sets[0], baked: Both)));
        Assert.Equal(
            ["baseColor", "occlusion"],
            Outputs(LayerStackGraph.Build(Stack(["baseColor"]), Stack(["baseColor"]).Sets[0], baked: occlusionOnly))
        );
    }

    [Fact]
    public void A_sets_bake_mixes_survive_the_file_and_the_default_writes_nothing() {
        var mixed = LayerStackYaml.Write(Stack(["baseColor"], LayerBakeMix.Replace, LayerBakeMix.Off));
        var read = LayerStackYaml.Read(mixed).Sets[0];

        Assert.Equal((LayerBakeMix.Replace, LayerBakeMix.Off), (read.BakedNormal, read.BakedOcclusion));
        Assert.DoesNotContain("baked", LayerStackYaml.Write(Stack(["baseColor"])), StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_and_one_layers_amount_read_no_bake() {
        var stack = Stack(["baseColor", "occlusion"]);
        var weights = LayerStackGraph.Weights(stack, stack.Sets[0]);

        Assert.DoesNotContain(weights.Graph.Nodes, static node => node.Type == "Source/Mesh Map");
    }

    /// <summary>
    ///     With nothing painted the set's normal is the bake's, turned to point green down as a set's normals do, and
    ///     its occlusion is the bake's. The set's own flat normal and white occlusion change nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Combined_the_set_shows_the_bake_where_nothing_is_painted(bool declared) {
        using var device = OpenDevice();
        using var evaluator = new TexturePlanEvaluator(device);
        string[] channels = declared ? ["baseColor", "normal", "occlusion"] : ["baseColor"];
        using var run = Open(evaluator, Stack(channels));

        var normal = Read(run, "normal");
        var occlusion = Read(run, "occlusion");

        Assert.InRange(normal[0], BakedNormal[0] - 3, BakedNormal[0] + 3);
        Assert.InRange(normal[1], 255 - BakedNormal[1] - 3, 255 - BakedNormal[1] + 3);
        Assert.InRange(occlusion[0], 125, 131);
    }

    /// <summary>
    ///     A painted occlusion of a half over a baked half is a quarter when combined, and a half when the bake is
    ///     where the channel starts and the paint covers it.
    /// </summary>
    [Theory]
    [InlineData(LayerBakeMix.Combine, 64)]
    [InlineData(LayerBakeMix.Replace, 128)]
    [InlineData(LayerBakeMix.Off, 128)]
    public void Painted_occlusion_is_multiplied_by_the_bake_or_covers_it(LayerBakeMix mix, int expected) {
        using var device = OpenDevice();
        using var evaluator = new TexturePlanEvaluator(device);
        var stack = Stack(["baseColor", "occlusion"], occlusion: mix);
        stack = stack with {
            Sets = [
                stack.Sets[0] with {
                    Layers = [
                        new() {
                            Id = "shadow", Kind = LayerKind.Fill, Values = { ["occlusion"] = [0.5f, 0.5f, 0.5f, 1f] }
                        }
                    ]
                }
            ]
        };
        using var run = Open(evaluator, stack);

        Assert.InRange(Read(run, "occlusion")[0], expected - 3, expected + 3);
    }

    /// <summary>
    ///     A normal filled flat over a replaced bake covers it; combined, the flat fill leaves the bake as it was, since
    ///     a flat detail reoriented into any base is that base.
    /// </summary>
    [Theory]
    [InlineData(LayerBakeMix.Combine, true)]
    [InlineData(LayerBakeMix.Replace, false)]
    public void A_flat_normal_fill_keeps_a_combined_bake_and_covers_a_replaced_one(LayerBakeMix mix, bool kept) {
        using var device = OpenDevice();
        using var evaluator = new TexturePlanEvaluator(device);
        var stack = Stack(["baseColor", "normal"], mix);
        stack = stack with {
            Sets = [
                stack.Sets[0] with {
                    Layers = [
                        new() { Id = "flat", Kind = LayerKind.Fill, Values = { ["normal"] = [0.5f, 0.5f, 1f, 1f] } }
                    ]
                }
            ]
        };
        using var run = Open(evaluator, stack);

        var red = Read(run, "normal")[0];

        if (kept) {
            Assert.InRange(red, BakedNormal[0] - 3, BakedNormal[0] + 3);
        } else {
            Assert.InRange(red, 125, 131);
        }
    }

    static IReadOnlyList<string> Outputs(LayerStackBuild build) => [
        ..build.Graph.Nodes.Where(static node => node.Type == "Output/Output")
            .Select(static node => node.Texts["Usage"])
    ];

    static byte[] Read(LayerStackEvaluator run, string usage) {
        using var bake = run.EvaluateClean();
        var pixels = bake.Read(run.Outputs.Single(o => o.Usage == usage).Image).Pixels;
        var middle = (Size / 2 * Size + Size / 2) * 4;

        return pixels[middle..(middle + 4)];
    }

    static LayerStackAsset Stack(
        string[] channels,
        LayerBakeMix normal = LayerBakeMix.Combine,
        LayerBakeMix occlusion = LayerBakeMix.Combine
    ) =>
        new() {
            Name = "Stack",
            BaseWidth = Size,
            BaseHeight = Size,
            Sets = [
                new() {
                    Name = "Body",
                    BakedNormal = normal,
                    BakedOcclusion = occlusion,
                    Channels = [
                        ..channels.Select(static usage => new ChannelAsset {
                                Usage = usage,
                                Default = usage switch {
                                    "normal" => [0.5f, 0.5f, 1f, 1f],
                                    "occlusion" => [1f, 1f, 1f, 1f],
                                    _ => [0.5f, 0.5f, 0.5f, 1f]
                                }
                            }
                        )
                    ]
                }
            ]
        };

    LayerStackEvaluator Open(TexturePlanEvaluator evaluator, LayerStackAsset stack) {
        var run = new LayerStackEvaluator(
            evaluator,
            new(long.MaxValue),
            Path.Combine(folder, $"{Guid.NewGuid():N}.vxlayers"),
            static (uploads, plan, owed) => {
                foreach (var external in owed) {
                    var texel = external.Asset.EndsWith(":normal", StringComparison.Ordinal)
                        ? BakedNormal
                        : BakedOcclusion;
                    uploads.AddPicture(plan, external.Image, 1, 1, texel, TextureFormat.Rgba8);
                }

                return [];
            }
        ) { Baked = Both };

        Assert.Empty(run.Prepare(stack, stack.Sets[0], Library.Registry, Library.SubGraphs));

        return run;
    }

    static VulkanDevice OpenDevice() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device, so nothing here can be proved");

        throw new InvalidOperationException("unreachable");
    }
}
