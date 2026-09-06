// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Engine.Diagnostics.Overlays;
using Vixen.Engine.Renderer;
using Vixen.Platform.Headless;
using Vixen.Rendering.Compositor;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     That a running game can reach the diagnostic overlays, the console and a drawn
///     <c>DebugDraw</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The state this closes: every one of those was built, tested and constructed by nothing
///         outside its own tests.</b> <c>DiagnosticOverlaySystem</c>, <c>DebugDrawRenderer</c>,
///         <c>FrameStatsOverlay</c>, <c>ConsoleOverlay</c> and water's six <c>water.show*</c> verbs
///         were complete at their own end and unreachable from a game — the same shape as the GPU
///         profiler that timed nothing for months.
///     </para>
///     <para>
///         ⚠ <b>A test asserting the objects exist proves the wiring and not the feature.</b> These
///         run the whole host against the Null backend, so the graph is built and the passes are
///         ordered with no GPU — which is everything except the pixels. The pixels are
///         <c>Vixen.Graphics.Golden.Tests</c>' <c>debug-overlay</c> and <c>debug-world</c>, and a run
///         of a real sample.
///     </para>
/// </remarks>
public sealed class HostedOverlayTests : IDisposable {
    readonly TemporaryFileSystemHost files = new();

    public void Dispose() => files.Dispose();

    /// <summary>Off unless asked for, which is what <c>GraphicsOptions.Overlays</c> promises.</summary>
    [Fact]
    public void AGameThatDidNotAskForThemHasNone() {
        using var application = Build(new SilentGame(), []);
        var graphics = application.Services.Graphics!;

        Assert.Null(graphics.Debug);
        Assert.Null(graphics.Overlays);
        Assert.Null(graphics.Console);
        Assert.Null(graphics.Renderer.Host.Debug);
    }

    /// <summary>
    ///     One flag, and the accumulator, the panels, the console and the node all exist.
    /// </summary>
    [Fact]
    public void TheFlagBuildsAllOfIt() {
        using var application = Build(new SilentGame(), ["--vixen-overlays"]);
        var graphics = application.Services.Graphics!;

        Assert.NotNull(graphics.Debug);
        Assert.NotNull(graphics.Overlays);
        Assert.NotNull(graphics.Console);
        Assert.NotNull(graphics.Renderer.Host.Debug);

        // The four panels doc 13 names for the frame itself: stats, the flame chart, the console and
        // the log tail. The last is added by AppBuilder, which owns the ring it reads.
        Assert.Contains(graphics.Overlays.Registered, overlay => overlay.Name == "stats");
        Assert.Contains(graphics.Overlays.Registered, overlay => overlay.Name == "framegraph");
        Assert.Contains(graphics.Overlays.Registered, overlay => overlay.Name == "console");
        Assert.Contains(graphics.Overlays.Registered, overlay => overlay.Name == "log");
    }

    /// <summary>
    ///     Doc 13's streaming panel is registered by the host, and says "not streaming" rather than
    ///     drawing zeroes on a target that has none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The claim that this overlay was blocked on an assembly boundary was wrong.</b>
    ///         The overview said the streaming panel needed <c>Vixen.Assets</c> to report and that it
    ///         may not reference <c>Vixen.Engine</c>, so it wanted a join assembly of its own. The
    ///         numbers are not <c>Vixen.Assets</c>' — its own README points at
    ///         <c>Vixen.Rendering</c>'s <c>PageResidency</c> — and the join assembly already exists
    ///         and already holds <c>GpuOverlay</c>. No project reference was added for this.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The registration is the half worth asserting</b>, because the panel being
    ///         complete and unreachable is precisely the shape this whole fixture was written for.
    ///         The Null backend has no bindless table, so no texture streamer is stood up — and a
    ///         panel of zeroes there would read as "streaming is idle and fine" rather than as "there
    ///         is no streamer". <c>ResidentFraction</c> of −1 is that distinction, asserted.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheStreamingPanelIsRegisteredAndSaysWhenThereIsNothingToReport() {
        using var application = Build(new SilentGame(), ["--vixen-overlays"]);
        var graphics = application.Services.Graphics!;

        var streaming = Assert.IsType<StreamingOverlay>(graphics.Overlays!.Find("streaming"));

        streaming.Enabled = true;

        application.Initialise();
        application.RunFrame();
        application.RunFrame();

        Assert.Equal(5, streaming.DrawnRows);
        Assert.Equal(-1f, streaming.ResidentFraction);
    }

    /// <summary>
    ///     ⚠ The instance the system pushes into is the instance the node drains.
    /// </summary>
    /// <remarks>
    ///     The trap this is here for is on the record and cost a session to find: the editor built a
    ///     <c>DiagnosticsModule</c> twice, the host fed one and the panels read the other, and what a
    ///     person saw was "No graphics device" beside a window Vulkan was drawing. Two
    ///     <c>DebugDraw</c>s here would fail exactly as quietly — an empty screen with every counter
    ///     reading as though it had worked.
    /// </remarks>
    [Fact]
    public void ThereIsOneAccumulatorAndTheNodeHasIt() {
        using var application = Build(new SilentGame(), ["--vixen-overlays"]);
        var graphics = application.Services.Graphics!;

        Assert.Same(graphics.Debug, graphics.Renderer.Host.Debug!.Draw);
    }

    /// <summary>
    ///     Water's six verbs are typable without the host naming water.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The count is the tell.</b> Water's were the only <c>[ConsoleCommand]</c>s in the tree
    ///     — not because nothing else wanted verbs, but because the only thing that could find an
    ///     attributed method was <c>RegisterFrom(Assembly)</c>, which is
    ///     <c>RequiresUnreferencedCode</c> and had no callers. <c>ConsoleCommands.Contribute</c> is
    ///     what makes the next subsystem's verbs arrive on their own, and this is the assertion that
    ///     it does.
    /// </remarks>
    [Fact]
    public void ASubsystemsVerbsArriveWithoutTheHostNamingIt() {
        using var application = Build(new SilentGame(), ["--vixen-overlays"]);
        var commands = application.Services.Graphics!.Console!;

        Assert.Contains(commands.Registered, command => command.Name == "water.showFlow");
        Assert.Contains(commands.Registered, command => command.Name == "water.showTiles");

        // And the registry's own two, which is what makes a panel switchable from the prompt.
        Assert.Contains(commands.Registered, command => command.Name == "overlay");
        Assert.Contains(commands.Registered, command => command.Name == "overlays");
    }

    /// <summary>Typing a verb changes the flag the renderer reads.</summary>
    [Fact]
    public void TypingAVerbMovesTheFlagBehindIt() {
        using var application = Build(new SilentGame(), ["--vixen-overlays"]);
        var console = (ConsoleOverlay)application.Services.Graphics!.Overlays!.Find("console")!;

        Rendering.Water.WaterDebug.Reset();

        console.Type("water.showFlow 1");
        Assert.True(console.Submit());
        Assert.True(Rendering.Water.WaterDebug.ShowFlow);

        console.Type("water.showFlow 0");
        Assert.True(console.Submit());
        Assert.False(Rendering.Water.WaterDebug.ShowFlow);
    }

    /// <summary>
    ///     ⚠ A frame's geometry survives long enough to be drawn.
    /// </summary>
    /// <remarks>
    ///     The bug this rules out is the one the plumbing was almost built with:
    ///     <c>DebugDrawSystem</c> ages in <c>PostRender</c>, and <c>VixenApplication</c> runs every
    ///     phase — <c>PostRender</c> included — <em>before</em> it records the frame. Ageing there
    ///     empties the accumulator between the overlay that filled it and the node that drains it,
    ///     and every count still reads correct. What this measures is the node's own: vertices
    ///     uploaded on a frame where a panel was on.
    /// </remarks>
    [Fact]
    public void APanelsGeometrySurvivesToTheNode() {
        using var application = Build(new SilentGame(), ["--vixen-overlays"]);
        var graphics = application.Services.Graphics!;

        application.Initialise();
        application.RunFrame();
        application.RunFrame();

        // FrameStatsOverlay is on by default and is a filled panel, a border and rows of text — all
        // of it screen-space line segments, which is the half a headless frame can still count.
        Assert.True(graphics.Renderer.Host.Debug!.ScreenCount > 0);
        Assert.Equal(0, graphics.Renderer.Host.Debug.Dropped);
    }

    /// <summary>
    ///     ⚠ And it survives a reload of the compositor, which is what <c>Samples/13</c> does.
    /// </summary>
    /// <remarks>
    ///     A host that appended the node to the tree the first build returned would lose it the
    ///     moment a game called <c>Host.Load</c> again — holding a node in a compositor the frame had
    ///     stopped drawing. Appending inside <c>Load</c> is what makes this hold.
    /// </remarks>
    [Fact]
    public void TheNodeSurvivesAFrameReload() {
        using var application = Build(new SilentGame(), ["--vixen-overlays"]);
        var graphics = application.Services.Graphics!;

        application.Initialise();
        application.RunFrame();

        graphics.Renderer.Host.Load(AppGraphics.DefaultFrame);

        application.RunFrame();
        application.RunFrame();

        Assert.True(graphics.Renderer.Host.Debug!.ScreenCount > 0);
        Assert.Contains(Walk(graphics.Renderer.Host.Compositor!.Game!), node => node is DebugOverlayRenderer);
    }

    /// <summary>
    ///     Doc 17 Q5b's second surface is raised: a Release build pointed at a directory carries a
    ///     standing notice a screenshot will show.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The negative is the half worth having.</b> A stamp that is set on every build
    ///         says nothing, and this is the wiring — not the drawing, which is
    ///         <c>Vixen.Engine.Tests</c>' <c>ALooseContentBuildIsStamped</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A notice on the registry, and no longer a property on the frame-statistics
    ///         panel</b> — see #919. The panel could be taken off the screen by four published calls
    ///         and by a write the registry never sees, after which the build was silent again.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ALooseContentBuildRaisesTheNotice() {
        var loose = Directory
            .CreateDirectory(Path.Combine(files.ApplicationDirectory, "..", "Loose"))
            .FullName;

        using var stamped = Build(new SilentGame(), ["--vixen-overlays", "--vixen-loose-content", loose]);
        stamped.Initialise();

        Assert.True(stamped.Services.Content.IsLoose);
        Assert.Contains("LOOSE", Assert.Single(Notices(stamped)), StringComparison.Ordinal);

        using var ordinary = Build(new SilentGame(), ["--vixen-overlays"]);
        ordinary.Initialise();

        Assert.False(ordinary.Services.Content.IsLoose);
        Assert.Empty(Notices(ordinary));
    }

    static IReadOnlyList<string> Notices(VixenApplication application) =>
        application.Services.Graphics!.Overlays!.Notices;

    static IEnumerable<SceneRenderer> Walk(SceneRenderer node) {
        yield return node;

        foreach (var child in node.Nested) {
            foreach (var nested in Walk(child)) {
                yield return nested;
            }
        }
    }

    VixenApplication Build(Game game, string[] extra) =>
        VixenApp.Create(["--vixen-workers", "1", "--vixen-frame-limit", "0", .. extra])
            .WithPlatform(new HeadlessPlatform(new HeadlessPlatformOptions { FileSystem = files }))
            .Build(game);

    class SilentGame : Game {
        protected internal override void OnConfigure(AppConfig config) => config.Window = null;
    }
}
