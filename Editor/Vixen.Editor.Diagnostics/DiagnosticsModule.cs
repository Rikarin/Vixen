// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Debugger;
using Vixen.Editor.Plugin;
using Vixen.Editor.Profiler;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Graphics;
using Vixen.Net.Diagnostics;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Vixen.Net.Transport.Local;
using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Diagnostics;

/// <summary>Doc 20's E4: the seven diagnostics panels, and what they are pointed at.</summary>
/// <remarks>
///     <para>
///         <b>The joining job, in the assembly whose job that is.</b> Neither
///         <c>Vixen.Editor.Profiler</c> nor <c>Vixen.Editor.Debugger</c> knows what a project, a
///         scene or a graphics device is — which is what lets both be tested against a bare
///         <c>UiDocument</c>. Deciding that the statistics panel counts <i>this</i> world, that the
///         GPU timeline reads <i>this</i> device and that the frame debugger captures <i>this</i>
///         frame is this type's.
///     </para>
///     <para>
///         ⚠ <b>A third assembly rather than a move into either of them, and that is the finding.</b>
///         Doc 36 § P3 says the built-ins go through the plugin API; for these two, doing it by
///         putting the joining code inside them would have spent the property their own remarks name.
///         The module is what a feature looks like when its parts are deliberately ignorant of each
///         other.
///     </para>
///     <para>
///         ⚠ <b>Sampling is off until somebody presses Record.</b> An always-on profiler is the right
///         default for a <i>game</i>, where the interesting thirty seconds are the ones before the
///         crash; an editor left running for a day would fill sixteen thread rings with a day of menu
///         clicks that nobody will ever collect. <c>ProfilerModel.Start</c> turns it on and empties
///         the rings first, which is what makes a capture start where the button was pressed.
///     </para>
/// </remarks>
public sealed class DiagnosticsModule : IEditorPlugin, IDisposable {
    /// <summary>What the host activates it under, and what a plugin depending on it names.</summary>
    public const string ModuleId = "vixen.diagnostics";

    /// <summary>What a plugin-management panel calls it.</summary>
    public const string ModuleName = "Diagnostics";

    /// <summary>The context id of the diagnostics panels.</summary>
    /// <remarks>
    ///     Leaving a context matters as much as entering one, so clicking a flame bar has to stop
    ///     Delete meaning "delete the selected entity".
    /// </remarks>
    public const string DiagnosticsContext = "diagnostics";

    readonly ProfilerModel profiler = new();
    readonly DeviceManager devices = new();

    /// <summary>What the remote inspector talks over, once somebody attaches.</summary>
    /// <remarks>
    ///     ⚠ <b>Made on attach rather than at start-up, and it is a loopback transport.</b> The
    ///     editor cannot open a socket to a phone until there is a device provider that knows how to
    ///     find one — see <c>IDeviceProvider</c>, which has no <c>adb</c> implementation yet — and a
    ///     UDP client pointed at nothing would sit retrying for the life of the editor.
    /// </remarks>
    LocalNetwork? inspectorNetwork;
    LocalTransport? inspectorTransport;
    RemoteInspectorClient? remoteInspector;

    EditorShell shell = null!;
    EditorProject project = null!;
    IActiveScene scenes = null!;

    /// <summary>Whoever in this editor can build a player and put it on a device.</summary>
    /// <remarks>
    ///     ⚠ <b>Optional, and the button says why when it is absent.</b> Building a player is a
    ///     project, a target, a content build and a process; a host that publishes no deployer is one
    ///     where Deploy is greyed with a sentence rather than one where the panel is missing.
    ///     <c>TryGet</c> rather than <c>Require</c> for exactly that reason — see
    ///     <c>PluginServices.TryGet</c>, whose own remark is about a plugin that should still install
    ///     half of itself.
    /// </remarks>
    IDeviceDeploy? deployer;

    /// <summary>The provider standing for the machine the editor is on, once it has activated.</summary>
    /// <remarks>
    ///     Held so that <see cref="InspectorEndpoint" /> can reach it. Null before
    ///     <see cref="Activate" />, which is when the manager is given its providers.
    /// </remarks>
    LocalDeviceProvider? localDevice;

    string? inspectorEndpoint;

    Func<FrameCapture>? frameCaptureSource;

    /// <summary>The frame debugger this session built, so a late capture source can reach it.</summary>
    /// <remarks>
    ///     ⚠ <b>The one panel this module holds, and every other one is a delegate for the reason
    ///     this field needs a guard.</b> A panel's factory runs again on every reopen, so a held view
    ///     outlives the panel it was drawn into — which is why the assignment below is made through
    ///     <see cref="Restate(FrameDebuggerView)" />, which ignores a view the workspace has already
    ///     torn down. The alternative — a delegate the panel pulls through — cannot work here: what the panel reads
    ///     is whether the source is <em>null</em>, and a wrapper that defers to this property is
    ///     never null whatever the host has.
    /// </remarks>
    FrameDebuggerView? frames;

    /// <summary>The GPU timeline this session built, so a late device can reach it.</summary>
    /// <remarks>
    ///     The same field, the same guard and the same reason as <see cref="frames" /> — see
    ///     <see cref="GraphicsDevice" /> for why a device is always late.
    /// </remarks>
    GpuTimelineView? timeline;

    IGraphicsDevice? graphicsDevice;

    /// <summary>The device the GPU timeline reads, when the host has one.</summary>
    /// <remarks>
    ///     <para>
    ///         Assigned by the host once Vulkan is up, which is several frames after this object
    ///         exists — a headless run never assigns it at all, and the panel says the device cannot
    ///         be timed rather than drawing an empty chart.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And "several frames after" is why this is not an auto-property</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1231">#1231</a>). The panel's factory
    ///         read it once, at build, and a restored layout opens the Profiling group — which names
    ///         <c>gpu</c> — before <c>EditorHost.EnsureDevice</c> has one to give. So every session
    ///         kept "No graphics device" beside a window Vulkan was plainly drawing, and it is not
    ///         cosmetic: <c>GpuTimelineView</c> returns <c>GpuChart.Empty</c> from its measure while
    ///         <c>Unavailable</c> is non-null, so the timeline drew nothing at all. The setter
    ///         restates the panel, and because <c>EditorDiagnostics.GraphicsDevice</c> is also the one
    ///         place a device is <em>lost</em>, the sentence comes back on its own when it goes.
    ///     </para>
    /// </remarks>
    public IGraphicsDevice? GraphicsDevice {
        get => graphicsDevice;
        set {
            graphicsDevice = value;
            Restate(timeline);
        }
    }

    /// <summary>The frame's GPU regions, as the host resolved them.</summary>
    /// <remarks>
    ///     A property rather than a <c>GpuProfiler</c> this class owns, because the object that
    ///     records the timestamps has to be the one recording the frame — which is the host.
    /// </remarks>
    public GpuFrame GpuFrame { get; set; } = GpuFrame.Empty;

    /// <summary>What a frame capture is taken from, when the host can take one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null on a Vulkan host, and that is the honest state.</b> Doc 20's E4 names
    ///         <c>Vixen.Graphics.Null</c>'s recorder as the shape a capture takes, and it is the only
    ///         recording path the engine has — the Vulkan backend records into a command buffer and
    ///         keeps nothing. The panel says so rather than offering a button that would do nothing.
    ///         ⚠ Which means <b>no editor Vixen has ever shipped sets this</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1208">#1208</a>): the Frame Debugger
    ///         is Unavailable in every one of them, and will be until either a Vulkan command-stream
    ///         hook exists (doc 13) or a host runs a play session on the Null device. The sentence
    ///         the panel shows is written for that state and not for an oversight.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it no longer has to be set before the panel is opened, which is the same
    ///         hazard <see cref="InspectorEndpoint" /> had and the same fix.</b> The panel's factory
    ///         reads this once, so a host that acquires a capture path after start-up — and every
    ///         host would, since a device arrives several frames in and a restored layout opens the
    ///         panel before that — used to leave a permanently greyed Capture button behind a
    ///         sentence saying the editor could not capture. <c>FrameDebuggerView.Source</c> and
    ///         <c>.Unavailable</c> are signal-backed precisely so a late assignment can reach them,
    ///         and this is what does the assigning.
    ///     </para>
    /// </remarks>
    public Func<FrameCapture>? FrameCaptureSource {
        get => frameCaptureSource;
        set {
            frameCaptureSource = value;
            Restate(frames);
        }
    }

    /// <summary>Rows the statistics panel shows that a world walk cannot produce.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Read at every <c>Refresh</c>, like every other source here, so what it answers with
    ///         is whatever is true when the button is pressed.</b> Empty is the ordinary answer for a
    ///         host that counts nothing of its own, and the panel then shows exactly the traversal it
    ///         always did.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it exists for is the behaviour population</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1216">#1216</a>).
    ///         <c>BehaviorStore.Population</c> answers doc 04's authoring rule — "one instance, or a
    ///         handful" against "many instances, the same operation over all of them" — and its only
    ///         reader was <c>vixen doctor behaviors</c>, which counts what a <em>scene authors</em>
    ///         and is therefore zero in every committed scene in this repository. The number the rule
    ///         is about is a run-time one, and the two things that can honestly produce it — which
    ///         store is live, and whether a play session is running — are both the application's and
    ///         neither is this module's.
    ///     </para>
    /// </remarks>
    public Func<IReadOnlyList<StatisticRow>>? SceneFacts { get; set; }

    /// <summary>Where a standalone play-mode process would listen for an inspector.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null on a bare editor, and that is the honest state</b> — the same shape
    ///         <see cref="FrameCaptureSource" /> and <see cref="NetworkLedger" /> are in, and it read
    ///         as an oversight only because it was the one of the three with no remark saying so.
    ///         Nothing in this tree sets it because nothing in this tree launches a player:
    ///         <c>EditorParity</c> declares <c>play.mode-standalone</c> as planned — <i>"Launching a
    ///         standalone player from the editor needs the build settings window. Milestone E6."</i> —
    ///         and the editor's own remote inspector talks over a <c>LocalTransport</c>, which has no
    ///         endpoint to report. The producer is E6's, and this is the seam it writes into.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It <em>is</em> read, and the claim that it was not came from a <c>*.cs</c>-only
    ///         sweep.</b> The value reaches <c>DeviceEntry.Endpoint</c>, which
    ///         <c>DeviceManagerView.vxml</c> draws as the device grid's Endpoint column — a view's
    ///         <c>&lt;code&gt;</c> block is production C#, and a grep that reads only <c>.cs</c>
    ///         reports a gap that is not there.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it no longer has to be set before start-up.</b> This used to be an
    ///         auto-property read once in <see cref="Activate" />, with a remark warning a host that a
    ///         later assignment "sets it too late" — a timing hazard on a property no host set at all.
    ///         The setter pushes the value into the live provider and rediscovers, so a producer that
    ///         arrives when a play session starts — which is when a standalone player's port is
    ///         actually known — reaches the panel.
    ///     </para>
    /// </remarks>
    public string? InspectorEndpoint {
        get => inspectorEndpoint;
        set {
            inspectorEndpoint = value;

            if (localDevice is null) {
                return;
            }

            localDevice.Endpoint = value;
            devices.Discover();
        }
    }

    /// <summary>The bandwidth ledger of whatever session is running, when the host has one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null on a bare editor, and that is the honest state.</b> A
    ///         <c>BandwidthLedger</c> is attached to a <c>ReplicationServer</c> and an
    ///         <c>RpcRouter</c> by whoever built them — a game, a play-mode process, a dedicated
    ///         server — and there is no session in an editor that has not started one. The panel says
    ///         so rather than drawing a table of zeroes, which would read as a game sending nothing.
    ///     </para>
    ///     <para>
    ///         A property rather than a ledger this class owns, for the same reason
    ///         <see cref="GpuFrame" /> is one: the object that records the numbers has to be the
    ///         object doing the thing being recorded.
    ///     </para>
    /// </remarks>
    public BandwidthLedger? NetworkLedger { get; set; }

    /// <summary>The replication registry that names the component types inside a packet.</summary>
    /// <remarks>
    ///     Separate from the ledger because it is a different kind of fact — the ledger is a running
    ///     total and this is the manifest a decode is read against — and because the two can
    ///     legitimately arrive from different places: a client has a registry and receives snapshots,
    ///     and has no ledger at all.
    /// </remarks>
    public ReplicationRegistry? NetworkRegistry { get; set; }

    /// <summary>The newest snapshot's bytes, exactly as they went on the wire.</summary>
    /// <remarks>
    ///     ⚠ <b><c>ReplicationServer</c> does not keep them and this does not ask it to.</b> It writes
    ///     each connection's snapshot into a caller's buffer and forgets it; which connection's bytes
    ///     are worth looking at is a question only the host can answer — see
    ///     <c>GameServer.LastSnapshot</c> in <c>Samples/08</c>, a game holding on to one for exactly
    ///     this purpose.
    /// </remarks>
    public ReadOnlyMemory<byte> NetworkSnapshot { get; set; }

    /// <summary>The session whose round trip and jitter the panel graphs, when the host has one.</summary>
    /// <remarks>
    ///     ⚠ <b>Independent of <see cref="NetworkLedger" />, and the split is not tidiness.</b> A
    ///     client has a session and no ledger — the ledger is attached to a replication server, and a
    ///     client is not one — so "how is the link" and "where is the bandwidth going" are two
    ///     questions with two sources, and the panel draws whichever of them it was given.
    /// </remarks>
    public NetworkSession? NetworkSession { get; set; }

    /// <summary>The profiler's model, for a test and for the host's own frame samples.</summary>
    public ProfilerModel Profiling => profiler;

    /// <summary>The devices this editor can see, so a build can say what it is doing to one.</summary>
    /// <remarks>
    ///     ⚠ <b>Exposed because a deploy is two halves and this owns one of them.</b> The module knows
    ///     what devices there are and shows their state; whoever can build a player has to be able to
    ///     say "deploying" and then "available again", or a row is left reading Deploying after a
    ///     build that failed and somebody waits on it.
    /// </remarks>
    public DeviceManager Devices => devices;

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        shell = context.Shell;
        project = context.Services.Require<EditorProject>();
        scenes = context.Services.Require<IActiveScene>();

        // ⚠ The toolset's own words, handed over the way its panels and its commands are. An
        // `All` list nothing walks is in no translator's template, which is the state
        // `StringFamily` exists to end one level down (#1202).
        context.AddStrings(DiagnosticsStrings.All);

        context.Services.TryGet<IDeviceDeploy>(out deployer);

        ProfilerTheme.Install(shell.Document);
        DebuggerTheme.Install(shell.Document);

        // ⚠ Added here rather than in a panel's factory, because a factory runs again every time the
        // panel is reopened — and a second `LocalProfileSource` over the same static rings would mean
        // two readers of a `Collect` that empties them, which is half a capture each.
        profiler.Add(new LocalProfileSource("Editor"));

        devices.Add(localDevice = new LocalDeviceProvider(inspectorEndpoint));
        devices.Discover();

        Panels(context);
        Commands(context);

        // ⚠ Drained every frame while a capture is running, whether or not the panel is open. The
        // rings overwrite, so a five-second capture of a busy thread would otherwise be the last
        // sixty milliseconds of it with the rest gone and nothing saying so.
        context.OnUpdate(
            delta => {
                profiler.Tick();
                remoteInspector?.Poll(delta);
            }
        );

        context.OnUnload(Release);
    }

    /// <inheritdoc />
    public void Deactivate() => Release();

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The same release the plugin lifecycle already runs, and it is here because the module
    ///     owns a transport.</b> Unloading calls <see cref="Deactivate" />; a host that constructed one
    ///     and never activated it — a test, a headless probe — has nothing else to call, and a
    ///     loopback network left open is a socket nobody closes.
    /// </remarks>
    public void Dispose() => Release();

    void Panels(PluginContext context) {
        context.AddPanel(
            "profiler",
            EditorStrings.PanelProfiler,
            panel => {
                // ⚠ The profiler keeps its toolbar and its two grids outside its own scroller and
                // scrolls only the flame chart, which is the one part with an unbounded number of
                // rows. It also sizes a grid at `height: 34%` of the panel — a percentage that needs
                // the panel's height to be the panel's height and not the content's.
                panel.Scrolls = false;

                panel.WhenPressedIn(() => shell.Context = DiagnosticsContext);

                var view = panel.Add<ProfilerView>();

                // ⚠ Where doc 13's other entry point writes, so the two land in one folder: `vixen
                // trace record` defaults to <project>/Traces/<name>-<timestamp>.json, and a trace of
                // the editor's own frame belongs beside the traces of the game it is editing. The
                // panel is given the folder rather than finding it, which is what keeps this
                // assembly the only one that knows there is a project.
                view.TraceDirectory = Path.Combine(project.Paths.Root, "Traces");
                view.Show(profiler);
            }
        );

        context.AddPanel(
            "gpu",
            EditorStrings.PanelGpu,
            panel => {
                // ⚠ The timeline lays its bars out absolutely inside a `width: 100%` lane strip whose
                // laid-out width it then reads back to place them. Every one of those three facts
                // wants the panel's box, not a content-sized one.
                panel.Scrolls = false;

                panel.WhenPressedIn(() => shell.Context = DiagnosticsContext);

                // ⚠ Held as well as filled, on the same terms as the frame debugger below: the
                // device arrives after the panel does, and the sentence has to follow it. The guard
                // that makes holding a view safe is in `Restate`.
                timeline = panel.Add<GpuTimelineView>();

                // ⚠ Pulled rather than pushed, and every panel here follows the same rule. A
                // reference to a panel kept on this object outlives the panel — a factory runs again
                // on every reopen — so a frame pushed into the previous one lands on an element that
                // has been removed from the document, which throws the moment it reads its bounds.
                // The *sentence* is pushed because what the panel reads is whether it is null, which
                // a delegate that defers to this object can never be.
                timeline.Source = () => GpuFrame;

                Restate(timeline);
            }
        );

        context.AddPanel(
            "memory",
            EditorStrings.PanelMemory,
            panel => {
                // Its own scroller, with the refresh button and the status line kept out of it.
                panel.Scrolls = false;

                panel.WhenPressedIn(() => shell.Context = DiagnosticsContext);

                // ⚠ Built rather than added, because doc 36 § F7 made this panel a `.vxml` and a
                // markup component is a `Component` — it *builds* elements and is not one. The host
                // element it creates carries the same `memory-view` tag the control did, so every
                // rule in `ProfilerTheme` still lands.
                var memory = BuildContext.Build<MemoryView>(panel.Document, panel);

                // ⚠ The asset provider is the editor's and the GPU one is not wired. A device reports
                // its heaps through `VK_EXT_memory_budget`, which the Vulkan backend does not query —
                // so the arena is absent rather than shown as zero, which is the difference between
                // "not measured" and "nothing allocated".
                memory.Providers.Assets = AssetResidency;

                // ⚠ And this is now the *only* reading taken rather than the second. The control
                // called `Take` from `OnCreated` as well, before the provider above existed, so the
                // panel measured the process twice on open and discarded the poorer answer. A
                // component has no build-time hook, which turned that into a thing the host says
                // once.
                memory.Take();
            }
        );

        context.AddPanel(
            "statistics",
            EditorStrings.PanelStatistics,
            panel => {
                panel.WhenPressedIn(() => shell.Context = DiagnosticsContext);

                var statistics = BuildContext.Build<StatisticsView>(panel.Document, panel);

                // Whichever scene the editor is showing, so opening a prefab and pressing Refresh
                // counts the prefab rather than the level behind it.
                //
                // ⚠ Plus whatever the host counts that a world walk cannot — the behaviour
                // population, which is a property of a `BehaviorStore` and not of the world. See
                // `SceneFacts`.
                statistics.Source = () => SceneStatistics.Collect(
                    scenes.Current.World,
                    depth: Deepest(),
                    counted: SceneFacts?.Invoke()
                );
                statistics.Take();
            }
        );

        context.AddPanel(
            "network",
            EditorStrings.PanelNetwork,
            panel => {
                panel.WhenPressedIn(() => shell.Context = DiagnosticsContext);

                // ⚠ Built rather than added, because the panel is a `.vxml` and a markup component
                // is a `Component` — it *builds* elements and is not one. The host element it
                // creates carries the `network-view` tag, so `ProfilerTheme`'s shared strip rule and
                // `DebuggerTheme`'s own rules both land.
                var network = BuildContext.Build<NetworkView>(panel.Document, panel);

                // ⚠ Four delegates and no ledger, for the reason every panel here is pulled rather
                // than pushed: this factory runs again on every reopen, so anything the module held
                // a reference to would outlive the panel it was pointing at.
                network.Source = () => NetworkLedger;
                network.Registry = () => NetworkRegistry;
                network.Capture = () => NetworkSnapshot;

                // ⚠ And the loss lanes come with it, wired by nobody. A session holds the transport
                // it runs on and `ITransport.Loss` is null on one that cannot count datagrams, so a
                // host that has pointed the panel at a session on UDP gets the two loss lanes for
                // free — and one on a loopback gets a sentence saying why it does not. A separate
                // property for the transport would be a fifth thing to wire, and the reason most
                // hosts would not wire it is that nothing tells them there is anything to wire.
                network.Session = () => NetworkSession;

                // ⚠ After the delegates, and that is the whole of why it is here. A component's
                // `OnComposed` runs *inside* the build, before `Build` has returned and therefore
                // before the four lines above — so the panel's own first reading is of nothing.
                // This is the one that sees whatever is attached.
                network.Take();
            }
        );

        context.AddPanel(
            "frame-debugger",
            EditorStrings.PanelFrameDebugger,
            panel => {
                panel.WhenPressedIn(() => shell.Context = DiagnosticsContext);

                // ⚠ Held as well as filled, so a capture source the host acquires later reaches this
                // panel rather than the next one somebody opens. See `FrameCaptureSource`.
                frames = panel.Add<FrameDebuggerView>();

                Restate(frames);
            }
        );

        context.AddPanel(
            "remote-inspector",
            EditorStrings.PanelRemoteInspector,
            panel => {
                panel.WhenPressedIn(() => shell.Context = DiagnosticsContext);

                panel.Add<RemoteInspectorView>().Show(Inspector());
            }
        );

        context.AddPanel(
            "devices",
            EditorStrings.PanelDevices,
            panel => {
                panel.WhenPressedIn(() => shell.Context = DiagnosticsContext);

                var manager = panel.Add<DeviceManagerView>();

                // ⚠ Before `Show`, because the panel derives the Deploy button's state from it and a
                // factory that set it afterwards would leave the button greyed until the next
                // selection change.
                manager.CanDeploy = Refuse;
                manager.Show(devices);

                // Opening the remote inspector on the device somebody chose, which is the whole of
                // what "attach" means from this panel: the connection is the inspector's.
                manager.AttachRequested += (_, _) => {
                    shell.Workspace.Toggle("remote-inspector");
                    Inspector().Attach();
                };

                manager.DeployRequested += (_, device) => deployer?.Deploy(device);
            }
        );
    }

    /// <summary>Doc 20's five Tools lines, the GPU one, and Deploy.</summary>
    /// <remarks>
    ///     ⚠ <b>Each one opens its panel rather than doing the thing.</b> "Profiler" is not a verb
    ///     that profiles — it is the verb that shows you the profiler, and the Record button inside it
    ///     is the one that samples. Making the menu line sample directly would be a menu item with no
    ///     visible effect and no way to stop it.
    /// </remarks>
    void Commands(PluginContext context) {
        Panel(context, "tools.profiler", "profiler");
        Panel(context, "tools.gpu", "gpu");
        Panel(context, "tools.frame-debugger", "frame-debugger");
        Panel(context, "tools.memory", "memory");
        Panel(context, "tools.statistics", "statistics");

        // Doc 16's diagnostics section asks for an editor panel over the bandwidth attribution the
        // same section specifies. It is its own line rather than a tab inside the profiler for the
        // reason the GPU timeline is: a different measurement of a different thing, absent on every
        // editor that is not running a session, and a tab that was empty there would read as a
        // broken profiler rather than as a game nobody has started.
        Panel(context, "tools.network", "network");

        Panel(context, "tools.remote-inspector", "remote-inspector");

        // ⚠ On the Build menu, where doc 20's Part C puts it, and it is the same window. A separate
        // "Deploy" dialog that listed the same devices would be a second list to keep in step.
        Panel(context, "build.deploy", "devices");
    }

    void Panel(PluginContext context, string id, string panel) =>
        context.AddCommand(id, DiagnosticsStrings.Commands[id], () => shell.Workspace.Toggle(panel));

    string? Refuse(DeviceEntry device) =>
        deployer?.Refuse(device)
        ?? "This editor cannot build a player, so there is nothing to put on a device.";

    /// <summary>The remote inspector's client, made on first use.</summary>
    RemoteInspectorClient Inspector() {
        if (remoteInspector is not null) {
            return remoteInspector;
        }

        inspectorNetwork = new LocalNetwork();
        inspectorTransport = new LocalTransport(inspectorNetwork);

        return remoteInspector = new(inspectorTransport);
    }

    /// <summary>Detaches, and gives back the transport the inspector was talking over.</summary>
    /// <remarks>
    ///     ⚠ <b>The client does not own its transport and says so</b>, because a transport may be a
    ///     listen server the editor is using for something else — so closing it is this class's job,
    ///     as opening it was.
    /// </remarks>
    void Release() {
        remoteInspector?.Dispose();
        inspectorTransport?.Dispose();

        remoteInspector = null;
        inspectorTransport = null;
        inspectorNetwork = null;
    }

    /// <summary>Why the GPU timeline has nothing to show, or <see langword="null" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Three reasons and not two, because the third one used to draw as an answer.</b> A
    ///     device that reports timestamp queries and a period of zero converts every duration to
    ///     zero, so the panel drew a timeline of empty bars — "the GPU is doing nothing" rather than
    ///     "this device cannot say" (#1168). The two are told apart here rather than in the panel,
    ///     which has one seam for "nothing to show" and needs the sentence rather than the reason.
    /// </remarks>
    string? GpuUnavailable() =>
        GraphicsDevice is null
            ? "No graphics device. A headless run has no GPU to time."
            : GraphicsDevice.Features.CanTimeFrames
                ? null
                : GraphicsDevice.Features.HasTimestampQueries
                    ? $"'{GraphicsDevice.Adapter.Name}' reports timestamp queries and no timestamp "
                    + "period, so a tick cannot be turned into a duration. Every frame would read as "
                    + "zero milliseconds, which is not a measurement."
                    : $"'{GraphicsDevice.Adapter.Name}' reports no timestamp queries on its graphics "
                    + "queue, so its frames cannot be timed.";

    /// <summary>What the project has loaded, as rows for the memory view.</summary>
    /// <remarks>
    ///     ⚠ <b>Counts rather than bytes, and the difference is what the asset database knows.</b> It
    ///     holds identities and paths; how many bytes a loaded texture occupies is the graphics
    ///     device's answer and nothing here can ask for it. A row showing a made-up size would be
    ///     worse than a row showing a true count.
    /// </remarks>
    public IEnumerable<MemoryRow> AssetResidency() {
        yield return new(MemoryArena.Assets, "Indexed assets", project.Assets.Count, IsCount: true);
        yield return new(MemoryArena.Assets, "Open documents", project.Documents.Count, IsCount: true);
    }

    /// <summary>How deep the shown scene's hierarchy goes.</summary>
    /// <remarks>
    ///     ⚠ <b>Computed here rather than in <c>SceneStatistics</c>, because the parent relation is
    ///     not the ECS's.</b> <c>Hierarchy</c> is an engine concept over components, and a statistics
    ///     model that reached for it would be a model that cannot count a world with no transforms in
    ///     it — which is every unit test it has.
    /// </remarks>
    public int Deepest() {
        var document = scenes.Current;
        var deepest = 0;

        foreach (var entity in document.Roots) {
            deepest = Math.Max(deepest, Depth(document.World, entity, 1));
        }

        return deepest;
    }

    /// <summary>Tells the frame debugger what it can capture with, and why it cannot when it cannot.</summary>
    /// <param name="view">The panel, or <see langword="null" /> when none is open.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The sentence is about the host and not about the editor being unfinished.</b> A
    ///         capture is a recorded command stream, and the only recording backend the engine has is
    ///         <c>Vixen.Graphics.Null</c> — a Vulkan device executes into a command buffer and keeps
    ///         nothing. So an editor drawing through Vulkan honestly has nothing to step, and the
    ///         panel says which of the two states it is in rather than showing a button that would do
    ///         nothing (<a href="https://github.com/Rikarin/Vixen/issues/1208">#1208</a>).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A view the workspace has torn down is skipped rather than written to.</b> Closing
    ///         a panel does not tell this module, so the held reference outlives the panel — writing
    ///         a signal on a removed element would be an update nobody sees and a reference kept
    ///         alive for the session.
    ///     </para>
    /// </remarks>
    void Restate(FrameDebuggerView? view) {
        if (view is null || view.IsRemoved) {
            return;
        }

        view.Source = FrameCaptureSource;

        view.Unavailable = FrameCaptureSource is null
            ? "This host records into a real command buffer, which keeps nothing. A capture "
            + "needs the recording backend — see NullFrameCapture."
            : null;
    }

    /// <summary>Tells the GPU timeline why it has nothing to show, or that it now has.</summary>
    /// <param name="view">The panel, or <see langword="null" /> when none is open.</param>
    /// <remarks>
    ///     ⚠ <b>Called from the panel's factory <em>and</em> from the device's setter, and it has to
    ///     be both.</b> Neither order is the one that happens: a cold start builds the panel first
    ///     and a reopened panel finds a device already there. The same torn-down guard as the frame
    ///     debugger's, for the same reason — a held view outlives the panel it was drawn into.
    /// </remarks>
    void Restate(GpuTimelineView? view) {
        if (view is null || view.IsRemoved) {
            return;
        }

        view.Unavailable = GpuUnavailable();

        // ⚠ And the chart is remeasured rather than left for the next layout pass. `Realise` is
        // driven by `LayoutFinished`, which fires only when something moved — a device that arrived
        // while nothing moved would leave the sentence's element updated and the bars still absent.
        view.Realise();
    }

    static int Depth(World world, Entity entity, int level) {
        var deepest = level;

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            deepest = Math.Max(deepest, Depth(world, child, level + 1));
        }

        return deepest;
    }
}
