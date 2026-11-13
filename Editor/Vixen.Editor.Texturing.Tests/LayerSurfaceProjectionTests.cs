// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Patina #130, #131, #133 and #134: a decal and a path are a fill's projection, kept as numbers in the stack.</summary>
/// <remarks>
///     <b>What the stack owes the kernels is the wiring</b>: the two mesh maps by usage, the numbers onto the node, the
///     curve sampled once, and the file. The pictures are <c>TextureSurfaceProjectionDeviceTests</c>'.
/// </remarks>
public class LayerSurfaceProjectionTests {
    static readonly LayerDecal Placed = new() {
        Centre = [1f, 2f, 3f],
        Facing = [0f, 0f, 2f],
        Up = [0f, 1f, 0.5f],
        Width = 0.4f,
        Height = 0.2f,
        Depth = 0.1f,
        Angle = 1f,
        Fade = 0.5f,
        Tiled = true,
        Sampling = LayerSampling.Point,
        Box = [-1f, -1f, -1f, 3f, 3f, 5f],
        Warp = [0f, 0f, 0.5f, 0f, 1f, 0f, 0f, 0.5f, 0.6f, 0.5f, 1f, 0.5f, 0f, 1f, 0.5f, 1f, 1f, 1f]
    };

    static readonly LayerSurfacePath Drawn = new() {
        Points = [new(0f, 0f, 0f, 0f, 0f, 1f), new(1f, 0.5f, 0f, 0f, 0f, 1f, 0.5f), new(2f, 0f, 0f, 0f, 1f, 0f)],
        Radius = 0.05f,
        Hardness = 0.25f,
        Ribbon = true,
        Repeat = 0.3f,
        Angle = 0.8f,
        Box = [0f, 0f, 0f, 2f, 1f, 1f]
    };

    static readonly float[] None = [];

    static readonly List<LayerSurfacePoint> NoPoints = [];

    static LayerStackAsset Stack(LayerAsset layer) =>
        new() {
            Name = "Test",
            BaseWidth = 32,
            BaseHeight = 32,
            Sets = [
                new() {
                    Name = "S", Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }], Layers = [layer]
                }
            ]
        };

    static LayerAsset Fill(LayerProjection projection, LayerDecal? decal = null, LayerSurfacePath? path = null) =>
        new() {
            Id = "l",
            Kind = LayerKind.Fill,
            Values = { ["baseColor"] = [1f, 0f, 0f, 1f] },
            Projection = projection,
            Decal = decal,
            SurfacePath = path
        };

    static GraphNode Node(LayerStackBuild build, string type) =>
        Assert.Single(build.Graph.Nodes, node => node.Type == type);

    /// <summary>A decal and a path come back from the file as they went in, every member.</summary>
    [Fact]
    public void A_decal_and_a_path_survive_the_file() {
        var layer = Fill(LayerProjection.Decal, Placed, Drawn);
        var read = LayerStackYaml.Read(LayerStackYaml.Write(Stack(layer))).Sets[0].Layers[0];

        Assert.Equal(LayerProjection.Decal, read.Projection);
        Assert.NotNull(read.Decal);
        Assert.Equal(Placed.Centre, read.Decal.Centre);
        Assert.Equal(Placed.Facing, read.Decal.Facing);
        Assert.Equal(Placed.Up, read.Decal.Up);
        Assert.Equal(Placed.Box, read.Decal.Box);
        Assert.Equal(Placed.Warp, read.Decal.Warp);
        Assert.Equal(
            Placed with {
                Centre = None,
                Facing = None,
                Up = None,
                Box = None,
                Warp = None
            },
            read.Decal with {
                Centre = None,
                Facing = None,
                Up = None,
                Box = None,
                Warp = None
            }
        );

        Assert.NotNull(read.SurfacePath);
        Assert.Equal(Drawn.Points, read.SurfacePath.Points);
        Assert.Equal(Drawn.Box, read.SurfacePath.Box);
        Assert.Equal(
            Drawn with { Points = NoPoints, Box = None },
            read.SurfacePath with { Points = NoPoints, Box = None }
        );

        // ⚠ And a layer with neither writes neither key, so every stack that exists reads and writes as it did.
        var bare = LayerStackYaml.Write(Stack(Fill(LayerProjection.Uv)));

        Assert.DoesNotContain("decal:", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("path:", bare, StringComparison.Ordinal);
        Assert.Null(LayerStackYaml.Read(bare).Sets[0].Layers[0].Decal);
    }

    /// <summary>A point that is not six or seven numbers is refused by name, rather than read as half a point.</summary>
    [Fact]
    public void A_point_of_the_wrong_length_is_refused() {
        var text = LayerStackYaml.Write(Stack(Fill(LayerProjection.Path, path: Drawn)))
            .Replace("- [0, 0, 0, 0, 0, 1, 1]", "- [0, 0, 0, 0]", StringComparison.Ordinal);

        Assert.Contains("[0, 0, 0, 0]", text, StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => LayerStackYaml.Read(text));
    }

    /// <summary>
    ///     ⚠ A decal reads the position and world bakes by usage, and every number reaches its node — a box whose
    ///     facing or up fell back to the node's default would be a decal on the wrong side of the model.
    /// </summary>
    [Fact]
    public void A_decal_fill_reads_the_two_bakes_and_carries_its_box() {
        var stack = Stack(Fill(LayerProjection.Decal, Placed));
        var build = LayerStackGraph.Build(stack, stack.Sets[0]);
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.NotNull(compilation.Plan);
        Assert.Equal(
            ["meshmap:position", "meshmap:world"],
            compilation.Externals.Select(static external => external.Asset).Order(StringComparer.Ordinal).ToArray()
        );

        var node = Node(build, "Space/Decal");

        Assert.Equal(3f, node.Values["Centre Z"][0]);
        Assert.Equal(2f, node.Values["Facing Z"][0]);
        Assert.Equal(0.5f, node.Values["Up Z"][0]);
        Assert.Equal(0.4f, node.Values["Width"][0]);
        Assert.Equal(-1f, node.Values["Box Min X"][0]);
        Assert.Equal(5f, node.Values["Box Max Z"][0]);
        Assert.Equal("Tile", node.Texts["Tiling"]);
        Assert.Equal("Point", node.Texts["Sampling"]);
        Assert.Equal(18, node.Texts["Warp"].Split(' ').Length);

        // And the op squared the frame: right, up and forward are unit and square to one another.
        var op = Assert.Single(compilation.Plan.Ops, static op => op.Kernel == "Decal");
        Vector3 Axis(string name) => new(Value(op, name + "X"), Value(op, name + "Y"), Value(op, name + "Z"));

        foreach (var (first, second) in new[] { ("right", "up"), ("up", "forward"), ("forward", "right") }) {
            Assert.Equal(0f, Vector3.Dot(Axis(first), Axis(second)), 4);
        }

        Assert.Equal(1f, Axis("forward").Length(), 4);
        Assert.Equal(1f, Value(op, "forwardZ"), 4);
        Assert.Equal(1f, Value(op, "warp"));
    }

    /// <summary>
    ///     ⚠ A host's bake says the box its position map spans, and every decal and path, in a group or not, reads through
    ///     that box rather than the one it was placed with (Patina #133) — which goes stale when the model changes.
    /// </summary>
    [Fact]
    public void A_bakes_box_replaces_every_decals_and_paths_own() {
        float[] baked = [-2f, -3f, -4f, 2f, 3f, 4f];
        var stack = Stack(
            new() {
                Id = "g",
                Kind = LayerKind.Group,
                Children = [
                    Fill(LayerProjection.Decal, Placed), Fill(LayerProjection.Path, path: Drawn) with { Id = "p" }
                ]
            }
        );

        var reboxed = LayerSurfaceBoxes.Of(stack, baked);
        var children = reboxed.Sets[0].Layers[0].Children;

        Assert.Equal(baked, children[0].Decal!.Box);
        Assert.Equal(baked, children[1].SurfacePath!.Box);
        Assert.Equal(Placed.Box, stack.Sets[0].Layers[0].Children[0].Decal!.Box);

        var node = Node(LayerStackGraph.Build(reboxed, reboxed.Sets[0]), "Space/Decal");

        Assert.Equal(-2f, node.Values["Box Min X"][0]);
        Assert.Equal(4f, node.Values["Box Max Z"][0]);

        // Nothing to replace is the same stack, so a host that refreshes every frame allocates nothing for it.
        Assert.Same(reboxed, LayerSurfaceBoxes.Of(reboxed, baked));
        var plain = Stack(Fill(LayerProjection.Uv));

        Assert.Same(plain, LayerSurfaceBoxes.Of(plain, baked));
    }

    /// <summary>
    ///     ⚠ A decal not yet placed and a path with no points cover nothing and say so, rather than covering the whole
    ///     mesh — which a fill with no box would do.
    /// </summary>
    [Theory]
    [InlineData(LayerProjection.Decal)]
    [InlineData(LayerProjection.Path)]
    public void An_unplaced_decal_or_path_covers_nothing_and_says_so(LayerProjection projection) {
        var stack = Stack(Fill(projection, path: new()));
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.NotNull(compilation.Plan);
        Assert.Contains(
            compilation.Problems,
            static problem => problem.Message.Contains("covers nothing", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(compilation.Plan.Ops, static op => op.Kernel is "Decal" or "SurfacePath");

        // ⚠ And what the layer composites is transparency, not the fill's red: a fill with no box would cover the whole
        // mesh. The red is still compiled, unread, so the blend's own foreground is what has to be asked.
        var blend = Assert.Single(compilation.Plan.Ops, static op => op.Kernel == "Blend");
        var foreground = Assert.Single(compilation.Plan.Ops, op => op.Output == blend.Inputs[1]);

        Assert.Equal("Uniform", foreground.Kernel);
        Assert.Equal(0f, foreground.Parameters.Single(static parameter => parameter.Name == "alpha").Value);
        Assert.Contains(
            compilation.Plan.Ops,
            static op => op.Kernel == "Uniform"
                && op.Parameters.Any(static parameter => parameter is { Name: "red", Value: 1f })
        );
    }

    /// <summary>A decal on a paint layer is warned about and composited through the atlas, for the other projections' reason.</summary>
    [Fact]
    public void A_decal_on_a_paint_layer_is_warned_about() {
        var stack = Stack(
            Fill(LayerProjection.Decal, Placed) with { Kind = LayerKind.Paint, Values = [], Paint = "Test.S.l.vxpaint" }
        );
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Contains(
            compilation.Problems,
            static problem => problem.Message.Contains("does nothing there", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ A path reaches its node as the curve through its points, sampled: it passes through every point, the points'
    ///     normals and pressures are lerped between them, and it is more than the three points it was drawn with.
    /// </summary>
    [Fact]
    public void A_path_reaches_its_node_as_the_curve_through_its_points() {
        var stack = Stack(Fill(LayerProjection.Path, path: Drawn));
        var build = LayerStackGraph.Build(stack, stack.Sets[0]);
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.NotNull(compilation.Plan);

        var node = Node(build, "Space/Surface Path");
        var points = node.Texts["Points"]
            .Split(';')
            .Select(static point => point.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(static number => float.Parse(number, CultureInfo.InvariantCulture))
                    .ToArray()
            )
            .ToArray();

        Assert.Equal("Ribbon", node.Texts["Mode"]);
        Assert.Equal(0.3f, node.Values["Repeat"][0]);
        Assert.True(points.Length > Drawn.Points.Count, $"the curve was {points.Length} points, which is no curve");
        Assert.All(points, static point => Assert.Equal(7, point.Length));

        // Through each clicked point, carrying that point's own normal and pressure.
        foreach (var clicked in Drawn.Points) {
            Assert.Contains(
                points,
                point => MathF.Abs(point[0] - clicked.X) < 1e-5f
                    && MathF.Abs(point[1] - clicked.Y) < 1e-5f
                    && MathF.Abs(point[5] - clicked.NormalZ) < 1e-5f
                    && MathF.Abs(point[6] - clicked.Pressure) < 1e-5f
            );
        }

        // Between the first two, the pressure falls from one to a half and never leaves that range.
        Assert.All(points.Where(static point => point[0] < 1f), static point => Assert.InRange(point[6], 0.5f, 1f));
        Assert.Contains(points, static point => point[6] is > 0.5f and < 1f);
    }

    /// <summary>
    ///     ⚠ The curve on the model is the curve on the atlas: points at nought depth sample to the same polyline the 2D
    ///     path does, so a path drawn on the surface bends exactly as one on the map.
    /// </summary>
    [Fact]
    public void The_curve_in_space_is_the_curve_on_the_atlas() {
        PaintPath flat = new();
        Vector2[] clicked = [new(10f, 10f), new(80f, 40f), new(120f, 10f), new(200f, 90f)];

        foreach (var point in clicked) {
            flat.Add(point);
        }

        List<Vector2> drawn = [];
        List<Vector3> raised = [];
        List<float> along = [];

        flat.Sample(drawn);
        PaintPath.Sample(
            [..clicked.Select(static point => new Vector3(point.X, point.Y, 0f))],
            PaintPath.Tolerance,
            raised,
            along
        );

        Assert.Equal(drawn.Count, raised.Count);
        Assert.Equal(raised.Count, along.Count);

        for (var index = 0; index < drawn.Count; index++) {
            Assert.Equal(drawn[index].X, raised[index].X, 4);
            Assert.Equal(drawn[index].Y, raised[index].Y, 4);
            Assert.Equal(0f, raised[index].Z);
        }

        Assert.Equal(0f, along[0]);
        Assert.Equal(clicked.Length - 1f, along[^1]);
        Assert.True(
            along.Zip(along.Skip(1)).All(static pair => pair.Second >= pair.First),
            "a sample went back along the path"
        );
    }

    static float Value(TextureOp op, string name) => op.Parameters.Single(parameter => parameter.Name == name).Value;
}
