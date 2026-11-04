// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Xunit;

namespace Vixen.Editor.NodeGraph.Tests;

/// <summary>What the inspector rows make of a port or setting the headless model describes.</summary>
public sealed class PortRowTests {
    [Fact]
    public void An_image_port_has_no_inspector_row_to_make() {
        // The contract `NodePortMember` documents: callers gate on `Fields` and this throws for
        // anything that answers zero. An image answering `float` here would put a box of digits
        // beside a socket that carries a raster.
        var thrown = Assert.Throws<ArgumentException>(static () => NodePortMember.TypeOf(PortKind.Image));

        Assert.Contains("Image", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bounded_setting_reaches_its_row_with_a_range() {
        // The range reaches a row rather than stopping at the definition, which is the half that
        // makes this a knob an artist can turn instead of a box in which "ture" is a value.
        NodeGraphModel published = new() { Name = "Grunge" };

        published.Interface.Add(new("Out", PortDirection.Output, PortKind.Float));
        published.Parameters.Add(new("Amount", "0.25", "How much", SettingKind.Float, 0f, 1f, "Wear"));

        var setting = Assert.Single(SubGraphs.Definition(published, "Sub-graphs/Grunge").Settings);

        Assert.NotNull(new NodeSettingMember(new(), setting).Range);
    }
}
