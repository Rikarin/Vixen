// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>How far an ambient value provided above a docking host actually reaches.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The question #658's last owed item turns on, and it had no answer.</b> That item is
///         to port <c>Samples/02-HelloUi/Shell.vxml</c>'s three repeated <c>Model="@Model"</c> onto
///         <c>&lt;provide&gt;</c>. A <c>&lt;provide&gt;</c> is scoped to the element the tag is
///         written in, and a <c>DockPanel</c> is famously not left where it was written — it is
///         parked and then placed by the arrangement — so whether the walk from inside a panel still
///         passes through the shell's frame was a guess either way.
///     </para>
///     <para>
///         <b>Docked, it does.</b> <see cref="DockingHost.Detached" /> is one of the host's own
///         parts and a group's body is inside the host's surface part, so every reparent the
///         arrangement makes keeps a panel under the host.
///     </para>
///     <para>
///         ⚠ <b>And torn out into its own window it <i>still</i> does, which is the answer neither
///         this file's author nor <c>TryInject</c>'s own remark expected.</b> That remark names "a
///         panel is torn off into its own window" as a reason the walk cannot be cached, which reads
///         as a prediction that the answer changes. It does not: a secondary <c>UiSurface</c>'s root
///         is parented under the element that asked for the window, so the chain from a floated
///         panel is
///         <c>row → dock-panel → dock-body → dock-group → ui-surface → docking-host → shell-frame</c>
///         and the frame is still an ancestor. Uncaching is still right — the walk crosses a
///         different set of elements before and after — but the value does not change, and a
///         cross-surface ambient is a thing this framework has rather than a thing it lacks.
///     </para>
///     <para>
///         <b>So the port is safe</b>, and this file is the evidence for it rather than the prose in
///         the guide.
///     </para>
/// </remarks>
public class AmbientAcrossDockingTests {
    /// <summary>Something to be ambient about, keyed by its own type.</summary>
    sealed class Selection {
        public Selection(string name) => Name = name;

        public string Name { get; }
    }

    [Fact]
    public void A_value_provided_on_the_frame_reaches_a_docked_panel() {
        using var fixture = new AdvancedFixture();

        var (frame, host) = Shell(fixture);
        var shell = new Selection("shell");

        frame.Provide(shell);

        var leaf = host.AddPanel("hierarchy", "Hierarchy").Add("row");

        fixture.Update();

        Assert.Same(shell, leaf.Inject<Selection>());
    }

    /// <summary>
    ///     ⚠ <b>The instrument, and it is not optional here.</b>
    /// </summary>
    /// <remarks>
    ///     The document is the last word in this walk, so a test that provided on the frame and on
    ///     the document at once would pass with the frame never consulted. With nothing provided
    ///     anywhere the same leaf finds nothing, which is what says the answer above came from the
    ///     frame.
    /// </remarks>
    [Fact]
    public void The_same_leaf_finds_nothing_when_nothing_provides() {
        using var fixture = new AdvancedFixture();

        var (_, host) = Shell(fixture);
        var leaf = host.AddPanel("hierarchy", "Hierarchy").Add("row");

        fixture.Update();

        Assert.Null(leaf.Inject<Selection>());
    }

    /// <summary>⚠ A panel torn into its own window keeps the frame's value, surface and all.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Asserted by name and against a document that provides a different one</b>, so the
    ///         test says which of the two won rather than merely that something was found. A walk
    ///         that stopped at the surface would report <c>document</c> and be green against a
    ///         weaker assertion.
    ///     </para>
    ///     <para>
    ///         The parent chain is asserted too, because the <i>reason</i> is the surprising part:
    ///         a secondary surface's root hangs off the element that asked for the window, so the
    ///         element tree spans surfaces even though the windows do not.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_panel_torn_into_its_own_window_still_sees_the_frame() {
        using var fixture = new AdvancedFixture();
        var windows = new Windows(fixture.Document);

        var (frame, host) = Shell(fixture);

        frame.Provide(new Selection("shell"));
        fixture.Document.Provide(new Selection("document"));

        var leaf = host.AddPanel("inspector", "Inspector").Add("row");

        host.AddPanel("scene", "Scene");
        fixture.Update();

        Assert.Equal("shell", leaf.Inject<Selection>()?.Name);

        host.Float("inspector", 200f, 120f, 320f, 240f);
        fixture.Update();

        // The tear-out happened: the panel is on the window's surface and not on the primary one.
        var window = Assert.Single(windows.Opened);

        Assert.Same(window.Surface, fixture.Document.SurfaceOf(leaf));
        Assert.NotSame(fixture.Document.Primary, fixture.Document.SurfaceOf(leaf));

        Assert.Equal("shell", leaf.Inject<Selection>()?.Name);
        Assert.Contains("shell-frame", Ancestry(leaf), StringComparison.Ordinal);
        Assert.Contains("ui-surface", Ancestry(leaf), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The half the two tests above do not cover, and the one the sample port actually
    ///     needs: the value has to be there <i>while the panel is being built</i>, not after the
    ///     next flush.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both tests above call <c>fixture.Update()</c> before they ask, so either would stay
    ///         green if a panel were parked somewhere detached during its own construction and
    ///         reparented on the flush. That is not a hypothetical shape: a <c>DockPanel</c> is
    ///         registered with the host and <i>then</i> placed by the arrangement, and a panel whose
    ///         <c>OnComposed</c> reads an ambient value — which is exactly what
    ///         <c>Samples/02-HelloUi/Panels/Inspector.vxml</c> does, <c>grid.Inspect(Model.Material)</c>
    ///         — would get null and throw before any flush happened.
    ///     </para>
    ///     <para>
    ///         So this asks with no update between: nested the way a `.vxml` nests, through
    ///         <c>BuildContext.Inner</c>, which is the call the emitter writes for a child inside a
    ///         tag. ⚠ It answers "yes", and the reason is that the parking happens in
    ///         <c>OnChildAdded</c> — synchronously, into the host's own parts — rather than being
    ///         deferred to the arrangement pass.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_child_added_inside_a_panel_sees_the_frame_before_any_flush() {
        using var fixture = new AdvancedFixture();

        var (frame, host) = Shell(fixture);

        frame.Provide(new Selection("shell"));

        var panel = BuildContext.Inner(host).Add<DockPanel>();

        panel.Id = "hierarchy";
        panel.Title = "Hierarchy";

        var leaf = BuildContext.Inner(panel).Add("row");

        // ⚠ No `fixture.Update()`. This is the moment a markup panel's own `Build` and `OnComposed`
        // run, and the whole question is whether the walk is connected by then.
        Assert.Equal("shell", leaf.Inject<Selection>()?.Name);
    }

    /// <summary>Every tag from an element up to the root, so a chain can be asserted as one string.</summary>
    static string Ancestry(UiElement element) {
        var tags = new List<string>();

        for (var walk = element; walk is not null; walk = walk.Parent) {
            tags.Add(walk.Tag);
        }

        return string.Join(" < ", tags);
    }

    /// <summary>Just enough of a window host for a panel to be torn onto a second surface.</summary>
    /// <remarks>
    ///     ⚠ A double rather than a reparent the test performs itself: what is under test is where
    ///     <see cref="DockingHost.Float" /> puts a panel, and a test that moved it by hand would be
    ///     asserting its own arrangement. Trimmed to what the tear-out path calls — the docking host
    ///     asks for a window, reads its surface, and never moves it here.
    /// </remarks>
    sealed class Windows : IUiWindowHost {
        readonly UiDocument document;

        public Windows(UiDocument owner) {
            document = owner;
            owner.Windows = this;
        }

        public bool CanOpen => true;

        public List<Window> Opened { get; } = [];

        public IUiWindow Open(UiDocument owner, in UiWindowRequest request) {
            var window = new Window(
                owner.CreateSurface(request.Width, request.Height, 1f, request.Owner),
                request.Title
            );

            Opened.Add(window);

            return window;
        }

        public bool TryLocate(UiSurface surface, out float x, out float y) {
            x = 0f;
            y = 0f;

            return ReferenceEquals(surface, document.Primary);
        }
    }

    sealed class Window : IUiWindow {
        public Window(UiSurface surface, string title) {
            Surface = surface;
            Title = title;
        }

        public UiSurface Surface { get; }

        public string Title { get; set; }

        public (float X, float Y, float Width, float Height) Bounds { get; set; }

        public float DpiScale => 1f;

        public bool IsClosed => false;

        public void Focus() { }

        public event Action<IUiWindow>? CloseRequested { add { } remove { } }

        public event Action<IUiWindow>? Moved { add { } remove { } }

        public event Action<IUiWindow>? DidBecomeKey { add { } remove { } }

        public void Dispose() { }
    }

    static (UiElement Frame, DockingHost Host) Shell(AdvancedFixture fixture) {
        // ⚠ An element between the root and the host, and the test is worthless without it: the
        // document is this walk's last word, so a host added straight to the root would make every
        // answer below the document's.
        var frame = fixture.Document.Root.Add("shell-frame");

        return (frame, frame.Add<DockingHost>());
    }
}
