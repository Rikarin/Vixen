// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A compound an author published in their own project reaches a layer stack's picture.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/924">#924</a>, which is
///         <a href="https://github.com/Rikarin/Vixen/issues/858">#858</a>'s fix reaching a
///         person.</b> <c>LayerStackCompiler.Compile</c> grew an <c>assets</c> parameter and every
///         production caller went on passing nothing, so a <c>LayerFillSource.Graph</c> fill naming
///         a graph out of <c>Assets/Compounds</c> compiled in the texture-graph panel and refused in
///         the layers panel — the same node type meaning two things depending on which pane the
///         author was in.
///     </para>
///     <para>
///         ⚠ <b>Through the panel and never through the parameter.</b> The whole content of the
///         issue is which argument a production caller passes, so a test that called
///         <c>Compile(stack, set, assets: …)</c> itself would prove the parameter works — which was
///         never in doubt — and nothing about the editor. Everything below goes through
///         <c>Open Layer Stack</c>, and the picture asserted on is the one the module uploaded.
///     </para>
///     <para>
///         ⚠ <b>Texels rather than a diagnostic count, and the compound's colour is the oracle.</b>
///         An assertion that the compilation had no problems is satisfied by a stack whose fill was
///         dropped, and the channel's own default is a perfectly plausible flat picture. The
///         compound is a single <c>Source/Uniform</c> at an ordered quarter/half/three-quarters, so
///         a picture that is that colour cannot have come from anything but the published graph
///         being inlined — the channel default under it is black.
///     </para>
/// </remarks>
public class ProjectCompoundDeviceTests {
    /// <summary>What the published compound paints, and nothing else in the project paints it.</summary>
    static readonly float[] Ochre = [0.25f, 0.5f, 0.75f, 1f];

    /// <summary>The node-type path the compound is published under, from its file name.</summary>
    const string Published = "Ochre";

    [Fact]
    public void A_projects_own_compound_reaches_the_layers_panels_picture() {
        using var device = TexturingDevice.Open();
        using var fixture = new TexturingFixture(device);

        Publish(fixture);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<LayerStackDocument>());

        Assert.NotNull(fixture.Graphics);

        var before = fixture.Graphics.Uploads.Count;

        document.Document = Filled(64);

        // The second run is what redraws with the stack above in place — `LayerStackPanelDeviceTests`
        // does the same, and for the same reason: the document is replaced after the first open.
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        // ⚠ A *new* upload, and the first draft of this test read the old one. The starter stack the
        // first open drew is a flat mid grey, so a compilation that refused the graph fill leaves the
        // last upload showing a plausible picture from before the stack under test existed — an
        // assertion on `Uploads[^1]` alone would be reading it.
        Assert.True(
            fixture.Graphics.Uploads.Count > before,
            $"{TexturingDevice.Adapter(device)}: the stack was replaced and redrawn and nothing new was "
            + "uploaded, so the compilation refused and the pane is still showing the starter."
        );

        var picture = fixture.Graphics.Uploads[^1];
        var (red, green, blue) = (picture.Pixels[0], picture.Pixels[1], picture.Pixels[2]);

        // ⚠ Ordered rather than exact: the fill travels through the set's own composite and the
        // read-back is eight-bit, so the claim that survives both is that the three components are
        // the compound's ascending ramp and not the channel's black default or a flat anything.
        Assert.True(
            red < green && green < blue && red > 0,
            $"{TexturingDevice.Adapter(device)}: the stack's one layer is a graph fill naming '{Published}', a "
            + $"compound in this project's own Assets/Compounds, and the baked base colour's first texel is "
            + $"({red}, {green}, {blue}). The compound paints an ordered quarter/half/three-quarters; black is "
            + "the channel default under a fill that never reached the plan."
        );
    }

    /// <summary>⚠ And the compilation says nothing, which is the half the picture cannot show.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>No device, because the refusal this is about happens before one is asked for.</b>
    ///         <c>LayerStackPreview.Evaluate</c> compiles first and reports the compilation whether
    ///         or not there is anything to draw with, so the sentence under a pane in a
    ///         device-less editor is the same sentence — which is exactly the state the editor is in
    ///         at start-up.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The refusal is a <c>LayerStackProblem</c> and not the <c>TG0001</c> the issue
    ///         predicted, and the difference is worth writing down.</b> A graph fill naming a type
    ///         the library does not have is caught by <c>LayerStackGraph</c> before a node is
    ///         emitted, so the stack produces no plan at all rather than a plan with an unresolved
    ///         node in it. <c>TG0001</c> is what a <em>mask effect</em> naming one would produce,
    ///         through the same missing library.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_layers_panel_does_not_refuse_a_project_compound() {
        using var fixture = new TexturingFixture(graphics: true);

        Publish(fixture);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<LayerStackDocument>());

        document.Document = Filled(64);

        using LentEvaluator evaluators = new();
        using LayerStackPreview preview = new(fixture.Graphics!, evaluators.Lease, new PaintCanvasStore());

        var picture = preview.Evaluate(document);

        Assert.Empty(picture.Problems);
        Assert.Empty(picture.Diagnostics);

        // The pane's sentence is about the device it has not got, not about the fill — which is the
        // difference between this build and the one that shipped the parameter with no caller.
        Assert.DoesNotContain(Published, picture.Status, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <c>LayerStackGraph.Build</c>'s null registry means the shipped compounds, and the
    ///     sentence it produces names the compound it could not find.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/946">#946</a>, which asked for the
    ///         default to be <em>said</em> rather than changed.</b> A remark is a claim like any
    ///         other, and this is the one that holds it: with no registry the same stack that works
    ///         through the panel is refused, and with the registry
    ///         <c>LayerStackCompiler.Library(out _, assets)</c> builds from the project's folder it is
    ///         not. Both halves, because "it is refused" alone is also what a stack with a typo in it
    ///         produces.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the refusal is a <c>LayerStackProblem</c> naming the path, not <c>TG0001</c>
    ///         and not a dropped layer.</b> The issue predicted the compiler's diagnostic; what the
    ///         builder actually does is refuse before it emits a node, which is a better answer and a
    ///         different one. The assertion reads the sentence, so a change that made the build fail
    ///         silently would be red here rather than quietly satisfying "there was a problem".
    ///     </para>
    /// </remarks>
    [Fact]
    public void Building_a_stack_without_a_registry_refuses_the_projects_own_compound_by_name() {
        using var fixture = new TexturingFixture();

        Publish(fixture);

        var stack = Filled(64);

        var alone = LayerStackGraph.Build(stack, stack.Sets[0]);
        var refusal = Assert.Single(alone.Problems);

        Assert.Contains(Published, refusal.Message, StringComparison.Ordinal);

        // And with the project's own folder published into the registry, the same stack builds.
        var registry = LayerStackCompiler.Library(out _, fixture.Paths.Assets);
        var withProject = LayerStackGraph.Build(stack, stack.Sets[0], registry);

        Assert.Empty(withProject.Problems);

        // ⚠ The instrument: "no problems" is also what a build that emitted nothing would say, and
        // the refused build above emitted the channel's default on its own.
        Assert.True(
            withProject.Graph.Nodes.Count > alone.Graph.Nodes.Count,
            $"the build with the project's compounds has {withProject.Graph.Nodes.Count} nodes and the one "
            + $"without has {alone.Graph.Nodes.Count}, so the fill reached neither graph."
        );
    }

    /// <summary>Writes a compound into the project's own <c>Assets/Compounds</c>.</summary>
    /// <param name="fixture">The project.</param>
    /// <remarks>
    ///     A <c>Source/Uniform</c> straight into the sub-graph's boundary: the smallest published
    ///     graph whose result is a colour nothing else in the project produces.
    /// </remarks>
    static void Publish(TexturingFixture fixture) {
        var folder = Path.Combine(fixture.Paths.Assets, TextureNodeLibrary.CompoundFolder);

        Directory.CreateDirectory(folder);

        var compound = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph(TextureNodeLibrary.CompoundFolder + "/" + Published),
            Path.Combine(folder, Published + TextureGraphDocument.Extension)
        );

        // ⚠ The starter goes first, and leaving it in is the mistake this line exists for. A new
        // `TextureGraphDocument` is a `Source/Uniform` into an `Output/Output` — sensible for a
        // material's own graph and wrong for a compound, because inlining carries that second output
        // into the containing graph and the stack then has two nodes writing 'baseColor' (TG0006).
        foreach (var node in compound.Graph.Nodes.ToArray()) {
            compound.Graph.Remove(node.Id, out _);
        }

        compound.Graph.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));

        var colour = compound.Graph.Add("Source/Uniform");
        var exit = compound.Graph.Add(SubGraphs.OutputType);

        colour.SetValue("Colour", Ochre);
        compound.Graph.Connect(new(colour.Id, "Out"), new(exit.Id, "Out"));
        compound.Save();
    }

    /// <summary>A one-layer stack whose fill is the published compound.</summary>
    /// <param name="extent">How wide and tall, in texels.</param>
    /// <returns>The stack.</returns>
    static LayerStackAsset Filled(int extent) =>
        new() {
            Name = "Hull",
            BaseWidth = extent,
            BaseHeight = extent,
            Seed = 5u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [
                        new() {
                            Id = "fill",
                            Name = "Fill",
                            Kind = LayerKind.Fill,
                            Fill = LayerFillSource.Graph,
                            Graph = Published
                        }
                    ]
                }
            ]
        };
}
