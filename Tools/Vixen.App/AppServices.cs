// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Core.IO;
using Vixen.Core.Threading;
using Vixen.Engine.Frames;
using Vixen.Platform;

namespace Vixen.App;

/// <summary>What the host built, named rather than looked up.</summary>
/// <remarks>
///     <para>
///         The handful of genuinely global things, as properties, so the common case is a field
///         access rather than a dictionary lookup and a cast. <see cref="Registry" /> is the same
///         set behind <c>ServiceRegistry</c> for anything that wants them generically — an editor
///         panel, a plugin — and for the subsystems that arrive in later phases.
///     </para>
///     <para>
///         Not a DI container, for the reasons <c>ServiceRegistry</c> gives: no constructor
///         injection, no lifetimes, no reflection, because under NativeAOT the alternative does not
///         work and on a frame budget it costs more than it is worth.
///     </para>
/// </remarks>
public sealed class AppServices {
    internal AppServices(
        IPlatform platform,
        IWindow? window,
        JobScheduler jobs,
        MainThreadDispatcher mainThread,
        VirtualFileSystem fileSystem,
        RingBufferSink logs,
        ILoggerFactory loggerFactory,
        AppConfig config,
        ContentMount content,
        EngineLoop? engine
    ) {
        Platform = platform;
        Window = window;
        Jobs = jobs;
        MainThread = mainThread;
        FileSystem = fileSystem;
        Logs = logs;
        LoggerFactory = loggerFactory;
        Config = config;
        Content = content;
        Engine = engine;

        Registry = new();
        Registry.Add(platform);
        Registry.Add(jobs);
        Registry.Add(mainThread);
        Registry.Add(fileSystem);
        Registry.Add(logs);
        Registry.Add(loggerFactory);
        Registry.Add(config);

        if (window is not null) {
            Registry.Add(window);
        }

        if (content.Assets is { } assets) {
            Registry.Add(assets);
        }

        if (engine is not null) {
            Registry.Add(engine);
            Registry.Add(engine.World);
        }
    }

    /// <summary>The operating system.</summary>
    public IPlatform Platform { get; }

    /// <summary>The window the host opened, or <see langword="null" /> if the application wanted
    /// none.</summary>
    /// <remarks>
    ///     Fixed at boot, and therefore a handle that can go stale: it is whichever window
    ///     <see cref="AppConfig.Window" /> asked for, not "the current one". That is exact for the
    ///     ordinary case, where the application ends when this closes. An application that set
    ///     <see cref="AppConfig.ExitWhenAllWindowsClose" /> to <see langword="false" /> — a tray
    ///     application, something that reopens on demand — has taken window management on itself, and
    ///     this still names the closed original. Ask <see cref="IPlatform.Windows" /> instead.
    /// </remarks>
    public IWindow? Window { get; }

    /// <summary>The job system.</summary>
    public JobScheduler Jobs { get; }

    /// <summary>The queue for work that has to run on the thread that owns the platform.</summary>
    public MainThreadDispatcher MainThread { get; }

    /// <summary>The virtual file system, with the platform's standard locations already mounted.</summary>
    public VirtualFileSystem FileSystem { get; }

    /// <summary>The always-on log ring the console and the crash reporter read.</summary>
    public RingBufferSink Logs { get; }

    /// <summary>Where a subsystem gets its logger.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>What the application was configured as.</summary>
    public AppConfig Config { get; }

    /// <summary>The content build this application reads from, and where it came from.</summary>
    public ContentMount Content { get; }

    /// <summary>
    ///     Everything the application can load by address, or <see langword="null" /> if it shipped
    ///     with no content.
    /// </summary>
    /// <remarks>
    ///     Nullable, and that is the honest shape rather than a convenience. A sample that draws a
    ///     triangle, a batch tool and a test each have nothing to load, and a host that refused to
    ///     start without a catalog would make the smallest possible program the hardest one to write.
    ///     <see cref="Content" /> says why when this is null.
    /// </remarks>
    public AssetManager? Assets => Content.Assets;

    /// <summary>
    ///     The world, its systems, its behaviours and its fixed-step accumulator, or
    ///     <see langword="null" /> if <see cref="AppConfig.UseEngine" /> is off.
    /// </summary>
    /// <remarks>
    ///     Driven by the host, once per frame, <em>before</em> <see cref="Game.OnUpdate" />. That
    ///     order is the useful one: <c>OnUpdate</c> is where an application reads the world it is
    ///     about to render, and reading it before it has been stepped renders last frame's positions.
    /// </remarks>
    public EngineLoop? Engine { get; }

    /// <summary>The same set, for code that resolves generically.</summary>
    public ServiceRegistry Registry { get; }

    /// <summary>Which build this is.</summary>
    public BuildVariant Variant => Config.Variant;
}
