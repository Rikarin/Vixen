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

    /// <summary>The verb doc 48 § D14 says a plugin cannot express as a double-click.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what stands in for an asset-editor registration</b>, because
    ///     <c>AssetEditorRegistry.Add</c> has no matching removal and a plugin that used it could
    ///     never be unloaded. Running the command is therefore the only path into the panel that a
    ///     plugin can offer today, which makes it the path worth asserting.
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

        // ⚠ And the preview pane is showing that graph's extent rather than a constant. There is no
        // handle — see `TexturePreviewBlocker` — so this is the whole of what "wired to the document"
        // can mean in this host, and a pane hard-coded to 1024 would look identical until somebody
        // changed the resolution.
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
        var view = new TextureGraphView(host, TexturePreviewBlocker.NoDevice);

        view.Show(document);

        Assert.Equal(512, view.Preview.ImageWidth);
        Assert.Equal(256, view.Preview.ImageHeight);
        Assert.Same(document.Graph, view.Canvas.Graph);

        // And the status line names what is in the way, so a reader of the empty pane is told rather
        // than left to guess.
        Assert.Contains("IGraphicsDevice", view.Status, StringComparison.Ordinal);
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
