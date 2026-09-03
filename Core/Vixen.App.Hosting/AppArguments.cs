// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Vixen.App;

/// <summary>The <c>--vixen-*</c> arguments the host understands, and everything else untouched.</summary>
/// <remarks>
///     <para>
///         One prefix, reserved for the engine, so an application can define whatever arguments it
///         likes without a collision and without the host needing to know about them. Anything not
///         starting with <c>--vixen-</c> comes back in <see cref="Remaining" /> in its original
///         order.
///     </para>
///     <para>
///         Unrecognised <c>--vixen-*</c> arguments are collected rather than ignored. A typo in a
///         launch script that silently does nothing is how a QA build runs for a week without the
///         profiler somebody thought they had enabled.
///     </para>
/// </remarks>
public sealed record AppArguments {
    /// <summary>Arguments the host did not consume, in order.</summary>
    public IReadOnlyList<string> Remaining { get; private init; } = [];

    /// <summary>
    ///     <c>--vixen-*</c> arguments the host does not recognise, with their values.
    /// </summary>
    public IReadOnlyList<string> Unrecognised { get; private init; } = [];

    /// <summary>The variant named by <c>--vixen-variant</c>, or <see langword="null" />.</summary>
    public BuildVariant? Variant { get; private init; }

    /// <summary>Whether <c>--vixen-headless</c> was given.</summary>
    public bool Headless { get; private init; }

    /// <summary>Whether <c>--vixen-offscreen</c> was given.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>"A real device, no window, no picture" — in as many words.</b> It is the second
    ///         statement of intent <see cref="CapturePath" /> used to be the only one of. A run that
    ///         wants counters rather than a photograph — <c>VIXEN_VSMTRACE</c>, GPU timings, a CSV —
    ///         had to name a capture directory it would never open, because asking for a picture was
    ///         the only sentence <c>GraphicsHost</c> accepted as "yes, I know there is no surface,
    ///         open the device anyway".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It lifts the no-surface refusal; it does not relax it.</b> The refusal itself is
    ///         load-bearing — without it a dedicated server, which asks for no window, would stop
    ///         running on the device that draws nothing and start needing a driver
    ///         (<a href="../../docs/plan/17-app-heads-and-shipping.md">doc 17</a>). A server that
    ///         does not type this flag is unaffected, which is the whole design.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it makes the Null fall-through a boot failure rather than a quiet
    ///         downgrade.</b> The failure this flag exists to end is a run that opens the device
    ///         drawing nothing and prints counters indistinguishable from a healthy one, so a
    ///         request for a real device that cannot be met has to stop the run — including when
    ///         <see cref="GraphicsBackend.Null" /> was named in <c>--vixen-backend</c>. See
    ///         <see cref="GraphicsOptions.Offscreen" /> for why there is no escape hatch.
    ///     </para>
    /// </remarks>
    public bool Offscreen { get; private init; }

    /// <summary>Whether <c>--vixen-gpu-profile</c> was given.</summary>
    /// <remarks>
    ///     Turns on <see cref="GraphicsOptions.GpuProfiling" />, which times every render-graph pass.
    ///     A flag rather than a setting because the thing it is most often wanted for is one run —
    ///     "why is this frame slow" — and because a profiled frame is not the frame that ships: on a
    ///     tiler the query writes can force tile resolves, so a project that turned this on in its
    ///     configuration would be shipping a cost nobody could account for.
    /// </remarks>
    public bool GpuProfiling { get; private init; }

    /// <summary>Whether <c>--vixen-overlays</c> was given.</summary>
    /// <remarks>
    ///     Turns on <see cref="GraphicsOptions.Overlays" />: the frame-stats panel, the console, the
    ///     log tail, the mini flame chart, and a frame's <c>DebugDraw</c> actually drawn. A flag for
    ///     <see cref="GpuProfiling" />'s reason turned round — this is wanted on the run where
    ///     something is already wrong, on a build that has already shipped, by somebody who cannot
    ///     rebuild it.
    /// </remarks>
    public bool Overlays { get; private init; }

    /// <summary>The panel names from <c>--vixen-overlay a,b</c>, which also implies the above.</summary>
    /// <remarks>
    ///     What a headless capture needs and the console cannot give it: <c>--vixen-overlays</c>
    ///     leaves every panel but the frame stats switched off, which is right for somebody at a
    ///     keyboard and useless to a run with nobody typing. See
    ///     <see cref="GraphicsOptions.EnabledOverlays" />.
    /// </remarks>
    public IReadOnlyList<string> EnabledOverlays { get; private init; } = [];

    /// <summary>The SDL video driver named by <c>--vixen-video-driver</c>, or
    /// <see langword="null" />.</summary>
    public string? VideoDriver { get; private init; }

    /// <summary>The worker count from <c>--vixen-workers</c>, or <see langword="null" />.</summary>
    public int? WorkerCount { get; private init; }

    /// <summary>Whether <c>--vixen-pin-workers</c> was given.</summary>
    /// <remarks>
    ///     ⚠ A flag rather than a default because pinning is a pessimisation on a machine that is
    ///     running anything else: a pinned worker waits behind whatever the OS put on its core
    ///     instead of being migrated off it. It is worth asking for on a console or a dedicated
    ///     server, and on a desktop it is a measurement to take rather than an assumption to ship.
    /// </remarks>
    public bool PinWorkers { get; private init; }

    /// <summary>The frame cap from <c>--vixen-frame-limit</c>, or <see langword="null" />.</summary>
    public int? FrameRateLimit { get; private init; }

    /// <summary>How many frames to run before stopping, from <c>--vixen-frames</c>.</summary>
    /// <remarks>
    ///     What makes a windowed application runnable in CI. It cannot assert what was drawn, but it
    ///     can prove the whole stack starts, presents and shuts down without a validation error or a
    ///     hang — which is most of what a smoke test is for, and is the only automated coverage the
    ///     swapchain's acquire and present path can have
    ///     ([10](../../docs/plan/10-platforms.md) § macOS).
    /// </remarks>
    public int? MaxFrames { get; private init; }

    /// <summary>
    ///     How long every frame is told it took, from <c>--vixen-fixed-step</c>, or
    ///     <see langword="null" /> for the wall clock.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <see cref="AppConfig.FixedStep" />.</b> That one is how long a
    ///         <em>simulation</em> step covers, and the number of them a frame owes still comes from
    ///         how much time passed. This replaces "how much time passed" — every frame is told the
    ///         same delta whatever the machine was doing — which is what makes frame <em>N</em> of a
    ///         run mean the same instant of simulated time on every run of the same build.
    ///     </para>
    ///     <para>
    ///         <b>What it is for is the A/B.</b> Without it two headless runs at the same
    ///         <c>--vixen-frames</c> land the camera about a centimetre apart, which is four pixels,
    ///         which saturates a per-pixel diff so completely that two runs of identical code differ
    ///         by more pixels than two consecutive frames of one run. A picture that cannot be
    ///         compared to another picture is not much of a measurement.
    ///     </para>
    ///     <para>
    ///         In seconds. <c>0</c> turns it off again, which is the value a game that set one in
    ///         <c>OnConfigure</c> needs in order to say "not this run".
    ///     </para>
    /// </remarks>
    public TimeSpan? FixedFrameTime { get; private init; }

    /// <summary>
    ///     The directory from <c>--vixen-capture</c> to write the last frame into, or
    ///     <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What makes a sample's picture a file rather than a screenshot. The alternative — run
    ///         it, raise its window, grab the display — cannot be done twice at once, fights every
    ///         convergence the frame has, and produces a different picture each time; an agent got
    ///         the <em>sign</em> of a lighting change wrong that way and blamed the sun.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It says "I want a picture", and that is what waives the no-surface refusal.</b>
    ///         <c>GraphicsHost</c> makes Vulkan decline when there is nothing to present to, on
    ///         purpose: without it a dedicated server would silently stop running on the device that
    ///         draws nothing and start needing a driver. This flag is the operator saying the
    ///         opposite in as many words, so it is the one thing allowed to turn that refusal off.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Wants <c>--vixen-frames</c> beside it.</b> The picture written is the last
    ///         frame's, and a run with no frame count has no last frame — the host says so at
    ///         startup rather than exiting with an empty directory.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It turns the clock fixed</b> — see <see cref="FixedFrameTime" />, which this
    ///         defaults to a sixtieth of a second unless the command line or the game says otherwise.
    ///         Asking for a picture is asking for one that can be compared with another, and a
    ///         wall-clock delta means frame 512 is a different instant on every run.
    ///     </para>
    /// </remarks>
    public string? CapturePath { get; private init; }

    /// <summary>The backend order from <c>--vixen-backend</c>, or empty for none given.</summary>
    /// <remarks>
    ///     <para>
    ///         Comma-separated and ordered, most preferred first: <c>--vixen-backend vulkan,null</c>
    ///         says "Vulkan, and if that will not open, the device that draws nothing".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One unreadable name rejects the whole argument</b> rather than silently dropping
    ///         the entry. Half-applying a preference list is the worst of the three options: an
    ///         operator debugging <c>--vixen-backend vulkan,nul</c> would be handed Vulkan-only
    ///         behaviour with no hint that anything was wrong, and the missing fallback would only
    ///         show up on the machine that needed it.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<GraphicsBackend> Backends { get; private init; } = [];

    /// <summary>The level from <c>--vixen-log-level</c>, or <see langword="null" />.</summary>
    public LogLevel? LogLevel { get; private init; }

    /// <summary>
    ///     The directory for rolling log files, from <c>--vixen-log-file</c>, or
    ///     <see langword="null" /> for no file at all.
    /// </summary>
    /// <remarks>
    ///     A directory rather than a file name, because the sink rolls by day and by size and
    ///     therefore owns the names. Off unless asked for: a game that writes a log file nobody
    ///     collects has bought disk churn and nothing else, whereas a dedicated server and a
    ///     playtest build both want one and know they do.
    /// </remarks>
    public string? LogFilePath { get; private init; }

    /// <summary>
    ///     The directory from <c>--vixen-loose-content</c>, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     <c>docs/plan/17</c> Q5b: a release build may be pointed at loose files, deliberately, so
    ///     that a bug reproducible only in a shipping configuration can be poked at. The trade is
    ///     that "release reads only bundles" stops being an invariant, so it is not allowed to be
    ///     quiet — a build running this way warns on a timer and stamps its diagnostic overlay and
    ///     crash reports. The content system that honours it arrives in Phase 3; the argument is
    ///     parsed here because the host's argument contract is the host's to define, and adding it
    ///     later would be a breaking change to a command line people had already scripted.
    /// </remarks>
    public string? LooseContentPath { get; private init; }

    /// <summary>
    ///     The scene address from <c>--vixen-scene</c>, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     What makes "start this build in that level" a thing an operator can say without a rebuild
    ///     — a QA note that reproduces on the third map, a bisect over which level regressed. It
    ///     overrides what the content build listed and is itself overridden by a game that names a
    ///     scene in <c>OnConfigure</c>, which is the order every other argument here takes and for the
    ///     reason <see cref="AppConfig.Apply" /> gives: a game that hard-codes something means it.
    /// </remarks>
    public string? StartupScene { get; private init; }

    /// <summary>Reads the <c>--vixen-*</c> arguments out of a command line.</summary>
    /// <param name="arguments">The process arguments, without the executable name.</param>
    /// <returns>What the host understood, and what it left alone.</returns>
    public static AppArguments Parse(IReadOnlyList<string>? arguments) {
        if (arguments is null || arguments.Count == 0) {
            return new();
        }

        var remaining = new List<string>();
        var unrecognised = new List<string>();
        var parsed = new AppArguments();

        for (var index = 0; index < arguments.Count; index++) {
            var argument = arguments[index];

            if (!argument.StartsWith("--vixen-", StringComparison.Ordinal)) {
                remaining.Add(argument);
                continue;
            }

            // `--vixen-x=y` and `--vixen-x y` both work: the first is what a container's command
            // line looks like and the second is what a person types.
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? argument : argument[..separator];
            var inlineValue = separator < 0 ? null : argument[(separator + 1)..];

            switch (name) {
                case "--vixen-headless":
                    parsed = parsed with { Headless = true };
                    continue;

                // Deliberately does not imply --vixen-headless. The two are orthogonal: this one is
                // about which device opens, that one about whether a display server is used at all,
                // and a run under Xvfb wants the first without the second.
                case "--vixen-offscreen":
                    parsed = parsed with { Offscreen = true };
                    continue;

                case "--vixen-gpu-profile":
                    parsed = parsed with { GpuProfiling = true };
                    continue;

                case "--vixen-overlays":
                    parsed = parsed with { Overlays = true };
                    continue;

                // Implies --vixen-overlays: asking for a panel by name and then not getting the
                // registry that holds it is a command line that is right and does nothing.
                case "--vixen-overlay" when Take(out var panels):
                    parsed = parsed with {
                        Overlays = true,
                        EnabledOverlays = [
                            .. parsed.EnabledOverlays,
                            .. panels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        ]
                    };

                    continue;

                case "--vixen-variant" when Take(out var variant) && Enum.TryParse<BuildVariant>(variant, true, out var value):
                    parsed = parsed with { Variant = value };
                    continue;

                case "--vixen-video-driver" when Take(out var driver):
                    parsed = parsed with { VideoDriver = driver };
                    continue;

                case "--vixen-workers" when Take(out var workers) && int.TryParse(workers, out var count):
                    parsed = parsed with { WorkerCount = Math.Max(0, count) };
                    continue;

                case "--vixen-pin-workers":
                    parsed = parsed with { PinWorkers = true };
                    continue;

                case "--vixen-frame-limit" when Take(out var limit) && int.TryParse(limit, out var frames):
                    parsed = parsed with { FrameRateLimit = Math.Max(0, frames) };
                    continue;

                case "--vixen-frames" when Take(out var total) && int.TryParse(total, out var count2):
                    parsed = parsed with { MaxFrames = Math.Max(0, count2) };
                    continue;

                case "--vixen-log-level" when Take(out var level) && Enum.TryParse<LogLevel>(level, true, out var parsedLevel):
                    parsed = parsed with { LogLevel = parsedLevel };
                    continue;

                case "--vixen-log-file" when Take(out var logPath):
                    parsed = parsed with { LogFilePath = logPath };
                    continue;

                case "--vixen-loose-content" when Take(out var path):
                    parsed = parsed with { LooseContentPath = path };
                    continue;

                case "--vixen-capture" when Take(out var capture):
                    parsed = parsed with { CapturePath = capture };
                    continue;

                // Seconds, and a negative one is a typo rather than a request to run time backwards.
                case "--vixen-fixed-step" when Take(out var step)
                    && double.TryParse(step, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && seconds >= 0d:
                    parsed = parsed with { FixedFrameTime = TimeSpan.FromSeconds(seconds) };
                    continue;

                case "--vixen-scene" when Take(out var scene):
                    parsed = parsed with { StartupScene = scene };
                    continue;

                case "--vixen-backend" when Take(out var backends) && TryBackends(backends, out var order):
                    parsed = parsed with { Backends = order };
                    continue;

                default:
                    unrecognised.Add(argument);
                    continue;
            }

            bool Take(out string taken) {
                if (inlineValue is not null) {
                    taken = inlineValue;
                    return true;
                }

                if (index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal)) {
                    taken = arguments[++index];
                    return true;
                }

                taken = string.Empty;
                return false;
            }
        }

        return parsed with { Remaining = remaining, Unrecognised = unrecognised };

        // ⚠ `Unknown` is rejected along with anything unparseable. It is a real member of the enum
        // so that a zeroed value is not silently a backend, which means `Enum.TryParse` accepts the
        // literal string "unknown" — and a preference list containing "not a backend" is a typo
        // every time.
        static bool TryBackends(string value, out IReadOnlyList<GraphicsBackend> order) {
            var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsed = new List<GraphicsBackend>(names.Length);

            foreach (var name in names) {
                if (!Enum.TryParse<GraphicsBackend>(name, ignoreCase: true, out var backend)
                    || backend == GraphicsBackend.Unknown) {
                    order = [];
                    return false;
                }

                parsed.Add(backend);
            }

            order = parsed;
            return parsed.Count > 0;
        }
    }
}
