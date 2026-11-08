// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Instance layers (Patina #108): a reference to another layer, resolved when the stack compiles.</summary>
public class LayerStackInstanceTests {
    static readonly float[] Red = [0.9f, 0.1f, 0.1f, 1f];
    static readonly float[] Blue = [0.1f, 0.2f, 0.8f, 1f];

    [Fact]
    public void An_instance_is_its_source_with_its_own_name_opacity_blend_and_switch() {
        var stack = Stack(
            Fill("rust", Red) with { Opacity = 0.4f, Blend = LayerBlendMode.Multiply, Channels = ["baseColor"] },
            Instance("copy", "rust") with { Name = "Copy", Opacity = 0.7f, Blend = LayerBlendMode.Screen }
        );

        var resolved = LayerStackInstances.Resolve(stack, stack.Sets[0], out var problems);
        var copy = resolved.Layers[1];

        Assert.Empty(problems);
        Assert.Equal(LayerKind.Fill, copy.Kind);
        Assert.Equal(("copy", "Copy", 0.7f, LayerBlendMode.Screen), (copy.Id, copy.Name, copy.Opacity, copy.Blend));
        Assert.Equal(Red, copy.Values["baseColor"]);
        Assert.Equal(["baseColor"], copy.Channels);

        var plain = Stack(Fill("rust", Red));
        Assert.Same(plain.Sets[0], LayerStackInstances.Resolve(plain, plain.Sets[0], out _));
    }

    [Fact]
    public void An_edit_to_the_source_is_an_edit_to_the_instance_when_it_next_compiles() {
        var before = Compile(Stack(Fill("rust", Red), Instance("copy", "rust")));
        var after = Compile(Stack(Fill("rust", Blue), Instance("copy", "rust")));

        Assert.Equal(2, Uniforms(before, Red));
        Assert.Equal(0, Uniforms(after, Red));
        Assert.Equal(2, Uniforms(after, Blue));
    }

    [Fact]
    public void A_folder_instance_brings_its_layers_under_ids_of_its_own_and_publishes_none_of_their_anchor_points() {
        var folder = new LayerAsset {
            Id = "folder",
            Kind = LayerKind.Group,
            Children = [Fill("inner", Red) with { AnchorPoint = "Inner" }]
        };
        var stack = Stack(folder, Instance("copy", "folder"));

        var copy = LayerStackInstances.Resolve(stack, stack.Sets[0], out _).Layers[1];
        var compiled = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Equal("copy/inner", Assert.Single(copy.Children).Id);
        Assert.Equal("", copy.Children[0].AnchorPoint);
        Assert.Empty(compiled.Problems);
        Assert.NotNull(compiled.Plan);
        Assert.Equal(2, Uniforms(compiled.Plan, Red));
    }

    [Fact]
    public void An_instance_reads_a_layer_of_another_set() {
        var stack = Stack(Instance("copy", "rust") with { Instance = new() { Set = "Other", Layer = "rust" } }) with {
            Sets = [
                Stack(Instance("copy", "rust") with { Instance = new() { Set = "Other", Layer = "rust" } }).Sets[0],
                new() {
                    Name = "Other",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [Fill("rust", Blue)]
                }
            ]
        };

        var compiled = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compiled.Problems);
        Assert.Equal(1, Uniforms(compiled.Plan!, Blue));
        Assert.Equal([("S", "copy")], LayerStackInstances.Of(stack, "Other", "rust"));
    }

    [Fact]
    public void A_cycle_is_refused_and_a_missing_source_shows_nothing_with_a_warning() {
        var cycle = Stack(Instance("a", "b"), Instance("b", "a"));
        var missing = Stack(Fill("base", Red), Instance("copy", "gone"));

        var refused = LayerStackCompiler.Compile(cycle, cycle.Sets[0]);
        var warned = LayerStackCompiler.Compile(missing, missing.Sets[0]);

        Assert.Contains(refused.Problems, static problem => problem.Severity == Vixen.NodeGraph.NodeSeverity.Error);
        Assert.Null(refused.Plan);
        var warning = Assert.Single(warned.Problems);
        Assert.Equal(("copy", Vixen.NodeGraph.NodeSeverity.Warning), (warning.Layer, warning.Severity));
        Assert.Equal(1, Uniforms(warned.Plan!, Red));

        Assert.True(LayerStackInstances.Cycles(Stack(Fill("a", Red), Instance("b", "a")), "S", "a", new() { Layer = "b" }));
        Assert.False(LayerStackInstances.Cycles(Stack(Fill("a", Red), Fill("c", Blue)), "S", "c", new() { Layer = "a" }));
    }

    [Fact]
    public void An_instances_own_mask_entries_narrow_the_sources_mask() {
        MaskLayerAsset half = new() { Source = LayerMaskSource.Constant, Value = 0.5f };
        var source = Fill("rust", Red) with { Mask = new() { Source = LayerMaskSource.Constant, Value = 0.8f } };
        var stack = Stack(source, Instance("copy", "rust") with { Mask = new() { Layers = [half] } });

        var copy = LayerStackInstances.Resolve(stack, stack.Sets[0], out _).Layers[1];

        Assert.Equal((LayerMaskSource.Constant, 0.8f), (copy.Mask.Source, copy.Mask.Value));
        Assert.Equal(LayerBlendMode.Multiply, Assert.Single(copy.Mask.Layers).Blend);
    }

    [Fact]
    public void An_instance_survives_the_file() {
        var stack = Stack(Fill("rust", Red), Instance("copy", "rust") with { Instance = new() { Set = "Other", Layer = "rust" } });

        var read = LayerStackYaml.Read(LayerStackYaml.Write(stack)).Sets[0].Layers[1];

        Assert.Equal(LayerKind.Instance, read.Kind);
        Assert.Equal(("Other", "rust"), (read.Instance.Set, read.Instance.Layer));
    }

    /// <summary>A mesh-map mask reads the lane it names (Patina #110), which a shuffle moves into every lane.</summary>
    [Fact]
    public void A_mesh_map_mask_reads_its_lane() {
        static int Greens(LayerMaskLane lane) {
            var stack = Stack(
                Fill("rust", Red) with { Mask = new() { Source = LayerMaskSource.Bake, Map = "world", Lane = lane } }
            );
            var plan = LayerStackCompiler.Compile(stack, stack.Sets[0]).Plan!;

            return plan.Ops.Count(op => op.Kernel == "ChannelShuffle"
                && op.Find("sourceR")?.Value == 1f
                && op.Find("sourceG")?.Value == 1f
                && op.Find("sourceB")?.Value == 1f
            );
        }

        Assert.Equal(1, Greens(LayerMaskLane.Green));
        Assert.Equal(0, Greens(LayerMaskLane.Red));
        Assert.Equal(
            LayerMaskLane.Blue,
            LayerStackYaml.Read(
                    LayerStackYaml.Write(
                        Stack(Fill("rust", Red) with { Mask = new() { Source = LayerMaskSource.Bake, Lane = LayerMaskLane.Blue } })
                    )
                )
                .Sets[0]
                .Layers[0]
                .Mask.Lane
        );
    }

    static LayerAsset Fill(string id, float[] colour) =>
        new() { Id = id, Kind = LayerKind.Fill, Values = { ["baseColor"] = colour } };

    static LayerAsset Instance(string id, string source) =>
        new() { Id = id, Kind = LayerKind.Instance, Instance = new() { Layer = source } };

    static LayerStackAsset Stack(params LayerAsset[] layers) =>
        new() {
            Name = "Test",
            BaseWidth = 32,
            BaseHeight = 32,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [.. layers]
                }
            ]
        };

    static TexturePlan Compile(LayerStackAsset stack) {
        var compiled = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compiled.Problems);

        return compiled.Plan!;
    }

    /// <summary>How many uniform sources in the plan are this colour.</summary>
    static int Uniforms(TexturePlan plan, float[] colour) =>
        plan.Ops.Count(op => op.Kernel == "Uniform"
            && Near(op.Find("red")?.Value, colour[0])
            && Near(op.Find("green")?.Value, colour[1])
            && Near(op.Find("blue")?.Value, colour[2])
        );

    static bool Near(float? value, float wanted) => value is { } held && Math.Abs(held - wanted) < 1e-4f;
}
