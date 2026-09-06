// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
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
    /// <summary>The panel these tests register, which is their own rather than the module's.</summary>
    const string TrailPanel = "texturing.graph.trail-test";

    static TimeSpan clock;


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

    /// <summary>Double-clicking a compound puts the graph it stands for on the canvas.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The other half of <a href="https://github.com/Rikarin/Vixen/issues/859">#859</a>,
    ///         and it is the half that decides whether any of the rest is reachable.</b>
    ///         <c>NodeGraphView.SubGraphOpened</c> was raised by the canvas and subscribed to by
    ///         nothing in the whole tree — one handler, in <c>Vixen.Editor.NodeGraph.Tests</c> — so an
    ///         author could place <c>Generators/Dirt</c>, compile it, bake it, and never see what it
    ///         was.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The gesture is dispatched as pointer events through the shell, not raised.</b> A
    ///         test that invoked the handler would take a path no interaction takes — and it could
    ///         not have caught what this one did catch, which is that the canvas was zero pixels high
    ///         in a real shell (#917) and there was nothing on it to click.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Double_clicking_a_compound_opens_it_on_the_canvas() {
        using var fixture = new TexturingFixture();
        var (view, canvas) = Opened(fixture);
        var compound = TextureGraph.TextureCompoundLibrary.Shipped[0];

        canvas.Graph.Add(compound, new(120f, 120f));

        Settle(fixture);

        Assert.Equal([canvas.Graph.Name], view.Trail);

        DoubleClick(fixture, Item(canvas, compound));

        // The canvas is showing the published graph, and the trail says how to get back.
        Assert.True(canvas.SubGraphSource!.TryGet(compound, out var inner));
        Assert.Same(inner, canvas.Graph);
        Assert.Equal(2, view.Trail.Count);
        Assert.Equal(compound, view.Trail[1]);
    }

    /// <summary>Inside a published graph the canvas refuses edits, and the strip says why.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One <c>NodeGraphModel</c> serves every graph that contains the node type</b> — the
    ///         library holds it and the compiler inlines from it — so an edit made here would rewrite
    ///         a compound for every material in the project, with no undo entry and no file to save
    ///         it to. For a shipped compound there is no file at all: it is an embedded resource.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The note is read off the strip rather than off the constant</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/930">#930</a>). This asserted
    ///         <c>Assert.Contains("Open its own asset", … ? TextureGraphView.ReadOnly : "")</c> — a
    ///         <c>const</c> against a substring somebody typed out of it, whose only variable was the
    ///         trail's length. It would have passed with <c>Retrail</c> never adding the note at all,
    ///         and it matters here more than usual: the same review found that leaving a compound
    ///         left the author's <em>own</em> graph read-only, a state this note would have been
    ///         wrong about in the opposite direction.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because "the note is on the screen" is satisfied by a note that is
    ///         always on the screen.</b> The strip is empty while the canvas is showing the author's
    ///         own graph and carries the note while it is showing somebody else's.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_published_graph_is_shown_read_only_and_the_strip_says_so() {
        using var fixture = new TexturingFixture();
        var (view, canvas) = Opened(fixture);
        var compound = TextureGraph.TextureCompoundLibrary.Shipped[0];

        canvas.Graph.Add(compound, new(120f, 120f));

        Settle(fixture);

        Assert.False(canvas.IsReadOnly);
        Assert.DoesNotContain("Open its own asset", Strip(view), StringComparison.Ordinal);

        DoubleClick(fixture, Item(canvas, compound));

        Assert.True(canvas.IsReadOnly);

        // The whole sentence, off the element the author looks at — so a note that stopped being
        // added, or that was added with somebody else's text, is a failure rather than a pass.
        Assert.Contains(TextureGraphView.ReadOnly, Strip(view), StringComparison.Ordinal);
    }

    /// <summary>Every word the trail strip is showing, in order.</summary>
    /// <param name="view">The panel.</param>
    /// <returns>The strip's text.</returns>
    /// <remarks>
    ///     ⚠ <b>The strip itself, found by tag, and its absence is a failure rather than an empty
    ///     answer.</b> A helper that returned <c>""</c> for "there is no strip" would make the
    ///     before-half of every assertion over it true on a panel that never built one, which is the
    ///     same shape as the constant this replaced. The strip exists from the constructor and is
    ///     emptied rather than removed, so an author outside a compound gets one with no children.
    /// </remarks>
    static string Strip(TextureGraphView view) {
        List<string> words = [];
        var strip = All(view.Root, "texture-graph-trail");

        Assert.Single(strip);

        foreach (var child in strip[0].Children) {
            Walk(child);
        }

        return string.Join(" ", words);

        void Walk(UiElement element) {
            if (element is Button { Label: { Length: > 0 } label }) {
                words.Add(label);
            } else if (element.Text is { Length: > 0 } text) {
                words.Add(text);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    /// <summary>Every element under a root with one tag.</summary>
    /// <param name="root">Where to start.</param>
    /// <param name="tag">The tag to match.</param>
    /// <returns>The matches, outermost first.</returns>
    static List<UiElement> All(UiElement root, string tag) {
        List<UiElement> found = [];

        Walk(root);

        return found;

        void Walk(UiElement element) {
            if (string.Equals(element.Tag, tag, StringComparison.Ordinal)) {
                found.Add(element);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    /// <summary>A refresh while inside a published graph does not throw the author out of it.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Show</c> runs on every edit and every evaluation, and it re-seats the canvas on
    ///     the document's own graph.</b> A view that did that unconditionally would close the
    ///     compound on the next refresh, which for this panel is immediately — the feature would look
    ///     as if the double-click had never worked.
    /// </remarks>
    [Fact]
    public void Refreshing_the_panel_leaves_the_author_inside_the_compound() {
        using var fixture = new TexturingFixture();
        var (view, canvas) = Opened(fixture);
        var compound = TextureGraph.TextureCompoundLibrary.Shipped[0];

        canvas.Graph.Add(compound, new(120f, 120f));

        Settle(fixture);
        DoubleClick(fixture, Item(canvas, compound));

        var inside = canvas.Graph;

        view.Show(view.Document, TexturePreviewBlocker.NoDevice);

        Assert.Same(inside, canvas.Graph);
        Assert.Equal(2, view.Trail.Count);
    }

    /// <summary>The first crumb is the way back, and it restores the document's own undo stack.</summary>
    [Fact]
    public void The_trail_goes_back_to_the_document_and_gives_the_stack_back() {
        using var fixture = new TexturingFixture();
        var (view, canvas) = Opened(fixture);
        var compound = TextureGraph.TextureCompoundLibrary.Shipped[0];

        canvas.Graph.Add(compound, new(120f, 120f));

        Settle(fixture);
        DoubleClick(fixture, Item(canvas, compound));

        var document = view.Document!;
        var crumbs = Crumbs(view);

        Assert.Equal(2, crumbs.Count);

        Click(fixture, crumbs[0]);

        Assert.Same(document.Graph, canvas.Graph);
        Assert.False(canvas.IsReadOnly);
        Assert.Single(view.Trail);
    }

    /// <summary>Opening a different graph starts a new trail rather than continuing the old one.</summary>
    [Fact]
    public void Showing_another_document_resets_the_trail() {
        using var fixture = new TexturingFixture();
        var (view, canvas) = Opened(fixture);
        var compound = TextureGraph.TextureCompoundLibrary.Shipped[0];

        canvas.Graph.Add(compound, new(120f, 120f));

        Settle(fixture);
        DoubleClick(fixture, Item(canvas, compound));

        Assert.Equal(2, view.Trail.Count);

        var second = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph("Tiles"),
            fixture.Paths.Absolute("Assets/Tiles" + TextureGraphDocument.Extension)
        );

        view.Show(second, TexturePreviewBlocker.NoDevice);

        Assert.Single(view.Trail);
        Assert.Same(second.Graph, canvas.Graph);
    }

    /// <summary>A compound saved while the author is inside it is re-resolved, not left stale.</summary>
    /// <remarks>
    ///     ⚠ <b>A republish builds a whole new library</b> — see
    ///     <a href="https://github.com/Rikarin/Vixen/issues/803">#803</a> — so every graph the trail
    ///     is holding belongs to the old one. An author who was looking inside a compound while it
    ///     was saved in another tab would go on inspecting a model nothing in the editor refers to
    ///     any more: a picture of the version they were trying to change, with no way to tell.
    /// </remarks>
    [Fact]
    public void A_compound_saved_while_it_is_open_is_re_resolved_on_the_canvas() {
        using var fixture = new TexturingFixture();

        var folder = System.IO.Path.Combine(fixture.Paths.Assets, TextureNodeLibrary.CompoundFolder);

        Directory.CreateDirectory(folder);

        var compound = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph(TextureNodeLibrary.CompoundFolder + "/Grunge"),
            System.IO.Path.Combine(folder, "Grunge" + TextureGraphDocument.Extension)
        );

        compound.Graph.Interface.Add(new("Out", NodeGraph.PortDirection.Output, PortKind.Image));
        compound.Save();

        var (view, canvas) = Opened(fixture, "Material");

        canvas.Graph.Add("Grunge", new(120f, 120f));

        Settle(fixture);
        DoubleClick(fixture, Item(canvas, "Grunge"));

        var before = canvas.Graph;

        Assert.Equal(2, before.Nodes.Count);
        Assert.Equal(2, view.Trail.Count);

        compound.Graph.Add("Filters/Blur");
        compound.Save();

        view.Show(view.Document, TexturePreviewBlocker.NoDevice);

        // Still inside it, and looking at the version that is now on disk.
        Assert.Equal(2, view.Trail.Count);
        Assert.NotSame(before, canvas.Graph);
        Assert.Equal(3, canvas.Graph.Nodes.Count);
    }

    /// <summary>Opens a docked panel holding the view, with a graph on it.</summary>
    /// <remarks>
    ///     ⚠ <b>A real <c>DockPanel</c> rather than a bare element under the root.</b> Every
    ///     assertion below is about clicking something on the canvas, and the canvas only has a size
    ///     inside the layout a panel gives it — a view built into a loose element would measure zero
    ///     and every click would land on nothing while the test went green.
    /// </remarks>
    static (TextureGraphView View, NodeGraphView Canvas) Opened(TexturingFixture fixture, string name = "Bricks") {
        TextureGraphView? built = null;

        fixture.Shell.RegisterPanel(
            TrailPanel,
            new StringId("editor.panel." + TrailPanel, "Texture Graph"),
            panel => built = new TextureGraphView(panel)
        );

        fixture.Shell.Workspace.Open(TrailPanel);

        Assert.NotNull(built);

        built.Show(Document(fixture, name), TexturePreviewBlocker.NoDevice);

        Settle(fixture);

        // ⚠ The instrument. A canvas with no height has nothing on it to click, which is exactly the
        // state this panel was in — see #917 — and a click that lands on nothing raises nothing.
        Assert.True(
            built.Canvas.Bounds.Height > 0f,
            "the canvas has no height, so nothing on it is clickable"
        );

        return (built, built.Canvas);
    }

    static TextureGraphDocument Document(TexturingFixture fixture, string name) =>
        new(
            fixture.Project,
            fixture.AddGraph(name),
            fixture.Paths.Absolute("Assets/" + name + TextureGraphDocument.Extension)
        );

    static void Settle(TexturingFixture fixture) {
        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();
    }

    static NodeItem Item(NodeGraphView canvas, string type) {
        var node = Assert.Single(canvas.Graph.Nodes, one => one.Type == type);

        return canvas.Canvas.Items.FirstOrDefault(item => item.Node?.Tag is NodeId id && id == node.Id)
            ?? throw new InvalidOperationException($"'{type}' has no element on the canvas.");
    }

    /// <summary>The buttons of the trail strip, outermost first.</summary>
    static List<Button> Crumbs(TextureGraphView view) {
        List<Button> found = [];

        Walk(view.Root);

        return found;

        void Walk(UiElement element) {
            if (element is Button button) {
                found.Add(button);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    /// <summary>Presses and releases in the middle of an element, twice, as a person does.</summary>
    /// <remarks>
    ///     ⚠ <b>Dispatched at the document rather than raised at the control.</b> A canvas marks its
    ///     own pointer events handled and <c>AddHandler</c> does not hear a handled event by default,
    ///     so a test that shortcut this would take a path no interaction takes.
    /// </remarks>
    static void DoubleClick(TexturingFixture fixture, UiElement element) {
        Click(fixture, element);
        Click(fixture, element);
    }

    static void Click(TexturingFixture fixture, UiElement element) {
        var bounds = element.Bounds;
        var x = bounds.X + (bounds.Width * 0.5f);
        var y = bounds.Y + (bounds.Height * 0.5f);

        Send(fixture, x, y, PointerAction.Pressed);
        Send(fixture, x, y, PointerAction.Released);
    }

    static void Send(TexturingFixture fixture, float x, float y, PointerAction action) {
        clock += TimeSpan.FromMilliseconds(16);

        fixture.Shell.Document.Dispatch(
            new PointerEvent {
                X = x,
                Y = y,
                Action = action,
                Button = PointerButton.Primary,
                Timestamp = clock
            }
        );

        Settle(fixture);
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
