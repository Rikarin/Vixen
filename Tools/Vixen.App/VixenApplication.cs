// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Core.IO;
using Vixen.Core.Threading;
using Vixen.Platform;

namespace Vixen.App;

/// <summary>An application that has been built and is ready to run.</summary>
/// <remarks>
///     <para>
///         Owns the platform, the window, the job system and the frame loop, and tears them down in
///         the reverse of the order it built them. Every step it takes is a public method or a
///         documented call an application can make itself — <c>docs/plan/17</c>'s rule that nothing
///         in the boot path is inaccessible, which is the property the prebuilt-player model cannot
///         offer.
///     </para>
///     <para>
///         Belongs to the thread that built it, because the platform does.
///     </para>
/// </remarks>
public sealed class VixenApplication : IDisposable {
    readonly Game game;
    readonly DisposeBag disposables = new();
    readonly FrameLimiter limiter = new();
    readonly ILogger logger;
    readonly Stopwatch clock = new();

    /// <summary>How often a loose-content build repeats its warning. Doc 17 Q5b says every 60 s.</summary>
    static readonly TimeSpan LooseContentWarningInterval = TimeSpan.FromSeconds(60);

    GameTime time = GameTime.Zero;
    TimeSpan lastLooseWarning = TimeSpan.Zero;
    long lastTimestamp;
    bool initialised;
    bool stopped;
    bool disposed;

    internal VixenApplication(Game game, AppServices services) {
        this.game = game;
        Services = services;

        logger = services.LoggerFactory.CreateLogger("Vixen.App");

        // Torn down in the reverse of construction: the game first, because it may still be using
        // everything below it, then the jobs it may have scheduled, then the platform that owns the
        // window those jobs might touch.
        disposables.Add(game);
        disposables.Add(services.Content);
        disposables.Add(services.Jobs);
        disposables.Add(services.Platform);
    }

    /// <summary>Everything the host built.</summary>
    public AppServices Services { get; }

    /// <summary>The clock, as the last frame saw it.</summary>
    public GameTime Time => time;

    /// <summary>Whether the loop has been asked to stop.</summary>
    public bool IsStopping =>
        stopped
        || Services.Platform.Lifecycle.IsQuitRequested
        || (Services.Config.MaxFrames > 0 && time.FrameCount >= Services.Config.MaxFrames);

    /// <summary>Runs until the application quits.</summary>
    /// <returns>A process exit code: <c>0</c> for a clean run, <c>1</c> for a crash.</returns>
    /// <remarks>
    ///     <para>
    ///         What happens to an exception that escapes a frame depends on the variant, and both
    ///         answers are right for their audience. Everywhere validation is on it is logged and
    ///         <b>rethrown</b>, so an attached debugger stops on it with the stack intact — swallowing
    ///         it there would hide the bug the developer is looking for.
    ///     </para>
    ///     <para>
    ///         In a <see cref="BuildVariant.Release" /> build it is logged and the exit code becomes
    ///         <c>1</c>, because on a player's machine there is no debugger and an unhandled
    ///         exception produces a stack trace in a console nobody is reading, whereas the log ring
    ///         is what the crash reporter uploads.
    ///     </para>
    ///     <para>
    ///         The shutdown sequence runs either way, so a crash still releases the window, the
    ///         workers and the platform.
    ///     </para>
    /// </remarks>
    public int Run() {
        Initialise();

        try {
            while (!IsStopping) {
                RunFrame();
            }
        } catch (Exception exception) {
            HostLog.FrameLoopFailed(logger, exception);
            Shutdown();

            if (Services.Config.Variant.HasValidation()) {
                throw;
            }

            return 1;
        }

        Shutdown();
        return 0;
    }

    /// <summary>
    ///     Builds the window, mounts the file system and calls <see cref="Game.OnInitialise" />.
    /// </summary>
    /// <remarks>
    ///     Called by <see cref="Run" />. Public because a host that drives the loop itself — an
    ///     editor running a game in a panel, a test — needs to do this once before its first
    ///     <see cref="RunFrame" />.
    /// </remarks>
    public void Initialise() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (initialised) {
            return;
        }

        initialised = true;

        HostLog.Started(
            logger,
            Services.Config.Variant,
            Services.Platform.Name,
            Services.Jobs.WorkerCount
        );

        if (Services.Config.HeadlessFallbackReason is { } reason) {
            HostLog.NoWindow(logger, reason);
        }

        if (Services.Content is { Assets: { } assets, Root: var root }) {
            HostLog.ContentMounted(logger, root, assets.Catalog.Entries.Count);
        } else if (Services.Content.Reason is { } why) {
            // Not a warning. An application with nothing to load is ordinary — a sample, a batch
            // tool, a test — and the one line saying so is what turns "my asset was not found" into
            // a five-second diagnosis.
            HostLog.NoContent(logger, why);
        }

        if (Services.Content.IsLoose) {
            // docs/plan/17 Q5b: allowed, and not allowed to be quiet.
            HostLog.LooseContent(logger, Services.Content.Root);
        }

        foreach (var argument in Services.Config.UnrecognisedArguments) {
            HostLog.UnrecognisedArgument(logger, argument);
        }

        Services.Window?.Show();
        game.Attach(Services);
        game.OnInitialise();

        clock.Start();
        lastTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Runs one frame: events, main-thread work, and — unless the frame's own events ended
    /// the application — update, render, pacing.</summary>
    /// <remarks>
    ///     <para>
    ///         The whole loop body, exposed. An editor's play mode drives this from its own frame,
    ///         and a test drives it a fixed number of times — neither needs a second implementation
    ///         of the order these things happen in.
    ///     </para>
    ///     <para>
    ///         <b>A stopping frame simulates and draws nothing.</b> Events are pumped and posted work
    ///         is drained, because both may be the application's last chance to act, and then the
    ///         frame ends. What it would otherwise render, nobody would see; worse, it may no longer
    ///         have anywhere to render it. See the note on the check itself.
    ///     </para>
    ///     <para>
    ///         This does not shorten a run by a frame. <see cref="IsStopping" /> is asked before
    ///         <see cref="Advance" />, so the frame that reaches <c>--vixen-frames N</c> is the one
    ///         that advances the count <em>to</em> N and it renders in full; the loop notices
    ///         afterwards. Likewise a game that calls <see cref="Stop" /> from its own
    ///         <c>OnUpdate</c> still gets that frame's <c>OnRender</c>.
    ///     </para>
    /// </remarks>
    public void RunFrame() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!initialised) {
            Initialise();
        }

        PumpEvents();

        // After events, so that anything an event handler posted runs this frame rather than next.
        // Before the bail below, too: work posted on the way out is exactly the work that would
        // otherwise be dropped.
        Services.MainThread.Drain();

        // Closing the last window disposes it inside PumpEvents — and on macOS disposing a window
        // destroys the Metal view whose layer a swapchain's surface was created from. A game that
        // then acquires and presents in the same frame is presenting to freed memory. It does not
        // reliably fault, which is the worst way for a bug like that to behave.
        //
        // Asked here rather than folded into the loop condition in Run, because the danger is
        // specific to *this* frame: the window died between its first line and this one.
        if (IsStopping) {
            return;
        }

        WarnAboutLooseContent();

        Advance();
        game.OnUpdate(time);
        game.OnRender(time);

        limiter.Wait(FrameRateLimit());
    }

    /// <summary>
    ///     Says again, on a timer, that this build is reading content it did not ship with.
    /// </summary>
    /// <remarks>
    ///     [Doc 17](../../docs/plan/17-app-heads-and-shipping.md) Q5b decides that a release build
    ///     may be pointed at loose content and <em>refuses to let it be quiet about it</em>: the
    ///     invariant "release reads only bundles" is being weakened deliberately, and the trade is
    ///     only acceptable while it is visible. Once at startup is not visible — a build left running
    ///     overnight in a QA lab scrolled that line away hours ago — so it repeats every minute. The
    ///     overlay and crash-report stamps doc 17 also asks for arrive with the things that have
    ///     them.
    /// </remarks>
    void WarnAboutLooseContent() {
        if (!Services.Content.IsLoose) {
            return;
        }

        var since = clock.Elapsed - lastLooseWarning;

        if (since < LooseContentWarningInterval) {
            return;
        }

        lastLooseWarning = clock.Elapsed;
        HostLog.LooseContentStill(logger, Services.Content.Root);
    }

    /// <summary>Asks the application to stop after the current frame.</summary>
    public void Stop() {
        stopped = true;
        Services.Platform.Lifecycle.RequestQuit();
    }

    /// <summary>Calls <see cref="Game.OnShutdown" /> and tears everything down.</summary>
    public void Shutdown() {
        if (!initialised || disposed) {
            return;
        }

        HostLog.Stopping(logger, time.FrameCount);
        game.OnShutdown();
        Dispose();
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        clock.Stop();
        disposables.Dispose();
    }

    void PumpEvents() {
        foreach (var platformEvent in Services.Platform.PumpEvents()) {
            if (game.OnEvent(platformEvent)) {
                continue;
            }

            switch (platformEvent.Kind) {
                case PlatformEventKind.WindowCloseRequested:
                    // The window closes and, if it was the last one, the application follows. An
                    // application that wants to ask "save first?" returns true from OnEvent.
                    if (Services.Platform.TryGetWindow(platformEvent.WindowId, out var window)) {
                        window.Dispose();
                    }

                    break;

                case PlatformEventKind.Quit:
                    stopped = true;
                    break;

                default:
                    break;
            }
        }

        if (Services.Config.ExitWhenAllWindowsClose && Services.Window is not null && !AnyWindowOpen()) {
            stopped = true;
        }
    }

    /// <summary>
    ///     Whether any window is still open — asked by state rather than by list membership.
    /// </summary>
    /// <remarks>
    ///     A platform's window list is allowed to lag: it drops disposed windows at the start of the
    ///     next pump, deliberately, so that an application enumerating it inside its own event
    ///     handling sees a list that does not change under it. Counting entries here would therefore
    ///     take an extra frame to notice the last window closing — which is not fatal and is exactly
    ///     the sort of one-frame lie that turns into a bug report about a window that lingers.
    /// </remarks>
    bool AnyWindowOpen() {
        var windows = Services.Platform.Windows;

        for (var index = 0; index < windows.Count; index++) {
            if (!windows[index].IsClosed) {
                return true;
            }
        }

        return false;
    }

    void Advance() {
        var now = Stopwatch.GetTimestamp();
        var elapsed = TimeSpan.FromSeconds((now - lastTimestamp) / (double)Stopwatch.Frequency);
        lastTimestamp = now;

        // A frame that took a second — a breakpoint, a stalled driver, a laptop lid — must not be
        // handed to the simulation as a second of elapsed time, or everything moving teleports. The
        // clamp is the standard one and it belongs here rather than in every consumer.
        time = time.Advance(elapsed > MaximumFrameTime ? MaximumFrameTime : elapsed, time.TimeScale);
    }

    int FrameRateLimit() {
        var config = Services.Config;

        if (config.UnfocusedFrameRateLimit <= 0 || Services.Window is null) {
            return config.FrameRateLimit;
        }

        var focused = Services.Platform.FocusedWindow() is not null;
        return focused ? config.FrameRateLimit : config.UnfocusedFrameRateLimit;
    }

    /// <summary>
    ///     The longest a frame is allowed to claim to have taken.
    /// </summary>
    /// <remarks>
    ///     A quarter of a second: long enough that no real frame hits it, short enough that a
    ///     resumed process does not move everything a metre before the first frame back.
    /// </remarks>
    static readonly TimeSpan MaximumFrameTime = TimeSpan.FromSeconds(0.25);
}
