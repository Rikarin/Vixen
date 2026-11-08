// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.TextureGraph;
using Vixen.Texturing;
using Vixen.Texturing.Layers;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Patina #107: a group flattened into a canvas composites the picture its children did.</summary>
/// <remarks>
///     ⚠ <b>Against the stack's own picture, texel by texel, in every channel.</b> A capture that kept the backdrop of
///     a pass-through group would lay it over itself again, and one that dropped an isolated group's transparency would
///     cover what the group never touched; both are a picture that differs by far more than a canvas's rounding.
/// </remarks>
public sealed class LayerFlattenDeviceTests(ITestOutputHelper output) : IDisposable {
    const int Size = 64;

    static readonly TextureLibrary Library = TextureNodeLibrary.Publish(null);

    readonly string folder = Path.Combine(Path.GetTempPath(), $"vixen-flatten-{Guid.NewGuid():N}");

    public void Dispose() {
        if (Directory.Exists(folder)) {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>The group isolated by its own mode, and the group passing through, flatten to the same picture.</summary>
    [Theory]
    [InlineData(LayerBlendMode.Copy)]
    [InlineData(LayerBlendMode.Multiply)]
    [InlineData(LayerBlendMode.Overlay)]
    public void A_flattened_group_composites_what_its_children_did(LayerBlendMode blend) {
        var stack = Stack(Group(blend));

        Assert.True(LayerStackEdit.CanFlatten(stack.Sets[0], "g", out var backdrop, out var reason), reason);
        Assert.False(backdrop);

        Compare(stack, "g", backdrop, $"group-{blend}");
    }

    /// <summary>The whole stack, gathered into one pass-through group whose children read what is beneath them.</summary>
    [Fact]
    public void A_stack_flattened_whole_composites_what_it_did() {
        var layered = Stack(Group(LayerBlendMode.Copy));
        var set = layered.Sets[0];
        var whole = layered with {
            Sets = [set with { Layers = [new() { Id = "all", Kind = LayerKind.Group, Children = [.. set.Layers] }] }]
        };

        Assert.True(LayerStackEdit.CanFlatten(whole.Sets[0], "all", out var backdrop, out var reason), reason);
        Assert.True(backdrop);

        Compare(whole, "all", backdrop, "stack");
    }

    /// <summary>A pass-through group holding a filter is refused, because no canvas could hold its picture alone.</summary>
    [Fact]
    public void A_pass_through_group_that_reads_beneath_it_is_refused_above_the_bottom() {
        var stack = Stack(Group(LayerBlendMode.Copy, filter: true));

        Assert.False(LayerStackEdit.CanFlatten(stack.Sets[0], "g", out _, out var reason));
        Assert.Contains("passes through", reason, StringComparison.Ordinal);
    }

    /// <summary>An anchor point inside a group that something outside reads cannot be flattened away.</summary>
    [Fact]
    public void A_group_whose_anchor_point_is_read_from_outside_is_refused() {
        var stack = Stack(Group(LayerBlendMode.Multiply));
        var set = stack.Sets[0];
        var group = set.Layers[1];
        var read = set with {
            Layers = [
                set.Layers[0],
                group with { Children = [group.Children[0] with { AnchorPoint = "Rust" }, .. group.Children[1..]] },
                .. set.Layers[2..],
                new() {
                    Id = "outside",
                    Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                    Mask = new() {
                        Source = LayerMaskSource.Anchor,
                        Anchor = new() { Anchor = "Rust", Channel = "roughness" }
                    }
                }
            ]
        };

        Assert.False(LayerStackEdit.CanFlatten(read, "g", out _, out var reason));
        Assert.Contains("Rust", reason, StringComparison.Ordinal);
    }

    void Compare(LayerStackAsset stack, string group, bool backdrop, string name) {
        using var device = TexturingDevice.Open();
        using TexturePlanEvaluator evaluator = new(device);
        Directory.CreateDirectory(folder);
        PaintCanvasStore store = new(long.MaxValue);
        var path = Path.Combine(folder, "stack.vxlayers");
        var set = stack.Sets[0];

        var before = Pictures(evaluator, store, path, stack, "", false);

        // What flattening writes: each channel of the capture, into a canvas beside the stack.
        PaintCanvas canvas = new(Size, Size);

        foreach (var (usage, picture) in Pictures(evaluator, store, path, stack, group, backdrop)) {
            canvas.Channel(usage).CopyFrom(picture.Pixels);
        }

        store.Adopt(Path.Combine(folder, "flat.vxpaint"), canvas);

        var flattened = stack with {
            Sets = [set with { Layers = Flattened(set.Layers, group) }]
        };
        var after = Pictures(evaluator, store, path, flattened, "", false);

        Same(before, after, name, device);

        // ⚠ A canvas that is not its backdrop's is a cache of the group alone: a layer put beneath it afterwards shows
        // through the flattened group exactly as through the group itself. One that kept its backdrop would cover it.
        if (!backdrop) {
            var under = Pictures(evaluator, store, path, Under(stack, group), "", false);

            Same(under, Pictures(evaluator, store, path, Under(flattened, group), "", false), $"{name}-under", device);
        }

        // ⚠ And the comparison could have failed: with the group switched off the picture is another one, so the group
        // is visibly in it and a canvas that held nothing would not have passed.
        var without = stack with {
            Sets = [set with { Layers = [.. Switched(set.Layers, group)] }]
        };
        var bare = Pictures(evaluator, store, path, without, "", false)["baseColor"].Pixels;
        var moved = bare.Zip(before["baseColor"].Pixels, static (a, b) => Math.Abs(a - b)).Max();

        Assert.True(moved > 20, $"{name}: the group changes the picture by only {moved}, so matching it proves nothing.");
    }

    void Same(
        Dictionary<string, Bitmap> before,
        Dictionary<string, Bitmap> after,
        string name,
        Vixen.Graphics.Vulkan.VulkanDevice device
    ) {
        foreach (var (usage, picture) in before) {
            var worst = 0;

            for (var at = 0; at < picture.Pixels.Length; at++) {
                worst = Math.Max(worst, Math.Abs(picture.Pixels[at] - after[usage].Pixels[at]));
            }

            Keep($"{name}-{usage}-before", picture);
            Keep($"{name}-{usage}-flattened", after[usage]);
            output.WriteLine($"{TexturingDevice.Adapter(device)}: {name} {usage} differs by at most {worst} of 255");

            Assert.True(
                worst <= 3,
                $"{name}: the flattened '{usage}' differs from the stack's own by {worst} of 255 somewhere; a canvas "
                + "rounds to eight bits once, so anything past a few is a different picture."
            );
        }
    }

    /// <summary>The same stack with a green layer put just beneath the group, which nothing in the group reads.</summary>
    static LayerStackAsset Under(LayerStackAsset stack, string group) {
        var set = stack.Sets[0];
        var at = set.Layers.FindIndex(layer => layer.Id == group);
        List<LayerAsset> layers = [.. set.Layers];

        layers.Insert(
            at,
            new() {
                Id = "under",
                Opacity = 0.6f,
                Values = { ["baseColor"] = [0.1f, 0.6f, 0.2f, 1f], ["roughness"] = [0.2f, 0.2f, 0.2f, 1f] }
            }
        );

        return stack with { Sets = [set with { Layers = layers }] };
    }

    static IEnumerable<LayerAsset> Switched(List<LayerAsset> layers, string group) =>
        layers.Select(layer => layer.Id == group
            ? layer with { Enabled = false }
            : layer with { Children = [.. Switched(layer.Children, group)] });

    static List<LayerAsset> Flattened(List<LayerAsset> layers, string group) => [
        .. layers.Select(layer => layer.Id == group
            ? layer with { Paint = "flat.vxpaint" }
            : layer with { Children = Flattened(layer.Children, group) })
    ];

    static Dictionary<string, Bitmap> Pictures(
        TexturePlanEvaluator evaluator,
        PaintCanvasStore store,
        string path,
        LayerStackAsset stack,
        string capture,
        bool backdrop
    ) {
        using LayerStackEvaluator run = new(evaluator, store, path) { Capture = capture, Backdrop = backdrop };

        Assert.Empty(run.Prepare(stack, stack.Sets[0], Library.Registry, Library.SubGraphs));

        using var bake = run.Evaluate();

        return run.Outputs.Where(static output => output.Usage is "baseColor" or "roughness")
            .ToDictionary(static output => output.Usage, output => bake.Read(output.Image));
    }

    /// <summary>A picture for a person to look at, when <c>VIXEN_FLATTEN_PICTURES</c> names a folder.</summary>
    static void Keep(string name, Bitmap picture) {
        if (Environment.GetEnvironmentVariable("VIXEN_FLATTEN_PICTURES") is { Length: > 0 } pictures) {
            Directory.CreateDirectory(pictures);
            File.WriteAllBytes(Path.Combine(pictures, $"{name}.png"), PngCodec.Encode(picture));
        }
    }

    static LayerAsset Group(LayerBlendMode blend, bool filter = false) =>
        new() {
            Id = "g",
            Name = "Rust",
            Kind = LayerKind.Group,
            Blend = blend,
            Opacity = 0.85f,
            Mask = new() { Source = LayerMaskSource.Anchor, Anchor = new() { Anchor = "Base", Channel = "baseColor" } },
            Children = [
                new() {
                    Id = "rust",
                    Values = { ["baseColor"] = [0.8f, 0.3f, 0.1f, 1f], ["roughness"] = [0.9f, 0.9f, 0.9f, 1f] },
                    Opacity = 0.7f,
                    Mask = new() {
                        Source = LayerMaskSource.Anchor,
                        Anchor = new() { Anchor = "Base", Channel = "roughness" }
                    }
                },
                new() {
                    Id = "dust",
                    Fill = LayerFillSource.Graph,
                    Graph = "Source/Checker",
                    Channels = ["baseColor"],
                    Opacity = 0.4f
                },
                .. filter
                    ? (LayerAsset[]) [new() { Id = "levels", Kind = LayerKind.Filter, Settings = { ["Gamma"] = [1.6f] } }]
                    : []
            ]
        };

    static LayerStackAsset Stack(LayerAsset group) =>
        new() {
            Name = "Flatten",
            BaseWidth = Size,
            BaseHeight = Size,
            Seed = 7u,
            Sets = [
                new() {
                    Name = "Body",
                    Channels = [
                        new() { Usage = "baseColor", Default = [0.5f, 0.5f, 0.5f, 1f] },
                        new() { Usage = "roughness", Default = [0.5f, 0.5f, 0.5f, 1f] }
                    ],
                    Layers = [
                        new() {
                            Id = "base",
                            AnchorPoint = "Base",
                            Fill = LayerFillSource.Graph,
                            Graph = "Source/Noise",
                            Settings = { ["Scale"] = [6f] }
                        },
                        group,
                        new() {
                            Id = "warm",
                            Kind = LayerKind.Filter,
                            Filter = LayerFilterKind.Hsl,
                            Settings = { ["Hue"] = [0.05f] }
                        }
                    ]
                }
            ]
        };
}
