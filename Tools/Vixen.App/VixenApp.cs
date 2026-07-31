// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Assets;
using Vixen.Core.Diagnostics;
using Vixen.Core.IO;
using Vixen.Core.Threading;
using Vixen.Engine.Frames;
using Vixen.Engine.Input;
using Vixen.Engine.Scenes;
using Vixen.Graphics;
using Vixen.Input;
using Vixen.Platform;

namespace Vixen.App;

/// <summary>The one line an application's <c>Program.cs</c> usually is.</summary>
/// <remarks>
///     <para>
///         <c>return VixenApp.Run&lt;MyGame&gt;(args);</c> and nothing else. The sequence it runs
///         through is <see cref="Create" /> → <see cref="AppBuilder.Build" /> →
///         <see cref="VixenApplication.Run" />, all of which are public, so an application that
///         wants control inlines the three calls and edits the middle one rather than fighting a
///         host that will not let go.
///     </para>
///     <para>
///         That property is deliberate and worth protecting: <c>docs/plan/17</c> chose the
///         "the app is the executable" model precisely so that nothing in the boot path is a black
///         box, and a convenience method that hid steps would give the property away for the sake of
///         one line.
///     </para>
/// </remarks>
public static class VixenApp {
    /// <summary>Builds and runs an application.</summary>
    /// <typeparam name="TGame">The application's <see cref="Game" />.</typeparam>
    /// <param name="arguments">The process arguments.</param>
    /// <returns>A process exit code.</returns>
    public static int Run<TGame>(string[]? arguments = null) where TGame : Game, new() {
        using var application = Create(arguments).Build(new TGame());
        return application.Run();
    }

    /// <summary>Starts configuring an application.</summary>
    /// <param name="arguments">The process arguments.</param>
    /// <returns>A builder.</returns>
    public static AppBuilder Create(string[]? arguments = null) => new(AppArguments.Parse(arguments));
}

/// <summary>Assembles an application, one decision at a time.</summary>
public sealed class AppBuilder {
    readonly AppArguments arguments;
    readonly List<Action<AppServices>> configurations = [];

    IPlatform? platform;
    IGraphicsDevice? device;
    IContentTransport? transport;

    internal AppBuilder(AppArguments arguments) => this.arguments = arguments;

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
    ///     The seam for the backends <see cref="GraphicsHost" /> does not choose between — OpenGL,
    ///     WebGPU, a device an XR runtime dictated the creation of — and the reason that class can
    ///     stay a function with two answers rather than a registry.
    /// </remarks>
    public AppBuilder WithGraphics(IGraphicsDevice graphics) {
        device = graphics;
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

        var loggerFactory = new HostLoggerFactory([.. sinks]);
        var host = platform ?? PlatformHost.Create(config);

        var fileSystem = new VirtualFileSystem();
        host.FileSystem.MountStandardLocations(fileSystem);

        var workers = config.WorkerCount ?? Math.Max(1, host.Processors.AvailableProcessors - 1);
        var jobs = new JobScheduler(workers);
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
        var graphics = config.Graphics.Enabled ? Graphics(config, window, content, engine, loggerFactory) : null;

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
        ILoggerFactory logs
    ) {
        var log = logs.CreateLogger("Vixen.App");
        var graphics = device;

        if (graphics is null) {
            graphics = GraphicsHost.Create(window, logs, out var reason);

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
            ownsDevice: device is null
        );
    }

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
///     ADR-008 takes <c>Microsoft.Extensions.Logging.Abstractions</c> and no more, so the concrete
///     <c>LoggerFactory</c> — which lives in the non-abstractions package — is not available and
///     should not be: it brings a configuration and options stack an engine has no use for. This is
///     the twenty lines that stand in for it, and the composition it performs is the host's decision
///     anyway.
/// </remarks>
sealed class HostLoggerFactory(params ILoggerProvider[] providers) : ILoggerFactory {
    readonly List<ILoggerProvider> providers = [.. providers];

    public void AddProvider(ILoggerProvider provider) {
        ArgumentNullException.ThrowIfNull(provider);
        this.providers.Add(provider);
    }

    public ILogger CreateLogger(string categoryName) {
        if (this.providers.Count == 1) {
            return this.providers[0].CreateLogger(categoryName);
        }

        return new Fanout([.. this.providers.Select(provider => provider.CreateLogger(categoryName))]);
    }

    public void Dispose() {
        foreach (var provider in this.providers) {
            provider.Dispose();
        }

        this.providers.Clear();
    }

    /// <summary>One logger that writes to several.</summary>
    sealed class Fanout(ILogger[] loggers) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) {
            foreach (var logger in loggers) {
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
            foreach (var logger in loggers) {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
