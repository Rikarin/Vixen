// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Platform.Headless;
using Vixen.Ui;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>A document's surfaces, and the windows they are shown in.</summary>
/// <remarks>
///     Over the headless platform, which is the whole point of there being one: a window with a
///     size, an id and an event stream and nothing to look at is exactly enough to prove that a
///     second surface was made, laid out for its own window, resized when that window was, and
///     taken away with it.
/// </remarks>
public class PlatformWindowHostTests {
    static (HeadlessPlatform Platform, UiDocument Document, PlatformWindowHost Host) Open() {
        var platform = new HeadlessPlatform();
        var main = platform.CreateWindow(new WindowOptions { Size = new Int2(1280, 720) });
        var document = new UiDocument(100f, 100f);

        return (platform, document, new PlatformWindowHost(platform, document, main));
    }

    [Fact]
    public void The_primary_surface_is_laid_out_for_the_window_it_is_already_in() {
        var (platform, document, host) = Open();

        using (platform) {
            using (host) {
                // Constructed at 100×100 and handed a 1280×720 window. Without the fit in the
                // constructor the document would keep the nominal size until the first resize —
                // which on a window that is never resized is for ever.
                Assert.Equal(1280f, document.Primary.Width, 0.001f);
                Assert.Equal(720f, document.Primary.Height, 0.001f);
            }
        }
    }

    [Fact]
    public void Opening_a_window_adds_a_surface_and_closing_it_takes_the_surface_away() {
        var (platform, document, host) = Open();

        using (platform) {
            using (host) {
                Assert.True(host.CanOpen);
                Assert.Single(document.Surfaces);

                var window = host.Open(document, new UiWindowRequest("Inspector", 40f, 60f, 320f, 240f));

                Assert.NotNull(window);
                Assert.Equal(2, document.Surfaces.Count);

                // The surface's root is a child of the document's root, which is what keeps one
                // style tree — and what makes a panel moved into this window a reparent rather than
                // a rebuild.
                Assert.Same(document.Root, window.Surface.Root.Parent);
                Assert.Same(window.Surface, document.SurfaceOf(window.Surface.Root));

                window.Dispose();

                Assert.Single(document.Surfaces);
                Assert.True(window.Surface.IsRemoved);
                Assert.Empty(host.Windows);
            }
        }
    }

    [Fact]
    public void A_windows_events_reach_its_own_surface_and_nobody_elses() {
        var (platform, document, host) = Open();

        using (platform) {
            using (host) {
                var window = host.Open(document, new UiWindowRequest("Inspector", 0f, 0f, 320f, 240f))!;
                var opened = Assert.IsType<PlatformUiWindow>(window);

                Assert.True(host.TryResolve(host.Main.Id, out var primary));
                Assert.Same(document.Primary, primary);

                Assert.True(host.TryResolve(opened.Window.Id, out var secondary));
                Assert.Same(window.Surface, secondary);

                Assert.False(host.TryResolve(uint.MaxValue, out _));
            }
        }
    }

    [Fact]
    public void A_resize_lays_the_right_surface_out_again_and_is_not_swallowed() {
        var (platform, document, host) = Open();

        using (platform) {
            using (host) {
                var window = host.Open(document, new UiWindowRequest("Inspector", 0f, 0f, 320f, 240f))!;
                var opened = (PlatformUiWindow) window;

                var moved = 0;
                window.Moved += _ => moved++;

                opened.Window.ClientSize = new Int2(500, 400);

                // ⚠ False rather than true: the head still has a swapchain to rebuild for this
                // window, and a resize eaten here is a window that lays out at its new size and
                // presents at its old one.
                Assert.False(
                    host.Handle(PlatformEvent.WindowResized(opened.Window.Id, 0L, new Int2(500, 400), new Int2(500, 400)))
                );

                Assert.Equal(500f, window.Surface.Width, 0.001f);
                Assert.Equal(400f, window.Surface.Height, 0.001f);
                Assert.Equal(1, moved);

                // The main window's surface is untouched by another window's resize.
                Assert.Equal(1280f, document.Primary.Width, 0.001f);
            }
        }
    }

    [Fact]
    public void A_close_on_a_torn_off_window_is_a_request_and_a_close_on_the_main_one_is_not_handled() {
        var (platform, document, host) = Open();

        using (platform) {
            using (host) {
                var window = host.Open(document, new UiWindowRequest("Inspector", 0f, 0f, 320f, 240f))!;
                var opened = (PlatformUiWindow) window;

                var asked = 0;
                window.CloseRequested += _ => asked++;

                Assert.True(host.Handle(PlatformEvent.Window(PlatformEventKind.WindowCloseRequested, opened.Window.Id, 0L)));
                Assert.Equal(1, asked);

                // ⚠ Still open. The whole point of the request is that somebody else decides — the
                // docking host brings the panels home first, and a window that closed itself here
                // would take them with it.
                Assert.False(window.IsClosed);
                Assert.Equal(2, document.Surfaces.Count);

                // Closing the last window is the application quitting, which is the head's business.
                Assert.False(host.Handle(PlatformEvent.Window(PlatformEventKind.WindowCloseRequested, host.Main.Id, 0L)));
            }
        }
    }

    [Fact]
    public void Disposing_the_host_closes_what_it_opened_and_leaves_the_main_window_alone() {
        var (platform, document, host) = Open();

        using (platform) {
            host.Open(document, new UiWindowRequest("One", 0f, 0f, 320f, 240f));
            host.Open(document, new UiWindowRequest("Two", 0f, 0f, 320f, 240f));

            Assert.Equal(3, document.Surfaces.Count);

            host.Dispose();

            Assert.Single(document.Surfaces);
            Assert.False(host.Main.IsClosed);
            Assert.Null(document.Windows);
        }
    }

    [Fact]
    public void A_platform_that_cannot_position_windows_refuses_to_locate_rather_than_guessing() {
        var (platform, document, host) = Open();

        using (platform) {
            using (host) {
                // Headless reports MultiWindow and nothing else — no WindowPositioning — so the one
                // honest answer is "I do not know", and docking degrades to working inside each
                // window rather than dropping panels at coordinates it invented.
                Assert.False(platform.Has(PlatformCapabilities.WindowPositioning));
                Assert.False(host.TryLocate(document.Primary, out _, out _));
            }
        }
    }
}
