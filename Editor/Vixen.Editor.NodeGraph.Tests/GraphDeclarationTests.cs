// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Editor.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>
///     What a graph declares about <em>itself</em> — its own settings and its exposed parameters —
///     and what a setting is now allowed to be.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two gaps of one shape, and both were silent.</b> A model carried a name, nodes,
///         edges, groups, comments and an interface, so a texture graph's base resolution, its seed
///         and its knobs were properties of the <em>compiler</em> — set by whichever host built one,
///         and gone the moment the file was closed (#719). And a
///         <see cref="SettingDefinition" /> carried a name, a default and a sentence, so a parameter
///         declared <c>0…1</c> arrived at an inspector as a text box with its range written out in
///         the tooltip (#730).
///     </para>
///     <para>
///         <b>Neither could fail a test, which is why neither had one.</b> Everything compiled and
///         everything drew; what was missing was a field, and a field that is not there cannot be
///         asserted about. So these are round-trips and shapes rather than behaviours.
///     </para>
/// </remarks>
public class GraphDeclarationTests {
    /// <summary>A graph's own settings and parameters survive a save and a load.</summary>
    /// <remarks>
    ///     ⚠ <b>Every field of a parameter, not just its name.</b> A round-trip that kept the name and
    ///     dropped the range would leave a published graph whose knob still exists and no longer
    ///     refuses 40 where it says 0…1 — and the file it came back from would look right in a diff.
    /// </remarks>
    [Fact]
    public void A_graphs_own_settings_and_parameters_survive_a_round_trip() {
        NodeGraphModel graph = new() { Name = "Rust" };

        graph.Settings["baseWidth"] = "2048";
        graph.Settings["seed"] = "90210";
        graph.Parameters.Add(new("Amount", "0.25", "How rusty", SettingKind.Float, 0f, 1f, "Wear"));
        graph.Parameters.Add(new("Tiles", "4", "", SettingKind.Int, 1f, 16f, "Layout"));
        graph.Parameters.Add(new("Preset", "Coarse", "Which profile"));

        var reopened = NodeGraphDocument.Load(NodeGraphDocument.Save(graph), out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(graph.Settings, reopened.Settings);
        Assert.Equal(graph.Parameters, reopened.Parameters);

        // The instrument: a comparison of two empty collections is true of anything, and both of
        // these are the fields that did not exist.
        Assert.Equal(2, reopened.Settings.Count);
        Assert.Equal(3, reopened.Parameters.Count);
        Assert.Equal("2048", reopened.SettingOf("baseWidth"));
        Assert.Equal("", reopened.SettingOf("baseHeight"));
    }

    /// <summary>Two parameters of one name is a signature that cannot say which, so the second goes.</summary>
    /// <remarks>
    ///     A bad merge's shape, and the same repair <see cref="NodeGraphModel.Interface" />'s
    ///     duplicate ports get: an override is stored under the parameter's name, so two of them is a
    ///     value with two meanings.
    /// </remarks>
    [Fact]
    public void A_duplicated_parameter_is_dropped_with_a_diagnostic() {
        var asset = new NodeGraphAsset {
            Parameters = [
                new() { Name = "Amount", Default = "0.25" },
                new() { Name = "Amount", Default = "0.75" },
                new() { Name = "", Default = "1" }
            ]
        };

        var graph = NodeGraphDocument.Load(asset, out var diagnostics);
        var parameter = Assert.Single(graph.Parameters);

        Assert.Equal("0.25", parameter.Default);
        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, diagnostic => Assert.Equal("NG0104", diagnostic.Id));
    }

    /// <summary>A setting's kind decides what a row edits it with, and its range makes it a slider.</summary>
    /// <remarks>
    ///     ⚠ <b>The <see cref="InspectorMember.MemberType" /> is what a drawer is chosen by</b>, so a
    ///     setting that stayed <see langword="string" /> is a text box whatever else it declares —
    ///     which is exactly what a <c>bool</c> parameter was, and what made <c>ture</c> a value.
    /// </remarks>
    [Theory]
    [InlineData(SettingKind.Text, typeof(string), false)]
    [InlineData(SettingKind.Bool, typeof(bool), false)]
    [InlineData(SettingKind.Int, typeof(int), true)]
    [InlineData(SettingKind.Float, typeof(float), true)]
    public void A_settings_kind_decides_what_a_row_edits(SettingKind kind, Type expected, bool bounded) {
        NodeGraphModel graph = new();
        var member = new NodeSettingMember(graph, new("Amount", "1", "", kind, 0f, 4f));

        Assert.Equal(expected, member.MemberType);
        Assert.Equal(bounded, member.Range is not null);

        // A range on a text setting is a declaration that disagrees with itself: a slider writing
        // numbers into a field a compiler reads as a name.
        Assert.Equal(bounded, new SettingDefinition("Amount", "1", "", kind, 0f, 4f).IsBounded);

        // And an unbounded numeric setting is a box rather than a slider, so the bounds are the
        // reason for the range rather than the kind.
        Assert.Null(new NodeSettingMember(graph, new("Amount", "1", "", kind)).Range);
    }

    /// <summary>A typed setting reads back as its type and writes back as text.</summary>
    /// <remarks>
    ///     ⚠ <b>The write is the half that was a defect waiting to happen.</b> The member stored
    ///     <c>value as string</c>, and a typed row hands back a <see cref="bool" /> or a
    ///     <see cref="double" /> — so the first edit of a <c>bool</c> setting would have written the
    ///     empty string over it, and the node would have gone back to its default while the row went
    ///     on showing what the author had just chosen.
    /// </remarks>
    [Fact]
    public void A_typed_setting_reads_back_as_its_type_and_stores_as_text() {
        NodeGraphModel graph = new();
        var node = graph.Add("Whatever");
        var flag = new NodeSettingMember(graph, new("Tiling", "false", "", SettingKind.Bool));
        var count = new NodeSettingMember(graph, new("Tiles", "4", "", SettingKind.Int, 1f, 16f));

        Assert.Equal(false, flag.GetBoxed(node));
        Assert.Equal(4, count.GetBoxed(node));

        flag.SetBoxed(node, true);
        count.SetBoxed(node, 9d);

        Assert.Equal("true", node.TextOf("Tiling"));
        Assert.Equal("9", node.TextOf("Tiles"));
        Assert.Equal(true, flag.GetBoxed(node));
        Assert.Equal(9, count.GetBoxed(node));
    }

    /// <summary>A setting whose text is nonsense reads as its declared default.</summary>
    /// <remarks>
    ///     ⚠ <b>Not as zero, which is the plausible-looking answer.</b> Zero is a legitimate value for
    ///     nearly every number a graph holds, so a hand edit that left <c>0.5.</c> behind would show,
    ///     and then save, a number nobody typed.
    /// </remarks>
    [Fact]
    public void A_setting_that_does_not_parse_reads_as_its_default() {
        NodeGraphModel graph = new();
        var node = graph.Add("Whatever");
        var member = new NodeSettingMember(graph, new("Amount", "0.25", "", SettingKind.Float, 0f, 1f));

        node.SetText("Amount", "0.5.");

        Assert.Equal(0.25f, member.GetBoxed(node));

        // And a default that is itself nonsense does not recur for ever.
        Assert.Equal(0f, new NodeSettingMember(graph, new("Amount", "?", "", SettingKind.Float)).GetBoxed(node));
    }

    /// <summary>A group starts a section once, at its first setting.</summary>
    /// <remarks>
    ///     <see cref="InspectorMember.Header" /> is "the section this member starts", so a header on
    ///     every member of a group would be the group's name repeated above every row in it.
    /// </remarks>
    [Fact]
    public void A_group_starts_one_section_at_its_first_setting() {
        NodeGraphModel graph = new();

        var definition = new NodeTypeDefinition(
            "Test/Grouped",
            [],
            static () => null!,
            Settings: [
                new("A", "", "", SettingKind.Text, Group: "Wear"),
                new("B", "", "", SettingKind.Text, Group: "Wear"),
                new("C", "", "", SettingKind.Text, Group: "Layout"),
                new("D", "", "")
            ]
        );

        var headers = NodePortEditProvider.For(graph, definition, NodeId.None)
            .Descriptor.Members
            .Select(member => ((InspectorMember) member).Header ?? "·")
            .ToArray();

        Assert.Equal(["Wear", "·", "Layout", "·"], headers);
    }
}
