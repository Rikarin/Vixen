// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Every pane in this plugin that shows a picture offers the two pickers over it.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1012">#1012</a>, from the panel end.</b>
///         <c>ImageView.Channels</c> and <c>ImageView.ColorSpace</c> worked, the shader read them,
///         a golden suite checked the texels, <c>ImageViewBar</c> was built to drive them and had
///         tests — and a tree-wide sweep of <c>.cs</c> and <c>.vxml</c> found the two enums named
///         nowhere outside the control and its own suite. Three panes built the viewer and none
///         built the strip: the picker was correct, tested, and unreachable.
///     </para>
///     <para>
///         ⚠ <b>A roll call over the three, and that shape is the point.</b> The defect this replaces
///         is not "the strip does not work" — <c>ImageViewBarTests</c> settles that against the
///         <em>draw command</em> — it is "one panel out of three was wired". A test per panel would
///         have been three tests somebody adds a fourth panel without; this one names the three and
///         is what a fourth has to join.
///     </para>
///     <para>
///         ⚠ <b>The segment is pressed through the shell's own pointer, at the coordinates a layout
///         pass gave it.</b> Setting <c>SegmentedControl.Value</c> would pass against a strip built
///         with no box, off the bottom of a fixed-width column, or hidden behind the picture — all
///         of which are "the panel does not offer it" wearing a green test. A dispatch that misses
///         reaches the root and changes nothing.
///     </para>
/// </remarks>
public class ImageViewBarWiringTests {
    /// <summary>The texture graph's result pane carries the strip, and it drives that pane.</summary>
    [Fact]
    public void The_texture_graph_preview_offers_the_pickers() {
        using var fixture = new TexturingFixture();
        var view = new TextureGraphView(fixture.Shell.Document.Root.Add<UiElement>());

        var document = new TextureGraphDocument(
            fixture.Project,
            fixture.AddGraph("Bricks"),
            fixture.Paths.Absolute("Assets/Bricks" + TextureGraphDocument.Extension)
        );

        view.Show(document, TexturePreviewBlocker.NoDevice);
        fixture.Shell.Document.Update();

        Assert.Same(view.Preview, view.Channels.View);

        Press(fixture, SegmentNamed(view.Channels, "image-view-channels", nameof(ImageChannels.Green)));

        Assert.Equal(ImageChannels.Green, view.Preview.Channels);
    }

    /// <summary>So does the layer stack's, which is the pane with four maps to tell apart.</summary>
    [Fact]
    public void The_layer_stack_preview_offers_the_pickers() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);
        fixture.Shell.Document.Update();

        var bar = Only<ImageViewBar>(panel);

        Assert.Same(Only<ImageView>(panel), bar.View);

        Press(fixture, SegmentNamed(bar, "image-view-space", nameof(ImageColorSpace.Linear)));

        Assert.Equal(ImageColorSpace.Linear, bar.View!.ColorSpace);
    }

    /// <summary>And the paint pane, where the isolate is what says how much of a stroke landed.</summary>
    /// <remarks>
    ///     ⚠ <b>The one that reads the <em>draw command</em>, because this pane is the one whose
    ///     picture is real without a device.</b> The atlas is uploaded through
    ///     <c>IEditorGraphics</c>, so <c>ImageView.Image</c> is a number the pane was given rather
    ///     than a number a fixture invented — which is the difference between proving the panel's
    ///     strip reaches the picture and proving it reaches a field. The assertion goes red if
    ///     anybody stops passing <c>view:</c> at the <c>DrawImage</c> call, which is exactly what
    ///     #1012 asked the instrument to survive.
    /// </remarks>
    [Fact]
    public void The_paint_pane_offers_the_pickers_and_the_choice_reaches_the_picture() {
        using var fixture = new TexturingFixture(graphics: true);

        Paintable(fixture, "Hull");

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.PaintCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.PaintPanel);

        Assert.NotNull(panel);
        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();

        var image = Only<ImageView>(panel);
        var bar = Only<ImageViewBar>(panel);

        Assert.Same(image, bar.View);

        // The instrument: the pane really is drawing a picture, so the command read below is the
        // atlas rather than some other pane's.
        Assert.NotEqual(0UL, image.Image);
        Assert.True(Drawn(fixture, image.Image).IsIdentity);

        Press(fixture, SegmentNamed(bar, "image-view-channels", nameof(ImageChannels.Alpha)));
        Press(fixture, SegmentNamed(bar, "image-view-space", nameof(ImageColorSpace.Linear)));

        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();

        Assert.Equal(new UiImageView(UiImageChannel.Alpha, true), Drawn(fixture, image.Image));
    }

    /// <summary>The image command for one image number, read out of the frame just drawn.</summary>
    static UiImageView Drawn(TexturingFixture fixture, ulong image) =>
        fixture.Shell.Document.Drawing.Commands
            .Single(command => command.Kind == DrawCommandKind.Image && command.Image == image)
            .View;

    /// <summary>Presses the middle of an element through the document, as a person would.</summary>
    static void Press(TexturingFixture fixture, UiElement element) {
        var bounds = element.Bounds;

        Assert.True(
            bounds is { Width: > 0f, Height: > 0f },
            $"'{element.Tag}' has no box, so a pointer at its middle lands on whatever is behind it."
        );

        var x = bounds.X + (bounds.Width * 0.5f);
        var y = bounds.Y + (bounds.Height * 0.5f);

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = x, Y = y, Action = PointerAction.Pressed, Button = PointerButton.Primary }
        );

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = x, Y = y, Action = PointerAction.Released, Button = PointerButton.Primary }
        );

        fixture.Shell.Document.Update();
    }

    static SegmentedControl Strip(ImageViewBar bar, string className) =>
        bar.Children.OfType<SegmentedControl>().Single(strip => strip.HasClass(className));

    static Segment SegmentNamed(ImageViewBar bar, string className, string value) =>
        Strip(bar, className).Segments.Single(segment => string.Equals(segment.Value, value, StringComparison.Ordinal));

    /// <summary>The one control of a kind under a panel, which is the roll call's own assertion.</summary>
    static T Only<T>(UiElement root) where T : UiElement {
        List<T> found = [];

        Walk(root);

        return Assert.Single(found);

        void Walk(UiElement element) {
            if (element is T match) {
                found.Add(match);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    /// <summary>A stack with a paint layer in it, open in the project.</summary>
    static void Paintable(TexturingFixture fixture, string name) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, name));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());

        // 64 square rather than the starter's 1024, for `PaintUvViewTests`' reason: a paint canvas
        // is four bytes a texel and every upload copies one.
        var stack = LayerStackDocument.Starter(name) with { BaseWidth = 64, BaseHeight = 64 };

        stack.Sets[0].Layers.Add(new() { Id = "rust", Name = "Rust", Kind = LayerKind.Paint });
        document.Document = stack;
    }
}
