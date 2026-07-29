// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The picture control, and the arithmetic under it.</summary>
public sealed class SurfaceViewTests {
    [Fact]
    public void StretchIsTheBox() {
        var box = new Rectangle(0, 0, 200, 100);

        Assert.Equal(box, SurfaceView.Place(SurfaceFit.Stretch, new Vector2(16, 9), box));
    }

    [Fact]
    public void ContainFitsInsideAndCentres() {
        // 16:9 in a square: the width runs out first, so the picture is 200 × 112.5 with bars above
        // and below — which are not drawn on, they are simply not covered.
        var placed = SurfaceView.Place(SurfaceFit.Contain, new Vector2(16, 9), new Rectangle(0, 0, 200, 200));

        Assert.Equal(200f, placed.Width, 3);
        Assert.Equal(112.5f, placed.Height, 3);
        Assert.Equal(43.75f, placed.Y, 3);
    }

    [Fact]
    public void CoverFillsAndOverflows() {
        // The same picture in the same square, the other way round: it is as tall as the box and
        // wider, and the element clips what hangs over.
        var placed = SurfaceView.Place(SurfaceFit.Cover, new Vector2(16, 9), new Rectangle(0, 0, 200, 200));

        Assert.Equal(200f, placed.Height, 3);
        Assert.True(placed.Width > 200f);
        Assert.True(placed.X < 0f);
    }

    [Fact]
    public void ADegenerateSourceIsTheBox() {
        // The ordinary case for the first frame of a video: something asks where the picture goes
        // before there is one.
        var box = new Rectangle(0, 0, 100, 50);

        Assert.Equal(box, SurfaceView.Place(SurfaceFit.Contain, Vector2.Zero, box));
    }

    [Fact]
    public void AnElementWithNoSourceDrawsNothing() {
        using var fixture = new ControlFixture(css: "surface { width: 200px; height: 200px; }");

        fixture.Add<SurfaceView>();
        fixture.Update();

        Assert.DoesNotContain(fixture.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.Surface);
    }

    [Fact]
    public void AnElementWithASourceEmitsOneSurfaceCommand() {
        using var fixture = new ControlFixture(css: "surface { width: 200px; height: 200px; }");

        var source = new object();
        var view = fixture.Add<SurfaceView>();

        view.Source = source;
        view.SourceSize = new Vector2(16, 9);
        view.Fit = SurfaceFit.Contain;
        fixture.Update();

        var list = fixture.Document.Drawing;
        var command = Assert.Single(list.Commands, entry => entry.Kind == DrawCommandKind.Surface);

        Assert.Same(source, list.Surfaces[command.Surface]);

        // Contained in a square box, so the rectangle is 200 × 112.5 and no clip was needed.
        Assert.Equal(112.5f, command.Height, 2);
        Assert.DoesNotContain(list.Commands, entry => entry.Kind == DrawCommandKind.ClipPush);
    }

    [Fact]
    public void CoverPushesAClipRatherThanPaintingBars() {
        using var fixture = new ControlFixture(css: "surface { width: 200px; height: 200px; }");

        var view = fixture.Add<SurfaceView>();

        view.Source = new object();
        view.SourceSize = new Vector2(16, 9);
        view.Fit = SurfaceFit.Cover;
        fixture.Update();

        var list = fixture.Document.Drawing;

        // ⚠ A clip, and never a pair of black rectangles. Bars painted by the control would be opaque
        // black over whatever the video was laid over, which is wrong the first time somebody puts one
        // behind a menu.
        Assert.Contains(list.Commands, entry => entry.Kind == DrawCommandKind.ClipPush);
        Assert.Contains(list.Commands, entry => entry.Kind == DrawCommandKind.ClipPop);
    }
}
