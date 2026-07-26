// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Platform;

namespace Vixen.App;

/// <summary>Everything the host decides before the first frame.</summary>
/// <remarks>
///     Handed to <see cref="Game.OnConfigure" /> already filled in with the defaults and with any
///     command-line overrides applied, so a game changes what it cares about and leaves the rest.
///     Mutable and then frozen: after <c>OnConfigure</c> returns, the host reads it and nothing
///     writes to it again.
/// </remarks>
public sealed class AppConfig {
    /// <summary>The name used for the window, the log category and the data directories.</summary>
    public string Name { get; set; } = "Vixen";

    /// <summary>The publisher name used to place <c>/data</c> and <c>/cache</c>.</summary>
    public string Organisation { get; set; } = "Vixen";

    /// <summary>Which build this is.</summary>
    /// <remarks>Resolved before <see cref="Game.OnConfigure" /> and writable there, because a tool
    /// that is always a server should be able to say so rather than requiring a flag.</remarks>
    public BuildVariant Variant { get; set; } = BuildVariant.Debug;

    /// <summary>The window to open, or <see langword="null" /> for a head with no window at all.</summary>
    /// <remarks>
    ///     Null is not the same as headless. A headless <em>platform</em> still creates windows —
    ///     they are how the frame loop stays the same shape — whereas this being null means the
    ///     application does not want one, which is what a batch tool wants.
    /// </remarks>
    public WindowOptions? Window { get; set; } = new();

    /// <summary>Whether to run without a display server even where one exists.</summary>
    public bool Headless { get; set; }

    /// <summary>
    ///     How many job-system workers to start, or <c>null</c> for one per available processor
    ///     less the main thread.
    /// </summary>
    /// <remarks>
    ///     <c>0</c> is a supported and tested value: <c>docs/plan/10</c> requires every subsystem to
    ///     work with no workers, because the browser without <c>SharedArrayBuffer</c> has none.
    /// </remarks>
    public int? WorkerCount { get; set; }

    /// <summary>
    ///     Frames per second to aim for, or <c>0</c> for as fast as the machine will go.
    /// </summary>
    /// <remarks>
    ///     Defaulted rather than unlimited, and it matters today more than it will: with no
    ///     swapchain there is no vsync, so an uncapped loop spins a core at 100 % to draw nothing.
    ///     Once a graphics backend is present, presentation is what paces a windowed frame and this
    ///     becomes the cap that applies when vsync is off or there is no window.
    /// </remarks>
    public int FrameRateLimit { get; set; } = 60;

    /// <summary>
    ///     Frames per second to aim for while no window has focus, or <c>0</c> to keep running at
    ///     <see cref="FrameRateLimit" />.
    /// </summary>
    /// <remarks>
    ///     A game alt-tabbed away is a game whose fans should stop. Ten is enough to stay
    ///     responsive to the click that comes back.
    /// </remarks>
    public int UnfocusedFrameRateLimit { get; set; } = 10;

    /// <summary>Whether closing the last window ends the application.</summary>
    /// <remarks>
    ///     True is right for a game and for most tools. False is right for something that lives in a
    ///     tray or reopens its window on demand, and for a server, which has no windows to close.
    /// </remarks>
    public bool ExitWhenAllWindowsClose { get; set; } = true;

    /// <summary>The lowest level the log ring keeps.</summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    /// <summary>
    ///     A directory of loose content to read instead of bundles, from
    ///     <c>--vixen-loose-content</c>.
    /// </summary>
    /// <remarks>Honoured by the content system in Phase 3. See
    /// <see cref="AppArguments.LooseContentPath" /> for why it is parsed now.</remarks>
    public string? LooseContentPath { get; set; }

    /// <summary>Which SDL video driver to insist on, or <see langword="null" /> to let it choose.</summary>
    public string? VideoDriver { get; set; }

    /// <summary>The arguments the host did not consume.</summary>
    public IReadOnlyList<string> Arguments { get; internal set; } = [];

    /// <summary>
    ///     <c>--vixen-*</c> arguments the host did not recognise.
    /// </summary>
    /// <remarks>
    ///     Warned about at startup rather than ignored. A typo in a launch script that silently does
    ///     nothing is how a QA build runs for a week without the profiler somebody thought they had
    ///     switched on.
    /// </remarks>
    public IReadOnlyList<string> UnrecognisedArguments { get; internal set; } = [];

    /// <summary>
    ///     Why the host fell back to the headless platform, or <see langword="null" /> if it did not.
    /// </summary>
    /// <remarks>
    ///     Recorded rather than only logged, so a diagnostic overlay and a crash report can both say
    ///     "this build wanted a window and did not get one" without re-deriving it.
    /// </remarks>
    public string? HeadlessFallbackReason { get; internal set; }

    /// <summary>Applies the command line over the defaults.</summary>
    /// <param name="arguments">What <see cref="AppArguments.Parse" /> understood.</param>
    /// <remarks>
    ///     Before <see cref="Game.OnConfigure" />, so a game sees what the operator asked for and
    ///     can still override it — which is the right order: a game that hard-codes a frame cap
    ///     means it, and a game that reads <see cref="FrameRateLimit" /> to build a settings menu
    ///     needs the operator's value in it.
    /// </remarks>
    public void Apply(AppArguments arguments) {
        ArgumentNullException.ThrowIfNull(arguments);

        Variant = BuildVariants.Detect(arguments.Variant);
        Headless = arguments.Headless || Variant.IsHeadless();
        Arguments = arguments.Remaining;
        UnrecognisedArguments = arguments.Unrecognised;

        if (arguments.WorkerCount is { } workers) {
            WorkerCount = workers;
        }

        if (arguments.FrameRateLimit is { } limit) {
            FrameRateLimit = limit;
        }

        if (arguments.LogLevel is { } level) {
            LogLevel = level;
        }

        if (arguments.VideoDriver is { } driver) {
            VideoDriver = driver;
        }

        if (arguments.LooseContentPath is { } path) {
            LooseContentPath = path;
        }
    }
}
