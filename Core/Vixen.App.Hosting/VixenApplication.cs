// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Core.IO;
using Vixen.Core.Threading;
using Vixen.Engine.Diagnostics.Overlays;
using Vixen.Engine.Scenes;
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

    /// <summary>The console panel, when this build has one. See <see cref="Initialise" />.</summary>
    ConsoleOverlay? console;
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

        // After the game, which may hold textures and buffers on this device, and before the
        // platform, which owns the window whose surface the swapchain was made from. Disposing it
        // waits for the GPU to go idle first, so nothing below this line frees memory a frame in
        // flight is still reading.
        if (services.Graphics is { } graphics) {
            disposables.Add(graphics);
        }

        // Before the jobs, because its systems may still have work scheduled on them.
        if (services.Engine is { } engine) {
            disposables.Add(engine);
        }

        disposables.Add(services.Content);
        disposables.Add(services.Jobs);
        disposables.Add(services.Platform);

        // Last, so that everything above it has already said why it was shutting down: disposing the
        // factory disposes the sinks, and the file sink's dispose is what flushes its background
        // buffer to disk. A log missing its final seconds is missing the part that explains them.
        disposables.Add(services.LoggerFactory);
    }

    /// <summary>Everything the host built.</summary>
    public AppServices Services { get; }

    /// <summary>The clock, as the last frame saw it.</summary>
    public GameTime Time => time;

    /// <summary>
    ///     How fast simulated time runs: <c>1</c> for real time, <c>0</c> to pause, above <c>1</c>
    ///     to fast-forward.
    /// </summary>
    /// <remarks>
    ///     A property rather than a config value, because pausing is something a game does at run
    ///     time and not something it decides at boot. It reaches both clocks: the host's
    ///     <see cref="Time" /> and — because the engine is handed the unscaled delta and this
    ///     separately — the fixed-step accumulator, so a paused game owes no simulation steps rather
    ///     than accumulating a debt it pays all at once on resume.
    /// </remarks>
    public float TimeScale {
        get => time.TimeScale;
        set {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            time = time with { TimeScale = value };
        }
    }

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

        // At startup rather than at the end, because the end of a run with no frame count is
        // whenever somebody closes the window — by which time the person who typed the flag has
        // stopped watching, and the absent file reads as a capture that failed.
        if (Services.Config.Graphics.CapturePath is { Length: > 0 } capture && Services.Config.MaxFrames <= 0) {
            HostLog.CaptureWithoutFrameCount(logger, capture);
        }

        if (Services.Content is { Assets: { } assets, Root: var root }) {
            HostLog.ContentMounted(logger, root, assets.Catalog.Entries.Count);

            if (Services.Content.IsUnpacked) {
                HostLog.UnpackedContent(logger, Services.Content.Root);
            }

            // Only when there is something to download. A build that ships everything in its package
            // says nothing, which is the ordinary case and the one that does not need a line.
            if (Services.Content.Cache is { } cache) {
                // Counted into a local rather than inside the call: the walk happens once at startup
                // over a handful of bundles either way, and a LINQ argument to a generated log method
                // is work done whether or not anybody is listening.
                var downloadable = assets.Catalog.Bundles.Count(bundle => bundle.Url.Length > 0);

                HostLog.RemoteContent(logger, downloadable, cache.Root);
            }
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

        // ⚠ Before OnInitialise, not after. OnInitialise is where a game finds its player, parents a
        // camera to it or adds a system that queries what the level placed — all of which need the
        // entities to exist. Loading afterwards would make the startup scene the one thing a game
        // could not react to without waiting a frame.
        LoadStartupScene();

        game.OnInitialise();

        // ⚠ After OnInitialise, and found by name in the registry the overlay system was handed
        // rather than kept beside it. That is the point: what this drives has to be the same object
        // the frame draws, and asking the registry is the only way to be sure of it — the editor's
        // two DiagnosticsModules are what a private field of my own would grow into.
        //
        // A game is free to have replaced or removed it, which is why this is a lookup and a null
        // check rather than an assertion.
        console = Services.Graphics?.Overlays?.Find("console") as ConsoleOverlay;

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

        // Before the game's own update, which is where an application reads the world it is about
        // to render. Reading it before it has been stepped renders last frame's positions, which is
        // the kind of wrong that looks like input lag and gets blamed on everything else.
        //
        // Handed the *unscaled* delta and the scale separately, because that is what the loop's own
        // contract takes: the host has already applied the scale to `time`, and passing the scaled
        // value with the scale again would square it.
        if (Services.Engine is { } loop) {
            loop.Frame(time.UnscaledElapsed, time.TimeScale);
        } else {
            // No engine, no SystemPhase.Input, so the host reads the actions itself — before
            // OnUpdate, which is the same place the engine's own input phase sits relative to
            // everything that reacts to it.
            Services.Input.Update(time);
        }

        game.OnUpdate(time);

        // The world's frame, opened before the game's own hook and closed after it. That order is
        // what makes OnRender useful on a host that draws: the scene is already recorded into
        // Services.Graphics.Commands by the time the application is asked, so an overlay, a UI or a
        // debug pass records on top of it rather than under it — and a game that draws nothing of its
        // own gets the whole thing for free.
        // ⚠ Asked after Advance, which is what makes "this is the last frame" answerable at all:
        // IsStopping is read at the *top* of RunFrame, so the frame that brings the count to N is
        // the one that renders in full and the loop exits after it. Before Begin, because the copy
        // it arranges goes on the list Begin opens.
        if (Services.Config.MaxFrames > 0 && time.FrameCount >= Services.Config.MaxFrames) {
            Services.Graphics?.RequestCapture("frame");
        }

        var frame = Services.Graphics?.Begin() ?? false;

        game.OnRender(time);

        if (frame) {
            Services.Graphics!.End();
        }

        // ⚠ Here, and not as a system in the loop above. The frame's debug geometry has to survive
        // until the compositor's node has drained it, and the node drains inside Begin — which is
        // after every phase of EngineLoop.Frame, PostRender included. A DebugDrawSystem in that loop
        // would delete each frame's lines one call before anything drew them, and the symptom is the
        // whole feature quietly drawing nothing. See AppGraphics.AdvanceDebug.
        Services.Graphics?.AdvanceDebug(time.DeltaSeconds);

        limiter.Wait(FrameRateLimit());
    }

    /// <summary>The scene <see cref="AppConfig.StartupScene" /> named, once it has been loaded.</summary>
    /// <remarks>
    ///     <see cref="SceneHandle.None" /> until <see cref="Initialise" /> has run, and afterwards
    ///     whenever no scene was named or the one named did not load. It is here rather than only in
    ///     the log because it is what unloads the level again — a game going back to its main menu
    ///     wants the handle, and re-deriving it from the manager's list would be guessing.
    /// </remarks>
    public SceneHandle StartupScene { get; private set; }

    /// <summary>Opens the scene the configuration named, if it named one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The boot end of the editor's Build Settings scene list.</b> What the game asks for
    ///         is an address; a content build wrote the addresses its project listed, in order, and
    ///         the host took the first of them unless the game named its own — see
    ///         <see cref="AppConfig.StartupScene" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every failure is reported and survived</b>, which is the same trade the catalog,
    ///         the shader bundle and the compositor each make: the thing that would show a player the
    ///         message is the thing that did not start. A game with no engine, no content or a broken
    ///         scene runs on with an empty world and one line saying which of those it was.
    ///     </para>
    /// </remarks>
    void LoadStartupScene() {
        if (Services.Config.StartupScene is not { Length: > 0 } address) {
            // Not even information. A sample, a batch tool and a test each open no scene, and a line
            // per run about a thing nobody asked for is a line that trains people to skim the log.
            return;
        }

        if (Services.Scenes is not { } scenes) {
            HostLog.NoStartupScene(logger, address, "this head runs without an engine, so there is no world for it.");
            return;
        }

        if (Services.Assets is not { } assets) {
            HostLog.NoStartupScene(logger, address, Services.Content.Reason ?? "this build shipped no content.");
            return;
        }

        try {
            if (assets.Load<SceneAsset>(address).Result is not { } asset) {
                HostLog.NoStartupScene(logger, address, "nothing was published under that address.");
                return;
            }

            StartupScene = asset.Load(scenes);

            // Counted from the asset rather than by asking the manager to sweep the world for the
            // tag: the answer is the same and the walk is not, and this is a line that is written
            // once whether or not anybody is listening at Information.
            HostLog.StartupSceneLoaded(logger, address, asset.Content.Count);
        } catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException)) {
            HostLog.NoStartupScene(logger, address, failure.Message);
        }
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
        // Before the drain, not after: this clears the frame's motion deltas, and a mouse delta
        // cleared after the events that carried it would leave the camera reading zero every frame.
        Services.Input.BeginFrame();

        foreach (var platformEvent in Services.Platform.PumpEvents()) {
            if (game.OnEvent(platformEvent)) {
                continue;
            }

            // ⚠ Before the device set, so that a console that is open keeps the game from seeing the
            // keys as well. `ConsoleOverlay.IsCapturingInput` exists for exactly this sentence, and
            // its own remarks name the bug it prevents: typing `reload` also making the player
            // reload. After OnEvent, so a game that wants the key first still gets it.
            if (Console(platformEvent)) {
                continue;
            }

            // After the game's own hook, so that an application intercepting an event also keeps the
            // action system from seeing it — which is what "return true to stop the host acting on
            // it" has to mean if it is to be usable for a modal dialog.
            Services.Input.Devices.Submit(platformEvent, Services.Platform.Input);

            switch (platformEvent.Kind) {
                // Against the window's current size rather than the event's, which is what makes a
                // burst free: a window opened maximised on a 4K display produces several of these at
                // once, they all read the same size, and Recreate's own check rebuilds for the first
                // and returns for the rest. Rebuilding per event would be a device-wide wait and a
                // fresh set of undefined images several times before a single frame is drawn.
                case PlatformEventKind.WindowResized:
                    Services.Graphics?.Recreate();
                    break;

                // The surface is going away and the device is not — Android destroys the native
                // window, iOS takes the layer back — so the swapchain is dropped here and rebuilt
                // from the resumed window's handle on the next frame.
                case PlatformEventKind.Suspending:
                    Services.Graphics?.Suspend();
                    break;

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

    /// <summary>Offers one event to the console, and says whether the console took it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The host end of a seam <c>ConsoleOverlay</c> deliberately leaves open.</b> The
    ///         overlay reads no keyboard — <c>Type</c>, <c>Backspace</c>, <c>Submit</c> and the
    ///         history moves are pushed in — because which device produces a character, and whether
    ///         an IME is involved, is a platform's question and <c>Vixen.Engine</c> has no platform.
    ///         This is the answer for the shipped host and it is the whole of it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Characters come from <see cref="PlatformEventKind.TextInput" /> and never from
    ///         key codes.</b> A key is a physical position: mapping <c>Key.A</c> to <c>'a'</c> gives
    ///         a console that types the wrong letters on AZERTY and nothing at all in Japanese, and
    ///         the platform already does this properly. On SDL desktop the events arrive without
    ///         anything asking — see the <see cref="ITextInput" /> handling below, which starts it
    ///         only where it is genuinely off.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The backtick key rather than a bindable action.</b> An action would have to come
    ///         from a game's <c>.vxinput</c>, and the console is most wanted in a build whose input
    ///         map is the thing being investigated. It is the key every engine uses for this, and it
    ///         is swallowed on the way in so that a game bound to it does not also see it. Which key
    ///         that is, physically, is <see cref="IsConsoleKey" />'s problem and not a small one.
    ///     </para>
    /// </remarks>
    bool Console(in PlatformEvent platformEvent) {
        if (console is null) {
            return false;
        }

        if (platformEvent.Kind is PlatformEventKind.KeyDown && IsConsoleKey(platformEvent.Key)) {
            console.Enabled = !console.Enabled;

            if (console.Enabled) {
                console.ClearInput();
            }

            // ⚠ Only when the platform says text input is off, and only then is it turned back off.
            // On SDL desktop it is already running — the characters arrive with nothing asked for —
            // and calling Activate anyway is a state change made for no reason on the one platform
            // where the console is most used. Where it genuinely is off (a browser canvas, a phone,
            // which is what ITextInput was drawn for) this is what starts it.
            if (console.Enabled) {
                if (!Services.Platform.TextInput.IsActive && Services.Window is { } window) {
                    Services.Platform.TextInput.Activate(window);
                    activatedTextInput = true;
                }
            } else if (activatedTextInput) {
                Services.Platform.TextInput.Deactivate();
                activatedTextInput = false;
            }

            return true;
        }

        if (!console.IsCapturingInput) {
            return false;
        }

        switch (platformEvent.Kind) {
            case PlatformEventKind.TextInput:
                // ⚠ The grave that opened the panel arrives here too on some platforms, as the
                // character it produced. Dropping it is why the panel does not open with a backtick
                // already typed into it.
                if (platformEvent.Text is not "`" and not "~") {
                    console.Type(platformEvent.Text);
                }

                return true;

            case PlatformEventKind.KeyDown:
                switch (platformEvent.Key) {
                    case Key.Backspace:
                        console.Backspace();
                        break;

                    case Key.Enter or Key.KeypadEnter:
                        console.Submit();
                        break;

                    case Key.Tab:
                        console.Complete();
                        break;

                    case Key.Up:
                        console.Recall(back: true);
                        break;

                    case Key.Down:
                        console.Recall(back: false);
                        break;

                    case Key.Escape:
                        console.Enabled = false;

                        if (activatedTextInput) {
                            Services.Platform.TextInput.Deactivate();
                            activatedTextInput = false;
                        }

                        break;

                    default:
                        break;
                }

                return true;

            // ⚠ Swallowed as well, and this is the half that is easy to leave out: a key-up the game
            // never saw the key-down for leaves an action latched on for ever, which is a player who
            // sprints until he next presses shift.
            case PlatformEventKind.KeyUp:
            case PlatformEventKind.TextEditing:
                return true;

            default:
                return false;
        }
    }

    /// <summary>The key that opens the console — both of them, and the second is not a courtesy.</summary>
    /// <remarks>
    ///     ⚠ <b>The physical key below <kbd>Esc</kbd> is <see cref="Key.Grave" /> on an ANSI keyboard
    ///     and the one beside left shift is <see cref="Key.NonUsBackslash" /> on an ISO one, and on a
    ///     Mac the key that types a backtick is the <em>second</em> of those.</b> A check for
    ///     <c>Grave</c> alone opens the console for nobody in Europe, which is how the first run of
    ///     this on a real machine reported "the key does nothing" with every count reading correct —
    ///     a scancode is a position on a board, not a character, and the two keyboards disagree about
    ///     which position carries this one.
    /// </remarks>
    static bool IsConsoleKey(Key key) => key is Key.Grave or Key.NonUsBackslash;

    /// <summary>
    ///     Whether this host started text input, so that it only stops what it started.
    /// </summary>
    bool activatedTextInput;

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
