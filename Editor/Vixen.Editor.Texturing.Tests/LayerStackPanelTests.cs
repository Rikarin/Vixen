// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Layers;
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
    ///     ⚠ <b>The instrument, and it is the assertion this file would otherwise be missing.</b>
    ///     With no graphics there is no <c>LayerStackPreview</c> at all — so a module that simply
    ///     handed the view a null picture would leave the pane blank with an empty line under it,
    ///     which says nothing about whether this host could ever have drawn one.
    /// </remarks>
    [Fact]
    public void A_host_with_no_graphics_says_so_under_the_pane() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        Assert.Contains(
            "IEditorGraphics",
            TexturePreview.Describe(TexturePreviewBlocker.NoGraphics),
            StringComparison.Ordinal
        );
    }

    /// <summary>Selecting nothing is a notification rather than an exception.</summary>
    [Fact]
    public void The_verb_with_nothing_selected_says_what_to_select() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.Empty(fixture.Project.Documents);
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
