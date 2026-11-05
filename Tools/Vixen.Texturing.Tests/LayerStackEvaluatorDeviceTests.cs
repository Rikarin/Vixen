// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Authoring;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>What a stroke repairs against what a clean evaluation produces, on a real device.</summary>
/// <remarks>
///     ⚠ <b>The evaluator's most important test, and painting's (plan § 7).</b> A repair that left one
///     stale texel would be a picture that looks right and exports wrong, and only a comparison byte by
///     byte against an evaluation that shares nothing with it can see that.
/// </remarks>
[Trait("Category", "Property")]
public sealed class LayerStackEvaluatorDeviceTests : IDisposable {
    const int Size = 520; // not a power of two, and more than two tiles a side
    const string Usage = "baseColor";

    static readonly TextureLibrary Library = TextureNodeLibrary.Publish(null);

    readonly string folder = Path.Combine(Path.GetTempPath(), $"vixen-stack-{Guid.NewGuid():N}");

    public LayerStackEvaluatorDeviceTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    /// <summary>Any stroke, undo and redo on any mix of layers, compared to a clean evaluation each time.</summary>
    [Fact]
    public void An_incremental_composite_equals_a_full_one() {
        using var device = OpenDevice();

        var strokes = Gen.Select(Gen.Int[0, Size - 1], Gen.Int[0, Size - 1], Gen.Int[1, 2]).Array[1, 4];

        Gen.Select(Gen.Int[2, 7], Gen.Int[0, 15], Gen.Int[0, 4], Gen.Int[0, int.MaxValue], strokes)
            .Sample(
                (count, mode, filter, seed, stamps) => {
                    using var evaluator = new TexturePlanEvaluator(device);

                    Check(evaluator, count, mode, filter, seed, stamps);
                },
                iter: 6,
                threads: 1
            );
    }

    /// <summary>A stack of local layers repairs: the property above is not being satisfied by running whole.</summary>
    [Fact]
    public void A_stroke_on_local_layers_touches_its_tiles_and_not_the_image() {
        using var device = OpenDevice();
        using var evaluator = new TexturePlanEvaluator(device);
        var run = Open(evaluator, 4, 1, filter: 0, out var image);

        using (run) {
            using (run.Evaluate()) { }

            var uploaded = run.Tiles.TilesUploaded;

            Paint(image, 40, 40, 0xFF2080E0u, 10f);

            using var bake = run.Evaluate();

            Assert.True(bake.Repaired > 0, "the stroke ran whole, so nothing here measured a repair");
            Assert.Equal(1L, run.Tiles.TilesUploaded - uploaded);
            Assert.Equal(bake.Repaired, bake.Dispatches);
        }
    }

    /// <summary>A blur reads its neighbours, so it runs whole and asks for whole inputs.</summary>
    [Fact]
    public void A_filter_that_reads_its_neighbours_runs_whole_above_a_stroke() {
        using var device = OpenDevice();
        using var evaluator = new TexturePlanEvaluator(device);
        var run = Open(evaluator, 4, 1, filter: 2, out var image);

        using (run) {
            using (run.Evaluate()) { }

            Paint(image, 40, 40, 0xFF2080E0u, 10f);

            using (var repaired = run.Evaluate()) {
                // What is under the blur can still be repaired where the cache held it; what is over it cannot.
                Assert.True(
                    repaired.Repaired < repaired.Ran.Length,
                    $"{repaired.Repaired} of {repaired.Ran.Length} ops were repaired, so nothing ran whole"
                );
            }

            Assert.Equal(Pixels(run, clean: false), Pixels(run, clean: true));
        }
    }

    /// <summary>⚠ A number moves a plan and compiles nothing, and everything else still compiles.</summary>
    /// <remarks>
    ///     <b>This said it compiled only for more than a value and every value compiled</b>: the graph
    ///     a stack builds is a new object each time, and the compiler refused anything but the object
    ///     it was given. The picture was right either way, so only asking can see it.
    /// </remarks>
    [Fact]
    public void A_value_edit_does_not_compile_and_what_the_graph_does_not_say_does() {
        using var device = OpenDevice();
        using var evaluator = new TexturePlanEvaluator(device);
        var run = Open(evaluator, 2, 1, filter: 0, out _, out var stack);

        using (run) {
            using (run.Evaluate()) { }

            var first = Pixels(run, clean: false);
            var layers = stack.Sets[0].Layers;
            var index = layers.FindIndex(static layer => layer.Id == "painted");

            layers[index] = layers[index] with { Opacity = 0.2f };

            Assert.Empty(run.Prepare(stack, stack.Sets[0], Library.Registry, Library.SubGraphs));
            Assert.False(run.Compiled, "a value edit compiled");
            Same(run, "after a value edit");
            Assert.False(first.AsSpan().SequenceEqual(Pixels(run, clean: false)), "the opacity reached no pixel");

            // The seed is in no node, so nothing about the graph says it moved.
            stack = stack with { Seed = stack.Seed + 1 };
            Assert.Empty(run.Prepare(stack, stack.Sets[0], Library.Registry, Library.SubGraphs));
            Assert.Equal(stack.Seed, run.Plan!.Seed);

            // ⚠ Nor is the size, and a refresh answering for it would be the old picture at the old size.
            stack = stack with { BaseWidth = Size / 2, BaseHeight = Size / 2 };
            run.Prepare(stack, stack.Sets[0], Library.Registry, Library.SubGraphs);
            Assert.True(run.Compiled, "the size moved and nothing compiled");
        }
    }

    void Check(TexturePlanEvaluator evaluator, int count, int mode, int filter, int seed, (int X, int Y, int R)[] stamps) {
        using var run = Open(evaluator, count, mode, filter, out var image);

        using (run.Evaluate()) { }

        List<(PaintStrokeCommand Command, byte[] After)> done = [];
        var random = new Random(seed);

        foreach (var (x, y, radius) in stamps) {
            done.Add(((PaintStrokeCommand)Paint(image, x, y, (uint)random.Next() | 0xFF000000u, radius * 6f), image.ToTexels()));
            Same(run, $"after a stroke at ({x}, {y}) over {count} layers, mode {mode}, filter {filter}");
        }

        for (var index = done.Count - 1; index >= 0; index--) {
            done[index].Command.Undo(null!);
            Same(run, $"after undoing stroke {index}");
        }

        foreach (var (command, after) in done) {
            command.Do(null!);
            Assert.Equal(after, image.ToTexels());
            Same(run, "after a redo");
        }
    }

    static void Same(LayerStackEvaluator run, string when) {
        var repaired = Pixels(run, clean: false);
        var clean = Pixels(run, clean: true);

        Assert.True(repaired.AsSpan().SequenceEqual(clean), $"The repaired picture differs from a clean one {when}.");
    }

    static byte[] Pixels(LayerStackEvaluator run, bool clean) {
        using var bake = clean ? run.EvaluateClean() : run.Evaluate();
        var output = run.Outputs.First(static o => o.Usage == Usage).Image;

        return bake.ReadTexels(output, [new(0, 0, Size, Size)])[0];
    }

    static IAuthoringCommand Paint(PaintImage image, int x, int y, uint colour, float radius) {
        var session = PaintSession.Begin(
            new(image, PaintCoverage.Everywhere(image.Width, image.Height), Gutter: 0),
            PaintBrush.Default with { Radius = radius, Spacing = 0.25f, Flow = 1f },
            colour
        );

        session.Move(new Vector2(x, y));
        session.Move(new Vector2(x + 3f, y + 2f));

        return session.End("Stroke")!;
    }

    LayerStackEvaluator Open(TexturePlanEvaluator evaluator, int count, int mode, int filter, out PaintImage image) =>
        Open(evaluator, count, mode, filter, out image, out _);

    LayerStackEvaluator Open(
        TexturePlanEvaluator evaluator,
        int count,
        int mode,
        int filter,
        out PaintImage image,
        out LayerStackAsset asset
    ) {
        var file = Path.Combine(folder, $"{Guid.NewGuid():N}.vxlayers");
        var paint = $"{Guid.NewGuid():N}.vxpaint";
        PaintCanvas canvas = new(Size, Size);

        image = canvas.Channel(Usage);
        image.Put(5, 5, 0xFF00FF00u);

        using (var stream = File.Create(Path.Combine(folder, paint))) {
            canvas.Write(stream);
        }

        var store = new PaintCanvasStore(long.MaxValue);
        var opened = store.Open(Path.Combine(folder, paint));

        image = opened!.Channel(Usage);

        List<LayerAsset> layers = [
            new() {
                Id = "under",
                Kind = LayerKind.Fill,
                Values = { [Usage] = [0.25f, 0.5f, 0.75f, 1f] }
            },
            new() {
                Id = "painted",
                Kind = LayerKind.Paint,
                Paint = paint,
                Blend = (LayerBlendMode)mode,
                Opacity = 0.8f
            }
        ];

        for (var index = 0; index < count; index++) {
            layers.Add(
                (filter, index % 2) switch {
                    (1, 0) => new() { Id = $"f{index}", Kind = LayerKind.Filter, Filter = LayerFilterKind.Hsl, Opacity = 0.5f },
                    (2, 0) => new() {
                        Id = $"f{index}",
                        Kind = LayerKind.Filter,
                        Filter = LayerFilterKind.Blur,
                        Settings = { ["Radius"] = [2f] }
                    },
                    (3, 0) => new() {
                        Id = $"f{index}",
                        Kind = LayerKind.Fill,
                        Blend = LayerBlendMode.Overlay,
                        Values = { [Usage] = [0.5f, 0.2f, 0.1f, 1f] },
                        Mask = new() { Source = LayerMaskSource.Constant, Value = 0.3f }
                    },
                    _ => new() {
                        Id = $"f{index}",
                        Kind = LayerKind.Fill,
                        Blend = (LayerBlendMode)((mode + index) % 16),
                        Opacity = 0.4f,
                        Values = { [Usage] = [0.1f * index, 0.3f, 0.7f, 0.9f] }
                    }
                }
            );
        }

        LayerStackAsset stack = new() {
            Name = "Stack",
            BaseWidth = Size,
            BaseHeight = Size,
            Sets = [
                new() {
                    Name = "Body",
                    Channels = [new() { Usage = Usage, Default = [0f, 0f, 0f, 1f] }],
                    Layers = layers
                }
            ]
        };

        var run = new LayerStackEvaluator(evaluator, store, file);
        var refusals = run.Prepare(stack, stack.Sets[0], Library.Registry, Library.SubGraphs);

        Assert.Empty(refusals);

        asset = stack;

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
