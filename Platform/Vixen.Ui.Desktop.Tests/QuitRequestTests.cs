// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>⌘Q, and whether anything gets a chance to say no.</summary>
/// <remarks>
///     ⚠ <b>What these asserted before the veto existed: nothing, because there was no hook to
///     assert on.</b> <c>Pump</c> set <c>running = false</c> outright on a platform Quit, so Save /
///     Don't Save / Cancel was not unimplemented — it was unreachable, in every <c>Vixen.Ui</c>
///     application. <c>EditorHost</c> has had the four correct lines since save-on-close was built;
///     this host was the copy one assembly over that still had the bug.
/// </remarks>
[Collection(SerialUiDevelopment.Name)]
public class QuitRequestTests {
    sealed class Probe : Component {
        protected override void Build(BuildContext ctx) => ctx.Element(Root, "probe-panel");
    }

    static (UiApplication Application, HeadlessPlatform Platform) Application(int frames) {
        var options = new UiApplicationOptions {
            Title = "test",
            Size = new Int2(320, 240),
            Frames = frames,
            InstallSystemFont = false,
            Content = () => new Probe()
        };

        var platform = new HeadlessPlatform();
        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(320, 240) });

        return (new UiApplication(options, platform, window), platform);
    }

    /// <summary>Nothing listening means the quit goes through, as it always did.</summary>
    /// <remarks>
    ///     ⚠ The half that proves the veto is a veto rather than a new refusal. Asked for sixteen
    ///     frames and stopped on the first, so "it quit" is a count and not a wall clock.
    /// </remarks>
    [Fact]
    public void A_quit_nobody_refuses_stops_the_loop() {
        var (application, platform) = Application(frames: 16);
        using var _ = application;

        platform.Lifecycle.RequestQuit();
        application.Run();

        Assert.True(application.FrameCount < 16);
    }

    /// <summary>A handler that refuses keeps the application running.</summary>
    /// <remarks>
    ///     ⚠ <b>And the platform's latch is cleared.</b> <c>DesktopLifecycle</c> holds
    ///     <c>IsQuitRequested</c> once set, so a host that refused and left the flag standing would
    ///     be one where the <i>next</i> quit is already half-answered — which is the detail the
    ///     editor's host writes a paragraph about and this one had no occasion to.
    /// </remarks>
    [Fact]
    public void A_refused_quit_leaves_the_loop_running_and_clears_the_platforms_latch() {
        var (application, platform) = Application(frames: 6);
        using var _ = application;

        var asked = 0;

        application.Document.CloseRequested += args => {
            asked++;
            args.Cancel();
        };

        platform.Lifecycle.RequestQuit();
        application.Run();

        Assert.Equal(1, asked);
        Assert.Equal(6, application.FrameCount);
        Assert.False(platform.Lifecycle.IsQuitRequested);
    }

    /// <summary>The request reaches the element tree, from the focus outwards.</summary>
    /// <remarks>
    ///     The whole point of routing it: the document object behind the focused view is the thing
    ///     that knows whether there is unsaved work, and the head does not know it exists.
    /// </remarks>
    [Fact]
    public void The_request_is_routed_through_the_element_tree() {
        var (application, platform) = Application(frames: 6);
        using var _ = application;

        var seen = 0;

        application.Document.Root.AddHandler<CloseRequestEvent>((_, args) => {
            seen++;
            args.Cancel();
        });

        platform.Lifecycle.RequestQuit();
        application.Run();

        Assert.Equal(1, seen);
        Assert.Equal(6, application.FrameCount);
    }

    /// <summary>Refusing is not the same as handling.</summary>
    /// <remarks>
    ///     ⚠ A document that saved silently has dealt with the request and is content to go. If
    ///     <c>Handled</c> were the veto, that handler could not say so.
    /// </remarks>
    [Fact]
    public void Marking_the_request_handled_does_not_refuse_it() {
        var (application, platform) = Application(frames: 16);
        using var _ = application;

        application.Document.Root.AddHandler<CloseRequestEvent>((_, args) => args.Handled = true);

        platform.Lifecycle.RequestQuit();
        application.Run();

        Assert.True(application.FrameCount < 16);
    }

    /// <summary>A handler that asked and got its answer quits by calling back.</summary>
    /// <remarks>
    ///     ⚠ <b>The shape a real prompt takes.</b> A dialog is answered frames later, so a
    ///     synchronous veto cannot wait for one; the handler refuses now and calls
    ///     <see cref="UiApplication.Quit" /> when it has an answer. Here the "prompt" is a counter,
    ///     which is what makes the assertion a matter of order rather than of elapsed time.
    /// </remarks>
    [Fact]
    public void A_handler_that_refuses_can_quit_later() {
        var (application, platform) = Application(frames: 32);
        using var _ = application;

        var asked = 0;

        application.Document.CloseRequested += args => {
            asked++;

            if (asked == 1) {
                args.Cancel();
            }
        };

        platform.Lifecycle.RequestQuit();
        application.Run();

        // The first request was refused, so the loop kept going and ran out of frames rather than
        // stopping — and the handler was asked exactly once, because nothing asked again.
        Assert.Equal(1, asked);
        Assert.Equal(32, application.FrameCount);

        // Asking again now that the "prompt" has an answer stops it, which is the second half of the
        // contract: a refusal is "not now", not "never".
        Assert.True(application.Quit());
        Assert.Equal(2, asked);
    }

    /// <summary>The platform is reachable, which is what an application needs to write any of this itself.</summary>
    [Fact]
    public void The_platform_is_reachable_from_the_application() {
        var (application, platform) = Application(frames: 1);
        using var _ = application;

        Assert.Same(platform, application.Platform);
    }
}
