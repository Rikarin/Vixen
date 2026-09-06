// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>
///     The shell is never larger than the window it was given, however tall or wide the thing in a
///     panel is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the property four committed screenshots were standing in for, and the
///         layout store was the wrong place to assert it.</b> The min-content probe used to open by
///         returning a clipping box's padding and border and nothing else, on the reading that CSS
///         Sizing §5.2.2 excludes scrollable overflow from every intrinsic size. It does not — it
///         excludes it from §4.5's automatic minimum — and Chrome says so: a <c>width: min-content</c>
///         box around an <c>overflow: scroll</c> container holding a 500-point box is 500 wide, not
///         zero. Deleting that clause was right and cost 24 grid conformance fixtures to keep, and it
///         put the editor's inspector off the right of the window, because the clause had been
///         standing in for two declarations the editor's own stylesheet was missing.
///     </para>
///     <para>
///         ⚠ <b>§4.5's opt-out reached every pane and not the frame, and that asymmetry was in
///         <c>EditorTheme.vcss</c> rather than in the layout store.</b> <c>docking-host</c>,
///         <c>dock-surface</c>, <c>dock-split</c>, <c>dock-group</c> and <c>dock-body</c> have each
///         carried <c>min-width: 0px</c> and <c>min-height: 0px</c> for as long as they have existed,
///         and <c>AdvancedTheme.vcss</c> spends two paragraphs on why. <c>editor-shell</c> and
///         <c>editor-workspace</c> — the two boxes between them and the root, and the two written in
///         a different file — had neither. So a flex item whose minimum is its content sat above a
///         chain that had all agreed to clip, and the chain clipped nothing: measured here at
///         1 049 × 40 138 inside a 900 × 700 window, which is the shell 149 points wide of its
///         frame and a docking area fifty-seven windows tall.
///     </para>
///     <para>
///         The oracle is the window and the content together. A store that had simply lost the
///         40 000-point content would satisfy an assertion about the shell alone, so the scrolled
///         box is asserted at its full height in the same breath — the point of a scroll container
///         is that both are true at once.
///     </para>
/// </remarks>
public class ShellFitsItsWindowTests {
    const float Tolerance = 0.5f;

    static StringId Title(string text) => new("test." + text, text);

    /// <summary>How tall the scrolled content is, which a virtualising grid of 2 000 tiles reaches.</summary>
    const float ContentHeight = 40000f;

    /// <summary>And how wide, which is wider than the panel it is in.</summary>
    const float ContentWidth = 800f;

    [Theory]
    [InlineData(900f, 700f)]
    [InlineData(1100f, 680f)]
    public void A_panel_scrolling_forty_thousand_points_does_not_widen_the_shell(float width, float height) {
        using var shell = new EditorShell(width, height);

        shell.RegisterPanel("hierarchy", Title("Hierarchy"), Scrolled);
        shell.RegisterPanel("scene", Title("Scene"), panel => panel.Add<TextBlock>().Text = "viewport");
        shell.RegisterPanel("inspector", Title("Inspector"), Scrolled);

        shell.RegisterLayout(
            "Default",
            Title("Default"),
            () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"])
        );

        shell.Workspace.Reset();
        shell.Document.Update();

        var chrome = Only(shell.Document.Root, "editor-shell");
        var workspace = Only(chrome, "editor-workspace");
        var host = Only(workspace, "docking-host");

        Assert.Equal(width, chrome.Width, Tolerance);
        Assert.Equal(height, chrome.Height, Tolerance);
        Assert.Equal(width, workspace.Width, Tolerance);
        Assert.True(workspace.Height <= height + Tolerance, $"the workspace is {workspace.Height} tall in a {height}-point window");
        Assert.True(host.Width <= width + Tolerance, $"the docking host is {host.Width} wide in a {width}-point window");
        Assert.True(host.Height <= height + Tolerance, $"the docking host is {host.Height} tall in a {height}-point window");

        // ⚠ And every other box too, by walking rather than by naming — the three above are the ones
        // that were wrong, and a chain has no reason to break at the same link twice. The walk stops
        // descending at the first box that is too big, because a scroll container's content is
        // *meant* to be larger than its container and a walk that kept going would report that as
        // the defect.
        var over = new StringBuilder();
        Walk(shell.Document.Root, width, height, string.Empty, over);
        Assert.True(over.Length == 0, $"boxes larger than the {width} × {height} window:\n{over}");

        // And the content really is still that big — a chain that had lost it would pass everything
        // above.
        foreach (var content in scrolled) {
            Assert.Equal(ContentHeight, content.Height, Tolerance);
            Assert.Equal(ContentWidth, content.Width, Tolerance);
        }
    }

    static void Walk(UiElement element, float width, float height, string path, StringBuilder over) {
        var here = path + "/" + element.Tag;

        // ⚠ What a scroll view holds is the one thing in the document that is *supposed* to be
        // larger than the window — that is what scrolling is. Everything above it is not.
        if (element.Tag == "scroll-content") {
            return;
        }

        if (element.Width > width + Tolerance || element.Height > height + Tolerance) {
            over.Append("  ")
                .Append(here)
                .Append(" is ")
                .Append(element.Width.ToString("0.#"))
                .Append(" × ")
                .Append(element.Height.ToString("0.#"))
                .AppendLine();

            return;
        }

        foreach (var child in element.Children) {
            Walk(child, width, height, here, over);
        }
    }

    readonly List<UiElement> scrolled = [];

    /// <summary>A panel whose body is a scroll view far larger than any window.</summary>
    void Scrolled(DockPanel panel) {
        // ⚠ A plain box between the panel and the scroller, deliberately: it is the shape the asset
        // browser has, and a box with `overflow: visible` is the one link in the chain that carries
        // its content's minimum upwards. The defect is not visible without it.
        var body = panel.Add<UiElement>("scrolled-body");

        // ⚠ The three declarations every real panel body in this tree carries — the samples' shell
        // sheet spends a paragraph on why. Without them this box would itself be 40 000 tall, which
        // would make the fixture a test of the fixture.
        body.SetStyle("flex-grow", "1");
        body.SetStyle("flex-basis", "0px");
        body.SetStyle("min-width", "0px");
        body.SetStyle("min-height", "0px");

        var scroller = body.Add<ScrollView>();

        scroller.Content.SetStyle("height", "40000px");
        scroller.Content.SetStyle("width", "800px");

        // ⚠ Or the scroller's own width shrinks it back to the panel's, and the test would be
        // asserting that a box narrower than its window is narrower than its window.
        scroller.Content.SetStyle("flex-shrink", "0");

        scrolled.Add(scroller.Content);
    }

    static UiElement Only(UiElement parent, string tag) {
        foreach (var child in parent.Children) {
            if (child.Tag == tag) {
                return child;
            }
        }

        throw new InvalidOperationException($"no <{tag}> under <{parent.Tag}>");
    }

}
