// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The panel, opened through the shell the way a person opens it.</summary>
/// <remarks>
///     ⚠ <b>Through <c>Workspace.Open</c> rather than by constructing the view.</b> The claim is that
///     a plugin's panel is reachable in a real shell — a test that built <c>TextureGraphView</c>
///     directly would pass in an editor where the registration was never made, which is precisely the
///     state doc 48 § D14 says this whole slice exists to leave.
/// </remarks>
public class TextureGraphPanelTests {
    [Fact]
    public void Opening_the_panel_builds_a_canvas_and_an_image_view() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var panel = fixture.Shell.Workspace.Open(TexturingModule.GraphPanel);

        Assert.NotNull(panel);
        Assert.NotNull(Find<NodeGraphView>(panel));

        // ⚠ The first production caller `ImageView` has had. Batch 1 built it for this panel and
        // nothing in the editor constructed one until now — a control with no caller is a control
        // whose first real use finds the bugs.
        Assert.NotNull(Find<ImageView>(panel));
    }

    [Fact]
    public void With_no_graph_open_it_says_so_and_shows_no_extent() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var image = Find<ImageView>(fixture.Shell.Workspace.Open(TexturingModule.GraphPanel)!);

        Assert.NotNull(image);

        // Zero is "nothing to show" and the control checks it rather than assuming — an extent with no
        // handle still draws its chequerboard, and an extent of zero draws nothing at all.
        Assert.Equal(0, image.ImageWidth);
        Assert.Equal(0, image.ImageHeight);
    }

    /// <summary>The verb, which is one of the two ways into the panel.</summary>
    /// <remarks>
    ///     ⚠ <b>It used to be the only one</b>, because <c>AssetEditorRegistry.Add</c> had no
    ///     matching removal and a plugin that claimed an extension could never give it back. The
    ///     other is now the double-click — see <c>TexturingClaimTests</c> — and this one stays,
    ///     because it is what a host with no asset-editor registry offers.
    /// </remarks>
    [Fact]
    public void The_open_command_puts_the_selected_graph_on_the_canvas() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var asset = fixture.AddGraph("Bricks");

        fixture.Project.Selection.Set(asset);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.GraphPanel);
        var canvas = Find<NodeGraphView>(panel!);
        var image = Find<ImageView>(panel!);

        Assert.NotNull(canvas);
        Assert.NotNull(image);

        // The document is on the canvas, and it is the starter graph read out of the empty file.
        Assert.Contains(canvas.Graph.Nodes, node => node.Type == "Output/Output");

        // ⚠ And the preview pane is showing that graph's extent rather than a constant. This fixture
        // publishes no graphics, so there is no handle — see `TexturePreviewBlocker` — and a pane
        // hard-coded to 1024 would look identical until somebody changed the resolution, which is
        // what the test below does.
        Assert.Equal(1024, image.ImageWidth);
        Assert.Equal(1024, image.ImageHeight);
    }

    /// <summary>⚠ And the extent follows the document rather than the default.</summary>
    /// <remarks>
    ///     The sabotage the test above cannot survive on its own: a panel that wrote 1024 into the
    ///     image view and never read the document would pass it. This one changes the document.
    /// </remarks>
    [Fact]
    public void The_preview_extent_is_the_documents_and_not_a_constant() {
        using var fixture = new TexturingFixture();

        var document = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph("Wide"),
            fixture.Paths.Absolute("Assets/Wide" + TextureGraphDocument.Extension)
        ) { BaseWidth = 512, BaseHeight = 256 };

        var host = fixture.Shell.Document.Root.Add<UiElement>();
        var view = new TextureGraphView(host);

        view.Show(document, TexturePreviewBlocker.NoDevice);

        Assert.Equal(512, view.Preview.ImageWidth);
        Assert.Equal(256, view.Preview.ImageHeight);
        Assert.Same(document.Graph, view.Canvas.Graph);

        // And the status line names what is in the way, so a reader of the empty pane is told rather
        // than left to guess.
        Assert.Contains("no graphics device", view.Status, StringComparison.Ordinal);
    }

    /// <summary>The canvas can look inside a published compound, because it has the library.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half of <a href="https://github.com/Rikarin/Vixen/issues/803">#803</a> the
    ///         document's own wire left dark.</b> <c>TextureGraphDocument</c> publishes the shipped
    ///         compounds and hands the source to the <em>compiler</em>, so a graph containing one
    ///         compiles — and the canvas was never given it, so <c>NodeGraphView.Opened</c> could not
    ///         tell a sub-graph node from an atomic one and a double-click on a compound did nothing
    ///         at all. A node type in the search popup that cannot be looked inside.
    ///     </para>
    ///     <para>
    ///         <b>The assertion is the question the view asks</b> — <c>SubGraphSource.TryGet</c> over
    ///         a type the registry offers — rather than a null check, which a source that resolved
    ///         nothing would also pass.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_canvas_resolves_a_published_compound_to_the_graph_it_stands_for() {
        using var fixture = new TexturingFixture();

        var document = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph("Bricks"),
            fixture.Paths.Absolute("Assets/Bricks" + TextureGraphDocument.Extension)
        );

        var view = new TextureGraphView(fixture.Shell.Document.Root.Add<UiElement>());

        view.Show(document, TexturePreviewBlocker.NoDevice);

        var compound = TextureGraph.TextureCompoundLibrary.Shipped[0];

        Assert.NotNull(view.Canvas.SubGraphSource);
        Assert.True(view.Canvas.Registry.TryGet(compound, out _), $"'{compound}' is not in the menu.");
        Assert.True(
            view.Canvas.SubGraphSource!.TryGet(compound, out var inner),
            $"the canvas cannot resolve '{compound}', so double-clicking it does nothing."
        );

        Assert.NotEmpty(inner!.Nodes);
    }

    /// <summary>A compound that will not read is said in the pane rather than nowhere.</summary>
    /// <remarks>
    ///     ⚠ <b>The cost of publishing being forgiving, and nothing read it.</b>
    ///     <c>TextureCompoundLibrary.Publish</c> reports and skips a file it cannot parse — one bad
    ///     compound must not cost an author the rest of the library — so what an author sees is a
    ///     node type missing from the search popup and no word anywhere.
    ///     <c>TextureGraphDocument.CompoundProblems</c> had no reader at all until the line this
    ///     asserts on, which is #803's own defect one level down.
    /// </remarks>
    [Fact]
    public void A_compound_that_will_not_read_is_named_in_the_pane() {
        using var fixture = new TexturingFixture();

        Directory.CreateDirectory(Path.Combine(fixture.Paths.Assets, "Compounds"));
        File.WriteAllText(
            Path.Combine(fixture.Paths.Assets, "Compounds", "Broken" + TextureGraphDocument.Extension),
            "nodes: [ this is not a graph"
        );

        var document = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph("Bricks"),
            fixture.Paths.Absolute("Assets/Bricks" + TextureGraphDocument.Extension)
        );

        // The instrument: the file really did fail to publish, rather than the pane being told
        // nothing because there was nothing to tell.
        Assert.Contains(document.CompoundProblems, problem => problem.Path == "Broken");

        var view = new TextureGraphView(fixture.Shell.Document.Root.Add<UiElement>());

        view.Show(document, TexturePreviewBlocker.NoDevice);

        Assert.Contains("Broken", view.Status, StringComparison.Ordinal);
        Assert.Contains("not in the menu", view.Status, StringComparison.Ordinal);

        // And the blocker's own sentence survives beside it: two things to say is two sentences.
        Assert.Contains("no graphics device", view.Status, StringComparison.Ordinal);
    }

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T found) {
            return found;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } inside) {
                return inside;
            }
        }

        return null;
    }
}
