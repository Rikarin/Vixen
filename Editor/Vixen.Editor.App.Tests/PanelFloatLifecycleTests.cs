// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The fourth state of doc 20 Part F's panel-lifecycle row: floated, and brought home again.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The row said this state could not be covered because "tearing a panel out needs a
///         window host and the harness has none", and that refusal has expired.</b>
///         <see cref="IUiWindowHost" /> is four members in <c>Vixen.Ui</c> with no platform in them,
///         and <c>Vixen.Ui.Controls.Advanced.Tests</c> has been exercising the whole tear-out path
///         over a double of it for as long as the path has existed. What was missing was the double
///         on this side, not a display server.
///         <see href="https://github.com/Rikarin/Vixen/issues/365">#365</see>.
///     </para>
///     <para>
///         ⚠ <b>Floating is the state most likely to break for the reason closing was.</b> A panel
///         that closes has its contents torn out by the host; a panel that floats has them moved to
///         a different surface — a second element tree with a root of its own — so anything holding
///         a reference to what a factory built, or to where it built it, has the same hazard one step
///         further along. Closing found two real defects; this is the same question asked about the
///         state nobody had asked it about.
///     </para>
///     <para>
///         ⚠ <b>And back, which is half the claim.</b> A panel that tears out and cannot come home is
///         a panel somebody has lost — the window's close button is how a person undoes this — and a
///         test that stopped at the tear-out would be green for an editor where floating a panel
///         destroys it.
///     </para>
/// </remarks>
public class PanelFloatLifecycleTests {
    /// <summary>Part F's fourth state, over every panel the shell and the editor register.</summary>
    [Fact]
    public void Every_registered_panel_survives_being_floated_and_brought_home() {
        using var fixture = EditorSession.Start();

        var windows = new FakeWindows(fixture.Document);
        var host = fixture.Shell.Workspace.Host;

        // The premise, and it is worth stating: without a window host the loop below would float
        // nothing and pass, which is the shape of test this file exists to not be.
        Assert.True(host.CanTearOut, "the fake window host did not reach the docking host");

        var floated = 0;

        foreach (var descriptor in fixture.Shell.Workspace.Panels.ToList()) {
            fixture.Open(descriptor.Id);
            fixture.Frames(2);

            Assert.True(fixture.Shell.Workspace.IsOpen(descriptor.Id), descriptor.Id + " would not open");

            var before = host.TornWindowCount;

            Assert.True(host.Float(descriptor.Id), descriptor.Id + " would not float");

            fixture.Frames(2);

            Assert.True(
                host.TornWindowCount > before,
                descriptor.Id + " floated without a window of its own, so the tear-out did not happen"
            );

            var window = windows.Opened[^1];
            var torn = Find(window.Surface.Root, descriptor.Id);

            Assert.True(torn is not null, descriptor.Id + " is not in the window it was torn into");
            Assert.NotEmpty(torn!.Children);

            // The title bar's close button, which is how a person puts a floated panel back.
            window.AskToClose();
            fixture.Frames(2);

            Assert.True(fixture.Shell.Workspace.IsOpen(descriptor.Id), descriptor.Id + " was lost with its window");

            var home = Find(fixture.Document.Root, descriptor.Id);

            Assert.True(home is not null, descriptor.Id + " did not come home to the main window");
            Assert.NotEmpty(home!.Children);

            floated++;
        }

        // ⚠ The count is part of the claim. Every assertion above is inside the loop, so an
        // enumeration that found no panels would agree with all of them.
        Assert.True(floated > 4, $"only {floated} panels were floated, which is not the registry");
    }

    /// <summary>The dock panel with an id, anywhere under a root.</summary>
    static DockPanel? Find(UiElement root, string id) {
        if (root is DockPanel panel && string.Equals(panel.Id, id, StringComparison.Ordinal)) {
            return panel;
        }

        foreach (var child in root.Children) {
            if (Find(child, id) is { } found) {
                return found;
            }
        }

        return null;
    }

    // ── The double ───────────────────────────────────────────────────────────

    /// <summary>A window host that opens surfaces and remembers where it put them.</summary>
    /// <remarks>
    ///     The same double <c>DockingWindowTests</c> uses, which is deliberate: everything the
    ///     docking host does with a window goes through these four members, so a fake that answers
    ///     them exercises the tear-out, the close and the trip home with no display server anywhere.
    /// </remarks>
    sealed class FakeWindows : IUiWindowHost {
        readonly Dictionary<UiSurface, FakeWindow> placed = [];

        public FakeWindows(UiDocument document) {
            Document = document;
            document.Windows = this;
        }

        public UiDocument Document { get; }

        public bool CanOpen => true;

        public List<FakeWindow> Opened { get; } = [];

        public IUiWindow? Open(UiDocument document, in UiWindowRequest request) {
            ArgumentNullException.ThrowIfNull(document);

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

            x = 0f;
            y = 0f;

            return ReferenceEquals(surface, Document.Primary);
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

        public event Action<IUiWindow>? DidBecomeKey { add { } remove { } }

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
}
