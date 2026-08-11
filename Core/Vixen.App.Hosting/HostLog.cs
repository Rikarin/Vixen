// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.IO;
using Vixen.Graphics;

namespace Vixen.App;

/// <summary>Everything the host logs, with the stable ids it logs them under.</summary>
/// <remarks>
///     Generated call sites rather than <c>logger.LogInformation(…)</c>: the interpolation and the
///     boxing of every argument happen only if the level is enabled, which for a line on a hot path
///     is the difference between free and not. Here it mostly buys the <em>ids</em> — a number in a
///     player's log survives the message being reworded, which is the whole argument for the
///     register in <c>docs/manual/log-events.md</c>.
/// </remarks>
static partial class HostLog {
    [LoggerMessage(
        EventId = 13001,
        Level = LogLevel.Information,
        Message = "Vixen {Variant} on {Platform}, {Workers} workers."
    )]
    public static partial void Started(ILogger logger, BuildVariant variant, string platform, int workers);

    [LoggerMessage(EventId = 13002, Level = LogLevel.Warning, Message = "No window: {Reason}")]
    public static partial void NoWindow(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 13003,
        Level = LogLevel.Warning,
        Message = "LOOSE CONTENT — reading from {Path} instead of bundles."
    )]
    public static partial void LooseContent(ILogger logger, VirtualPath path);

    [LoggerMessage(
        EventId = 13004,
        Level = LogLevel.Warning,
        Message = "Unrecognised engine argument {Argument} — it was ignored."
    )]
    public static partial void UnrecognisedArgument(ILogger logger, string argument);

    [LoggerMessage(EventId = 13005, Level = LogLevel.Information, Message = "Stopping after {Frames} frames.")]
    public static partial void Stopping(ILogger logger, long frames);

    [LoggerMessage(
        EventId = 13006,
        Level = LogLevel.Critical,
        Message = "The frame loop threw and the application is stopping."
    )]
    public static partial void FrameLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 13007,
        Level = LogLevel.Information,
        Message = "Content mounted from {Root}: {Addresses} addresses."
    )]
    public static partial void ContentMounted(ILogger logger, VirtualPath root, int addresses);

    /// <summary>
    ///     Information rather than a warning. An application with nothing to load is ordinary — a
    ///     sample, a batch tool, a test — but "my asset was not found" is a five-second diagnosis
    ///     with this line and an afternoon without it.
    /// </summary>
    [LoggerMessage(EventId = 13008, Level = LogLevel.Information, Message = "No content: {Reason}")]
    public static partial void NoContent(ILogger logger, string reason);

    /// <summary>
    ///     Said again every minute, because doc 17 Q5b's trade is only acceptable while it is
    ///     visible and one line at startup scrolls away.
    /// </summary>
    [LoggerMessage(
        EventId = 13009,
        Level = LogLevel.Warning,
        Message = "LOOSE CONTENT — still reading from {Path} instead of bundles."
    )]
    public static partial void LooseContentStill(ILogger logger, VirtualPath path);

    [LoggerMessage(
        EventId = 13010,
        Level = LogLevel.Information,
        Message = "Graphics on {Adapter} ({Kind}), {Width}×{Height}."
    )]
    public static partial void GraphicsStarted(ILogger logger, string adapter, AdapterKind kind, int width, int height);

    /// <summary>
    ///     The line that says a chosen number was reinterpreted.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>WindowOptions.Size</c> is in logical points and a swapchain is in physical pixels,
    ///         which is the engine's deliberate choice and documented on the property. The consequence
    ///         is not: on a 2× display a game that asked for 1600×900 renders 3200×1800 — four times
    ///         the pixels, and rather more than four times the cost of the screen-space passes, whose
    ///         rays also march twice as far in texels.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="GraphicsStarted" /> reports the result, not that it differs from the
    ///         request.</b> Nothing else in the run does either, so the only way to discover that the
    ///         frame is four times the size somebody picked is to notice the number and do the
    ///         division — which is exactly what nobody does, and which has already cost this repo an
    ///         afternoon once, when stand-in frame targets were imported at the logical size into a
    ///         retina frame and quarter-sized half the chain.
    ///     </para>
    ///     <para>
    ///         Information rather than a warning: every retina Mac and every 150% Windows desktop
    ///         says this, so a warning would be noise within a week. It is said once, at startup, and
    ///         not on every resize.
    ///     </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 13026,
        Level = LogLevel.Information,
        Message = "The window asked for {PointWidth}×{PointHeight} points and the display scale is "
            + "×{Scale}, so the frame is {PixelWidth}×{PixelHeight} — {Factor}× the pixels. What is "
            + "rendered is the frame document's to decide: scale its scene-sized resources, or lower "
            + "a !StandardFrame's resolution.renderScale."
    )]
    public static partial void FramebufferScaled(
        ILogger logger,
        int pointWidth,
        int pointHeight,
        float scale,
        int pixelWidth,
        int pixelHeight,
        float factor
    );

    /// <summary>
    ///     A warning rather than information, even though it is exactly what a dedicated server wants.
    ///     A head that asked for a window and is drawing into nothing has to say so — the same stance
    ///     the headless platform fallback takes, and for the same reason: the alternative is an
    ///     afternoon spent wondering why the window is black.
    /// </summary>
    [LoggerMessage(
        EventId = 13011,
        Level = LogLevel.Warning,
        Message = "Nothing will be presented: {Reason} The frame runs against the Null backend."
    )]
    public static partial void NoPresentingDevice(ILogger logger, string reason);

    [LoggerMessage(EventId = 13012, Level = LogLevel.Information, Message = "Shaders: {Variants} baked variants.")]
    public static partial void ShadersMounted(ILogger logger, int variants);

    /// <summary>
    ///     Information, because a project that has not captured a manifest yet is an ordinary
    ///     project — and the line that turns "every material draws as a miss" from a mystery into a
    ///     build step somebody has not run.
    /// </summary>
    [LoggerMessage(EventId = 13013, Level = LogLevel.Information, Message = "No baked shaders: {Reason}")]
    public static partial void NoShaders(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 13014,
        Level = LogLevel.Error,
        Message = "The graphics device was lost. Nothing more will be drawn this run."
    )]
    public static partial void DeviceLost(ILogger logger);

    [LoggerMessage(EventId = 13015, Level = LogLevel.Information, Message = "Compositor {Address} loaded.")]
    public static partial void CompositorLoaded(ILogger logger, string address);

    [LoggerMessage(
        EventId = 13016,
        Level = LogLevel.Warning,
        Message = "Compositor {Address} was not loaded ({Reason}) — the built-in frame is being used."
    )]
    public static partial void NoCompositor(ILogger logger, string address, string reason);

    /// <summary>Which layer supplied the project look — the document's inline one, or the host's.</summary>
    [LoggerMessage(EventId = 13023, Level = LogLevel.Information, Message = "Look profile {Source} applied.")]
    public static partial void LookApplied(ILogger logger, string source);

    /// <summary>
    ///     Warning rather than error, on <see cref="NoCompositor" />'s reasoning: a missing look is a
    ///     frame at the engine's neutral values, which draws — and looks exactly like a look nobody
    ///     wired, which is why the line exists.
    /// </summary>
    [LoggerMessage(
        EventId = 13024,
        Level = LogLevel.Warning,
        Message = "Look profile {Address} was not loaded ({Reason}) — the frame keeps its neutral values."
    )]
    public static partial void NoLook(ILogger logger, string address, string reason);

    /// <summary>
    ///     The failure that draws an empty window and reports nothing: a stage's index is assigned by
    ///     the render system when the document declares it, so a name the document does not have
    ///     leaves the extraction with a mask of none — every object extracted, none of them in any
    ///     pass.
    /// </summary>
    [LoggerMessage(
        EventId = 13017,
        Level = LogLevel.Warning,
        Message = "The compositor declares no stage called {Stage}, so nothing in the world will be drawn."
    )]
    public static partial void NoStage(ILogger logger, string stage);

    /// <summary>
    ///     One of the render graph's lint findings, said once per distinct finding. Every one
    ///     describes a frame that draws and quietly wastes or discards work — the class of
    ///     wrongness no exception ever reaches, which is exactly why it has to be a log line.
    /// </summary>
    [LoggerMessage(
        EventId = 13022,
        Level = LogLevel.Warning,
        Message = "{Finding}"
    )]
    public static partial void FrameLint(ILogger logger, string finding);

    /// <summary>
    ///     Whether the frame's passes are being timed, said once at startup. It is worth a line in
    ///     both directions: on, because a profiled frame is measurably not the frame that ships and
    ///     a reader comparing numbers has to know which one they have; off-because-unsupported,
    ///     because the alternative is an empty timeline and no reason for it.
    /// </summary>
    [LoggerMessage(
        EventId = 13025,
        Level = LogLevel.Information,
        Message = "GPU pass timing requested: {Attached} on '{Adapter}'."
    )]
    public static partial void GpuProfiling(ILogger logger, bool attached, string adapter);

    /// <summary>
    ///     That the diagnostic overlays are in this frame, with the two counts that say whether the
    ///     wiring reached anything. A build with the switch on and zero commands is a console that
    ///     will answer <c>help</c> and nothing else, which is worth knowing before somebody types a
    ///     subsystem's verb and concludes the subsystem is broken.
    /// </summary>
    [LoggerMessage(
        EventId = 13026,
        Level = LogLevel.Information,
        Message = "Diagnostic overlays on: {Panels} panel(s), {Commands} console command(s). "
            + "Press the grave key for the console; type 'overlays' to list them."
    )]
    public static partial void OverlaysEnabled(ILogger logger, int panels, int commands);

    [LoggerMessage(
        EventId = 13018,
        Level = LogLevel.Information,
        Message = "Startup scene {Address} loaded: {Entities} entities."
    )]
    public static partial void StartupSceneLoaded(ILogger logger, string address, int entities);

    /// <summary>
    ///     A warning, and one of the few here that is. Something asked for a level — a game in its
    ///     <c>OnConfigure</c>, a project's Build Settings, an operator's <c>--vixen-scene</c> — and it
    ///     did not arrive, so the window is empty for a reason nothing else in the log would give.
    /// </summary>
    [LoggerMessage(
        EventId = 13019,
        Level = LogLevel.Warning,
        Message = "The startup scene {Address} was not loaded ({Reason}) — the world is empty."
    )]
    public static partial void NoStartupScene(ILogger logger, string address, string reason);

    /// <summary>
    ///     Said whenever a build can download, because it is the line that turns a first-run stall
    ///     into an explanation: some of this game's content is not in the package, and the cache is
    ///     where it lands.
    /// </summary>
    [LoggerMessage(
        EventId = 13020,
        Level = LogLevel.Information,
        Message = "Remote content: {Bundles} downloadable bundle(s), cached under {Cache}."
    )]
    public static partial void RemoteContent(ILogger logger, int bundles, VirtualPath cache);

    /// <summary>
    ///     Doc 17's Editor variant, said once. A build reading an import's own artefacts is not a
    ///     shipping configuration, and a run whose content came from somebody's <c>Library/</c> has
    ///     to be identifiable as such in a log attached to a bug report.
    /// </summary>
    [LoggerMessage(
        EventId = 13021,
        Level = LogLevel.Information,
        Message = "Unpacked content: chunks read from the artefact store at {Root}, with nothing bundled."
    )]
    public static partial void UnpackedContent(ILogger logger, VirtualPath root);
}
