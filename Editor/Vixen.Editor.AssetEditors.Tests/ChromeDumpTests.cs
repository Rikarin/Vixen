// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Code;
using Vixen.Editor.AssetEditors.Compositor;
using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.AssetEditors.Vfx;
using Vixen.Editor.ShaderGraph;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The four chrome ports of doc 36 § F7 wave 8, held to what they replaced.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A committed test rather than a wave note.</b> Wave 6 found that "byte-identical in N
///         dumped states" was claimed by nine ledger rows and gated by three test files; every other
///         comparison was run once, eyeballed and deleted. These are kept.
///     </para>
///     <para>
///         ⚠ <b>Two dumps, because a tree dump is blind.</b> <c>UiTest.Tree</c> sees tags, classes,
///         rectangles and text and nothing else; a button's <c>Label</c>, a toggle's
///         <c>IsChecked</c>, a text box's <c>Value</c> and its <c>Placeholder</c> all live in parts a
///         control owns. Wave 7's <c>StandardFrameView</c> matched byte-for-byte in six states while
///         carrying a binding that could not work, which is what <c>UiTest.Flags</c> exists for.
///     </para>
///     <para>
///         ⚠ <b>And every <c>change:</c> the four files declare is exercised through its
///         control.</b> Wave 7's dumps drove their panels from the model, which is the leg that
///         cannot fail. There are three here — the VFX transport's Play, the shader graph's
///         Generated-code toggle, and the property node's rename box — and each is written by
///         setting the control's own property.
///     </para>
/// </remarks>
public sealed class ChromeDumpTests {
    // ── The shared row, which three of the four now build ────────────────────

    /// <summary>
    ///     ⚠ <b>The part five panels were already on and nothing dumped.</b> <c>AnalysisRow</c> was
    ///     extracted in wave 6 and carried no comparison of its own; this wave put three more panels
    ///     on it, so the four lines it replaces are worth a gate. Both forms are built in the same
    ///     place, because <c>UiTest.Tree</c> prints absolute positions.
    /// </summary>
    [Fact]
    public void The_analysis_row_is_the_four_lines_every_report_wrote() {
        using var harness = new ViewHarness();

        (string Stage, string Message)[] notes = [
            ("file", "VX0001: the file could not be read"),
            ("graph", "SG0002: two masters"),
            ("shader", "Opened compiles: 3 node(s), 1 uniform(s), 42 lines of Raven.")
        ];

        var handWritten = Dump(harness, host => {
            foreach (var (stage, message) in notes) {
                var row = host.Add("analysis-row");

                row.Add("analysis-stage").Text = stage;
                row.Add("analysis-message").Text = message;
            }
        });

        var part = Dump(harness, host => {
            foreach (var (stage, message) in notes) {
                var row = host.Add<AnalysisRow>();

                row.Stage = stage;
                row.Message = message;
            }
        });

        Assert.Equal(handWritten, part);
        Assert.NotEqual("", part.Tree);

        // ⚠ And the cells are the two children the tests index positionally. `ShaderGraphTests`
        // reads `row.Children[0].Text` and `row.Children[^1].Text`, so a part that wrapped either
        // cell would pass a tree comparison against itself and fail them.
        Assert.Contains("<analysis-stage> ", part.Tree, StringComparison.Ordinal);
        Assert.Contains("<analysis-message> ", part.Tree, StringComparison.Ordinal);
    }

    // ── The shader graph ─────────────────────────────────────────────────────

    /// <summary>
    ///     The chrome: a canvas, a hidden source pane, and a column of five things under it.
    /// </summary>
    [Fact]
    public void The_shader_graph_chrome_is_the_tree_it_was() {
        using var harness = new ViewHarness();

        var view = Shader(harness, "Chrome.vxshadergraph", out _);
        var tree = harness.Ui.Tree(view);
        var flags = harness.Ui.Flags(view);

        // The columns, in order, and the two sibling `analysis-list`s that differ by nothing else.
        Assert.Contains("<shadergraph-source .hidden>", tree, StringComparison.Ordinal);
        Assert.Contains("<shadergraph-transport>", tree, StringComparison.Ordinal);
        Assert.Equal(2, Lines(tree, "<analysis-list>"));

        // The two the tree dump cannot see, which is the half wave 7 was blind to.
        Assert.Contains("Label=\"Compile\"", flags, StringComparison.Ordinal);
        Assert.Contains("Label=\"Generated code\"", flags, StringComparison.Ordinal);
        Assert.Contains("ReadOnly=True", flags, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The Generated-code toggle is a <c>change:IsChecked</c> leg, driven through the
    ///     control.</b> It used to be a <c>CheckedChanged</c> handler calling
    ///     <c>Pane.AddClass</c>/<c>RemoveClass</c>; it writes a signal now and the class is a function
    ///     of it. A binding over a plain field would compile, run once and never re-run — so this
    ///     toggles it twice, because a one-way defect passes a one-way test.
    /// </summary>
    [Fact]
    public void The_generated_code_toggle_moves_the_class_both_ways() {
        using var harness = new ViewHarness();

        var view = Shader(harness, "Toggled.vxshadergraph", out _);

        Assert.True(view.Pane.HasClass("hidden"));

        view.ShowCode.IsChecked = true;
        harness.Ui.Frame();

        Assert.False(view.Pane.HasClass("hidden"));
        Assert.Contains("IsChecked=True", harness.Ui.Flags(view), StringComparison.Ordinal);

        view.ShowCode.IsChecked = false;
        harness.Ui.Frame();

        Assert.True(view.Pane.HasClass("hidden"));
        Assert.DoesNotContain("IsChecked=True", harness.Ui.Flags(view), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The rename box is a <c>change:Value</c> inside a surviving <c>@if</c> arm.</b> The
    ///     arm's predicate is "is a property node selected", so moving from one property node to
    ///     another does not rebuild it — the wave-4 rule — and every readout in it goes back through
    ///     the signal. A handler that had closed over the node would rename the first one selected,
    ///     for ever, and the existing suite would pass because it only ever selects one.
    /// </summary>
    [Fact]
    public void Renaming_reaches_the_node_that_is_selected_now() {
        using var harness = new ViewHarness();

        var view = Shader(harness, "Twins.vxshadergraph", out var document);

        // ⚠ The second node is added here rather than hoped for. A test that skipped its own second
        // half when a fixture happened to hold one property node would pass every day and check the
        // thing it was written for on none of them.
        var first = document.Graph.Nodes.First(node => node.Type == "Input/Colour Property");
        var second = document.Graph.Add("Input/Colour Property", new(80f, 400f));

        harness.Ui.Frame();

        Assert.Null(view.PropertyName);

        view.GraphView.Select([first.Id]);
        harness.Ui.Frame();

        Assert.NotNull(view.PropertyName);

        view.PropertyName!.Value = "first";
        harness.Ui.Frame();

        Assert.Equal("first", first.TextOf(ShaderProperties.Key));

        // The arm survives — its predicate is still "is a property node selected" — so this is the
        // write a captured node would send to the wrong place.
        view.GraphView.Select([second.Id]);
        harness.Ui.Frame();

        Assert.Equal("", view.PropertyName!.Value ?? "");

        view.PropertyName!.Value = "second";
        harness.Ui.Frame();

        Assert.Equal("second", second.TextOf(ShaderProperties.Key));
        Assert.Equal("first", first.TextOf(ShaderProperties.Key));
    }

    // ── The compositor ───────────────────────────────────────────────────────

    /// <summary>The chrome, and the diagnostics list that is now a keyed loop.</summary>
    [Fact]
    public void The_compositor_chrome_is_the_tree_it_was() {
        using var harness = new ViewHarness();

        var path = harness.Project.Write("Assets/Dumped.vxcomp", string.Empty);
        var document = new CompositorDocument(harness.Project.Project, AssetId.New(), path);

        var view = harness.Ui.Document.Root.Add<CompositorView>();

        view.Show(document);
        harness.Ui.Frame();

        var tree = harness.Ui.Tree(view);

        Assert.Contains("<compositor-side>", tree, StringComparison.Ordinal);
        Assert.Contains("<material-parameters>", tree, StringComparison.Ordinal);
        Assert.Contains("<analysis-list>", tree, StringComparison.Ordinal);

        // ⚠ Empty until something has been compiled, and that is the panel's own behaviour rather
        // than the port's: `Show` reports but does not compile here, where the shader and VFX
        // editors both do. Worth pinning, because "the list is empty" and "the list never ran" look
        // the same and the frame line below is what tells them apart.
        Assert.Empty(view.Diagnostics.Children);

        view.Compile();
        harness.Ui.Frame();

        // Said on success too: a list that empties itself when everything is fine cannot be told
        // apart from one that never ran.
        Assert.NotEmpty(view.Diagnostics.Children);
        Assert.All(view.Diagnostics.Children, row => Assert.Equal("analysis-row", row.Tag));
        Assert.Contains(view.Diagnostics.Children, row => row.Children[0].Text == "frame");

        Assert.Contains("Label=\"Compile frame\"", harness.Ui.Flags(view), StringComparison.Ordinal);

        // ⚠ The caption is one element that changes its text rather than one that comes and goes,
        // and it is deliberately still imperative — so it is right on the line after `Show`.
        Assert.Contains("Select a node", view.Caption.Text ?? "", StringComparison.Ordinal);
    }

    // ── The VFX graph ────────────────────────────────────────────────────────

    /// <summary>The three columns, and the transport above the preview.</summary>
    [Fact]
    public void The_vfx_chrome_is_the_tree_it_was() {
        using var harness = new ViewHarness();

        var view = Effect(harness, "Dumped.vxvfx");
        var tree = harness.Ui.Tree(view);
        var flags = harness.Ui.Flags(view);

        Assert.Contains("<vfx-side>", tree, StringComparison.Ordinal);
        Assert.Contains("<vfx-transport>", tree, StringComparison.Ordinal);
        Assert.Contains("<vfx-readout>", tree, StringComparison.Ordinal);
        Assert.Contains("<node-inspector>", tree, StringComparison.Ordinal);

        Assert.Contains("Label=\"Compile\"", flags, StringComparison.Ordinal);
        Assert.Contains("Label=\"Restart\"", flags, StringComparison.Ordinal);
        Assert.Contains("Label=\"Play\"", flags, StringComparison.Ordinal);

        // Opening compiles, so the list has the effect's own summary in it rather than nothing.
        Assert.NotEmpty(view.Diagnostics.Children);
    }

    /// <summary>
    ///     ⚠ <b>Play is the third <c>change:</c> leg.</b> It was a <c>CheckedChanged</c> subscription
    ///     in <c>OnCreated</c>; it is a binding now, and what it writes is a property of a control the
    ///     panel does not own. Toggled twice, because a binding that fired once would pass a one-way
    ///     test.
    /// </summary>
    [Fact]
    public void Play_pauses_the_preview_and_starts_it_again() {
        using var harness = new ViewHarness();

        var view = Effect(harness, "Paused.vxvfx");

        Assert.True(view.Play.IsChecked);
        Assert.True(view.Preview.IsPlaying);

        view.Play.IsChecked = false;
        harness.Ui.Frame();

        Assert.False(view.Preview.IsPlaying);

        view.Play.IsChecked = true;
        harness.Ui.Frame();

        Assert.True(view.Preview.IsPlaying);
    }

    // ── The preview pane ─────────────────────────────────────────────────────

    /// <summary>
    ///     The pane beside the editor, whose contents are the only thing here that markup cannot
    ///     describe: the tags come out of a file the author typed a second ago.
    /// </summary>
    [Fact]
    public void The_preview_pane_chrome_is_the_tree_it_was() {
        using var harness = new ViewHarness();

        var path = harness.Project.Write(
            "Assets/Dumped.vxml",
            "@component Dumped\n<panel class=\"card\"><text>Hello</text></panel>\n"
        );

        var view = harness.Ui.Document.Root.Add<PreviewCodeEditorView>();

        view.Show(new MarkupDocument(harness.Project.Project, AssetId.New(), path));
        harness.Ui.Frame();

        var tree = harness.Ui.Tree(view);

        // The base's part first, then this one's pane — which is what two `OnCreated`s produced and
        // what two generated ones have to keep producing.
        Assert.Contains("<code-editor ", tree, StringComparison.Ordinal);
        Assert.Contains("<preview-pane>", tree, StringComparison.Ordinal);
        Assert.True(
            tree.IndexOf("<code-editor ", StringComparison.Ordinal)
            < tree.IndexOf("<preview-pane>", StringComparison.Ordinal),
            "the editor must come before the pane, as it did when both were hand-written"
        );

        Assert.Same(view.Pane, view.Status.Parent);
        Assert.Same(view.Pane, view.Surface.Parent);

        // A panel, its text element, and the text node inside it — the file's own tags, drawn.
        Assert.Equal(3, view.ElementCount);
    }

    // ── The plumbing ─────────────────────────────────────────────────────────

    static (string Tree, string Flags) Dump(ViewHarness harness, Action<UiElement> build) {
        var host = harness.Ui.Document.Root.Add("analysis-list");

        build(host);
        harness.Ui.Frames(2);

        var written = (harness.Ui.Tree(host), harness.Ui.Flags(host));

        host.Remove();
        harness.Ui.Frames(2);

        return written;
    }

    static ShaderGraphView Shader(ViewHarness harness, string name, out ShaderGraphDocument document) {
        document = new(
            harness.Project.Project,
            AssetId.New(),
            harness.Project.Write("Assets/" + name, string.Empty)
        );

        var view = harness.Ui.Document.Root.Add<ShaderGraphView>();

        view.Show(document);
        harness.Ui.Frame();

        return view;
    }

    static VfxGraphView Effect(ViewHarness harness, string name) {
        var document = new VfxDocument(
            harness.Project.Project,
            AssetId.New(),
            harness.Project.Write("Assets/" + name, string.Empty)
        );

        var view = harness.Ui.Document.Root.Add<VfxGraphView>();

        view.Show(document);
        harness.Ui.Frame();

        return view;
    }

    static int Lines(string dump, string needle) =>
        dump.Split('\n').Count(line => line.Trim().StartsWith(needle, StringComparison.Ordinal));
}
