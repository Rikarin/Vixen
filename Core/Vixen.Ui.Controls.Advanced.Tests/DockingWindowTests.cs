// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>Floating dock groups as real windows, and what happens when there cannot be one.</summary>
/// <remarks>
///     Over a fake <see cref="IUiWindowHost" /> rather than a platform: everything the docking host
///     does with a window goes through that interface, so a fake that opens surfaces and remembers
///     where it put them exercises the whole path — the tear-out, the drag between windows, the
///     close that brings the panels home — with no display server anywhere.
/// </remarks>
public class DockingWindowTests {
    /// <summary>A window host that opens surfaces and places them on an imaginary desktop.</summary>
    sealed class FakeWindows : IUiWindowHost {
        readonly Dictionary<UiSurface, FakeWindow> placed = [];

        public FakeWindows(UiDocument document, float mainX = 0f, float mainY = 0f) {
            Document = document;
            document.Windows = this;

            Origin = (mainX, mainY);
        }

        public UiDocument Document { get; }

        /// <summary>Where the main window's corner is, so that "desktop space" is not the document's.</summary>
        public (float X, float Y) Origin { get; }

        public bool CanOpen { get; set; } = true;

        public List<FakeWindow> Opened { get; } = [];

        public IUiWindow? Open(UiDocument document, in UiWindowRequest request) {
            if (!CanOpen) {
                return null;
            }

            var surface = document.CreateSurface(request.Width, request.Height, 1f, request.Owner);
            var window = new FakeWindow(this, surface, request);

            placed[surface] = window;
            Opened.Add(window);

            return window;
        }

        public bool TryLocate(UiSurface surface, out float x, out float y) {
            if (placed.TryGetValue(surface, out var window)) {
                (x, y, _, _) = window.Bounds;
                return true;
            }

            if (ReferenceEquals(surface, Document.Primary)) {
                (x, y) = Origin;
                return true;
            }

            x = 0f;
            y = 0f;

            return false;
        }

        public void Forget(FakeWindow window) {
            placed.Remove(window.Surface);
            Opened.Remove(window);
        }
    }

    sealed class FakeWindow : IUiWindow {
        readonly FakeWindows host;

        public FakeWindow(FakeWindows host, UiSurface surface, in UiWindowRequest request) {
            this.host = host;

            Surface = surface;
            Title = request.Title;
            Bounds = (request.X, request.Y, request.Width, request.Height);
        }

        public UiSurface Surface { get; }

        public string Title { get; set; }

        public (float X, float Y, float Width, float Height) Bounds { get; set; }

        public float DpiScale => 1f;

        public bool IsClosed { get; private set; }

        public void Focus() { }

        public event Action<IUiWindow>? CloseRequested;

        public event Action<IUiWindow>? Moved;

        public void Dispose() {
            if (IsClosed) {
                return;
            }

            IsClosed = true;

            host.Forget(this);
            host.Document.RemoveSurface(Surface);
        }

        /// <summary>What the user closing the title bar does.</summary>
        public void AskToClose() => CloseRequested?.Invoke(this);

        /// <summary>What the user dragging the window does.</summary>
        public void MoveTo(float x, float y) {
            var (_, _, width, height) = Bounds;

            Bounds = (x, y, width, height);
            Moved?.Invoke(this);
        }
    }

    static (AdvancedFixture Fixture, DockingHost Host, FakeWindows Windows) Open(float originX = 0f, float originY = 0f) {
        var fixture = new AdvancedFixture();
        var windows = new FakeWindows(fixture.Document, originX, originY);
        var host = fixture.Add<DockingHost>();

        return (fixture, host, windows);
    }

    [Fact]
    public void Floating_a_panel_opens_a_window_and_puts_the_panel_in_it() {
        var (fixture, host, windows) = Open();

        using (fixture) {
            var panel = host.AddPanel("scene", "Scene");
            host.AddPanel("inspector", "Inspector");

            host.Float("inspector", 200f, 120f, 320f, 240f);
            fixture.Update();

            var window = Assert.Single(windows.Opened);

            Assert.Equal(1, host.TornWindowCount);
            Assert.Equal("Inspector", window.Title);

            // ⚠ The panel is *in* the window's surface, which is the whole reason a window is a
            // surface rather than a document: it got there by a reparent, so it is the same element
            // with the same scroll offset, selection and half-typed text it had a moment ago.
            var inspector = host.Panels["inspector"];

            Assert.Same(window.Surface, fixture.Document.SurfaceOf(inspector));
            Assert.Same(fixture.Document.Primary, fixture.Document.SurfaceOf(panel));

            // And nothing floats inside the host itself — that is what the fallback does instead.
            Assert.DoesNotContain(host.Children, child => child.Tag == "dock-float");
        }
    }

    [Fact]
    public void Without_a_window_host_the_same_call_floats_inside_the_document() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("scene", "Scene");
        host.AddPanel("inspector", "Inspector");

        Assert.False(host.CanTearOut);

        host.Float("inspector", 200f, 120f, 320f, 240f);
        fixture.Update();

        // The arrangement is identical — same file, same groups — and only the presentation differs.
        // A browser tab, an Android activity and iOS all land here.
        Assert.Equal(0, host.TornWindowCount);
        Assert.Single(fixture.Document.Surfaces);
        Assert.Contains(host.Children, child => child.Tag == "dock-float");
    }

    [Fact]
    public void A_host_that_will_not_open_a_window_falls_back_rather_than_losing_the_panel() {
        var (fixture, host, windows) = Open();

        using (fixture) {
            windows.CanOpen = false;

            host.AddPanel("scene", "Scene");
            host.AddPanel("inspector", "Inspector");

            host.Float("inspector", 200f, 120f, 320f, 240f);
            fixture.Update();

            Assert.Empty(windows.Opened);
            Assert.Contains(host.Children, child => child.Tag == "dock-float");
            Assert.True(host.Panels.ContainsKey("inspector"));
        }
    }

    [Fact]
    public void A_rebuild_keeps_the_window_a_floating_group_already_has() {
        var (fixture, host, windows) = Open();

        using (fixture) {
            host.AddPanel("scene", "Scene");
            host.AddPanel("inspector", "Inspector");
            host.Float("inspector", 200f, 120f, 320f, 240f);

            fixture.Update();
            var window = Assert.Single(windows.Opened);

            // Every structural change rebuilds the views. ⚠ A window keyed on its index in
            // `Layout.Floating` would be closed and reopened here — a window blinking off and on
            // again the first time the user docked something somewhere else.
            host.AddPanel("console", "Console");
            host.Rebuild();
            fixture.Update();

            Assert.Same(window, Assert.Single(windows.Opened));
            Assert.False(window.IsClosed);
        }
    }

    [Fact]
    public void Moving_the_window_writes_its_new_place_into_the_arrangement() {
        var (fixture, host, windows) = Open();

        using (fixture) {
            host.AddPanel("scene", "Scene");
            host.AddPanel("inspector", "Inspector");
            host.Float("inspector", 200f, 120f, 320f, 240f);

            fixture.Update();

            var changed = 0;
            host.LayoutChanged += _ => changed++;

            windows.Opened[0].MoveTo(640f, 480f);

            var floated = Assert.Single(host.Layout.Floating);

            Assert.Equal(640f, floated.X, 0.001f);
            Assert.Equal(480f, floated.Y, 0.001f);
            Assert.Equal(1, changed);

            // ⚠ Not a rebuild. Nothing structural changed, and reparenting every panel in the window
            // sixty times a second while it is being dragged is the difference between a drag and a
            // slideshow.
            Assert.Same(windows.Opened[0], Assert.Single(windows.Opened));
        }
    }

    [Fact]
    public void Closing_the_window_brings_the_panels_home_rather_than_destroying_them() {
        var (fixture, host, windows) = Open();

        using (fixture) {
            host.AddPanel("scene", "Scene");
            host.AddPanel("inspector", "Inspector");
            host.Float("inspector", 200f, 120f, 320f, 240f);

            fixture.Update();

            windows.Opened[0].AskToClose();
            fixture.Update();

            // ⚠ Every docking system that made this a close grew a bug report titled "I lost my
            // inspector": the window's close button is a foot away from the panel's, and one of them
            // destroys work while the other rearranges it.
            Assert.True(host.Panels.ContainsKey("inspector"));
            Assert.Empty(host.Layout.Floating);
            Assert.Equal(0, host.TornWindowCount);

            var group = Assert.IsType<DockGroupNode>(host.Layout.Root);
            Assert.Equal(["scene", "inspector"], group.Panels);

            // The surface went with the window, and the panel did not go with the surface.
            Assert.Single(fixture.Document.Surfaces);
            Assert.False(host.Panels["inspector"].IsRemoved);
        }
    }

    [Fact]
    public void Docking_the_last_panel_out_of_a_window_closes_it() {
        var (fixture, host, windows) = Open();

        using (fixture) {
            host.AddPanel("scene", "Scene");
            host.AddPanel("inspector", "Inspector");
            host.Float("inspector", 200f, 120f, 320f, 240f);

            fixture.Update();
            Assert.Single(windows.Opened);

            var docked = Assert.IsType<DockGroupNode>(host.Layout.Root);
            host.Dock("inspector", docked, DockZone.Center);
            fixture.Update();

            // The floating group emptied, the prune took it out of the arrangement, and the window
            // went with it. One place closes windows rather than two that have to agree.
            Assert.Empty(windows.Opened);
            Assert.Single(fixture.Document.Surfaces);
            Assert.Equal(["scene", "inspector"], docked.Panels);
        }
    }

    [Fact]
    public void Removing_the_panel_in_a_window_closes_it_too() {
        var (fixture, host, windows) = Open();

        using (fixture) {
            host.AddPanel("scene", "Scene");
            host.AddPanel("inspector", "Inspector");
            host.Float("inspector", 200f, 120f, 320f, 240f);

            fixture.Update();

            host.RemovePanel("inspector");
            fixture.Update();

            Assert.Empty(windows.Opened);
            Assert.Single(fixture.Document.Surfaces);
        }
    }

    [Fact]
    public void Dragging_a_tab_off_the_window_tears_it_out_onto_the_desktop() {
        // The main window's corner is at (300, 200) on the imaginary desktop, so a drop reported in
        // document space has to be lifted through it — a torn-off window placed at the raw document
        // coordinates would appear 300 pixels to the left of the cursor that made it.
        var (fixture, host, windows) = Open(300f, 200f);

        using (fixture) {
            host.AddPanel("scene", "Scene");
            host.AddPanel("inspector", "Inspector");

            fixture.Update();

            var tab = host.Groups[0].Tabs.Children.OfType<DockTab>().First(tab => tab.PanelId == "inspector");

            // ⚠ Pressed near the tab's left edge rather than in its middle, which is where the close
            // button is — a drag that starts on the close button has the button as its source and is
            // not a tab drag at all.
            //
            // Released well outside the host's own rectangle, which is what a tear-out is: the gaps
            // *inside* the arrangement are six-pixel splitters, and a drop on one of those is a
            // fumbled drag rather than a request for a new window.
            fixture.Press(tab.Bounds.X + 4f, tab.Bounds.Y + 4f);
            fixture.Move(2000f, 1500f);
            fixture.Move(2000f, 1500f);
            fixture.Release(2000f, 1500f);

            fixture.Update();

            var floated = Assert.Single(host.Layout.Floating);

            Assert.Equal("inspector", Assert.Single(floated.Group.Panels));
            Assert.Single(windows.Opened);

            // 2000 + 300 is where the pointer was on the desktop; the grip is what keeps the tab
            // roughly under it rather than the window's corner.
            Assert.Equal(2300f - 48f, floated.X, 0.001f);
        }
    }

    [Fact]
    public void A_drop_inside_the_host_docks_rather_than_tearing_out() {
        var (fixture, host, windows) = Open(300f, 200f);

        using (fixture) {
            host.AddPanel("scene", "Scene");
            host.AddPanel("inspector", "Inspector");

            fixture.Update();

            var tab = host.Groups[0].Tabs.Children.OfType<DockTab>().First(tab => tab.PanelId == "inspector");
            var bounds = host.Groups[0].Bounds;

            var x = bounds.X + 4f;
            var y = bounds.Y + (bounds.Height * 0.5f);

            fixture.Press(tab.Bounds.X + 4f, tab.Bounds.Y + 4f);
            fixture.Move(x, y);
            fixture.Move(x, y);
            fixture.Release(x, y);

            fixture.Update();

            Assert.Empty(windows.Opened);
            Assert.Empty(host.Layout.Floating);

            // Left edge of the only group: a split, with the dragged panel in the first half.
            var split = Assert.IsType<DockSplitNode>(host.Layout.Root);
            Assert.Equal("inspector", Assert.Single(Assert.IsType<DockGroupNode>(split.First).Panels));
        }
    }

    [Fact]
    public void A_tab_dragged_from_a_torn_off_window_into_the_main_one_docks_there() {
        var (fixture, host, windows) = Open(100f, 100f);

        using (fixture) {
            host.AddPanel("scene", "Scene");
            host.AddPanel("inspector", "Inspector");
            host.Float("inspector", 900f, 600f, 320f, 240f);

            fixture.Update();

            var window = Assert.Single(windows.Opened);
            var floating = host.Groups.First(view => ReferenceEquals(fixture.Document.SurfaceOf(view), window.Surface));
            var tab = floating.Tabs.Children.OfType<DockTab>().Single();

            // ⚠ The drag is reported in the *torn-off* window's coordinates for its whole life, even
            // once the cursor is over the main window — every platform keeps sending a captured
            // pointer's position relative to the window the press happened in. So the target has to
            // be found in desktop space: the main window's group sits at (100, 100) on the desktop,
            // which is (-800, -500) from here.
            var docked = host.Groups.First(view => ReferenceEquals(fixture.Document.SurfaceOf(view), fixture.Document.Primary));
            var centre = docked.Bounds;

            var x = 100f + centre.X + (centre.Width * 0.5f) - 900f;
            var y = 100f + centre.Y + (centre.Height * 0.5f) - 600f;

            fixture.Press(window.Surface, tab.Bounds.X + 4f, tab.Bounds.Y + 4f);
            fixture.Move(window.Surface, x, y);
            fixture.Move(window.Surface, x, y);
            fixture.Release(window.Surface, x, y);

            fixture.Update();

            Assert.Empty(host.Layout.Floating);
            Assert.Empty(windows.Opened);

            var group = Assert.IsType<DockGroupNode>(host.Layout.Root);
            Assert.Equal(["scene", "inspector"], group.Panels);
        }
    }
}
