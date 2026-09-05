// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Graphics.Null;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Xunit.Sdk;

namespace Vixen.Testing;

/// <summary>
///     A whole application, in this process, on a device that draws nothing, a file system that is a
///     dictionary, a clock that does not read the wall and an input source a test drives.
/// </summary>
/// <remarks>
///     <para>
///         <c>docs/plan/12</c> § "Test infrastructure worth building early" asks for this by name and
///         by parts: *"an in-process engine host with the Null backend, an in-memory VFS, a fake
///         clock, and a synthetic input source"*. ⚠ <b>All four parts already existed; none of them
///         was ever assembled.</b> <see cref="HeadlessPlatform" /> is the host,
///         <see cref="MemoryFileProvider" /> is the VFS, <see cref="AppConfig.FixedFrameTime" /> is
///         the clock and <see cref="HeadlessInputSource" /> plus <see cref="HeadlessPlatform.Post" />
///         are the input. So this is arrangement, and what it buys is the three refusals below rather
///         than tests that could not be written — the document's claim that "every later phase
///         depends on it" is refuted by the tree, which shipped 178 test projects without it.
///     </para>
///     <para>
///         ⚠ <b>The refusals are the point, and each one replaces a form that is green when it should
///         be red.</b> The document says whatever lands "has to answer the question the Null device
///         already taught this repository once": a host that quietly falls back to a backend drawing
///         nothing reports a healthy frame count and proves nothing. Three shapes of that live in
///         this seam:
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <b>An application with no graphics at all.</b> A game whose <c>OnConfigure</c>
///                 sets <c>Graphics.Enabled = false</c> builds, initialises and runs frames — and
///                 every assertion over its command log is an assertion over a device that was never
///                 opened. <see cref="Create" /> refuses to hand one back.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Frames that did not run.</b> <see cref="VixenApplication.RunFrame" /> returns
///                 normally on a stopping application after pumping events and drawing nothing, so a
///                 <c>for</c> loop of a hundred of them over an application that stopped on the first
///                 is a hundred calls, no simulation, an empty log and a green suite.
///                 <see cref="RunFrames" /> counts the frames the clock actually advanced through.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Input that reaches nothing.</b> <see cref="HeadlessInputSource.SetKey" /> sets
///                 the <em>polled</em> state and posts no event, while <c>Services.Input</c> is fed
///                 from the event stream in <c>PumpEvents</c> — so a test that "pressed" a key that
///                 way and then asserted on an action never sees it move.
///                 <see cref="PressKey" /> does both halves.
///             </description>
///         </item>
///     </list>
///     <para>
///         Linked test-only source rather than a project, for the reasons in
///         <c>Testing/Vixen.Testing.props</c>, and in its own props file because it names
///         <c>Vixen.App</c>, <c>Vixen.Platform.Headless</c> and <c>Vixen.Graphics.Null</c>.
///     </para>
/// </remarks>
sealed class TestApp : IDisposable {
    /// <summary>
    ///     What every test app asks for before the caller's own arguments are appended.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>--vixen-backend null</c> is a statement rather than a default. The default order is
    ///     Vulkan then Null, so a machine with a driver would give a suite a real GPU device — and
    ///     the same suite would take the Null one on a machine without, which is two different tests
    ///     wearing one name. Naming Null alone also makes <see cref="Device" />'s refusal reachable:
    ///     a caller that appends <c>--vixen-backend vulkan</c> overrides this, and is told.
    ///     <c>--vixen-fixed-step</c> is the fake clock, and the value is the one a capture run uses.
    /// </remarks>
    static readonly string[] Defaults = [
        "--vixen-headless",
        "--vixen-backend",
        "null",
        "--vixen-workers",
        "1",
        "--vixen-frame-limit",
        "0",
        "--vixen-fixed-step",
        "0.016666666666666666"
    ];

    readonly VixenApplication application;

    TestApp(VixenApplication application, HeadlessPlatform platform, TestFileSystem files) {
        this.application = application;
        Platform = platform;
        Files = files;
    }

    /// <summary>The application, for everything this class deliberately does not wrap.</summary>
    public VixenApplication Application => application;

    /// <summary>Everything the host built.</summary>
    public AppServices Services => application.Services;

    /// <summary>The platform, for its window list, its lifecycle and <see cref="Post" />.</summary>
    public HeadlessPlatform Platform { get; }

    /// <summary>The four mounts, as dictionaries a test can seed and read back.</summary>
    public TestFileSystem Files { get; }

    /// <summary>The clock, as the last frame saw it.</summary>
    public GameTime Time => application.Time;

    /// <summary>How much simulated time one <see cref="RunFrames" /> frame is worth.</summary>
    /// <remarks>
    ///     Read from the configuration rather than restated, so a caller that appended its own
    ///     <c>--vixen-fixed-step</c> gets the value the host is actually using.
    ///     <see cref="TimeSpan.Zero" /> means the wall clock is in charge, which only happens when a
    ///     caller asked for that.
    /// </remarks>
    public TimeSpan Step => Services.Config.FixedFrameTime ?? TimeSpan.Zero;

    /// <summary>The synthetic input source, for the polled half of the input surface.</summary>
    /// <remarks>
    ///     ⚠ Pointer position and modifier state are read from here by <c>InputDeviceSet.Submit</c>,
    ///     so setting them is meaningful. Setting a <em>key</em> here is not enough on its own — see
    ///     <see cref="PressKey" />.
    /// </remarks>
    public HeadlessInputSource Input => Platform.SimulatedInput;

    /// <summary>The device, which is the Null one or a failure saying what was opened instead.</summary>
    /// <remarks>
    ///     ⚠ The alternative is <c>(NullDevice)services.Graphics!.Device</c>, which is what a suite
    ///     writes today, and on the day a preference list changes it is an
    ///     <see cref="InvalidCastException" /> from inside an assertion. This says which line of the
    ///     fixture is wrong. Recording is on — <c>GraphicsHost</c> opens Null with
    ///     <c>Record = true</c> — so <c>device.Log()</c> from <c>RecordingBackend</c> is available to
    ///     a project that links both files.
    /// </remarks>
    public NullDevice Device =>
        Services.Graphics!.Device as NullDevice
        ?? throw new XunitException(
            $"This TestApp opened a {Services.Graphics!.Device.GetType().Name} rather than the Null "
            + "device, so nothing here is the deterministic in-process host it claims to be. Drop the "
            + "`--vixen-backend` argument that overrode TestApp's own."
        );

    /// <summary>Builds an application on the headless platform, the Null device and a memory VFS.</summary>
    /// <param name="game">The game to host. The application takes ownership and disposes it.</param>
    /// <param name="arguments">
    ///     Appended after <see cref="Defaults" />, so anything here wins — the command line is applied
    ///     in order and the last statement of a value is the one that survives.
    /// </param>
    /// <returns>The app, initialised on the first <see cref="RunFrames" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game" /> is null.</exception>
    /// <exception cref="XunitException">The application that was built has no graphics device.</exception>
    public static TestApp Create(Game game, params string[]? arguments) {
        ArgumentNullException.ThrowIfNull(game);

        var files = new TestFileSystem();
        var platform = new HeadlessPlatform(new() { FileSystem = files });

        VixenApplication built;

        try {
            built = VixenApp.Create([.. Defaults, .. arguments ?? []]).WithPlatform(platform).Build(game);
        } catch {
            // Nothing else owns it yet: VixenApplication takes the platform into its DisposeBag, and
            // there is no application.
            platform.Dispose();
            throw;
        }

        // ⚠ Refused rather than returned. `AppConfig.Graphics.Enabled` is false for a game that said
        // so in OnConfigure, and everything downstream still works: the host builds, initialises,
        // pumps events and runs frames, and `Services.Graphics` is null the whole time. A fixture
        // that then asserted over a command log would be asserting over nothing at all — the Null
        // device's own trap, one layer up, which is the failure doc 12 says this type has to answer.
        if (built.Services.Graphics is null) {
            built.Dispose();

            throw new XunitException(
                "This TestApp built an application with no graphics device: the game's OnConfigure set "
                + "AppConfig.Graphics.Enabled to false, so there is no device, no command log and no "
                + "frame to assert over. A test that wants a host without graphics should build it "
                + "through VixenApp.Create directly, where that is a statement rather than an accident."
            );
        }

        return new(built, platform, files);
    }

    /// <summary>Runs frames, and fails if they did not happen.</summary>
    /// <param name="count">How many.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is not positive.</exception>
    /// <exception cref="XunitException">Fewer frames were simulated than were asked for.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Counted off the clock, not off the loop.</b> <see cref="GameTime.FrameCount" />
    ///         advances in <c>Advance</c>, which a stopping frame returns before reaching — so this
    ///         measures frames that simulated rather than calls that were made. The distinction is
    ///         the whole reason the method exists: <c>--vixen-frames 1</c>, a game that called
    ///         <c>Stop</c>, or a window closed by an event all leave <c>RunFrame</c> returning
    ///         normally for ever, and the fixture that wrote <c>for (…) app.RunFrame();</c> sees a
    ///         hundred successful calls and an empty command log.
    ///     </para>
    ///     <para>
    ///         A property expressed as work rather than as elapsed time, which is what makes it
    ///         deterministic under load: with <see cref="Step" /> set, frame <i>N</i> is the same
    ///         instant of simulated time on a busy machine as on an idle one.
    ///     </para>
    /// </remarks>
    public void RunFrames(int count) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        application.Initialise();

        var before = application.Time.FrameCount;

        for (var frame = 0; frame < count; frame++) {
            application.RunFrame();
        }

        var ran = application.Time.FrameCount - before;

        if (ran != count) {
            throw new XunitException(
                $"{count} frame(s) were asked for and {ran} were simulated. RunFrame returns normally "
                + "on an application that is stopping, so the calls were all made and nothing "
                + $"happened — {StoppedBecause()}. Anything this test asserts about the frame is an "
                + "assertion about a frame that did not run."
            );
        }
    }

    /// <summary>Posts a platform event, as a real platform's event pump would deliver it.</summary>
    /// <param name="platformEvent">The event.</param>
    /// <returns>Whether the queue took it.</returns>
    /// <remarks>The escape hatch for every event shape this class does not name a helper for.</remarks>
    public bool Post(in PlatformEvent platformEvent) => Platform.Post(platformEvent);

    /// <summary>Presses a key: the event the device set reads, and the polled state beside it.</summary>
    /// <param name="key">Which key.</param>
    /// <param name="modifiers">What is held with it.</param>
    /// <remarks>
    ///     ⚠ <b>Both halves, because they are read by different callers and disagreeing is silent.</b>
    ///     <c>Services.Input</c> is fed by <c>InputDeviceSet.Submit</c> from the event stream, so an
    ///     action bound to this key only moves if the event is posted; <c>IInputSource.IsKeyDown</c>
    ///     is what a caller polling the platform directly reads, and only <see cref="Input" />'s
    ///     state answers that. A helper that did one of the two would make the other silently false.
    /// </remarks>
    public void PressKey(Key key, KeyModifiers modifiers = KeyModifiers.None) {
        Input.SetKey(key, true);
        Input.Modifiers = modifiers;
        Post(PlatformEvent.Keyboard(PlatformEventKind.KeyDown, WindowId, Timestamp, key, modifiers));
    }

    /// <summary>Releases a key, in both halves. See <see cref="PressKey" />.</summary>
    /// <param name="key">Which key.</param>
    /// <param name="modifiers">What is still held.</param>
    public void ReleaseKey(Key key, KeyModifiers modifiers = KeyModifiers.None) {
        Input.SetKey(key, false);
        Input.Modifiers = modifiers;
        Post(PlatformEvent.Keyboard(PlatformEventKind.KeyUp, WindowId, Timestamp, key, modifiers));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The application owns the platform, the device, the jobs and the game, and disposes them in
    ///     the reverse of the order it built them. There is nothing else here to release: the file
    ///     system is a dictionary.
    /// </remarks>
    public void Dispose() => application.Dispose();

    /// <summary>The window an event belongs to, or <c>0</c> for a head that opened none.</summary>
    uint WindowId => Services.Window?.Id ?? 0u;

    /// <summary>
    ///     The event timestamp. Frames rather than a stopwatch reading, so a posted event's stamp is
    ///     the same on every run of the build — this is the type whose whole argument is that.
    /// </summary>
    long Timestamp => application.Time.FrameCount;

    /// <summary>Why the loop stopped, in the words a reader can act on.</summary>
    string StoppedBecause() {
        var config = Services.Config;

        if (config.MaxFrames > 0 && application.Time.FrameCount >= config.MaxFrames) {
            return $"--vixen-frames {config.MaxFrames} was reached";
        }

        return Services.Window is { IsClosed: true }
            ? "the window was closed, which stops the application"
            : "the application was stopped, by Game.Stop or by an event";
    }

    /// <summary>Every standard location as a dictionary, so no test touches a disk.</summary>
    /// <remarks>
    ///     <para>
    ///         The alternative in this tree is a throwaway directory under the temp path, which is
    ///         what <c>Vixen.App.Tests</c>' own fixture was: real files, a real virus scanner, a real
    ///         chance that two runs of the same suite see each other's leftovers, and a directory left
    ///         behind whenever a run is killed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The four directory paths are empty, and that is the honest answer rather than a
    ///         stub.</b> <see cref="IFileSystemHost.ApplicationDirectory" /> documents emptiness for
    ///         content that is not a directory at all, and <c>WebFileSystemHost</c> — a browser, where
    ///         there are no native paths — answers exactly this way. A synthetic path would be a lie
    ///         that something could pass to <see cref="System.IO.File" /> and get a mysterious
    ///         failure from; the mounts are the interface.
    ///     </para>
    /// </remarks>
    internal sealed class TestFileSystem : IFileSystemHost {
        /// <summary><c>/app</c>: read-only, as a shipped build's content directory is.</summary>
        /// <remarks><c>Seed</c> works regardless — seeding is not writing.</remarks>
        public MemoryFileProvider App { get; } = new(isReadOnly: true);

        /// <summary><c>/data</c>.</summary>
        public MemoryFileProvider Data { get; } = new();

        /// <summary><c>/cache</c>.</summary>
        public MemoryFileProvider Cache { get; } = new();

        /// <summary><c>/temp</c>.</summary>
        public MemoryFileProvider Temp { get; } = new();

        /// <inheritdoc />
        /// <remarks>Empty. There is no directory; see the type's remarks.</remarks>
        public string ApplicationDirectory => string.Empty;

        /// <inheritdoc />
        /// <remarks>Empty. See <see cref="ApplicationDirectory" />.</remarks>
        public string DataDirectory => string.Empty;

        /// <inheritdoc />
        /// <remarks>Empty. See <see cref="ApplicationDirectory" />.</remarks>
        public string CacheDirectory => string.Empty;

        /// <inheritdoc />
        /// <remarks>Empty. See <see cref="ApplicationDirectory" />.</remarks>
        public string TemporaryDirectory => string.Empty;

        /// <inheritdoc />
        /// <remarks>
        ///     True, and not only because it is tidy: a sandboxed platform is the one where a native
        ///     path assembled by hand does not work, which is what is true here.
        /// </remarks>
        public bool IsSandboxed => true;

        /// <inheritdoc />
        public void MountStandardLocations(VirtualFileSystem fileSystem) {
            ArgumentNullException.ThrowIfNull(fileSystem);

            fileSystem.Mount(MountPoints.App, App);
            fileSystem.Mount(MountPoints.Data, Data);
            fileSystem.Mount(MountPoints.Cache, Cache);
            fileSystem.Mount(MountPoints.Temp, Temp);
        }

        /// <inheritdoc />
        /// <remarks>Granted. There is no user here to ask.</remarks>
        public ValueTask<bool> RequestPermissionAsync(
            PermissionKind permission,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult(true);
    }
}
