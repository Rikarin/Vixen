// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     What an <see cref="Expander" /> written in markup can say, now that it publishes a header.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gap this closes was the last thing keeping an editor panel out of
///         <c>.vxml</c>.</b> <c>UiElement.ContentHost</c> is one property, so nested tags could fill
///         a foldout's body and had no spelling at all for its header — which is where an inspector
///         puts a component's icon, its remove button and the grab handle a drag reads. The panel
///         ledger declined <c>ComponentsView</c> three times on exactly that.
///     </para>
///     <para>
///         ⚠ <b>Both halves are asserted, not just the header.</b> A <c>NamedHost</c> that answered
///         every name — or a <c>ContentHost</c> a slot attribute quietly overrode — would put the
///         body's rows in the header too, and a test that only counted the header's children would
///         pass.
///     </para>
/// </remarks>
public class DisclosureMarkupTests {
    /// <summary>The sections the fixture builds, so the dumps have something to be about.</summary>
    static readonly FoldoutSheet.Section[] Sections = [new("Transform", "0, 0, 0"), new("Light", "6500 K")];

    /// <summary>
    ///     A child with <c>slot="header"</c> lands in the header and its siblings land in the body.
    /// </summary>
    [Fact]
    public void A_header_slot_takes_the_children_that_ask_for_it() {
        using var ui = Sheet(out var sheet);

        var folds = Folds(sheet);
        Assert.Equal(2, folds.Length);

        foreach (var fold in folds) {
            // The header's own parts come first — the chevron the control moved to the front and the
            // label — and the markup's two children are appended after them. Appending is the whole
            // contract: anything that has to sit in front of the label says so with `order`.
            Assert.Equal(
                ["icon", "label", "icon", "icon-button"],
                fold.Header.Children.Select(child => child.Tag)
            );

            Assert.True(fold.Header.Children[2].HasClass("section-icon"));
            Assert.True(fold.Header.Children[3].HasClass("section-remove"));

            // And nothing leaked into the body, which is the half a header-only assertion misses.
            Assert.Equal(["section-body"], fold.Content.Children.Select(child => child.Tag));
        }

        Assert.Equal("Transform", folds[0].Label);
        Assert.Equal("Light", folds[1].Label);
        Assert.Contains("0, 0, 0", ui.Tree(folds[0].Content), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The header stays the header: it is still the strip that toggles, and the content is
    ///     still what collapsing hides.</b> A slot that had been implemented by reparenting after the
    ///     fact, or by pointing <c>ContentHost</c> somewhere else while the markup ran, would leave a
    ///     foldout that draws correctly and cannot be shut.
    /// </summary>
    [Fact]
    public void A_slotted_header_still_opens_and_closes_the_foldout() {
        using var ui = Sheet(out var sheet);
        var fold = Folds(sheet)[0];

        Assert.True(fold.IsExpanded);

        // Through the pointer rather than the property, because the claim is about the header being
        // the thing that takes the click — and the icon and the button now sit inside it.
        ui.Get("expander-header").First().Click();
        ui.Frame();

        Assert.False(fold.IsExpanded);
        Assert.False(fold.HasClass("open"));

        ui.Get("expander-header").First().Click();
        ui.Frame();

        Assert.True(fold.IsExpanded);
    }

    /// <summary>
    ///     ⚠ <b>A button written into the header is still a button, and pressing it does not toggle
    ///     the foldout.</b> <c>Expander.Chosen</c> acts only when the click's source is the header
    ///     itself; a remove button inside it raises a click that bubbles straight through, and a
    ///     foldout that shut itself every time somebody pressed one would be the same defect the
    ///     control's own remark records for its content.
    /// </summary>
    [Fact]
    public void A_button_in_the_header_does_not_toggle_the_foldout() {
        using var ui = Sheet(out var sheet);
        var fold = Folds(sheet)[0];

        Assert.True(fold.IsExpanded);

        ui.Get("icon-button").First().Click();
        ui.Frame();

        Assert.True(fold.IsExpanded);
    }

    /// <summary>
    ///     ⚠ <b>What <c>Tree</c> cannot see, carried beside it.</b> A dump is blind to
    ///     <c>Label</c>, <c>State</c> and <c>IsExpanded</c>, so a port that lost a header's text or
    ///     left every section collapsed is byte-identical in every state anybody dumped — which is
    ///     the failure doc 36 § F7 wave 8 added <see cref="UiTest.Flags" /> for.
    ///     <para>
    ///         ⚠ <b>The expander's own <c>State=Open</c> is new and the header's <c>State=Checked</c>
    ///         beside it is not, which is the pair worth reading.</b> <c>:open</c> is CSS's name for
    ///         the disclosure being expanded and it belongs to the disclosure; the header's
    ///         <c>Checked</c> is what turns the chevron and is a statement about a control's value.
    ///         A change that collapsed the two into one bit shows up here as a line losing half its
    ///         flags.
    ///     </para>
    /// </summary>
    [Fact]
    public void The_flags_dump_carries_what_the_tree_dump_cannot() {
        using var ui = Sheet(out var sheet);

        Assert.Equal(
            """
            <expander .open .size-md .variant-default> State=Open IsExpanded=True Label="Transform"
            <expander-header .size-md .variant-default> State=Checked Label="Transform"
            <icon-button .section-remove .size-md .variant-default> Label="Remove"
            <expander .open .size-md .variant-default> State=Open IsExpanded=True Label="Light"
            <expander-header .size-md .variant-default> State=Checked Label="Light"
            <icon-button .section-remove .size-md .variant-default> Label="Remove"
            """,
            ui.Flags(sheet.Root)
        );
    }

    /// <summary>
    ///     ⚠ <b>The whole tree, written down.</b> A port is only proved by a dump somebody can read
    ///     and diff — the panel ledger carries rows claiming "byte-identical in N states" with no
    ///     committed dump behind them at all — so this is the state in full, including where the
    ///     slotted children landed among the header's own parts.
    /// </summary>
    [Fact]
    public void The_tree_dump_is_what_it_says() {
        using var ui = Sheet(out var sheet);

        Assert.Equal(
            """
            <foldoutsheet> 0,0 289×400
              <expander-host> 0,0 289×400
                <expander .open .size-md .variant-default> 0,0 167×400
                  <expander-header .size-md .variant-default> 0,0 167×44
                    <icon .size-md .variant-default> 4,16 12×12
                    <label> 24,8 87×28 "Transform"
                    <icon .section-icon .size-md .variant-default> 119,16 12×12
                    <icon-button .section-remove .size-md .variant-default> 139,8 24×24
                      <icon .size-md .variant-default> 6,6 12×12
                      <label> 0,0 0×0 "Remove"
                  <expander-content> 0,44 167×44
                    <section-body> 20,4 143×28
                      <text> 0,0 51×28 "0, 0, 0"
                <expander .open .size-md .variant-default> 167,0 122×400
                  <expander-header .size-md .variant-default> 0,0 122×44
                    <icon .size-md .variant-default> 4,16 12×12
                    <label> 24,8 42×28 "Light"
                    <icon .section-icon .size-md .variant-default> 74,16 12×12
                    <icon-button .section-remove .size-md .variant-default> 94,8 24×24
                      <icon .size-md .variant-default> 6,6 12×12
                      <label> 0,0 0×0 "Remove"
                  <expander-content> 0,44 122×44
                    <section-body> 20,4 98×28
                      <text> 0,0 60×28 "6500 K"
            """,
            ui.Tree(sheet.Root)
        );
    }

    /// <summary>
    ///     ⚠ <b>And the collapsed state, because that is the one a header slot could plausibly
    ///     break.</b> Collapsing is <c>expander-content { display: none }</c>, so a slotted child
    ///     that had landed beside the part rather than inside the header would stay on screen with
    ///     the chevron flipping over it — the exact failure <c>Expander.ContentHost</c>'s own remark
    ///     records for the body.
    /// </summary>
    [Fact]
    public void The_collapsed_dump_keeps_the_header_and_loses_the_body() {
        using var ui = Sheet(out var sheet);

        ui.Get("expander-header").First().Click();
        ui.Frame();

        Assert.Equal(
            """
            <expander-header .size-md .variant-default> 0,0 167×44
              <icon .size-md .variant-default> 4,16 12×12
              <label> 24,8 87×28 "Transform"
              <icon .section-icon .size-md .variant-default> 119,16 12×12
              <icon-button .section-remove .size-md .variant-default> 139,8 24×24
                <icon .size-md .variant-default> 6,6 12×12
                <label> 0,0 0×0 "Remove"
            """,
            ui.Tree(Folds(sheet)[0].Header)
        );

        // The body is still there and measures nothing, which is what `display: none` means and is
        // the state an open-and-shut has to be able to come back from.
        var content = Folds(sheet)[0].Content;

        Assert.Equal(0f, content.Bounds.Width);
        Assert.Equal(0f, content.Bounds.Height);
    }

    /// <summary>
    ///     ⚠ <b><c>order</c> is what puts a slotted child in front of the label, and it is measured
    ///     rather than declared.</b> The header slot appends, so an icon that belongs between the
    ///     chevron and the name is a stylesheet's business — and this is the assertion that makes
    ///     <c>ComponentsView</c>'s <c>Document.Move(glyph, 1)</c> deletable rather than merely
    ///     replaced. The layout sorts by order-modified document order, so the icon's rectangle has
    ///     to start left of the label's.
    /// </summary>
    [Fact]
    public void Order_puts_a_slotted_icon_in_front_of_the_label() {
        using var ui = Sheet(
            out var sheet,
            """
            expander-header { flex-direction: row; align-items: center; }
            expander-header label { order: 1; }
            .section-icon { width: 16px; height: 16px; }
            .section-remove { order: 2; }
            """
        );

        var header = Folds(sheet)[0].Header;
        var icon = header.Children[2];
        var label = header.Children[1];
        var chevron = header.Children[0];

        // Document order is chevron, label, icon, button; painted order is chevron, icon, label,
        // button. The rectangles are what say which one happened.
        Assert.True(
            chevron.AbsoluteLeft < icon.AbsoluteLeft,
            $"the chevron is at {chevron.AbsoluteLeft} and the icon at {icon.AbsoluteLeft}"
        );

        Assert.True(
            icon.AbsoluteLeft < label.AbsoluteLeft,
            $"the icon is at {icon.AbsoluteLeft} and the label at {label.AbsoluteLeft}"
        );
    }

    static Expander[] Folds(FoldoutSheet sheet) =>
        [.. sheet.Root.Children[0].Children.OfType<Expander>()];

    static UiTest Sheet(out FoldoutSheet sheet, string? css = null) {
        var ui = ControlHarness.Open(500f, 400f, css);

        sheet = new() { Rows = Sections };

        BuildContext.BuildInto(sheet, ui.Document, ui.Document.Root);
        ui.Frame();

        return ui;
    }
}
