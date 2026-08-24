// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Platform.Headless;
using Vixen.Ui;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>What the pointer looks like, asked of the window rather than of the document.</summary>
/// <remarks>
///     ⚠ <b>Every assertion here is on <see cref="IWindow.CursorShape" />, and that is the point.</b>
///     <c>UiDocument.Cursor</c> and <c>CursorOf</c> answered correctly for as long as they have
///     existed and nothing read either — so a test that asked the document would have passed
///     unchanged on the day before this wire was built, and doc 43's consumption probe scored
///     <c>cursor-*</c> as <i>works</i> for exactly that reason. The only honest witness is the
///     window.
/// </remarks>
public class PlatformCursorTests {
    static (HeadlessPlatform Platform, UiDocument Document, PlatformWindowHost Host) Open(string css) {
        var platform = new HeadlessPlatform();
        var main = platform.CreateWindow(new WindowOptions { Size = new Int2(400, 300) });
        var document = new UiDocument(400f, 300f);

        document.Load(css);

        return (platform, document, new PlatformWindowHost(platform, document, main));
    }

    static PointerEvent At(float x, float y) => new() { X = x, Y = y, Action = PointerAction.Moved };

    [Fact]
    public void The_window_is_told_what_the_pointer_is_over() {
        var (platform, document, host) = Open(
            """
            root { width: 400px; height: 300px; flex-direction: row; }
            .grip { width: 50px; height: 50px; cursor: col-resize; }
            .link { width: 50px; height: 50px; cursor: pointer; }
            .plain { width: 50px; height: 50px; }
            """
        );

        using (platform) {
            using (host) {
                document.Root.Add("div", classNames: "grip");
                document.Root.Add("div", classNames: "link");
                document.Root.Add("div", classNames: "plain");
                document.Update();

                document.Dispatch(At(10f, 10f));
                Assert.Same(host.Main, PlatformCursor.Apply(host));
                Assert.Equal(CursorShape.ResizeHorizontal, host.Main.CursorShape);

                // ⚠ `cursor-pointer` is the one everybody assumes already worked, and it was in
                // exactly the same position as `cursor-help`: resolved by the cascade, read by
                // nobody, shown to no one.
                document.Dispatch(At(60f, 10f));
                PlatformCursor.Apply(host);
                Assert.Equal(CursorShape.Hand, host.Main.CursorShape);

                // And back to the arrow where nothing says otherwise, rather than keeping the last
                // shape — a window left holding a resize cursor over the whole page is the failure
                // this direction catches.
                document.Dispatch(At(110f, 10f));
                PlatformCursor.Apply(host);
                Assert.Equal(CursorShape.Arrow, host.Main.CursorShape);
            }
        }
    }

    [Fact]
    public void A_torn_off_window_is_told_its_own_cursor_and_the_main_one_is_not() {
        var (platform, document, host) = Open(
            """
            root { width: 400px; height: 300px; }
            .grip { width: 50px; height: 50px; cursor: col-resize; }
            """
        );

        using (platform) {
            using (host) {
                var opened = (PlatformUiWindow) host.Open(document, new UiWindowRequest("Inspector", 0f, 0f, 320f, 240f))!;

                opened.Surface.Root.Add("div", classNames: "grip");
                document.Update();

                document.Dispatch(opened.Surface, At(10f, 10f));
                Assert.Same(opened.Window, PlatformCursor.Apply(host));

                // ⚠ Two windows do not share a pointer. Writing the cursor to the main window would
                // give the panel the main window's arrow and the main window a resize cursor for
                // something nobody is over.
                Assert.Equal(CursorShape.ResizeHorizontal, opened.Window.CursorShape);
                Assert.Equal(CursorShape.Arrow, host.Main.CursorShape);
            }
        }
    }

    [Fact]
    public void Cursor_none_hides_the_pointer_and_leaving_brings_it_back() {
        var (platform, document, host) = Open(
            """
            root { width: 400px; height: 300px; flex-direction: row; }
            .hidden { width: 50px; height: 50px; cursor: none; }
            .plain { width: 50px; height: 50px; }
            """
        );

        using (platform) {
            using (host) {
                document.Root.Add("div", classNames: "hidden");
                document.Root.Add("div", classNames: "plain");
                document.Update();

                document.Dispatch(At(10f, 10f));
                PlatformCursor.Apply(host);
                Assert.Equal(CursorMode.Hidden, host.Main.CursorMode);

                document.Dispatch(At(60f, 10f));
                PlatformCursor.Apply(host);
                Assert.Equal(CursorMode.Normal, host.Main.CursorMode);
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>A game in mouse-look owns the pointer and a stylesheet may not take it back.</b>
    ///     <see cref="CursorMode.Relative" /> is the first-person camera mode, and an interface
    ///     drawn over the top of one that dragged the pointer out of it between frames would be a
    ///     camera that stops turning while a menu is open.
    /// </summary>
    [Fact]
    public void A_pointer_somebody_else_took_is_left_where_they_put_it() {
        var (platform, document, host) = Open(
            """
            root { width: 400px; height: 300px; }
            .hidden { width: 50px; height: 50px; cursor: none; }
            """
        );

        using (platform) {
            using (host) {
                document.Root.Add("div", classNames: "hidden");
                document.Update();

                host.Main.CursorMode = CursorMode.Relative;

                document.Dispatch(At(10f, 10f));
                PlatformCursor.Apply(host);

                Assert.Equal(CursorMode.Relative, host.Main.CursorMode);
            }
        }
    }

    [Fact]
    public void A_pointer_over_nothing_leaves_every_window_alone() {
        var (platform, document, host) = Open(
            """
            root { width: 400px; height: 300px; cursor: crosshair; }
            """
        );

        using (platform) {
            using (host) {
                document.Update();

                // Never hovered, so the document has no answer and there is no window to give it to.
                Assert.Null(PlatformCursor.Apply(host));
                Assert.Equal(CursorShape.Arrow, host.Main.CursorShape);
            }
        }
    }

    /// <summary>
    ///     The two enums are not one to one in either direction, which is why the mapping is a
    ///     method with a test rather than a cast.
    /// </summary>
    [Theory]
    [InlineData(UiCursor.Auto, CursorShape.Arrow)]
    [InlineData(UiCursor.Default, CursorShape.Arrow)]
    [InlineData(UiCursor.None, CursorShape.Arrow)]
    [InlineData(UiCursor.Pointer, CursorShape.Hand)]
    [InlineData(UiCursor.Grab, CursorShape.Hand)]
    [InlineData(UiCursor.Grabbing, CursorShape.Hand)]
    [InlineData(UiCursor.Text, CursorShape.TextBeam)]
    [InlineData(UiCursor.Move, CursorShape.ResizeAll)]
    [InlineData(UiCursor.NotAllowed, CursorShape.NotAllowed)]
    [InlineData(UiCursor.Crosshair, CursorShape.Crosshair)]
    [InlineData(UiCursor.Wait, CursorShape.Wait)]
    [InlineData(UiCursor.Progress, CursorShape.Wait)]
    [InlineData(UiCursor.ColumnResize, CursorShape.ResizeHorizontal)]
    [InlineData(UiCursor.EastWest, CursorShape.ResizeHorizontal)]
    [InlineData(UiCursor.RowResize, CursorShape.ResizeVertical)]
    [InlineData(UiCursor.NorthSouth, CursorShape.ResizeVertical)]
    [InlineData(UiCursor.Help, CursorShape.Arrow)]
    public void Every_cursor_the_cascade_can_resolve_maps_to_a_stock_shape(UiCursor cursor, CursorShape shape) =>
        Assert.Equal(shape, PlatformCursor.ToShape(cursor));
}
