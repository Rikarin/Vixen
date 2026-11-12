// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Graphics.Vulkan;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     Patina #115, #116 and #117: the tone, colour, shape and surface-finish filters, each run as a filter layer over a
///     stack and held to what it says it does.
/// </summary>
/// <remarks>
///     <c>VIXEN_FILTER_PICTURES</c> names a folder, and <see cref="A_before_and_after_for_a_person" /> writes each
///     filter over a real material there: the stack without it on the left, with it on the right, a row per channel.
/// </remarks>
public class FilterLibraryDeviceTests(ITestOutputHelper output) {
    [Fact]
    public void Smoothstep_eases_between_its_ends() {
        using var device = TexturingDevice.Open();
        var maps = Bake(device, Grey(0.6f), Filter("Adjust/Smoothstep"));

        // t = (0.6 − 0.25) / 0.5 = 0.7, and 3t² − 2t³ = 0.784.
        Assert.InRange(Red(maps, "baseColor"), 197, 203);
    }

    [Fact]
    public void Histogram_shift_wraps_what_passes_white() {
        using var device = TexturingDevice.Open();
        var maps = Bake(device, Grey(0.8f), Filter("Adjust/Histogram Shift", ("shift", "0.3")));

        Assert.InRange(Red(maps, "baseColor"), 23, 28);
    }

    [Fact]
    public void Height_adjust_scales_about_mid_grey_then_moves() {
        using var device = TexturingDevice.Open();
        var maps = Bake(device, Grey(0.6f), Filter("Adjust/Height Adjust", ("position", "0.1"), ("contrast", "2")));

        Assert.InRange(Red(maps, "height"), 201, 207);
    }

    [Fact]
    public void Colour_correct_brightens_and_takes_the_colour_out() {
        using var device = TexturingDevice.Open();
        var brighter = Bake(device, Grey(0.5f), Filter("Adjust/Colour Correct", ("brightness", "0.2")));

        // Output from 0.2 to 1.2: 0.2 + 0.5 = 0.7.
        Assert.InRange(Red(brighter, "baseColor"), 175, 181);

        var grey = Bake(
                device,
                Colour(0.8f, 0.6f, 0.4f),
                Filter("Adjust/Colour Correct", ("saturation", "0"))
            )["baseColor"]
            .Pixels;

        Assert.InRange(grey[0] - grey[2], -2, 2);
        Assert.InRange(grey[1] - grey[2], -2, 2);
    }

    [Fact]
    public void Colour_balance_tints_the_shadows_and_leaves_the_highlights() {
        using var device = TexturingDevice.Open();
        var dark = Bake(device, Grey(0.1f), Filter("Adjust/Colour Balance", ("shadowRed", "1")))["baseColor"].Pixels;

        // Weight 1 − smoothstep(0, ½, 0.1) = 0.896, so red is 0.1 + 0.4 · 0.896 = 0.458 and green stays 0.1.
        Assert.InRange(dark[0], 114, 120);
        Assert.InRange(dark[1], 24, 28);

        var bright = Bake(device, Grey(0.9f), Filter("Adjust/Colour Balance", ("shadowRed", "1")))["baseColor"].Pixels;

        Assert.InRange(bright[0], 228, 231);
    }

    [Fact]
    public void Drop_shadow_darkens_under_the_shape_it_reads() {
        using var device = TexturingDevice.Open();
        var shadow = Fed(
            Filter("Adjust/Drop Shadow"),
            "Shape",
            new() { Source = LayerInputSource.Generator, Generator = "Source/Shape" }
        );
        var colour = Bake(device, Grey(0.5f), shadow, 64)["baseColor"];

        // ⚠ A disc, which is one channel: in the middle the shadow is whole, 0.5 · (1 − 0.7), and in the corner there
        // is none. A shape read through `Grayscale` came out at a fifth of itself, which a white uniform could not show.
        Assert.InRange(colour.Pixels[(32 * 64 + 32) * 4], 36, 41);
        Assert.InRange(colour.Pixels[0], 126, 130);
    }

    /// <summary>
    ///     A material filter reads base colour and metalness and writes base colour alone: a non-metal darker than
    ///     charcoal turns magenta, one in range is left, and its roughness is not touched at all.
    /// </summary>
    [Theory]
    [InlineData(0.005f, 255, 0, 255)]
    [InlineData(0.5f, 128, 128, 128)]
    public void Pbr_validate_paints_what_is_out_of_range(float value, int red, int green, int blue) {
        using var device = TexturingDevice.Open();
        var maps = Bake(device, Grey(value), Filter("Adjust/PBR Validate"));
        var colour = maps["baseColor"].Pixels;

        Assert.InRange(colour[0], red - 2, red + 2);
        Assert.InRange(colour[1], green - 2, green + 2);
        Assert.InRange(colour[2], blue - 2, blue + 2);
        Assert.InRange(Red(maps, "roughness"), (int)(value * 255f) - 2, (int)(value * 255f) + 2);
    }

    /// <summary>
    ///     Brushing sets the roughness it is told, in streaks that run along the brushing: neighbours along a streak
    ///     differ less than neighbours across it.
    /// </summary>
    [Fact]
    public void Brushed_streaks_run_along_the_brushing() {
        using var device = TexturingDevice.Open();
        var maps = Bake(device, Grey(0.8f), Filter("Finishes/Brushed"), 64);
        var rough = maps["roughness"];

        Assert.InRange(Mean(rough), 0.3f, 0.4f);
        Assert.True(
            Step(rough, 1, 0) < Step(rough, 0, 1) * 0.5f,
            $"along {Step(rough, 1, 0)}, across {Step(rough, 0, 1)}"
        );
        Assert.True(Spread(maps["height"]) > 0.002f, "the brushing leaves no grooves in the height");
        Assert.InRange(Red(maps, "baseColor"), 202, 206);
    }

    /// <summary>Hammering dents the height and polishes it to the roughness it is told, on average.</summary>
    [Fact]
    public void Hammered_dents_the_height() {
        using var device = TexturingDevice.Open();
        var maps = Bake(device, Grey(0.8f), Filter("Finishes/Hammered"), 64);

        Assert.InRange(Mean(maps["roughness"]), 0.2f, 0.4f);
        Assert.True(Spread(maps["height"]) > 0.01f, "the hammer leaves no dents in the height");
    }

    /// <summary>A powder coat is an even roughness, with a small ripple in the height.</summary>
    [Fact]
    public void Powder_coat_is_an_even_satin() {
        using var device = TexturingDevice.Open();
        var maps = Bake(device, Grey(0.1f), Filter("Finishes/Powder Coat"), 64);

        Assert.InRange(Mean(maps["roughness"]), 0.5f, 0.6f);
        Assert.InRange(Spread(maps["roughness"]), 0.001f, 0.08f);
        Assert.True(Spread(maps["height"]) > 0.001f, "the coat has no ripple");
    }

    /// <summary>Each filter over a real material, the stack without it beside the stack with it.</summary>
    [Fact]
    public void A_before_and_after_for_a_person() {
        if (Environment.GetEnvironmentVariable("VIXEN_FILTER_PICTURES") is not { Length: > 0 } folder) {
            return;
        }

        Directory.CreateDirectory(folder);

        using var device = TexturingDevice.Open();

        output.WriteLine($"adapter: {TexturingDevice.Adapter(device)}");

        foreach (var (name, metal, filter, shown) in Pictures()) {
            var before = Bake(device, Material(metal, filter with { Enabled = false }), Side);
            var after = Bake(device, Material(metal, filter), Side);
            var sheet = new byte[2 * Side * shown.Length * Side * 4];

            for (var row = 0; row < shown.Length; row++) {
                Paste(sheet, before[shown[row]], 0, row);
                Paste(sheet, after[shown[row]], 1, row);
            }

            PngCodec.Save(Path.Combine(folder, $"{name}.png"), new(2 * Side, shown.Length * Side, sheet));
            output.WriteLine($"{name}: {string.Join(", ", shown)}");
        }
    }

    const int Side = 256;

    static IEnumerable<(string Name, bool Metal, LayerAsset Filter, string[] Shown)> Pictures() {
        string[] colour = ["baseColor"];
        string[] surface = ["roughness", "height", "normal"];

        yield return ("smoothstep", false, Filter("Adjust/Smoothstep", ("low", "0.15"), ("high", "0.45")), colour);
        yield return ("histogram shift", false, Filter("Adjust/Histogram Shift", ("shift", "0.35")), colour);
        yield return ("height adjust", false,
            Filter("Adjust/Height Adjust", ("position", "-0.15"), ("contrast", "1.6")) with { Channels = ["height"] },
            ["height", "normal"]);
        yield return ("colour correct", false,
            Filter(
                "Adjust/Colour Correct",
                ("contrast", "0.3"),
                ("gamma", "1.4"),
                ("hue", "-0.04"),
                ("saturation", "1.4")
            ), colour);
        yield return ("colour balance", false,
            Filter("Adjust/Colour Balance", ("shadowBlue", "0.6"), ("highlightRed", "0.5"), ("highlightGreen", "0.3")),
            colour);
        yield return (
            "drop shadow",
            false,
            Fed(
                Filter("Adjust/Drop Shadow", ("right", "0.04"), ("down", "0.05"), ("softness", "4")) with {
                    Channels = ["baseColor"]
                },
                "Shape",
                new() {
                    Source = LayerInputSource.Generator, Generator = "Source/Shape", Values = { ["Scale"] = [0.6f] }
                }
            ),
            colour
        );
        yield return ("pbr validate", true, Filter("Adjust/PBR Validate"), ["baseColor", "metalness"]);
        yield return ("brushed", true, Filter("Finishes/Brushed", ("angle", "20")), surface);
        yield return ("hammered", true, Filter("Finishes/Hammered"), surface);
        yield return ("powder coat", true, Filter("Finishes/Powder Coat"), surface);
        yield return (
            "warp by clouds",
            false,
            new LayerAsset {
                Id = "filter",
                Kind = LayerKind.Filter,
                FilterNode = "Filters/Warp",
                Settings = { ["Intensity"] = [0.6f] },
                Inputs = { ["Warp"] = new() { Source = LayerInputSource.Generator, Generator = "Noise/Clouds" } }
            },
            colour
        );
        yield return (
            "slope blur by height",
            false,
            new LayerAsset {
                Id = "filter",
                Kind = LayerKind.Filter,
                FilterNode = "Filters/Slope Blur",
                Channels = ["baseColor"],
                Settings = { ["Intensity"] = [12f] },
                Inputs = { ["Slope"] = LayerInputAsset.BeneathIn("height") }
            },
            colour
        );
        yield return (
            "occlusion from height",
            false,
            new LayerAsset {
                Id = "filter",
                Kind = LayerKind.Filter,
                FilterNode = "Surface/Height to AO",
                Channels = ["occlusion"],
                Inputs = { [OnlyImage("Surface/Height to AO")] = LayerInputAsset.BeneathIn("height") }
            },
            ["height", "occlusion"]
        );
    }

    static string OnlyImage(string path) {
        var library = LayerStackCompiler.Library(out _);

        library.TryGet(path, out var type);

        return type!.Ports.First(static port => port is {
                    Direction: Vixen.NodeGraph.PortDirection.Input, Kind: Vixen.NodeGraph.PortKind.Image
                }
            )
            .Name;
    }

    /// <summary>A plaster wall of bricks, or a metal panel, as the stack beneath a filter.</summary>
    static LayerStackAsset Material(bool metal, LayerAsset filter) {
        List<LayerAsset> layers = [
            new() {
                Id = "base",
                Values = {
                    ["baseColor"] = metal ? [0.6f, 0.6f, 0.62f, 1f] : [0.55f, 0.3f, 0.2f, 1f],
                    ["roughness"] = metal ? [0.25f, 0.25f, 0.25f, 1f] : [0.7f, 0.7f, 0.7f, 1f],
                    ["metalness"] = metal ? [1f, 1f, 1f, 1f] : [0f, 0f, 0f, 1f],
                    ["height"] = [0.5f, 0.5f, 0.5f, 1f],
                    ["occlusion"] = [1f, 1f, 1f, 1f]
                }
            },
            new() {
                Id = "shape",
                Fill = LayerFillSource.Graph,
                Graph = metal ? "Patterns/Panels" : "Patterns/Varied Brick",
                Channels = ["height"],
                Opacity = metal ? 0.4f : 1f
            },
            new() {
                Id = "tone",
                Fill = LayerFillSource.Graph,
                Graph = metal ? "Patterns/Panels" : "Patterns/Varied Brick",
                Channels = ["baseColor"],
                Blend = LayerBlendMode.Multiply,
                Opacity = 0.7f
            },
            new() {
                Id = "grime",
                Fill = LayerFillSource.Graph,
                Graph = "Grunges/Grunge Concrete",
                Channels = ["baseColor", "roughness"],
                Blend = LayerBlendMode.Overlay,
                Opacity = 0.6f
            }
        ];

        if (metal) {
            // A patch of paint too dark for any surface, for the validator to find.
            layers.Add(
                new() {
                    Id = "soot",
                    Channels = ["baseColor", "metalness"],
                    Values = { ["baseColor"] = [0.004f, 0.004f, 0.004f, 1f], ["metalness"] = [0f, 0f, 0f, 1f] },
                    Mask = new() { Source = LayerMaskSource.Generator, Generator = "Noise/Spots" }
                }
            );
        }

        layers.Add(filter);

        return Stack(["baseColor", "roughness", "metalness", "height", "occlusion"], [..layers]);
    }

    static LayerAsset Filter(string node, params (string Name, string Value)[] texts) {
        LayerAsset layer = new() { Id = "filter", Kind = LayerKind.Filter, FilterNode = node };

        foreach (var (name, value) in texts) {
            layer.Texts[name] = value;
        }

        return layer;
    }

    static LayerAsset Fed(LayerAsset layer, string port, LayerInputAsset input) {
        layer.Inputs[port] = input;

        return layer;
    }

    static LayerStackAsset Grey(float value) => Colour(value, value, value);

    static LayerStackAsset Colour(float red, float green, float blue) =>
        Stack(
            ["baseColor", "roughness", "height"],
            new LayerAsset {
                Id = "base",
                Values = {
                    ["baseColor"] = [red, green, blue, 1f],
                    ["roughness"] = [red, green, blue, 1f],
                    ["height"] = [red, green, blue, 1f]
                }
            }
        );

    static LayerStackAsset Bake(LayerStackAsset stack, LayerAsset filter) =>
        stack with { Sets = [stack.Sets[0] with { Layers = [..stack.Sets[0].Layers, filter] }] };

    static Dictionary<string, Bitmap> Bake(
        VulkanDevice device,
        LayerStackAsset stack,
        LayerAsset filter,
        int side = 16
    ) =>
        Bake(device, Bake(stack, filter), side);

    static Dictionary<string, Bitmap> Bake(VulkanDevice device, LayerStackAsset stack, int side) {
        stack = stack with { BaseWidth = side, BaseHeight = side };

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.True(compilation.Problems.IsEmpty, string.Join("; ", compilation.Problems));
        Assert.True(compilation.Diagnostics.IsEmpty, string.Join("; ", compilation.Diagnostics));
        Assert.NotNull(compilation.Plan);

        using TexturePlanEvaluator evaluator = new(device);
        using TextureUploads uploads = new(device);
        using var bake = evaluator.Evaluate(compilation.Plan, uploads.Externals);

        return compilation.Outputs.ToDictionary(
            static output => output.Usage,
            map => bake.Read(map.Image),
            StringComparer.Ordinal
        );
    }

    static int Red(Dictionary<string, Bitmap> maps, string usage) => maps[usage].Pixels[0];

    static float Mean(Bitmap map) {
        var sum = 0f;

        for (var index = 0; index < map.Width * map.Height; index++) {
            sum += map.Pixels[index * 4] / 255f;
        }

        return sum / (map.Width * map.Height);
    }

    static float Spread(Bitmap map) {
        var mean = Mean(map);
        var sum = 0f;

        for (var index = 0; index < map.Width * map.Height; index++) {
            var difference = map.Pixels[index * 4] / 255f - mean;

            sum += difference * difference;
        }

        return MathF.Sqrt(sum / (map.Width * map.Height));
    }

    /// <summary>The mean difference between a texel and its neighbour one step along an axis.</summary>
    static float Step(Bitmap map, int dx, int dy) {
        var sum = 0f;
        var count = 0;

        for (var y = 0; y < map.Height - dy; y++) {
            for (var x = 0; x < map.Width - dx; x++) {
                sum += MathF.Abs(map.Pixels[(y * map.Width + x) * 4] - map.Pixels[((y + dy) * map.Width + x + dx) * 4]);
                count++;
            }
        }

        return sum / count;
    }

    static void Paste(byte[] sheet, Bitmap map, int column, int row) {
        var width = 2 * Side;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var from = (y * Side + x) * 4;
                var to = ((row * Side + y) * width + column * Side + x) * 4;

                sheet[to] = map.Pixels[from];
                sheet[to + 1] = map.Pixels[from + 1];
                sheet[to + 2] = map.Pixels[from + 2];
                sheet[to + 3] = 255;
            }
        }
    }

    static LayerStackAsset Stack(string[] channels, params LayerAsset[] layers) =>
        FilterInputTests.Stack(channels, layers);
}
