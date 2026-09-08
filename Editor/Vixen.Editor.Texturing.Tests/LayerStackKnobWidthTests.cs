// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;
using GraphPortDirection = Vixen.Editor.NodeGraph.PortDirection;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>How many fields a filter's knob row draws, and what happens to the ones it does not.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1097">#1097</a>, and the issue is right
///         about the defect and wrong about how far away it is.</b> It reads the cap —
///         <c>Math.Min(4, port.Default.Length)</c> — and says nothing declares a port of more than
///         four lanes, so the narrowing is a thing a future compound could do. What the cap was
///         standing on is the second half of that expression: the lane count came from the
///         <em>default</em>, and a default is optional where a <see cref="PortKind" /> is not. A
///         <c>Float4</c> input declared with no default therefore reported one lane and was drawn as
///         one field, four lanes short, today.
///     </para>
///     <para>
///         ⚠ <b>And that is not a shape only a hand-written registry can make.</b>
///         <c>NodeTypeGenerator.Default</c> takes a port's default from the field's initializer and
///         reads it as <em>one</em> constant, so a compiled vector input has a matching default only
///         where somebody wrote <c>[Input(Default = …)]</c> out by hand;
///         <c>NodeGraphModel.Interface</c> — which is where a published compound's ports come from,
///         and what these tests use — is a list an author fills in, in which the default is a column
///         that may simply be blank.
///     </para>
///     <para>
///         ⚠ <b>Every port here reaches the panel the way a compound's does</b>: written into
///         <c>Assets/Compounds</c> as a <c>.vxtexgraph</c> before the stack document exists, so
///         <c>TextureNodeLibrary.Publish</c> is what turns it into the node type the row is drawn
///         from. A <see cref="NodeTypeDefinition" /> handed straight to the view would be a fixture
///         proving that a method works rather than that the panel does.
///     </para>
/// </remarks>
public class LayerStackKnobWidthTests {
    /// <summary>The compound every test here publishes, and the key the filter layer names.</summary>
    const string Compound = "Tinted";

    /// <summary>⚠ A vector port with no declared default gets a field per lane, not one.</summary>
    /// <remarks>
    ///     <b>The count is the assertion and the row existing is not.</b> A row was always drawn for
    ///     this port — it is a declared, non-image input — so every reading that asks whether the
    ///     panel knows about <c>Tint</c> was green against the defect. Four is what the port's
    ///     <c>Float4</c> says it carries, and one is what its empty default used to say.
    /// </remarks>
    [Fact]
    public void A_vector_port_with_no_default_is_drawn_with_one_field_per_lane() {
        using var fixture = new TexturingFixture();

        Publish(fixture, new("Tint", GraphPortDirection.Input, PortKind.Float4));

        Open(fixture, Filtered());

        Assert.Equal(4, Fields(Panel(fixture), "Tint").Count);
    }

    /// <summary>⚠ Touching one lane of a stored vector leaves the other three where they were.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The data loss itself, which is the half the field count only implies.</b>
    ///         <c>LayerStackView.Lanes</c> treats a stored value whose width is not the drawn count
    ///         as absent, so under a one-field row the file's four numbers were invisible <em>and</em>
    ///         replaced: the first <c>NumberChanged</c> wrote a one-element array over them, from a
    ///         panel that had shown the artist nothing but a zero.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read before written, because either half alone is satisfied by the defect.</b> A
    ///         row that showed the stored numbers and then narrowed them is green on the write
    ///         assertion's <c>0.9</c>; a row that showed zeros and wrote four numbers is green on the
    ///         read. The stored value is four <em>distinct</em> numbers for the same reason — three
    ///         fields agreeing on a constant cannot tell a lane apart from its neighbour.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Editing_one_lane_of_a_stored_vector_keeps_the_rest_of_it() {
        using var fixture = new TexturingFixture();

        Publish(fixture, new("Tint", GraphPortDirection.Input, PortKind.Float4));

        var document = Open(
            fixture,
            Filtered(new() { ["Tint"] = [0.1f, 0.2f, 0.3f, 0.4f] })
        );

        var fields = Fields(Panel(fixture), "Tint");

        Assert.Equal([0.1d, 0.2d, 0.3d, 0.4d], fields.Select(field => Math.Round(field.Number, 3)));

        fields[0].Number = 0.9d;

        Assert.Equal([0.9f, 0.2f, 0.3f, 0.4f], Layer(document).Settings["Tint"]);
    }

    /// <summary>⚠ A port declaring more numbers than any kind holds is listed and disarmed.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>#1097's own case, reached the only way it can be reached.</b> No
    ///         <see cref="PortKind" /> is wider than four, so five lanes is a declaration disagreeing
    ///         with itself — and the issue's reasoning holds for it: drawing the first four would
    ///         write four numbers over five the moment one was touched, which is the same narrowing
    ///         with a smaller number in it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two panels, because "the row drew no fields" and "the panel drew no row" are the
    ///         same green.</b> The second fixture declares an ordinary <c>Float4</c> under the same
    ///         name, so the assertion is a differential: a <c>Knobs</c> that dropped the port
    ///         entirely, or a class renamed in the view, goes red on the half that must have fields.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_port_wider_than_any_kind_is_listed_with_a_sentence_and_no_fields() {
        using var fixture = new TexturingFixture();

        Publish(fixture, new("Tint", GraphPortDirection.Input, PortKind.Float4, [1f, 1f, 1f, 1f, 1f]));

        Open(fixture, Filtered());

        var refused = Row(Panel(fixture), "Tint");

        Assert.Empty(Named(refused, "layer-stack-knob-value"));

        Assert.Equal(
            LayerStackView.WideKnob("Tint", 5),
            Assert.Single(Named(refused, "layer-stack-row-refusal")).Text
        );

        using var ordinary = new TexturingFixture();

        Publish(ordinary, new("Tint", GraphPortDirection.Input, PortKind.Float4));

        Open(ordinary, Filtered());

        Assert.NotEmpty(Named(Row(Panel(ordinary), "Tint"), "layer-stack-knob-value"));
        Assert.Empty(Named(Row(Panel(ordinary), "Tint"), "layer-stack-row-refusal"));
    }

    /// <summary>Writes a compound exposing one input into the project, before any document reads it.</summary>
    /// <remarks>
    ///     ⚠ <b>On disk and not in memory.</b> <c>LayerStackDocument</c> publishes its library from
    ///     <c>Assets/Compounds</c> in its constructor, so a compound written afterwards is a node
    ///     type the panel's registry has never heard of and every row below would be the one
    ///     <c>FilterType</c> draws for an unresolved path — which is no row at all.
    /// </remarks>
    static void Publish(TexturingFixture fixture, PortDefinition port) {
        var folder = Path.Combine(fixture.Paths.Assets, TextureNodeLibrary.CompoundFolder);

        Directory.CreateDirectory(folder);

        var compound = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph(TextureNodeLibrary.CompoundFolder + "/" + Compound),
            Path.Combine(folder, Compound + TextureGraphDocument.Extension)
        );

        // The starter's own `Output/Output` goes, for `ExternalCompoundTests`' reason: inlining a
        // second output into a containing graph is a TG0006 about two nodes writing one usage.
        foreach (var node in compound.Graph.Nodes.ToArray()) {
            compound.Graph.Remove(node.Id, out _);
        }

        compound.Graph.Interface.Add(new("In", GraphPortDirection.Input, PortKind.Image));
        compound.Graph.Interface.Add(port);
        compound.Graph.Interface.Add(new("Out", GraphPortDirection.Output, PortKind.Image));

        var colour = compound.Graph.Add("Source/Uniform");
        var exit = compound.Graph.Add(SubGraphs.OutputType);

        compound.Graph.Connect(new(colour.Id, "Out"), new(exit.Id, "Out"));

        File.WriteAllText(compound.AssetPath, compound.ToYaml());
    }

    /// <summary>A one-filter stack whose filter names the published compound.</summary>
    /// <param name="settings">What the filter layer already holds, as a file would.</param>
    static LayerStackAsset Filtered(Dictionary<string, float[]>? settings = null) {
        LayerAsset filter = new() { Id = "adjust", Name = "Adjust", Kind = LayerKind.Filter, FilterNode = Compound };

        foreach (var (port, value) in settings ?? []) {
            filter.Settings[port] = value;
        }

        return new() {
            Name = "Hull",
            BaseWidth = 32,
            BaseHeight = 32,
            Seed = 7u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [
                        new() {
                            Id = "bottom",
                            Name = "Bottom",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [0.25f, 0.25f, 0.25f, 1f] }
                        },
                        filter
                    ]
                }
            ]
        };
    }

    /// <summary>Opens a stack through the module's own verb and puts a made one in front of the panel.</summary>
    static LayerStackDocument Open(TexturingFixture fixture, LayerStackAsset stack) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        // ⚠ Of this type rather than the only one: `Publish` leaves the compound's own
        // `TextureGraphDocument` open in the project, so a `Single` over all of them throws before
        // any assertion is reached.
        var document = fixture.Project.Documents.OfType<LayerStackDocument>().Single();

        document.Document = stack;

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        return document;
    }

    /// <summary>The filter layer, read back off the document.</summary>
    static LayerAsset Layer(LayerStackDocument document) =>
        document.Document.Sets[0].Layers.Single(layer => string.Equals(layer.Id, "adjust", StringComparison.Ordinal));

    /// <summary>The panel the module registers, opened.</summary>
    static UiElement Panel(TexturingFixture fixture) {
        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        return panel;
    }

    /// <summary>The knob row whose label names that port.</summary>
    static UiElement Row(UiElement panel, string port) {
        foreach (var row in Named(panel, "layer-stack-knob-row")) {
            foreach (var label in Named(row, "layer-stack-knob-label")) {
                if (string.Equals(label.Text, port, StringComparison.Ordinal)) {
                    return row;
                }
            }
        }

        throw new InvalidOperationException($"the panel drew no knob row for '{port}'");
    }

    /// <summary>Every numeric field on that port's row, in the order the row lays them out.</summary>
    static List<NumericInput> Fields(UiElement panel, string port) =>
        [.. Named(Row(panel, port), "layer-stack-knob-value").Select(Assert.IsType<NumericInput>)];

    /// <summary>Every element under a tag or a class, which may legitimately be none.</summary>
    static List<UiElement> Named(UiElement root, string name) {
        List<UiElement> found = [];

        Walk(root);

        return found;

        void Walk(UiElement element) {
            if (string.Equals(element.Tag, name, StringComparison.Ordinal) || element.HasClass(name)) {
                found.Add(element);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }
}
