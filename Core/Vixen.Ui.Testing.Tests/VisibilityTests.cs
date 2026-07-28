// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>
///     <c>visibility</c> and <c>opacity</c>, asserted and drawn — checked together on purpose.
/// </summary>
/// <remarks>
///     ⚠ <b>Every test here makes the claim twice: once through <c>ShouldBeVisible</c> and once by
///     reading the picture.</b> That is the whole point. An assertion that read a property the
///     renderer ignored would fail on an element that is plainly on the screen, and a renderer that
///     honoured one the assertion did not would draw something no test could describe. Asserting
///     both in one test is what stops the two drifting apart.
/// </remarks>
public class VisibilityTests {
    const int Side = 32;

    static UiTest Opened(string css) {
        var ui = UiTest.Create(
            Side,
            Side,
            new UiTestOptions { Background = new Color4(0f, 0f, 0f, 1f), RetryFrames = 2 }
        );

        ui.Load($"root {{ width: {Side}px; height: {Side}px; }} {css}");
        return ui;
    }

    static int Ink(UiTest ui) {
        var image = ui.Capture();
        var total = 0;

        for (var i = 0; i < image.Width * image.Height; i++) {
            total += image.Pixels[i * 4];
        }

        return total;
    }

    [Fact]
    public void Visibility_hidden_is_neither_visible_nor_drawn() {
        using var ui = Opened(".box { width: 16px; height: 16px; background-color: #ffffff; }");
        var box = ui.Create("div", ui.Document.Root, "box", "box");
        ui.Frame();

        ui.Get("#box").ShouldBeVisible();
        Assert.True(Ink(ui) > 0);

        box.SetStyle("visibility", "hidden");
        ui.Frame();

        ui.Get("#box").ShouldNotBeVisible();
        Assert.Equal(0, Ink(ui));

        // ⚠ And it still takes up its space, which is what separates it from `display: none`.
        Assert.Equal(16f, ui.Get("#box").Element.Width, 0.001f);
    }

    [Fact]
    public void Visibility_is_inherited_and_a_child_can_come_back() {
        using var ui = Opened("""
            .panel { width: 32px; height: 32px; visibility: hidden; }
            .child { width: 8px; height: 8px; background-color: #ffffff; }
            .shown { visibility: visible; }
        """);

        var panel = ui.Create("div", ui.Document.Root, "panel", "panel");
        ui.Create("div", panel, "buried", "child");
        ui.Frame();

        ui.Get("#buried").ShouldNotBeVisible();
        Assert.Equal(0, Ink(ui));

        ui.Get("#buried").Element.AddClass("shown");
        ui.Frame();

        // ⚠ The whole reason CSS has two properties for this: `visibility` hides an element and not
        // its subtree, so a child that declares `visible` reappears inside a hidden parent.
        ui.Get("#buried").ShouldBeVisible();
        Assert.True(Ink(ui) > 0);
    }

    [Fact]
    public void Opacity_zero_is_neither_visible_nor_drawn() {
        using var ui = Opened(".box { width: 16px; height: 16px; background-color: #ffffff; }");
        var box = ui.Create("div", ui.Document.Root, "box", "box");
        ui.Frame();

        box.SetStyle("opacity", "0");
        ui.Frame();

        ui.Get("#box").ShouldNotBeVisible();
        Assert.Equal(0, Ink(ui));
    }

    [Fact]
    public void An_opaque_ancestor_of_zero_takes_its_subtree_with_it() {
        using var ui = Opened("""
            .panel { width: 32px; height: 32px; opacity: 0; }
            .child { width: 8px; height: 8px; background-color: #ffffff; }
        """);

        var panel = ui.Create("div", ui.Document.Root, "panel", "panel");
        ui.Create("div", panel, "buried", "child");
        ui.Frame();

        ui.Get("#buried").ShouldNotBeVisible();
        Assert.Equal(0, Ink(ui));

        // ⚠ Opacity multiplies rather than being inherited, so nothing below can bring it back — and
        // the failure says which ancestor, which is what turns this from a puzzle into a fix.
        var failure = Assert.Throws<UiTestException>(() => ui.Get("#buried").ShouldBeVisible());
        Assert.Contains("#panel", failure.Message, StringComparison.Ordinal);
        Assert.Contains("opacity 0", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partial_opacity_fades_rather_than_hiding() {
        using var ui = Opened(".box { width: 16px; height: 16px; background-color: #ffffff; }");
        var box = ui.Create("div", ui.Document.Root, "box", "box");
        ui.Frame();

        var opaque = Ink(ui);

        box.SetStyle("opacity", "0.5");
        ui.Frame();

        var faded = Ink(ui);

        // Still visible, and drawn dimmer — over a black background, half the light.
        ui.Get("#box").ShouldBeVisible();
        Assert.InRange(faded, opaque / 4, opaque * 3 / 4);
    }

    [Fact]
    public void Opacity_multiplies_down_the_tree() {
        using var ui = Opened("""
            .panel { width: 32px; height: 32px; opacity: 0.5; }
            .child { width: 16px; height: 16px; background-color: #ffffff; opacity: 0.5; }
        """);

        var panel = ui.Create("div", ui.Document.Root, "panel", "panel");
        ui.Create("div", panel, "child", "child");
        ui.Frame();

        var command = ui.Document.Drawing.Commands.Single(c => c.Kind == DrawCommandKind.Rectangle);

        // A quarter, not a half and not two separate halves applied twice to different things.
        Assert.Equal(0.25f, command.Color.A, 0.001f);
    }

    [Fact]
    public void The_computed_style_assertions_read_what_the_cascade_resolved() {
        using var ui = Opened("""
            .box {
                width: 16px;
                height: 16px;
                background-color: #3b82f6;
                border-radius: 4px;
                opacity: 0.5;
                flex-direction: column;
            }
        """);

        ui.Create("div", ui.Document.Root, "box", "box");
        ui.Frame();

        var box = ui.Get("#box");

        // A keyword compares as text; a colour and a length are parsed, because ExCSS normalises
        // both on the way in and a test should be able to write what it means.
        box.ShouldHaveStyle("flex-direction", "column");
        box.ShouldHaveStyle("opacity", "0.5");
        box.ShouldHaveColor("background-color", ui.ColorOf(box.Element, "background-color")!.Value);

        // ⚠ The longhand. ExCSS expands `border-radius` on parse exactly as a browser does, so the
        // cascade never holds the shorthand and a test asking for it is told the property is absent.
        box.ShouldHaveLength("border-top-left-radius", 4f);

        var failure = Assert.Throws<UiTestException>(() => box.ShouldHaveStyle("border-radius", "4px"));
        Assert.Contains("has no border-radius", failure.Message, StringComparison.Ordinal);
    }
}
