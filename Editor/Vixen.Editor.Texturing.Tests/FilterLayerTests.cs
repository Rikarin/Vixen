// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     Issue <a href="https://github.com/Rikarin/Vixen/issues/807">#807</a>'s third defect: nothing
///     proved a filter's setting reached its node, and two of the five kinds were never compiled.
/// </summary>
/// <remarks>
///     ⚠ <b>The gap was in the tests rather than in the compiler, and that is the finding.</b>
///     <c>LayerFilterKind</c> has five members; before this file, <c>Blur</c>, <c>Levels</c> and
///     <c>Hsl</c> appeared in a test and <c>Invert</c> and <c>Grayscale</c> appeared in none — so a
///     wrong node path or a wrong port name on either would have compiled to a graph whose edges
///     silently did not land, and no suite would have said so.
/// </remarks>
public class FilterLayerTests(ITestOutputHelper output) {
    /// <summary>Every filter kind compiles, and every number it declares reaches its op.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Proved by <em>difference</em> rather than by reading the parameter, and that is
    ///         what makes it derived.</b> A port's name in the layer stack — <c>Input Black</c> — and
    ///         its name in the emitted op — <c>inputBlack</c> — are two different spellings, and a
    ///         table mapping one to the other here would be a third list that could drift from both.
    ///         Compiling the same layer twice with two values for one port and requiring the plans to
    ///         differ needs no such table: if the value reaches the op the plans cannot match, and if
    ///         it does not they cannot differ.
    ///     </para>
    ///     <para>
    ///         <b>0 and 1 rather than two arbitrary numbers</b>, because <c>Colour/Invert</c>'s four
    ///         ports are flags: <c>TextureEmitter.Flag</c> is <c>Number(port) != 0</c>, so 0.25 and
    ///         0.75 are both <em>true</em> and a pair like that would report all four of them
    ///         unreachable when they are fine.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_filter_kind_compiles_and_every_number_it_takes_reaches_its_op() {
        foreach (var kind in Enum.GetValues<LayerFilterKind>()) {
            var (type, ports) = LayerStackGraph.Filter(kind);
            var plain = Compile(kind, null);

            Assert.NotNull(plain.Plan);
            Assert.Empty(plain.Problems);
            Assert.Empty(plain.Diagnostics);

            output.WriteLine($"{kind} -> {type}, {ports.Length} numbers");

            foreach (var port in ports) {
                var low = Compile(kind, (port, 0f));
                var high = Compile(kind, (port, 1f));

                Assert.NotNull(low.Plan);
                Assert.NotNull(high.Plan);
                Assert.Empty(low.Problems);
                Assert.Empty(low.Diagnostics);

                Assert.False(
                    string.Equals(
                        LayerStackDifferential.Describe(low.Plan),
                        LayerStackDifferential.Describe(high.Plan),
                        StringComparison.Ordinal
                    ),
                    $"'{kind}' declares the number '{port}', and a layer that sets it to 0 compiles to the "
                    + $"same plan as one that sets it to 1. Either '{type}' has no such port — in which case "
                    + "the value was dropped and the filter is running on its defaults — or the node reads it "
                    + "under another name. Both are silent: the picture is a filter nobody configured."
                );
            }
        }
    }

    /// <summary>A grayscale filter really does make the picture grey, on a device.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>Colour/Grayscale</c> is the one filter whose output is a one-channel image,
    ///         and this is the check that the other three channels survive it.</b> Its kernel writes
    ///         <c>float4(grey, grey, grey, alpha)</c> — splatted on purpose, because grey and colour
    ///         are one port kind — while the node asks for a <c>TextureChannels.Grey</c> target. If
    ///         that pairing ever stored red alone, a grayscale filter would tint the whole stack red
    ///         instead of desaturating it, and every structural assertion above would still pass.
    ///     </para>
    ///     <para>
    ///         <b>The oracle is Rec. 709 and closed form.</b> Pure red is 0.2126 grey — 54 in a byte —
    ///         in all three channels. Red staying 255 would mean the filter never ran; green or blue
    ///         at 0 would mean the splat was lost.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_grayscale_filter_greys_every_channel_rather_than_only_red() {
        using var device = TexturingDevice.Open();
        var adapter = TexturingDevice.Adapter(device);

        LayerStackAsset stack = new() {
            Name = "Grey",
            BaseWidth = 16,
            BaseHeight = 16,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [1f, 0f, 0f, 1f] }],
                    Layers = [new() { Id = "g", Kind = LayerKind.Filter, Filter = LayerFilterKind.Grayscale }]
                }
            ]
        };

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.NotNull(compilation.Plan);

        using TexturePlanEvaluator evaluator = new(device);
        using TextureUploads uploads = new(device);
        using var bake = evaluator.Evaluate(compilation.Plan, uploads.Externals);

        var picture = bake.Read(LayerStackDifferential.ImageOf(compilation, "baseColor"));
        var red = picture.Pixels[0];
        var green = picture.Pixels[1];
        var blue = picture.Pixels[2];

        output.WriteLine($"{adapter}: pure red through a grayscale filter baked ({red}, {green}, {blue})");

        Assert.True(
            red is >= 52 and <= 56 && green is >= 52 and <= 56 && blue is >= 52 and <= 56,
            $"{adapter}: pure red through a grayscale filter baked ({red}, {green}, {blue}) and Rec. 709 "
            + "says 0.2126 in all three — 54. (255, 0, 0) would mean the filter never reached the "
            + "composite; (54, 0, 0) would mean its one-channel target threw the splat away and a "
            + "'grayscale' filter is a red tint."
        );
    }

    /// <summary>One filter layer over a red base, with at most one of its numbers set.</summary>
    static LayerStackCompilation Compile(LayerFilterKind kind, (string Port, float Value)? setting) {
        LayerAsset layer = new() { Id = "f", Kind = LayerKind.Filter, Filter = kind };

        if (setting is { } set) {
            layer.Settings[set.Port] = [set.Value];
        }

        LayerStackAsset stack = new() {
            Name = "Filters",
            BaseWidth = 16,
            BaseHeight = 16,
            Seed = 9u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0.5f, 0.25f, 0.75f, 1f] }],
                    Layers = [layer]
                }
            ]
        };

        return LayerStackCompiler.Compile(stack, stack.Sets[0]);
    }
}
