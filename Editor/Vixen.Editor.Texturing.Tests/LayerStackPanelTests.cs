// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Layers;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A <c>.vxlayers</c> opens, and something is on the screen when it does.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/806">#806</a>, from the other end.</b>
///         The issue is that three and a half thousand lines of layer stack were reachable only from
///         xunit — so a test asserting that the registration <em>exists</em> would be one more thing
///         reachable only from xunit. What is asserted here is the route a person takes: select the
///         file, run the verb, and the panel holds the stack's rows.
///     </para>
///     <para>
///         ⚠ <b>Device-free, deliberately, and it is not the whole claim.</b> Nothing here dispatches
///         a kernel, so what it can settle is that the document opens and the rows are drawn.
///         <see cref="LayerStackPanelDeviceTests" /> is the half that needs an adapter and looks at
///         the texels; a suite that only had this one would be green against a preview that never
///         evaluated anything.
///     </para>
/// </remarks>
public class LayerStackPanelTests {
    /// <summary>The verb opens the selected stack and puts its layers in the panel.</summary>
    [Fact]
    public void Opening_a_stack_puts_its_rows_in_the_panel() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        var document = Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());

        // The starter stack is one Fill layer called "Base" — `LayerStackDocument.Starter`. A panel
        // that opened the document and drew nothing would satisfy every assertion above.
        var row = Assert.Single(LayerStackView.Describe(document));

        Assert.Contains("Base", row, StringComparison.Ordinal);
        Assert.Contains("Fill", row, StringComparison.Ordinal);
        Assert.Equal("1024 × 1024", LayerStackView.Resolution(document));
    }

    /// <summary>⚠ And the rows are topmost-first, which is the reverse of the file.</summary>
    /// <remarks>
    ///     <b>The one arithmetic decision a layers panel makes, and getting it wrong is invisible on
    ///     a one-layer stack.</b> <c>TextureSetAsset.Layers</c> is stored in composite order so that
    ///     reading the file is reading the arithmetic; every layers panel shows the top layer at the
    ///     top. A stack of three is the smallest input where the two orders differ and where a
    ///     reversal that dropped the middle row would show.
    /// </remarks>
    [Fact]
    public void The_rows_are_topmost_first() {
        using var fixture = new TexturingFixture();
        var stack = LayerStackDocument.Starter("Hull");

        stack.Sets[0].Layers.Add(new() { Id = "middle", Name = "Middle", Kind = LayerKind.Fill });
        stack.Sets[0].Layers.Add(new() { Id = "top", Name = "Top", Kind = LayerKind.Fill, Enabled = false });

        var document = Open(fixture, "Hull");

        document.Document = stack;

        var rows = LayerStackView.Describe(document);

        Assert.Equal(3, rows.Count);
        Assert.StartsWith("Top", rows[0], StringComparison.Ordinal);
        Assert.StartsWith("Middle", rows[1], StringComparison.Ordinal);
        Assert.StartsWith("Base", rows[2], StringComparison.Ordinal);

        // A disabled layer is listed and marked rather than hidden: a row that vanished when it was
        // switched off would leave nobody a way to switch it back on.
        Assert.Contains("off", rows[0], StringComparison.Ordinal);
        Assert.DoesNotContain("off", rows[1], StringComparison.Ordinal);
    }

    /// <summary>A host with no device says which of the two reasons the pane is empty for.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument, and until
    ///         <a href="https://github.com/Rikarin/Vixen/issues/831">#831</a> it could not fail.</b>
    ///         This test asserted <c>Assert.Contains("IEditorGraphics", TexturePreview.Describe(
    ///         TexturePreviewBlocker.NoGraphics))</c> — a pure function over a switch expression. It
    ///         opened no panel, touched no view and read no status, so deleting
    ///         <c>TexturingModule.RefreshStack</c>'s fallback entirely left it green, which is the one
    ///         thing its own remark says it exists to prevent.
    ///     </para>
    ///     <para>
    ///         <b>What it reads now is the element on the screen.</b> With no graphics there is no
    ///         <c>LayerStackPreview</c> at all, so a module that handed the view a null picture would
    ///         leave the pane blank with an empty line under it — which says nothing about whether
    ///         this host could ever have drawn one. The status line is that line.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_host_with_no_graphics_says_so_under_the_pane() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        // The pane is showing a stack — so an empty line under it is a claim that nothing is wrong,
        // which is exactly what is wrong.
        Assert.NotEmpty(Rows(panel));
        Assert.Contains("IEditorGraphics", Status(panel), StringComparison.Ordinal);
    }

    /// <summary>⚠ And a tab opened by a double-click says which pane holds the picture.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/831">#831</a>'s other half.</b>
    ///         <c>LayerStackEditorFactory.CreateView</c> was <c>view.Show(stack)</c> with no picture,
    ///         so the tab a double-click opens was a chequerboard under a blank line — while the
    ///         factory's own doc comment said it "lists the stack and says so".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it must not say <c>NoGraphics</c>, which is what the sibling graph factory
    ///         passes.</b> A double-click happens in the editor and the editor publishes
    ///         <c>IEditorGraphics</c>, so that sentence is false in the only place it is ever read —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/841">#841</a>. Both halves are
    ///         asserted, because a picture carrying the wrong sentence passes the first alone.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_tab_opened_by_a_double_click_says_which_pane_holds_the_picture() {
        using var fixture = new TexturingFixture(editors: true);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var asset = AddStack(fixture, "Hull");

        Assert.True(
            fixture.Editors.TryGetForFile("Assets/Hull" + LayerStackDocument.Extension, out var editor)
        );

        Assert.True(fixture.Editors.TryOpen(fixture.Project, asset, out var document));

        var host = fixture.Shell.Document.Root.Add<UiElement>();

        editor.CreateView(document, host);

        var status = Status(host);

        Assert.NotEmpty(Rows(host));
        Assert.Contains("Layer Stack", status, StringComparison.Ordinal);
        Assert.DoesNotContain("publishes no IEditorGraphics", status, StringComparison.Ordinal);
    }

    /// <summary>⚠ Choosing a set moves the picture and its diagnostics, not only the rows.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half of the set picker that was missing.</b> The rows, the channel ticks and the
    ///         part picker all followed the chosen set; the preview beside them went on compiling
    ///         <c>Sets[0]</c>. So a panel showing the Head set's rows drew the Body set's map and
    ///         listed the Body set's diagnostics — naming layers that were not in the list in front of
    ///         the reader, with nothing saying why.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The warning is the instrument, because it is the one thing that names a set
    ///         without a device.</b> Only the second set has the bad setting, so the message can only
    ///         appear once the preview has compiled that set — a picture assertion would need a
    ///         device and a row assertion would pass against the defect.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Choosing_a_set_moves_the_previews_diagnostics_too() {
        using var fixture = new TexturingFixture(graphics: true);
        var document = Open(fixture, "Hull");

        LayerStackAsset stack = new() {
            Name = "Hull",
            BaseWidth = 32,
            BaseHeight = 32,
            Seed = 7u,
            Sets = [
                new() {
                    Name = "Body",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [new() { Id = "body", Name = "Body", Kind = LayerKind.Fill }]
                },
                new() {
                    Name = "Head",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],

                    // 'Wobble' is not a port `Filters/Blur` declares, so this set — and only this
                    // set — produces a warning when it is the one compiled.
                    Layers = [
                        new() {
                            Id = "haze",
                            Name = "Haze",
                            Kind = LayerKind.Filter,
                            Filter = LayerFilterKind.Blur,
                            Settings = { ["Wobble"] = [1f] }
                        }
                    ]
                }
            ]
        };

        document.Document = stack;

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);
        Assert.Empty(Messages(panel));

        var picker = Assert.IsType<Select>(Find(panel, "layer-stack-set"));

        picker.Value = "Head";

        panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel)!;

        var message = Assert.Single(Messages(panel));

        Assert.Contains("Wobble", message, StringComparison.Ordinal);
    }

    /// <summary>⚠ A warning the compile produced is on the screen, which is what #830 found missing.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/830">#830</a>.</b> The terminus
    ///         rescale was chosen over a silent one because "it is said" — and nothing said it. No
    ///         production type rendered a diagnostic, and <c>LayerStackPreview.Refused</c> both runs
    ///         only when there is no plan and keeps the errors, so a warning against a stack that
    ///         compiled reached nothing but xunit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The input is a stack that <em>compiles</em>, deliberately.</b> A refusal was
    ///         already on the screen — it is the whole of the status line. What was invisible is the
    ///         thing that did not stop the map, and a test whose stack failed to compile would be
    ///         green against the code this closes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Graphics with no device, which is a state the editor really starts in</b> — it
    ///         builds its plugin host in its constructor and acquires a device when the window can
    ///         present. It is also what makes this assertion device-free: the compile is pure, so
    ///         everything the author needs to be told is known before a device is asked for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_warning_a_compiled_stack_produced_is_listed_in_the_panel() {
        using var fixture = new TexturingFixture(graphics: true);
        var document = Open(fixture, "Hull");
        var stack = LayerStackDocument.Starter("Hull");

        // ⚠ 'Wobble' is not a port `Filters/Blur` declares, so `LayerStackGraph` drops the value and
        // says so — a warning, and the graph still builds, still compiles and still bakes a map.
        stack.Sets[0]
            .Layers.Add(
                new() {
                    Id = "haze",
                    Name = "Haze",
                    Kind = LayerKind.Filter,
                    Filter = LayerFilterKind.Blur,
                    Settings = { ["Wobble"] = [1f] }
                }
            );

        document.Document = stack;

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        var message = Assert.Single(Messages(panel));

        Assert.StartsWith("Warning", message, StringComparison.Ordinal);
        Assert.Contains("Wobble", message, StringComparison.Ordinal);
        Assert.Contains("haze", message, StringComparison.Ordinal);

        // ⚠ The premise, and it is what makes this test about a warning rather than about a refusal.
        // `LayerStackPreview.Evaluate` reports a refused compilation *before* it looks at the device,
        // so a status line naming the device is a statement that the plan was produced — the stack
        // compiled, the map is sound, and the warning is the one thing an author would otherwise
        // never learn. A refusal reaches the status line on its own and always did.
        Assert.Contains("no graphics device", Status(panel), StringComparison.Ordinal);
    }

    /// <summary>⚠ And a stack with nothing wrong shows no list at all.</summary>
    /// <remarks>
    ///     <b>The predicate that could not be false without this.</b> Every assertion above holds of
    ///     a panel that listed something on every refresh; the ordinary stack is the one an artist
    ///     looks at all day, and a heading with nothing under it reads as a checked box.
    /// </remarks>
    [Fact]
    public void A_stack_with_nothing_wrong_lists_nothing() {
        using var fixture = new TexturingFixture(graphics: true);

        Open(fixture, "Hull");

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);
        Assert.NotEmpty(Rows(panel));
        Assert.Empty(Messages(panel));
    }

    /// <summary>Selecting nothing is a notification rather than an exception.</summary>
    [Fact]
    public void The_verb_with_nothing_selected_says_what_to_select() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.Empty(fixture.Project.Documents);
    }

    /// <summary>What the status line under the pane says, read off the tree the panel built.</summary>
    /// <remarks>
    ///     ⚠ <b>Off the tree and not off <c>LayerStackView.Status</c>, because the view is the
    ///     module's private field.</b> A test that constructed its own view would pass in an editor
    ///     where the panel was never registered — <c>TextureGraphPanelTests</c> states the same rule —
    ///     and a test that called a static helper would pass in an editor where the panel drew
    ///     nothing at all, which is what <a href="https://github.com/Rikarin/Vixen/issues/831">#831</a>
    ///     found here.
    /// </remarks>
    static string Status(UiElement panel) => Find(panel, "layer-stack-status")?.Text ?? "";

    /// <summary>The rows the panel drew.</summary>
    /// <remarks>
    ///     ⚠ <b>The name element inside each row, not the row.</b> A row carries controls now —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/819">#819</a> — and an element with text
    ///     may not have children, so the summary moved into a child of its own. Reading the row would
    ///     hand back a list of empty strings, which every <c>NotEmpty</c> here would still pass.
    /// </remarks>
    static IReadOnlyList<string> Rows(UiElement panel) {
        List<string> lines = [];

        Walk(panel);

        return lines;

        void Walk(UiElement element) {
            if (string.Equals(element.Tag, "layer-stack-row-name", StringComparison.Ordinal)) {
                lines.Add(element.Text ?? "");
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    /// <summary>Everything the compile had to say, as the panel drew it.</summary>
    static IReadOnlyList<string> Messages(UiElement panel) => Lines(panel, "layer-stack-messages");

    /// <summary>The text of every child of one of the view's containers.</summary>
    static IReadOnlyList<string> Lines(UiElement panel, string tag) {
        if (Find(panel, tag) is not { } container) {
            return [];
        }

        var lines = new List<string>(container.Children.Count);

        foreach (var child in container.Children) {
            lines.Add(child.Text ?? "");
        }

        return lines;
    }

    /// <summary>The first element in the tree with that tag.</summary>
    static UiElement? Find(UiElement element, string tag) {
        if (string.Equals(element.Tag, tag, StringComparison.Ordinal)) {
            return element;
        }

        foreach (var child in element.Children) {
            if (Find(child, tag) is { } found) {
                return found;
            }
        }

        return null;
    }

    /// <summary>Opens a stack through the verb and hands back the document it made.</summary>
    static LayerStackDocument Open(TexturingFixture fixture, string name) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(AddStack(fixture, name));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        return Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());
    }

    /// <summary>Writes an empty <c>.vxlayers</c> and scans it in.</summary>
    /// <remarks>
    ///     Here rather than on <c>TexturingFixture</c>, whose <c>AddGraph</c> is another slice's
    ///     helper — the two differ only in an extension, and merging them is a change to a shared
    ///     file this slice does not own.
    /// </remarks>
    internal static AssetId AddStack(TexturingFixture fixture, string name) {
        var relative = "Assets/" + name + LayerStackDocument.Extension;

        File.WriteAllText(fixture.Paths.Absolute(relative), LayerStackDocument.NewContents);

        var report = fixture.Project.Assets.Scan();

        // A `MetaCreated` is what a scan is for; anything else is a fixture that has gone wrong.
        Assert.DoesNotContain(report.Issues, issue => issue.Kind != AssetIssueKind.MetaCreated);
        Assert.True(fixture.Project.Assets.TryGetByPath(relative, out var entry), "the scan did not pick the stack up");

        return entry.Guid;
    }
}
