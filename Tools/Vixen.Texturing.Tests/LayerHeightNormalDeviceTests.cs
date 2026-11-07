// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>
///     Painted height reaches the surface through the set's normal. It used to reach the height file and nothing else,
///     so a bump painted in height changed no pixel of what the viewport lit.
/// </summary>
public sealed class LayerHeightNormalDeviceTests : IDisposable {
    const int Size = 256;
    const int Middle = Size / 2;
    const int Radius = 40;

    /// <summary>How far from the middle the disc's falloff is steepest: inside its plateau and short of its foot.</summary>
    const int Slope = Radius - 7;

    static readonly TextureLibrary Library = TextureNodeLibrary.Publish(null);

    readonly string folder = Path.Combine(Path.GetTempPath(), $"vixen-height-{Guid.NewGuid():N}");

    public LayerHeightNormalDeviceTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void A_set_with_no_height_writes_no_normal_it_did_not_declare() {
        var build = LayerStackGraph.Build(Stack(["baseColor"], LayerHeightNormal.Smooth, out _), Set(["baseColor"]));

        Assert.DoesNotContain(Outputs(build), static usage => usage == "normal");
    }

    [Fact]
    public void A_height_writes_one_normal_whether_or_not_the_set_declares_one() {
        foreach (var channels in new[] { new[] { "height" }, ["normal", "height"], ["height", "normal"] }) {
            var build = LayerStackGraph.Build(Stack(channels, LayerHeightNormal.Smooth, out _), Set(channels));

            Assert.False(build.HasErrors, string.Join("; ", build.Problems));
            Assert.Single(Outputs(build), static usage => usage == "normal");
            Assert.Single(Outputs(build), static usage => usage == "height");
        }
    }

    /// <summary>
    ///     A raised disc leans its normal away from its middle on every side: red under a half on its left edge and over
    ///     it on its right, and green under a half on its top edge, which is the Y-down convention the kernel derives.
    ///     Far from it the surface is flat.
    /// </summary>
    [Theory]
    [InlineData(LayerHeightNormal.Sharp, false)]
    [InlineData(LayerHeightNormal.Smooth, false)]
    [InlineData(LayerHeightNormal.Smooth, true)]
    public void A_painted_disc_of_height_leans_the_normal_around_it(LayerHeightNormal method, bool declared) {
        using var device = OpenDevice();
        using var evaluator = new TexturePlanEvaluator(device);
        string[] channels = declared ? ["normal", "height"] : ["height"];
        using var run = Open(evaluator, channels, method, out var image);

        Disc(image);
        var normal = Normal(run, clean: false);

        Assert.Equal((128, 128, 255), Texel(normal, 10, 10));
        Assert.Equal((128, 128, 255), Texel(normal, Middle, Middle));
        Assert.True(Texel(normal, Middle - Slope, Middle).R < 110, $"left edge {Texel(normal, Middle - Slope, Middle)}");
        Assert.True(Texel(normal, Middle + Slope, Middle).R > 146, $"right edge {Texel(normal, Middle + Slope, Middle)}");
        Assert.True(Texel(normal, Middle, Middle - Slope).G < 110, $"top edge {Texel(normal, Middle, Middle - Slope)}");
        Assert.True(Texel(normal, Middle, Middle + Slope).G > 146, $"bottom edge {Texel(normal, Middle, Middle + Slope)}");
        Assert.Equal(normal, Normal(run, clean: true));
    }

    [Fact]
    public void Sharp_and_smooth_are_two_pictures() {
        using var device = OpenDevice();
        using var evaluator = new TexturePlanEvaluator(device);
        using var sharp = Open(evaluator, ["height"], LayerHeightNormal.Sharp, out var first);
        using var smooth = Open(evaluator, ["height"], LayerHeightNormal.Smooth, out var second);

        Disc(first);
        Disc(second);

        Assert.NotEqual(Normal(sharp, clean: true), Normal(smooth, clean: true));
    }

    /// <summary>A normal channel under the height keeps its own lean where no height was painted.</summary>
    [Fact]
    public void The_height_is_combined_over_the_normal_channel_and_does_not_replace_it() {
        using var device = OpenDevice();
        using var evaluator = new TexturePlanEvaluator(device);
        var tilted = new[] { 0.7f, 0.5f, 0.92f, 1f };
        using var run = Open(evaluator, ["normal", "height"], LayerHeightNormal.Smooth, out var image, tilted);

        Disc(image);
        var normal = Normal(run, clean: true);

        var far = Texel(normal, 10, 10);
        Assert.InRange(far.R, 175, 185);
        Assert.NotEqual(far, Texel(normal, Middle - Radius, Middle));
    }

    static IReadOnlyList<string> Outputs(LayerStackBuild build) => [
        ..build.Graph.Nodes.Where(static node => node.Type == "Output/Output").Select(static node => node.Texts["Usage"])
    ];

    static (int R, int G, int B) Texel(byte[] pixels, int x, int y) {
        var at = ((y * Size) + x) * 4;

        return (pixels[at], pixels[at + 1], pixels[at + 2]);
    }

    static byte[] Normal(LayerStackEvaluator run, bool clean) {
        using var bake = clean ? run.EvaluateClean() : run.Evaluate();

        return bake.Read(run.Outputs.Single(static o => o.Usage == "normal").Image).Pixels;
    }

    static void Disc(PaintImage image) {
        var session = PaintSession.Begin(
            new(image, PaintCoverage.Everywhere(image.Width, image.Height), Gutter: 0),
            PaintBrush.Default with { Radius = Radius, Flow = 1f, Falloff = 0.3f },
            0xFFFFFFFFu
        );

        session.Move(new Vector2(Middle, Middle));
        session.End("Disc");
    }

    static TextureSetAsset Set(string[] channels, LayerHeightNormal method = LayerHeightNormal.Smooth, float[]? normal = null) =>
        new() {
            Name = "Body",
            HeightNormal = method,
            Channels = [
                ..channels.Select(usage => new ChannelAsset {
                        Usage = usage,
                        Default = usage switch {
                            "normal" => normal ?? [0.5f, 0.5f, 1f, 1f],
                            "height" => [0.5f, 0.5f, 0.5f, 1f],
                            _ => [0.5f, 0.5f, 0.5f, 1f]
                        }
                    }
                )
            ]
        };

    static LayerStackAsset Stack(string[] channels, LayerHeightNormal method, out string paint, float[]? normal = null) {
        paint = $"{Guid.NewGuid():N}.vxpaint";

        return new() {
            Name = "Stack",
            BaseWidth = Size,
            BaseHeight = Size,
            Sets = [
                Set(channels, method, normal) with {
                    Layers = [
                        new() {
                            Id = "painted",
                            Kind = LayerKind.Paint,
                            Paint = paint,
                            Channels = ["height"]
                        }
                    ]
                }
            ]
        };
    }

    LayerStackEvaluator Open(
        TexturePlanEvaluator evaluator,
        string[] channels,
        LayerHeightNormal method,
        out PaintImage image,
        float[]? normal = null
    ) {
        var stack = Stack(channels, method, out var paint, normal);

        using (var stream = File.Create(Path.Combine(folder, paint))) {
            new PaintCanvas(Size, Size).Write(stream);
        }

        var store = new PaintCanvasStore(long.MaxValue);

        image = store.Open(Path.Combine(folder, paint))!.Channel("height");

        var run = new LayerStackEvaluator(evaluator, store, Path.Combine(folder, $"{Guid.NewGuid():N}.vxlayers"));

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
