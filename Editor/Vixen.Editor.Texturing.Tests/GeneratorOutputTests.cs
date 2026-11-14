// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Vixen.Texturing;
using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     Patina #120: a generator that writes several pictures is read through the one a mask entry or an input names, and
///     one named nothing, or a name it does not write, is refused rather than read through its first.
/// </summary>
public class GeneratorOutputTests {
    /// <summary>A mask entry reads the picture it names, and the file keeps the name.</summary>
    [Fact]
    public void A_mask_entry_reads_the_picture_it_names() {
        using var fixture = new TexturingFixture();
        var (registry, compounds) = TwoPictures(fixture);
        var stack = Stack(Masked("Edge"));

        var build = LayerStackGraph.Build(stack, stack.Sets[0], registry, subGraphs: compounds);

        Assert.False(build.HasErrors, string.Join("; ", build.Problems));
        Assert.Equal(["Edge"], ReadPorts(build));

        var read = LayerStackYaml.Read(LayerStackYaml.Write(stack)).Sets[0].Layers[1].Mask;

        Assert.Equal("Edge", read.GeneratorOutput);
        Assert.Equal("Edge", read.Layers[0].GeneratorOutput);
    }

    /// <summary>Named nothing, or a picture it does not write, a generator with two is refused: neither is the first.</summary>
    [Theory]
    [InlineData("", "has to be named")]
    [InlineData("Rim", "writes no picture called 'Rim'")]
    public void A_mask_entry_naming_no_picture_it_writes_is_refused(string output, string said) {
        using var fixture = new TexturingFixture();
        var (registry, compounds) = TwoPictures(fixture);
        var stack = Stack(Masked(output));

        var build = LayerStackGraph.Build(stack, stack.Sets[0], registry, subGraphs: compounds);

        Assert.Contains(
            build.Problems,
            problem => problem is { Severity: NodeSeverity.Error, Layer: "masked" }
                && problem.Message.Contains(said, StringComparison.Ordinal)
        );
    }

    /// <summary>A filter's input reading the generator takes the picture it names, and the file keeps the name.</summary>
    [Fact]
    public void An_input_reads_the_picture_it_names() {
        using var fixture = new TexturingFixture();
        var (registry, compounds) = TwoPictures(fixture);
        var stack = Stack(Warped("Mask"));

        var build = LayerStackGraph.Build(stack, stack.Sets[0], registry, subGraphs: compounds);

        Assert.False(build.HasErrors, string.Join("; ", build.Problems));
        Assert.Equal(["Mask"], ReadPorts(build));
        Assert.Equal("Mask", LayerStackYaml.Read(LayerStackYaml.Write(stack)).Sets[0].Layers[1].Inputs["Warp"].Output);
    }

    /// <summary>An input naming no picture of a generator with two is refused by name, not read through the first.</summary>
    [Fact]
    public void An_input_naming_no_picture_is_refused() {
        using var fixture = new TexturingFixture();
        var (registry, compounds) = TwoPictures(fixture);
        var stack = Stack(Warped(""));

        var build = LayerStackGraph.Build(stack, stack.Sets[0], registry, subGraphs: compounds);

        Assert.Contains(
            build.Problems,
            static problem => problem is { Severity: NodeSeverity.Error, Layer: "warp" }
                && problem.Message.Contains("writes several pictures", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     A generator of one picture still reads it named nothing, so every file written before the name existed
    ///     builds as it did.
    /// </summary>
    [Fact]
    public void A_generator_of_one_picture_needs_no_name() {
        var stack = Stack(
            new LayerAsset {
                Id = "masked",
                Kind = LayerKind.Fill,
                Values = { ["baseColor"] = [1f, 0f, 0f, 1f] },
                Mask = new() { Source = LayerMaskSource.Generator, Generator = "Generators/Dirt" }
            }
        );

        var build = LayerStackGraph.Build(stack, stack.Sets[0]);

        Assert.DoesNotContain(build.Problems, static problem => problem.Severity == NodeSeverity.Error);
    }

    const string Generator = "Two Pictures";

    /// <summary>A project compound that reads nothing and writes two pictures, Mask and Edge.</summary>
    static (NodeTypeRegistry Registry, ISubGraphSource Compounds) TwoPictures(TexturingFixture fixture) {
        var folder = Path.Combine(fixture.Paths.Assets, TextureNodeLibrary.CompoundFolder);

        Directory.CreateDirectory(folder);

        var compound = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph(TextureNodeLibrary.CompoundFolder + "/" + Generator),
            Path.Combine(folder, Generator + TextureGraphDocument.Extension)
        );

        compound.Graph.Interface.Add(new("Mask", PortDirection.Output, PortKind.Image));
        compound.Graph.Interface.Add(new("Edge", PortDirection.Output, PortKind.Image));
        compound.Save();

        var registry = LayerStackCompiler.Library(out var compounds, fixture.Paths.Assets);

        return (registry, compounds);
    }

    static LayerAsset Masked(string output) =>
        new() {
            Id = "masked",
            Kind = LayerKind.Fill,
            Values = { ["baseColor"] = [1f, 0f, 0f, 1f] },
            Mask = new() {
                Source = LayerMaskSource.Generator,
                Generator = Generator,
                GeneratorOutput = output,
                Layers = [new() { Source = LayerMaskSource.Generator, Generator = Generator, GeneratorOutput = output }]
            }
        };

    static LayerAsset Warped(string output) =>
        new() {
            Id = "warp",
            Kind = LayerKind.Filter,
            FilterNode = "Filters/Warp",
            Channels = ["baseColor"],
            Inputs = {
                ["Warp"] = new() { Source = LayerInputSource.Generator, Generator = Generator, Output = output }
            }
        };

    static LayerStackAsset Stack(LayerAsset layer) => FilterInputTests.Stack(FilterInputTests.Base(), layer);

    /// <summary>The ports of the generator that anything reads, each once.</summary>
    static string[] ReadPorts(LayerStackBuild build) {
        var generators = build.Graph.Nodes.Where(static node => node.Type == Generator)
            .Select(static node => node.Id)
            .ToHashSet();

        Assert.NotEmpty(generators);

        return [
            ..build.Graph.Edges.Where(edge => generators.Contains(edge.From.Node))
                .Select(static edge => edge.From.Port)
                .Distinct()
        ];
    }
}
