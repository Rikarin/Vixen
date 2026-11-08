// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>
///     What an author says about a graph beyond its wiring — a switched-off node, a frame's colour and
///     description, how a parameter is described and drawn, and its presets — survives the file.
/// </summary>
public class GraphAuthoringFileTests {
    static NodeGraphModel RoundTrip(NodeGraphModel graph, out IReadOnlyList<NodeDiagnostic> diagnostics) {
        var yaml = YamlSerializer.ToYaml(NodeGraphDocument.Save(graph));

        return NodeGraphDocument.Load(YamlSerializer.Parse<NodeGraphAsset>(yaml), out diagnostics);
    }

    [Fact]
    public void A_bypassed_node_stays_bypassed() {
        NodeGraphModel graph = new();
        var on = graph.Add("Test/Constant");
        var off = graph.Add("Test/Constant");
        off.Bypassed = true;

        var reloaded = RoundTrip(graph, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.True(reloaded.TryGet(off.Id, out var a) && a.Bypassed);
        Assert.True(reloaded.TryGet(on.Id, out var b) && !b.Bypassed);
    }

    [Fact]
    public void A_frame_keeps_its_colour_and_description() {
        NodeGraphModel graph = new();
        graph.Groups.Add(new() { Title = "Rust", Color = "var(--color-chart-3)", Description = "Where the edges wear." });

        var group = Assert.Single(RoundTrip(graph, out _).Groups);

        Assert.Equal("var(--color-chart-3)", group.Color);
        Assert.Equal("Where the edges wear.", group.Description);
    }

    [Fact]
    public void A_parameter_keeps_its_label_widget_and_condition() {
        NodeGraphModel graph = new();
        graph.Parameters.Add(new("Worn", "true", Kind: SettingKind.Bool, Widget: SettingWidget.Toggle));
        graph.Parameters.Add(
            new(
                "Angle",
                "45",
                "Which way the scratches run.",
                SettingKind.Float,
                0f,
                360f,
                "Scratches",
                Label: "Scratch angle",
                Widget: SettingWidget.Angle,
                VisibleIf: new("Worn", "true")
            )
        );

        var reloaded = RoundTrip(graph, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(graph.Parameters, reloaded.Parameters);
        Assert.Equal("Scratch angle", reloaded.Parameters[1].Title);
        Assert.Equal("Worn", reloaded.Parameters[0].Title);
    }

    [Fact]
    public void Presets_survive_and_a_second_of_one_name_is_dropped_with_a_diagnostic() {
        NodeGraphModel graph = new();
        GraphPreset rusty = new() { Name = "Rusty" };
        rusty.Values["Amount"] = "0.8";
        graph.Presets.Add(rusty);

        var reloaded = RoundTrip(graph, out var clean);

        Assert.Empty(clean);
        var preset = Assert.Single(reloaded.Presets);
        Assert.Equal("Rusty", preset.Name);
        Assert.Equal("0.8", preset.Values["Amount"]);

        var asset = NodeGraphDocument.Save(graph) with {
            Presets = [new() { Name = "Rusty" }, new() { Name = "Rusty" }, new() { Name = " " }]
        };

        var repaired = NodeGraphDocument.Load(asset, out var diagnostics);

        Assert.Single(repaired.Presets);
        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Id == "NG0105"));
    }

    [Theory]
    [InlineData("true", "true", false, true)]
    [InlineData("TRUE", "true", false, true)]
    [InlineData("false", "true", false, false)]
    [InlineData("1.0", "1", false, true)]
    [InlineData("2", "1", true, true)]
    [InlineData("Gold", "Gold", true, false)]
    public void A_condition_compares_numbers_as_numbers_and_names_ignoring_case(
        string current,
        string value,
        bool negated,
        bool shown
    ) =>
        Assert.Equal(shown, new SettingCondition("Other", value, negated).Holds(current));
}
