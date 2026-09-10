// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Assets;
using Vixen.Core.Diagnostics;
using Vixen.Core.IO;
using Vixen.Core.Threading;
using Vixen.Engine.Diagnostics.Overlays;
using Vixen.Engine.Frames;
using Vixen.Engine.Input;
using Vixen.Engine.Scenes;
using Vixen.Graphics;
using Vixen.Input;
using Vixen.Platform;

namespace Vixen.App;

/// <summary>Assembles an application, one decision at a time.</summary>
/// <remarks>
///     ⚠ <b>A builder with no backends refuses rather than guesses.</b> Choosing between Vulkan and
///     Null, or between the desktop platform and the headless one, means referencing those
///     assemblies — and they are in <c>Platform/</c>, which this one may not reference. So the two
///     choices arrive as <see cref="IPlatformFactory" /> and <see cref="IGraphicsBackend" />, and
///     <c>VixenApp.Create</c> in the <c>Vixen.App</c> package is what installs the defaults. A
///     caller assembling a builder by hand supplies them, or supplies a platform and a device
///     outright with <see cref="WithPlatform" /> and <see cref="WithGraphics" />.
/// </remarks>
public sealed class AppBuilder {
    readonly AppArguments arguments;
    readonly List<Action<AppServices>> configurations = [];
    readonly List<Func<LogFilter, ILoggerProvider>> loggerProviders = [];

    IPlatform? platform;
    IGraphicsDevice? device;
    IContentTransport? transport;
    IPlatformFactory? platforms;
    IGraphicsBackend? backend;

    /// <summary>Starts configuring an application from parsed arguments.</summary>
    /// <param name="arguments">The parsed command line.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arguments" /> is null.</exception>
    public AppBuilder(AppArguments arguments) {
        ArgumentNullException.ThrowIfNull(arguments);

        this.arguments = arguments;
    }

    /// <summary>Uses a platform the caller built, rather than detecting one.</summary>
    /// <param name="host">The platform. The application takes ownership and disposes it.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    ///     What an editor's play mode uses to run a game against the platform it already owns, and
    ///     what a test uses to run against <c>HeadlessPlatform</c> deterministically.
    /// </remarks>
    public AppBuilder WithPlatform(IPlatform host) {
        platform = host;
        return this;
    }

    /// <summary>Uses a device the caller built, rather than choosing one.</summary>
    /// <param name="graphics">
    ///     The device. The application does <em>not</em> take ownership: a device handed in is one
    ///     somebody else's frame is also drawing with, which is exactly the editor's play mode, and a
    ///     host that disposed it would take the editor's own window down with the game.
    /// </param>
    /// <returns>This builder.</returns>
    /// <remarks>
    ///     The seam for the backends an app head does not choose between — OpenGL, WebGPU, a device
    ///     an XR runtime dictated the creation of — and the reason <see cref="IGraphicsBackend" /> can
    ///     stay a function with two answers rather than a registry.
    /// </remarks>
    public AppBuilder WithGraphics(IGraphicsDevice graphics) {
        device = graphics;
        return this;
    }

    /// <summary>Uses a factory to pick the platform, when none was handed in.</summary>
    /// <param name="factory">What answers "which platform".</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="WithPlatform" /> wins.</b> A caller that handed over a live platform is
    ///     one that already has a window open, and a factory that ran anyway would start a second.
    /// </remarks>
    public AppBuilder WithPlatformFactory(IPlatformFactory factory) {
        platforms = factory;
        return this;
    }

    /// <summary>Uses a backend to open the device, when none was handed in.</summary>
    /// <param name="graphics">What answers "which device".</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="WithGraphics" /> wins</b>, and for a sharper reason than the platform's:
    ///     a device handed in belongs to somebody else's frame, and opening a second one would leave
    ///     two devices addressing the same GPU with neither aware of the other's submissions.
    /// </remarks>
    public AppBuilder WithGraphicsBackend(IGraphicsBackend graphics) {
        backend = graphics;
        return this;
    }

    /// <summary>Uses a transport for remote content, rather than plain HTTP.</summary>
    /// <param name="content">
    ///     How a downloaded bundle is fetched. The application does <em>not</em> take ownership: one
    ///     handed in is a client somebody else configured — with an authorisation header, a
    ///     certificate pin, a retry policy — and may well outlive the game's own content mount.
    /// </param>
    /// <returns>This builder.</returns>
    /// <remarks>
    ///     ⚠ <b>This does not decide <i>whether</i> anything is downloaded — the catalog does.</b> A
    ///     bundle carries a URL when its group declared <c>loadPath: Remote</c>, and a build with no
    ///     such group never fetches anything whatever is handed in here. What this is for is the case
    ///     where the URLs are real and reaching them takes more than <c>HttpClient</c>'s defaults.
    /// </remarks>
    public AppBuilder WithContent(IContentTransport content) {
        transport = content;
        return this;
    }

    /// <summary>Adds a log sink before the host has any logger of its own.</summary>
    /// <param name="provider">
    ///     The sink. The application takes ownership: the logger factory disposes every provider it
    ///     holds, this one included.
    /// </param>
    /// <returns>This builder.</returns>
    /// <remarks>
    ///     ⚠ <b>This is not the same as
    ///     <c>WithServices(services =&gt; services.LoggerFactory.AddProvider(…))</c>, and the
    ///     difference is the whole boot.</b> <see cref="WithServices" /> callbacks are the last
    ///     thing <see cref="Build" /> runs — after the platform, the mounts, the log-config read,
    ///     the workers, the engine loop, the content mount, input and the whole graphics build,
    ///     every one of which has already logged. A provider installed here is in the factory
    ///     before the first of those lines is written, which on Android and iOS is the difference
    ///     between a system log that has the bring-up in it and one that starts at
    ///     <c>OnInitialise</c> (#1197).
    /// </remarks>
    public AppBuilder WithLoggerProvider(ILoggerProvider provider) {
        ArgumentNullException.ThrowIfNull(provider);

        return WithLoggerProvider(_ => provider);
    }

    /// <summary>Adds a log sink built over the host's own filter, before the host logs anything.</summary>
    /// <param name="create">
    ///     Given the filter every sink the host composes shares, returns the sink. The application
    ///     takes ownership of what it returns.
    /// </param>
    /// <returns>This builder.</returns>
    /// <remarks>
    ///     The overload to reach for when the sink should answer to <c>--vixen-log-level</c> and
    ///     <c>vixen.log.yaml</c> like the console and the file do: a sink constructed with a filter
    ///     of its own is a sink those two settings cannot reach, which is the shape of "the switch
    ///     did nothing" that this repository keeps finding.
    /// </remarks>
    public AppBuilder WithLoggerProvider(Func<LogFilter, ILoggerProvider> create) {
        ArgumentNullException.ThrowIfNull(create);
        loggerProviders.Add(create);

        return this;
    }

    /// <summary>Registers extra services once everything else exists.</summary>
    /// <param name="configure">Called with the built services.</param>
    /// <returns>This builder.</returns>
    public AppBuilder WithServices(Action<AppServices> configure) {
        ArgumentNullException.ThrowIfNull(configure);
        configurations.Add(configure);
        return this;
    }

    /// <summary>Builds the application.</summary>
    /// <param name="game">The application's game.</param>
    /// <returns>Something ready to <see cref="VixenApplication.Run" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    ///     There is no platform and nothing to make one with.
    /// </exception>
    /// <remarks>
    ///     The whole boot sequence, in the order it has to happen: read the command line, ask the
    ///     game what it wants, start the platform, mount the file system, start the workers, open
    ///     the window.
    /// </remarks>
    public VixenApplication Build(Game game) {
        ArgumentNullException.ThrowIfNull(game);

        var config = new AppConfig();
        config.Apply(arguments);

        // Before the platform exists, because it decides what the platform will be.
        game.OnConfigure(config);

        // After it, because a game asking for a capture directory in code wants the reproducible
        // clock the flag implies just as much as the operator who typed the flag does.
        config.ImplyCaptureFrameTime();

        // One filter, shared by every sink: doc 13 asks for per-category levels that are
        // live-editable, and a host whose console and file disagreed about what "verbose asset
        // loading" means would make that setting untrustworthy.
        var levels = new LogFilter { MinimumLevel = config.LogLevel };
        var logs = new RingBufferSink(filter: levels);
        var sinks = new List<ILoggerProvider> { logs };

        if (config.LogToConsole) {
            sinks.Add(new ConsoleSink(filter: levels));
        }

        if (config.LogFileDirectory is { Length: > 0 } logDirectory) {
            sinks.Add(new ZLoggerFileSink(logDirectory, FileNamePrefix(config.Name), filter: levels));
        }

        // Last in the list and still before every logger this method makes, which is the point of
        // the seam: a platform sink installed after the boot is a platform sink that missed it.
        sinks.AddRange(loggerProviders.Select(create => create(levels)));

        var loggerFactory = new HostLoggerFactory([.. sinks]);

        // ⚠ Named rather than silently headless. Falling back to a platform that opens no window
        // would turn "this head forgot to install its backends" into a game that boots, runs, and
        // shows nothing — the single hardest failure in this whole path to attribute.
        var host = platform
            ?? platforms?.Create(config)
            ?? throw new InvalidOperationException(
                "This AppBuilder has no platform and no IPlatformFactory to make one with. "
                + "VixenApp.Create installs the defaults; a builder constructed directly has to be "
                + "given a platform with WithPlatform or a factory with WithPlatformFactory."
            );

        var fileSystem = new VirtualFileSystem();
        host.FileSystem.MountStandardLocations(fileSystem);

        // The first thing done with the mounted file system, and it has to be: everything below
        // this line logs, and a per-category rule that arrives after the subsystem it names has
        // already spoken is a rule that did nothing on the run somebody was watching. What it
        // cannot cover is the platform above — the mounts do not exist until the platform does —
        // which is why `--vixen-log-level` stays the way to turn up a boot that never got this far.
        LogConfigFile.ApplyStandardLocations(
            fileSystem,
            levels,
            honourMinimumLevel: arguments.LogLevel is null,
            loggerFactory.CreateLogger("Vixen.App")
        );

        var workers = config.WorkerCount
            ?? DefaultWorkerCount(host.Processors.AvailableProcessors, OperatingSystem.IsBrowser());

        // The other half of `IProcessorTopology`, and until now the unused one: the count has been
        // read from it since it existed, and nothing in the tree ever called TrySetAffinity. Null
        // unless asked for — see AppConfig.PinWorkers for why pinning is not a default — and the
        // placement itself answers false on every platform that has no affinity to give.
        var placement = config.PinWorkers ? new ProcessorAffinityPlacement(host.Processors) : null;
        var jobs = new JobScheduler(workers, placement);
        var mainThread = new MainThreadDispatcher();

        // Null rather than a hidden window when the application asked for none: a batch tool that
        // creates a window it never shows still pays for a swapchain-capable surface and still fails
        // on a machine with no Vulkan.
        var window = config.Window is { } options
            ? host.CreateWindow(options with { Title = options.Title == "Vixen" ? config.Name : options.Title })
            : null;

        // After the standard locations are mounted, because /app is where a shipped content build
        // is; before the game sees the services, because OnInitialise is the first place a game
        // would reasonably ask for an asset.
        var content = ContentMount.Open(fileSystem, config.LooseContentPath, transport);

        // ⚠ After OnConfigure and after the mount, which is the only order that works: the default
        // comes out of the content build's own manifest, and a game is asked what it wants before
        // there is any content to ask. A game that named a scene keeps it — see the property.
        config.StartupScene ??= content.Scenes.Count > 0 ? content.Scenes[0] : null;

        // After the jobs, because systems hand work to them; before the game sees the services,
        // because OnInitialise is where a game adds its own systems and spawns its first entities.
        var engine = config.UseEngine
            ? new EngineLoop(jobs: jobs, fixedStep: config.FixedStep is { } step ? new(step) : null)
            : null;

        // The one manager over the one world, so that a scene loaded at boot and a scene loaded from
        // a loading screen are unloadable the same way. Nothing is loaded into it here: that is
        // VixenApplication.Initialise's, because it happens once and belongs beside OnInitialise.
        var scenes = engine is not null ? new SceneManager(engine.World) : null;

        // A pad plugged in before the process started produced no GamepadConnected for anyone to
        // hear, so an input layer built only from the event stream would see a controller that does
        // nothing until it is unplugged and plugged back in.
        var input = new InputService();
        input.Devices.SyncGamepads(host.Input);

        // With an engine, input is read in SystemPhase.Input — the phase that exists for it, and the
        // reason SystemPhase names its stages rather than deriving them. Without one, the host reads
        // it itself before OnUpdate; either way it happens exactly once a frame and before anything
        // that reacts to it.
        engine?.Add(new InputUpdateSystem(input));

        // After the content, because the baked variants and the compositor both come out of it;
        // after the engine, because this adds the extraction systems that fill the frame from the
        // world; and before the game sees the services, because OnInitialise is where a game places
        // its camera and expects something to be looking through it.
        // ⚠ Defaulted here rather than inside the backend, which cannot know where this platform
        // keeps a cache. It is the difference between a driver that recompiles every pipeline on
        // every boot and one that does it once per machine — and a cache directory rather than a
        // data one, because the blob is derivable, must not be backed up or synced, and a driver
        // update invalidates it. `""` is a head saying it wants no file.
        //
        // Joined with a forward slash rather than System.IO.Path, which Core is barred from using
        // (VXIO0001) and which is the wrong tool anyway: this is a host path being handed straight
        // back to the platform layer that produced it, and every OS this runs on accepts a forward
        // slash — including the one whose own separator is a backslash. ZLoggerFileSink does the
        // same thing for the same reason.
        if (config.Graphics.PipelineCachePath is null
            && host.FileSystem.CacheDirectory is { Length: > 0 } caches) {
            var directory = caches.EndsWith('/') || caches.EndsWith('\\') ? caches[..^1] : caches;
            config.Graphics.PipelineCachePath = $"{directory}/pipelines.vkcache";
        }

        var graphics = config.Graphics.Enabled ? Graphics(config, window, content, engine, loggerFactory, jobs) : null;

        // ⚠ Added here rather than inside AppGraphics because the ring is this method's. It goes into
        // the same DiagnosticOverlays the overlay system was handed — the object, not a copy — which
        // is the one thing about this feature that fails silently if it is got wrong.
        if (graphics?.Overlays is { } panels) {
            panels.Add(new LogOverlay(logs));

            // Said here rather than where the rest were built, because the count has to be the final
            // one. A build with the switch on and no commands is a console that will answer `help`
            // and nothing else, which is worth knowing before somebody types a subsystem's verb and
            // concludes the subsystem is broken.
            // Into locals first: CA1873 is right that building a logger inside the call is work done
            // whether or not anybody is listening, and this happens once at start-up either way.
            var overlayLog = loggerFactory.CreateLogger("Vixen.App");
            var commands = graphics.Console!.Registered.Count;

            HostLog.OverlaysEnabled(overlayLog, panels.Registered.Count, commands);
        }

        var services = new AppServices(
            host,
            window,
            jobs,
            mainThread,
            fileSystem,
            logs,
            loggerFactory,
            config,
            content,
            engine,
            scenes,
            input,
            graphics
        );

        foreach (var configure in configurations) {
            configure(services);
        }

        return new(game, services);
    }

    /// <summary>Opens a device and builds the frame the world is drawn through.</summary>
    /// <remarks>
    ///     Both of the things that can go wrong here are reported and survived rather than thrown
    ///     for. A machine with no Vulkan gets the Null backend and a warning — which is also, word
    ///     for word, how a dedicated server boots — and a build with no baked shaders gets a line
    ///     saying so, because "every material resolved to a miss" is otherwise a mystery with a
    ///     build step for an answer.
    /// </remarks>
    AppGraphics Graphics(
        AppConfig config,
        IWindow? window,
        ContentMount content,
        EngineLoop? engine,
        HostLoggerFactory logs,
        JobScheduler jobs
    ) {
        var log = logs.CreateLogger("Vixen.App");
        var graphics = device;

        if (graphics is null) {
            // Same refusal as the platform's, and for the same reason: a device nobody can open is
            // not something to paper over with a fallback this assembly cannot reach anyway.
            var source = backend
                ?? throw new InvalidOperationException(
                    "This AppBuilder has no device and no IGraphicsBackend to open one with. "
                    + "VixenApp.Create installs the defaults; a builder constructed directly has to "
                    + "be given a device with WithGraphics or a backend with WithGraphicsBackend, or "
                    + "to set AppConfig.Graphics.Enabled to false."
                );

            graphics = source.Create(config.Graphics, window, logs, out var reason);

            // ⚠ Nothing opened, and that is a stop rather than a warning. It only happens when a
            // preference list was written and every entry in it refused — an operator running
            // `--vixen-backend vulkan` on a machine with no Vulkan, most often — and the whole point
            // of naming a list is to find out. Falling back to a device that draws nothing here
            // would answer the question with the exact silence it was asked to break.
            if (graphics is null) {
                throw new InvalidOperationException(
                    $"No graphics backend could be opened: {reason} Add GraphicsBackend.Null to "
                    + "GraphicsOptions.Backends to fall back to a device that draws nothing, or set "
                    + "AppConfig.Graphics.Enabled to false to run without one."
                );
            }

            if (reason is { } why) {
                HostLog.NoPresentingDevice(log, why);
            }
        }

        if (content.Shaders is null && content.ShaderReason is { } shaders) {
            HostLog.NoShaders(log, shaders);
        }

        return new(
            graphics,
            config.Graphics,
            window,
            content.Assets,
            content.Shaders,
            engine,
            logs,

            // A device this built is this application's to close. One handed to WithGraphics belongs
            // to whoever handed it over — the editor, an XR runtime — and outlives the game.
            ownsDevice: device is null,

            // ⚠ The same scheduler the world is stepped on, deliberately. A tier is a choice between
            // two things a worker could pick up next, so a frame's work and the work that would
            // rather be late than make a frame late have to be queued on one scheduler for the
            // choice to exist at all. A second scheduler here would give both of them their own
            // workers and neither of them anything to yield to.
            jobs: jobs
        );
    }

    /// <summary>
    ///     How many job workers a head starts when <see cref="AppConfig.WorkerCount" /> says nothing.
    /// </summary>
    /// <param name="availableProcessors">What the platform's <c>IProcessorTopology</c> reports.</param>
    /// <param name="isBrowser">Whether this is a <c>browser-wasm</c> runtime.</param>
    /// <returns>One per available processor beyond the calling thread, and none in a browser.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The browser is decided by the target and not by the count, and it has to be:
    ///         <c>Math.Max(1, available - 1)</c> has a floor of one, so this could never return the
    ///         one value the browser can accept</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/486">#486</a>). Even the
    ///         non-isolated page — where <c>WebProcessors</c> already reported a single processor —
    ///         came out of that expression as <c>Math.Max(1, 0)</c>, so every browser head this
    ///         builder produced asked for a worker thread, and
    ///         <see cref="System.Threading.Thread.Start()" /> on <c>browser-wasm</c> throws
    ///         <see cref="PlatformNotSupportedException" /> before the first frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So <c>AppConfig.WorkerCount</c>'s "0 is a supported and tested value" was
    ///         reachable only by passing it.</b> doc 10 § Cross-platform discipline requires every
    ///         subsystem to work with <c>workerCount == 0</c> because the browser has none, and the
    ///         one code path that decides the default for a real head could not produce it.
    ///     </para>
    ///     <para>
    ///         The rule is <c>JobScheduler.DefaultWorkerCount</c>'s, deliberately — that type made
    ///         the same call for the same reason and this one silently disagreed. A cross-origin
    ///         isolated build on a threaded runtime that wants the threads it has still passes the
    ///         count it wants; what it must not do is get them by accident.
    ///     </para>
    /// </remarks>
    internal static int DefaultWorkerCount(int availableProcessors, bool isBrowser) =>
        isBrowser ? 0 : Math.Max(1, availableProcessors - 1);

    /// <summary>
    ///     Turns an application's name into something a file can be called. A title is allowed to
    ///     contain a slash, a colon or a quote; a path is not, and a log file that failed to open
    ///     because the game was called <c>Half-Life: Alyx</c> would be found out on the day somebody
    ///     needed the log.
    /// </summary>
    static string FileNamePrefix(string name) {
        var cleaned = new string([
            .. name.Select(static character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'
            )
        ]).Trim('-');

        return cleaned.Length == 0 ? "vixen" : cleaned;
    }
}

/// <summary>
///     The smallest thing that turns the log ring into an <see cref="ILoggerFactory" />.
/// </summary>
/// <remarks>
///     <para>
///         ADR-008 takes <c>Microsoft.Extensions.Logging.Abstractions</c> and no more, so the
///         concrete <c>LoggerFactory</c> — which lives in the non-abstractions package — is not
///         available and should not be: it brings a configuration and options stack an engine has no
///         use for. This is the twenty lines that stand in for it, and the composition it performs
///         is the host's decision anyway.
///     </para>
///     <para>
///         ⚠ <b>A closed factory is loud, not deaf.</b> <c>Microsoft.Extensions.Logging</c>'s own
///         <c>LoggerFactory</c> throws <see cref="ObjectDisposedException" /> from
///         <c>CreateLogger</c> after disposal, and this one deliberately does not: several
///         subsystems log from inside their own <c>Dispose</c>, so throwing would turn a dropped
///         line into an exception on a path that is already going down. What it does instead is
///         write every record it is given after disposal to <see cref="Console.Error" />, prefixed
///         and named, because the one behaviour that is not acceptable is the one this class used
///         to have: <c>Dispose</c> cleared the provider list, <c>CreateLogger</c> returned a
///         fan-out over nothing, and every record after that was accepted and written nowhere. A
///         disposed factory was not dead, it was <i>deaf</i> — the shape this repository keeps
///         rediscovering, where the thing reports success on the day it stopped working.
///     </para>
///     <para>
///         ⚠ <b>Which is why there is no single-provider fast path any more.</b> It used to hand
///         back the provider's own logger, so a logger cached before disposal — and
///         <c>VixenApplication</c> caches one in its constructor — could not notice the factory
///         closing underneath it. Every logger this factory makes is now its own, and asks the
///         factory on each record. That is one field read per line, against a fan-out that was
///         already walking an array.
///     </para>
///     <para>
///         <see cref="AddProvider" /> does throw, because there is no teardown excuse for it: a
///         provider added to a closed factory would be leaked, never disposed, and never written
///         to.
///     </para>
/// </remarks>
sealed class HostLoggerFactory(params ILoggerProvider[] providers) : ILoggerFactory {
    readonly Lock gate = new();
    readonly List<ILoggerProvider> providers = [.. providers];

    /// <summary>
    ///     One logger per category, kept so that <see cref="AddProvider" /> can reach the loggers
    ///     that already exist.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This used to be no cache and a snapshot, which made <see cref="AddProvider" /> a
    ///     no-op for every logger already handed out.</b> The two mobile samples are exactly that
    ///     shape — <c>WithServices(services =&gt; services.LoggerFactory.AddProvider(new
    ///     PlatformSink()))</c>, and <c>WithServices</c> callbacks are the last thing
    ///     <see cref="AppBuilder.Build" /> runs — so on the two platforms where the system log is
    ///     the only log there is, the sink received nothing the platform, the graphics stack or the
    ///     engine had written (#1197). <c>Microsoft.Extensions.Logging</c>'s own
    ///     <c>LoggerFactory</c> caches by category for the same reason.
    /// </remarks>
    readonly Dictionary<string, Fanout> loggers = new(StringComparer.Ordinal);

    bool disposed;

    /// <summary>
    ///     Where a record written after <see cref="Dispose" /> goes. <see cref="Console.Error" />
    ///     unless a test names somewhere it can read back — asserting on the last-resort channel is
    ///     the only oracle that can tell a diverted record from a discarded one, and
    ///     <c>Console.SetError</c> is process-wide and would collide with a parallel test.
    /// </summary>
    internal TextWriter? LastResort { get; init; }

    public void AddProvider(ILoggerProvider provider) {
        ArgumentNullException.ThrowIfNull(provider);
        ObjectDisposedException.ThrowIf(disposed, this);

        lock (gate) {
            this.providers.Add(provider);

            // The loggers that already exist, not just the ones made from here on. Everything the
            // host built before this point is holding one of these.
            foreach (var (category, logger) in loggers) {
                logger.Add(provider.CreateLogger(category));
            }
        }
    }

    public ILogger CreateLogger(string categoryName) {
        lock (gate) {
            // Not cached after the close: the diversion below needs a logger, and a closed factory
            // is not going to gain providers that would want to find it again.
            if (disposed) {
                return new Fanout(this, categoryName, []);
            }

            if (loggers.TryGetValue(categoryName, out var existing)) {
                return existing;
            }

            var created = new Fanout(
                this,
                categoryName,
                [.. this.providers.Select(provider => provider.CreateLogger(categoryName))]
            );

            loggers[categoryName] = created;

            return created;
        }
    }

    public void Dispose() {
        lock (gate) {
            if (disposed) {
                return;
            }

            disposed = true;

            foreach (var provider in this.providers) {
                provider.Dispose();
            }

            this.providers.Clear();
            loggers.Clear();
        }
    }

    /// <summary>Says out loud that a record arrived after the sinks were closed.</summary>
    void WriteAfterClose(string category, LogLevel level, string message, Exception? exception) {
        var writer = LastResort ?? Console.Error;

        writer.WriteLine($"[vixen: logged after shutdown] {level}: {category}: {message}");

        if (exception is not null) {
            writer.WriteLine(exception);
        }

        writer.Flush();
    }

    /// <summary>One logger that writes to several — or, once the factory is closed, to none.</summary>
    /// <remarks>
    ///     The target array is replaced rather than mutated, so a record being written walks either
    ///     the set before an <see cref="AddProvider" /> or the set after it and never a half-built
    ///     one — and the per-record path stays a field read and an array walk, with no lock and no
    ///     allocation.
    /// </remarks>
    sealed class Fanout(HostLoggerFactory factory, string category, ILogger[] loggers) : ILogger {
        volatile ILogger[] targets = loggers;

        /// <summary>Adds a provider's logger to the set this one writes to.</summary>
        /// <remarks>Called under the factory's lock, which is what makes the read-copy-write safe.</remarks>
        internal void Add(ILogger logger) => targets = [.. targets, logger];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) {
            // ⚠ True, and that is the point. The source-generated logging methods check this before
            // they format anything, so a closed factory answering false here would make the
            // diversion below unreachable and put the silence straight back.
            if (factory.disposed) {
                return true;
            }

            foreach (var logger in targets) {
                if (logger.IsEnabled(logLevel)) {
                    return true;
                }
            }

            return false;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) {
            if (factory.disposed) {
                factory.WriteAfterClose(category, logLevel, formatter(state, exception), exception);

                return;
            }

            foreach (var logger in targets) {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
