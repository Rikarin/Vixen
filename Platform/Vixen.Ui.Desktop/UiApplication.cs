// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Platform;
using Vixen.Platform.Desktop;
using Vixen.Platform.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.Reactive;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Ui.Desktop;

/// <summary>An application whose whole content is a user interface: a window, a device, and the four steps of a frame.</summary>
/// <remarks>
///     <para>
///         <b>The four steps are worth naming, because they are the whole loop:</b> pump the
///         platform's events into the document, run the layout and draw passes, turn the draw list
///         into geometry, and record that geometry into a frame. Only the last of the four knows what
///         a GPU is — which is why <see cref="UiApplicationOptions.Frames" /> means something on a
///         machine with no Vulkan at all.
///     </para>
///     <para>
///         ⚠ <b>This existed three times before it existed once.</b> <c>Samples/02-HelloUi</c>,
///         <c>Tools/Vixen.Templates</c>' <c>AppHost</c> and <c>Vixen.Editor.Host</c>'s
///         <c>EditorHost</c> each carried it, and the copies had already diverged in ways nothing
///         failed on: the sample never called <see cref="UiRenderer.Compose" />, so every translucent
///         subtree in it drew at full strength; two of the three could not open a second window; and
///         one of the three cached its tessellation while the others rebuilt every path in the draw
///         list sixty times a second for a window where nothing had moved. What is here is the union
///         of the correct halves.
///     </para>
///     <para>
///         ⚠ <b>No <c>Vixen.Engine</c> and no <c>Vixen.App</c>, and the absence is the reason this
///         assembly exists.</b> That host owns a frame loop built around an ECS world and a
///         fixed-step accumulator; an interface's loop redraws a document. An application that wanted
///         a window used to choose between writing this file and dragging a scene graph behind it.
///     </para>
///     <para>
///         ⚠ <b>It draws every frame rather than when something changes.</b> Redrawing only on input
///         is the right end state for a desktop application and it is not free: every animation,
///         every timer and every background task's progress has to say that it moved, and one that
///         forgets leaves a progress bar frozen at forty per cent. Said out loud here rather than
///         left to be discovered on a laptop battery. The tessellation <i>is</i> skipped for a window
///         whose drawing did not change — see <see cref="UiWindowSurface.Tessellate" /> — which is
///         most of the cost of a still frame.
///     </para>
/// </remarks>
/// <example>
///     The whole of an application's <c>Main</c>:
///     <code>
///     static int Main(string[] arguments) =&gt;
///         UiApplication.Run(
///             new UiApplicationOptions {
///                 Title = "Hello",
///                 Content = () =&gt; new Shell(),
///                 Styles = { VixenUtilityStyles.Css }
///             },
///             arguments
///         );
///     </code>
/// </example>
public sealed class UiApplication : IDisposable {
    /// <summary>The development assembly, looked for by name once per process.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Because a <c>[ModuleInitializer]</c> runs when a module is *loaded*, and the CLR
    ///         loads lazily.</b> <c>Vixen.Ui.Desktop.HotReload</c> exists to fill
    ///         <see cref="UiDevelopment" />'s hooks and deliberately has no type anybody names — that
    ///         is what makes referencing it the whole of the opt-in — so nothing ever triggers the
    ///         load and the initializer never runs. The assembly ships in the output directory and
    ///         does nothing at all, which is exactly what happened the first time this was wired.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>By name and in a <c>try</c>, which is the whole cost of the arrangement.</b> A
    ///         Release build does not resolve the reference, so the assembly is not beside the
    ///         executable and this throws <see cref="FileNotFoundException" /> — the ordinary case,
    ///         once, at start-up. What it must not do is <i>reference</i> the assembly: that is the
    ///         thing that would put a non-trimmable development tool into every shipped application.
    ///     </para>
    /// </remarks>
    const string Development = "Vixen.Ui.Desktop.HotReload";

    static UiApplication() {
        try {
            var assembly = System.Reflection.Assembly.Load(Development);

            // ⚠ **Loading it is not enough, and this is the line the first attempt was missing.** A
            // module initializer is triggered by the first *access* to something in the module, the
            // way a type initializer is — not by the assembly being loaded. Nothing here accesses
            // anything in it, deliberately, so the assembly sat in the output directory fully loaded
            // and completely inert. `RunModuleConstructor` is the API that says "run it now".
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        } catch (Exception exception) when (exception is FileNotFoundException or BadImageFormatException) {
            // The shipped build. Nothing to do and nothing to say: the hooks stay null and the
            // application mounts its content the ordinary way.
        }
    }

    readonly UiApplicationOptions options;
    readonly IPlatform platform;
    readonly IWindow window;
    readonly PlatformWindowHost windows;
    readonly PlatformTextInput textInput;

    /// <summary>One shared atlas, because a glyph rasterised for one window is the same glyph in the next.</summary>
    readonly GlyphFieldCache glyphs = new(new GlyphAtlas(1024, 1024));

    readonly List<UiWindowSurface> surfaces = [];

    VulkanDevice? device;
    TransientResourcePool? pool;
    RenderGraph? graph;
    UiShaders shaders;

    bool running = true;
    bool lost;
    bool resized;

    /// <summary>Builds an application over a platform and a window somebody else made.</summary>
    /// <remarks>
    ///     ⚠ <b>Internal because the supported entry point is <see cref="Run(UiApplicationOptions)" />,
    ///     and visible to the tests because the alternative is a suite that cannot run the loop.</b>
    ///     <c>Vixen.Platform.Headless</c> makes windows that have a size, an id and an event stream
    ///     and show nobody anything, which is exactly the run <see cref="UiApplicationOptions.Frames" />
    ///     exists to make meaningful — everything above the RHI executes and nothing needs a display
    ///     server or a driver.
    /// </remarks>
    internal UiApplication(UiApplicationOptions options, IPlatform platform, IWindow window) {
        this.options = options;
        this.platform = platform;
        this.window = window;

        Document = new UiDocument(window.FramebufferSize.X / Scale, window.FramebufferSize.Y / Scale);

        if (options.InstallControlTheme) {
            // The control set's theme, as a user-agent stylesheet. Everything loaded after it
            // out-specifies it simply by being an author sheet, which is the arrangement the whole
            // theme is designed around.
            ControlTheme.Install(Document);
        }

        // ⚠ **A user-agent sheet of the host's own, and it is four declarations that every
        // application would otherwise have to discover.** See `WindowStyle` for what each one is for
        // and what its absence looks like. Loaded after `ControlTheme` so that the two agree about
        // origin, and before the author sheets so that anything in `Styles` beats it.
        Document.Load(WindowStyle, StyleOrigin.UserAgent);

        foreach (var css in options.Styles) {
            Document.Load(css);
        }

        foreach (var className in options.RootClasses) {
            Document.Root.AddClass(className);
        }

        if (options.InstallSystemFont) {
            SystemFonts.Install(Document);
        }

        // ⚠ After the sheets and the font, before the content. A component's `Build` reads class
        // names against the cascade as it goes and measures text against whatever face is registered,
        // so mounting first would resolve the first frame against an empty stylesheet and a
        // zero-width font — which settles a frame later and reads as a flash of unstyled interface.
        options.Configure?.Invoke(Document);

        // ⚠ Installed on the document, which is what makes a torn-off dock group a real window rather
        // than a rectangle drawn inside this one. Nothing in `Vixen.Ui.Controls.Advanced` names this
        // type: the docking host asks the document, the document asks `IUiWindowHost`, and this
        // assembly is the only one in the chain allowed to know what a window is.
        windows = new PlatformWindowHost(platform, Document, window);
        textInput = new PlatformTextInput(platform.TextInput);

        // ⚠ `Mount` first and `Content` second, because a development build supplies the first to
        // put its components under a `HotReloadHost` — see `UiApplicationOptions.Mount`, which is
        // the whole of what a `.vxml` reload needs from this assembly.
        if (Mounted(options) is { } mounted) {
            // ⚠ **On the component's host element, which is not the root and not the component's
            // first tag.** A component draws into a host of its own — `<app-shell>` for a
            // `Shell.vxml` — and that element is what the window has to be told to fill. Without the
            // class it is a flex item with no `flex-grow` in a row, so it comes out as wide as its
            // widest word and the whole interface renders in a strip down the left of a black
            // window. That is what this sample looked like before the rule existed, and the symptom
            // reads as a layout-engine bug rather than as a missing declaration.
            mounted.Root.AddClass(ContentClass);
            Content = mounted;
        }

        // ⚠ **The process-wide start hook, before the options' own.** It is what attaches a
        // stylesheet watcher in a development build, and an application's own `Started` may
        // reasonably depend on that having happened — the reverse is not true.
        if (UiDevelopment.Started is { } observing) {
            Started += observing;
        }

        // ⚠ The options' three hooks, subscribed to the three events. They exist twice because the
        // shortest way to run an application is `UiApplication.Run(options)`, which constructs this
        // object itself and hands the caller nothing to subscribe to — so an options object that
        // could not carry them would force every application wanting one to open its own window.
        // Last, so that a `Started` handler sees a document with the content already in it.
        if (options.Started is { } started) {
            Started += started;
        }

        if (options.Frame is { } framed) {
            Frame += framed;
        }

        if (options.Stopping is { } stopping) {
            Stopping += stopping;
        }
    }

    /// <summary>The class the mounted content's host element carries.</summary>
    /// <remarks>
    ///     Public because it is nameable from a stylesheet, which is the whole point of it being a
    ///     class rather than an inline style: an application that wants its content laid out some
    ///     other way writes <c>.ui-window-content { … }</c> in its own sheet and wins on origin.
    /// </remarks>
    public const string ContentClass = "ui-window-content";

    /// <summary>What a window is, said once so that no application has to discover it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A user-agent sheet, on exactly a browser's terms.</b> No browser makes an author
    ///         write <c>html, body { height: 100% }</c> before a page can fill a window, and neither
    ///         should this: every declaration below is one that every application would otherwise
    ///         have had to find out about, and every one of them fails in a way that reads as an
    ///         engine bug rather than as a missing line.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The root is a column, and CSS's initial value is <c>row</c>.</b> A window's
    ///         content stacks — a menu bar, then a body, then a status bar — and a root left as a row
    ///         lays those three out side by side, each as wide as its own text. Being a column also
    ///         makes width the <i>cross</i> axis, where <c>align-items: stretch</c> (the CSS initial,
    ///         and the engine's) already fills it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="ContentClass" /> grows, and this is the declaration the sample went
    ///         without.</b> A component draws into a host element of its own, so what is under the
    ///         root is not the markup's first tag but <c>&lt;app-shell&gt;</c> — an element no file
    ///         mentions and nothing styles. With the root a column that element is full width and
    ///         content <i>height</i>, so an interface that meant to fill the window ends up as tall
    ///         as its content with the clear colour under it; with the root left a row it comes out
    ///         as a strip down the left instead. One <c>flex-grow</c> answers both.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And <c>min-height: 0</c>, which is the one that bites later rather than
    ///         immediately.</b> A flex item's minimum size is its content, so a scroller inside the
    ///         content host cannot shrink below the height of everything in it — the panel grows past
    ///         the window instead of scrolling, and the scrollbar it draws has nothing to do. It
    ///         looks like the scroller not working.
    ///     </para>
    /// </remarks>
    const string WindowStyle = """
        root {
            flex-direction: column;
            align-items: stretch;
        }

        .ui-window-content {
            flex-grow: 1;
            min-height: 0px;
        }
        """;

    /// <summary>Builds the interface, by whichever of the three routes is available.</summary>
    /// <remarks>
    ///     ⚠ <b>The application's own hook wins, then the process's, then the ordinary build.</b> An
    ///     application that set <see cref="UiApplicationOptions.Mount" /> asked for something
    ///     specific and gets it; one that did not, in a process where a development assembly filled
    ///     <see cref="UiDevelopment.Mount" />, gets hot reload without having written a line about
    ///     it; and a shipped build resolves neither and builds the content.
    /// </remarks>
    Component? Mounted(UiApplicationOptions options) {
        if (options.Content is not { } content) {
            // ⚠ Still offered to the options' own hook, because an application may legitimately mount
            // something it did not describe as a factory. `UiDevelopment` is not offered the same,
            // since it has nothing to build from.
            return options.Mount?.Invoke(Document, Document.Root);
        }

        if (options.Mount is { } mount) {
            return mount(Document, Document.Root);
        }

        if (UiDevelopment.Mount is { } development) {
            return development(Document, Document.Root, content);
        }

        var component = content();
        BuildContext.BuildInto(component, Document, Document.Root);

        return component;
    }

    /// <summary>The component the interface was built from, once it has been.</summary>
    /// <remarks>
    ///     ⚠ Public so that a caller which handed over a <see cref="UiApplicationOptions.Mount" />
    ///     can find what it mounted without keeping its own reference — and so that a hot reload's
    ///     handler can tell whether the component it is looking at is still the live one. A
    ///     *recreated* component is a different object, so this is not it: ask the reload host.
    /// </remarks>
    public Component? Content { get; private set; }

    /// <summary>The document the loop lays out, draws and dispatches into.</summary>
    /// <remarks>
    ///     ⚠ Public because an application legitimately needs it: a hot-reload watcher is constructed
    ///     over it, a test drives it with no window at all, and a stylesheet loaded at run time goes
    ///     through <see cref="UiDocument.Load" />. What an application should <i>not</i> do through
    ///     it is build its interface — that is <see cref="UiApplicationOptions.Content" />, and a
    ///     <c>.vxml</c>.
    /// </remarks>
    public UiDocument Document { get; }

    /// <summary>The window the application was opened on.</summary>
    public IWindow Window => window;

    /// <summary>Everything the operating system does for this application.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Its absence is what made three finished, six-platform capabilities unreachable
    ///         from application code.</b> <see cref="Run(UiApplicationOptions)" /> is the only public
    ///         way to start an application and the constructor is internal by design, so a caller had
    ///         <see cref="Window" /> and <see cref="Document" /> and no route at all to
    ///         <see cref="IPlatform.Clipboard" />, <see cref="IPlatform.Dialogs" />,
    ///         <see cref="IPlatform.Displays" /> or <see cref="IPlatform.Lifecycle" /> — none of which
    ///         a UI framework can offer from <c>Core/</c>, because <c>Vixen.Platform</c> sits above
    ///         it. No cut and paste, no file dialogs, no veto on quit, and nothing in the framework
    ///         to point at as the reason.
    ///     </para>
    ///     <para>
    ///         <b>Where an application reaches it is <see cref="UiApplicationOptions.Started" />,</b>
    ///         which is handed this object and runs after the interface is built and before the first
    ///         frame — <see cref="UiApplicationOptions.Configure" /> is offered the document alone
    ///         and deliberately stays that way, because what it is for is loading sheets and
    ///         registering types before the content mounts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read <see cref="IPlatform.Capabilities" /> before using most of it.</b> A
    ///         headless build has no displays and no pickers, and a Linux session may have no picker
    ///         either — <c>PlatformExtensions.Pickers()</c> is that question for the one service
    ///         where the "nothing chosen" answer is indistinguishable from a cancellation.
    ///     </para>
    ///     <para>
    ///         <b>Threading.</b> The platform is owned by the loop thread. Every member of it must be
    ///         called from there, which for an application using this loop means from
    ///         <see cref="Started" />, <see cref="Frame" />, <see cref="Stopping" /> or an event
    ///         handler — not from a continuation that resumed on a pool thread. See
    ///         <see cref="IPlatform" />, which says why that is the operating systems' restriction
    ///         rather than one of ours.
    ///     </para>
    /// </remarks>
    public IPlatform Platform => platform;

    /// <summary>How many frames have been drawn.</summary>
    public int FrameCount { get; private set; }

    /// <summary>The long operations this application is running, and what they have got to.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Pumped once a frame by the loop, which is the half a manager cannot do for
    ///         itself.</b> <see cref="BackgroundTaskManager" /> queues everything the work reports
    ///         and applies it in <see cref="BackgroundTaskManager.Pump" />; without a caller for
    ///         that, every task sits at nought per cent and no progress bar bound to one ever moves.
    ///         An application that uses this loop gets the pump for free and never writes it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Pumped before <see cref="Frame" />, so a handler reads this frame's numbers
    ///         rather than last frame's.</b> The alternative costs one frame of lag on every
    ///         progress bar, which is exactly the artefact the ordering note on <see cref="Frame" />
    ///         is about.
    ///     </para>
    ///     <para>
    ///         Disposed with the application, which cancels whatever is still running and stops the
    ///         report queue growing behind a loop that has stopped draining it.
    ///     </para>
    /// </remarks>
    public BackgroundTaskManager Tasks { get; } = new();

    /// <summary>Raised once, after the interface is built and before the first frame is pumped.</summary>
    /// <remarks>
    ///     Where an application wires anything that needs the document and the window to both exist —
    ///     a hot-reload watcher, a window title bound to a model, a first-run dialog.
    /// </remarks>
    public event Action<UiApplication>? Started;

    /// <summary>Raised once a frame, after the events are pumped and before the document is updated.</summary>
    /// <remarks>
    ///     ⚠ <b>Before <see cref="UiDocument.Update" />, so a handler that changes a signal is drawn
    ///     in the same frame.</b> The reverse order costs a frame of latency on everything driven from
    ///     here — a spinner's phase, a progress bar's value, a poll of a watcher — which is visible as
    ///     an animation that lags the input that started it.
    /// </remarks>
    public event Action<UiApplication, UiFrame>? Frame;

    /// <summary>Raised once, after the loop stops and while the document is still alive.</summary>
    /// <remarks>
    ///     ⚠ Before the document goes, which is the reason it is not <see cref="Dispose" />. Anything
    ///     that persists state reads it out of the tree — a docking arrangement, a window placement,
    ///     a form's contents — and a disposed document has none.
    /// </remarks>
    public event Action<UiApplication>? Stopping;

    /// <summary>Opens a window, runs the interface in it, and returns a process exit code.</summary>
    /// <param name="options">What the application is.</param>
    /// <returns>Zero.</returns>
    public static int Run(UiApplicationOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        // ⚠ The GPU surface has to be asked for when the window is made. SDL needs the Vulkan window
        // flag at creation time, and a window made without it has nothing to present to — which
        // surfaces much later, as a device that will not create a swapchain, in a place that looks
        // nothing like this line. UiApplicationOptions.Platform says the same thing to anyone
        // supplying their own, who has no flag to be passed and must simply always be true of it.
        using IPlatform platform = options.Platform?.Invoke(options)
            ?? new DesktopPlatform(
                new() {
                    Organisation = options.Organisation,
                    Application = options.Application,
                    RequestGpuSurface = true
                }
            );

        using var window = platform.CreateWindow(
            new WindowOptions {
                Title = options.Title,
                Size = options.Size,
                IsVisible = true,
                IsResizable = options.IsResizable
            }
        );

        using var application = new UiApplication(options, platform, window);

        return application.Run();
    }

    /// <summary>The same, having first read the arguments every Vixen application understands.</summary>
    /// <param name="options">What the application is.</param>
    /// <param name="arguments">The process arguments.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///     Exactly one argument today — <c>--frames N</c>, which runs N frames and exits — and it is
    ///     here rather than in every <c>Main</c> because a CI job that cannot say "start, present and
    ///     stop" about an application is a CI job that only builds it.
    /// </remarks>
    public static int Run(UiApplicationOptions options, params ReadOnlySpan<string> arguments) {
        ArgumentNullException.ThrowIfNull(options);

        for (var i = 0; i + 1 < arguments.Length; i++) {
            if (arguments[i] is "--frames" or "--vixen-frames"
                && int.TryParse(arguments[i + 1], CultureInfo.InvariantCulture, out var count)) {
                options.Frames = Math.Max(0, count);
            }
        }

        return Run(options);
    }

    /// <summary>Asks the loop to stop at the end of this frame.</summary>
    /// <remarks>
    ///     A menu item's Quit, and the honest way to write one: the frame in progress finishes, the
    ///     document is still alive when <see cref="Stopping" /> is raised, and everything is torn down
    ///     in the order the window close path uses.
    /// </remarks>
    public void Stop() => running = false;

    /// <summary>Runs until the window closes, or for <see cref="UiApplicationOptions.Frames" /> frames.</summary>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is where the signal graph is told which thread owns it, and until this line
    ///         existed nothing in the tree ever told it.</b> Every write in <c>Vixen.Ui.Reactive</c>
    ///         calls <c>ReactiveGraph.AssertOwningThread</c>, and that assert compares against
    ///         <see cref="ReactiveGraph.OwningThread" /> — a static that was null in every shipping
    ///         build, so a plug-in writing a signal from a
    ///         pool thread was reported by nobody and corrupted an edge list instead. The loop thread
    ///         is the graph's thread by construction: everything under this line reads and writes
    ///         signals on it, and <see cref="EffectScheduler.Post" /> is the only sanctioned way in
    ///         from anywhere else.
    ///     </para>
    ///     <para>
    ///         ⚠ Restored rather than cleared on the way out, because the owner is process-wide. A
    ///         test host that runs two applications one after the other, or an editor that opens a
    ///         second graph, must find the static as it left it.
    ///     </para>
    /// </remarks>
    internal int Run() {
        var previousOwner = ReactiveGraph.OwningThread;
        ReactiveGraph.OwningThread = Thread.CurrentThread;

        try {
            return Loop();
        } finally {
            ReactiveGraph.OwningThread = previousOwner;
        }
    }

    int Loop() {
        var clock = Stopwatch.StartNew();
        var previous = TimeSpan.Zero;

        // ⚠ Before `Started` and therefore before the first frame. The platform posts no event for
        // the appearance it already had at boot — there is nothing to notice — so a host that only
        // handled `SystemColorSchemeChanged` would draw every frame of a session against the wrong
        // palette on a machine whose appearance never changed, which is most of them.
        PlatformInput.ApplyColorScheme(Document, platform.ColorScheme);

        Started?.Invoke(this);

        while (running && (options.Frames == 0 || FrameCount < options.Frames)) {
            var now = clock.Elapsed;
            var delta = now - previous;
            previous = now;

            Pump();

            if (!running) {
                break;
            }

            // ⚠ Once per frame, however many resize events arrived. A window opened maximised on a
            // 4K display produces a burst of them, and handling each one where it arrives means a
            // `vkDeviceWaitIdle` and a full swapchain rebuild several times before a single frame is
            // drawn — every rebuild handing the compositor images whose contents are undefined, which
            // is what the flicker is. It also keeps the layout and the geometry in step: both are
            // read from the same framebuffer size, once, before anything uses it.
            //
            // Only the main window's, because only the main window's size is the document's. Every
            // other surface was resized by the window host as the event arrived, and its swapchain is
            // rebuilt from the size comparison in `UiWindowSurface.Recreate`.
            if (resized) {
                resized = false;

                Document.Resize(window.FramebufferSize.X / Scale, window.FramebufferSize.Y / Scale);
            }

            // ⚠ Before `Frame`, so a handler that reads `Tasks` sees what the work reported since
            // the last frame rather than what it had reported the frame before that. This is the
            // only call to it an application using this loop gets, and a loop that skipped it would
            // leave `Tasks` an API that compiles, accepts work, and never visibly does anything.
            Tasks.Pump();

            Frame?.Invoke(this, new UiFrame(now, delta));

            // ⚠ `Document.Tick`, and not `Document.Gestures.Tick`. The recogniser is only one of the
            // four things that needs the clock; the others are `UiDocument.Ticked`, which is what an
            // `Overlay`'s delay and a toast's dismissal hang on, `UiDocument.Now`, which is what a
            // toast is stamped with, and the CSS animator. That last one fails in the direction
            // nobody expects: a transition stamped against a clock that never leaves zero makes no
            // progress on any frame, so a declared `transition` holds the property at the value it
            // was leaving rather than jumping to the new one. All three copies of this loop got it
            // wrong at some point, which is a copied frame loop going wrong three times.
            Document.Tick(now);

            Document.Update();

            // ⚠ After the update, because the cursor is a computed style and the hover the pointer
            // moved this frame is what decides whose. One of the two places this call has to be —
            // the other is `EditorHost` — and a host that forgets it is a host where every
            // `cursor-*` class in every theme resolves correctly and shows nothing.
            PlatformCursor.Apply(windows);

            // ⚠ Beside the cursor and for the same reason: the focus moves between frames and the
            // caret moves within one, so neither has an event to hang on that is not "the frame".
            // Until this line existed nothing in the framework ever called `ITextInput.Activate`, so
            // a focused field on the web or a phone received nothing at all — and desktop only
            // worked because SDL leaves text input running.
            textInput.Apply(windows);

            Document.Draw();

            Sync();
            Tessellate();
            Present();

            FrameCount++;
        }

        device?.WaitIdle();

        // ⚠ Text input is process state, not window state: SDL leaves it running after the window
        // that asked for it has gone, and a second application started in the same process would
        // find the keyboard already handed to an input method.
        textInput.Deactivate();

        Stopping?.Invoke(this);

        return 0;
    }

    void Pump() {
        foreach (var platformEvent in platform.PumpEvents()) {
            switch (platformEvent.Kind) {
                case PlatformEventKind.Quit:
                    running = false;

                    // ⚠ Not `return`. The rest of this pump is the frame's input — a click, a
                    // keystroke, the resize that arrived with it — and dropping it is what makes a
                    // close that arrived in the same batch as a quit never reach anything.
                    break;

                case PlatformEventKind.WindowCloseRequested:
                    // ⚠ Asked first, and the answer decides whose close this is. A torn-off panel's
                    // window is handled by the host — it becomes a request the docking host answers
                    // by bringing the panels home — and only a close nobody claimed is the
                    // application being asked to quit.
                    if (windows.Handle(platformEvent)) {
                        break;
                    }

                    running = false;
                    break;

                case PlatformEventKind.WindowResized:
                case PlatformEventKind.WindowDpiChanged:
                    windows.Handle(platformEvent);

                    // Recorded rather than acted on: the window is the authority on its own size and
                    // the frame reads it once, above.
                    if (platformEvent.WindowId == window.Id) {
                        resized = true;
                    }

                    break;

                case PlatformEventKind.WindowMoved:
                    windows.Handle(platformEvent);
                    break;

                case PlatformEventKind.Suspending:
                    Release();
                    break;

                case PlatformEventKind.SystemColorSchemeChanged:
                    // ⚠ Not routed by window id, because it names none. The appearance is a setting
                    // of the machine and every surface of the document answers `@media
                    // (prefers-color-scheme: …)` with it — falling through to the default branch
                    // would resolve window 0, find nothing, and drop the change silently.
                    PlatformInput.ApplyColorScheme(Document, platform.ColorScheme);
                    break;

                default:
                    // ⚠ Routed by the window the event names. Two windows do not share a coordinate
                    // space, so an event delivered to the wrong surface lands at the right numbers in
                    // the wrong place — which looks exactly like a hit-testing bug and is a routing
                    // one. A window this host does not know about is one that has just been closed,
                    // and its last few events are dropped rather than sent somewhere arbitrary.
                    if (windows.TryResolve(platformEvent.WindowId, out var target)) {
                        PlatformInput.Dispatch(Document, target, platformEvent);
                    }

                    break;
            }
        }
    }

    /// <summary>Brings the per-window state into line with the document's surfaces.</summary>
    /// <remarks>
    ///     Once a frame rather than on an event, because a surface appears and disappears from inside
    ///     a docking host — a tab dragged onto the desktop, a window closed — and a host that had to
    ///     be told would be a second place that has to agree with the first.
    /// </remarks>
    void Sync() {
        for (var i = surfaces.Count - 1; i >= 0; i--) {
            if (!surfaces[i].Surface.IsRemoved) {
                continue;
            }

            // The images this window's swapchain owns may still be in flight. A window closed while a
            // frame referencing it is queued is a use-after-free the validation layers will name and
            // the driver will not.
            device?.WaitIdle();

            surfaces[i].Dispose();
            surfaces.RemoveAt(i);
        }

        foreach (var surface in Document.Surfaces) {
            if (Find(surface) is not null || !windows.TryWindow(surface, out var opened)) {
                continue;
            }

            surfaces.Add(new UiWindowSurface(surface, opened));
        }
    }

    UiWindowSurface? Find(UiSurface surface) {
        foreach (var candidate in surfaces) {
            if (ReferenceEquals(candidate.Surface, surface)) {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Turns each window's draw list into vertices.</summary>
    /// <remarks>
    ///     ⚠ <b>Built whether or not there is a device.</b> On a headless run — no surface, no Vulkan
    ///     — everything above the RHI still executes, which is what makes
    ///     <see cref="UiApplicationOptions.Frames" /> a smoke test of the whole framework rather than
    ///     only of the backend.
    /// </remarks>
    void Tessellate() {
        foreach (var surface in surfaces) {
            surface.Tessellate(glyphs);
        }
    }

    /// <summary>How many physical pixels one device-independent one is, never zero.</summary>
    float Scale => window.DpiScale <= 0f ? 1f : window.DpiScale;

    void Present() {
        if (lost || !EnsureDevice()) {
            return;
        }

        device!.BeginFrame();

        foreach (var surface in surfaces) {
            surface.IsDrawing = false;

            if (!surface.Ensure(device, shaders)) {
                continue;
            }

            surface.Recreate(device);

            switch (surface.Acquire(device, out var view)) {
                case null:
                    lost = true;
                    continue;

                case false:
                    continue;

                default:
                    break;
            }

            surface.Acquired = view;
            surface.IsDrawing = true;

            Record(surface);
        }

        // ⚠ Ended even when nothing was drawn, and this is not tidiness. `BeginFrame` waits on this
        // slot's fence and resets it; `EndFrame` is what submits the signal that makes the wait
        // return. Leaving without it means the frame counter never advances, so the next frame waits
        // on the same reset fence with no submission behind it — `vkWaitForFences` with no timeout,
        // which is a hang rather than a dropped frame.
        device.EndFrame();

        foreach (var surface in surfaces) {
            if (!surface.IsDrawing) {
                continue;
            }

            switch (surface.SwapChain!.Present()) {
                case SwapChainStatus.OutOfDate:
                    surface.Recreate(device, force: true);
                    break;

                // ⚠ Suboptimal is a hint, and rebuilding on it unconditionally is the flicker. It
                // means "this still presents correctly, but the surface would prefer other
                // parameters" — and a compositor that keeps saying so, which a scaled 4K surface
                // does, then gets a `vkDeviceWaitIdle` and a fresh set of undefined images every
                // single frame. Honoured only when the window has actually changed size, which
                // `Recreate` is what decides.
                case SwapChainStatus.Suboptimal:
                    surface.Recreate(device);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>Records one window's frame into its own command list.</summary>
    /// <remarks>
    ///     ⚠ <b>A command list and a graph execution per window, rather than one of each for the
    ///     frame.</b> Each window has its own backbuffer, its own extent and its own imported
    ///     resource, and a graph carrying two windows' passes would have to be told they are
    ///     independent — which is a thing to get wrong in exchange for one fewer submission.
    /// </remarks>
    void Record(UiWindowSurface surface) {
        var scale = surface.Scale;
        var viewport = surface.Viewport;
        var frame = surface.Frame;
        var renderer = surface.Renderer!;

        using var commands = device!.BeginCommandList(QueueKind.Graphics, "ui");

        var backbuffer = graph!.ImportTexture(
            surface.SwapChain!.CurrentTexture,
            surface.Acquired,
            new(
                surface.SwapChain.Format,
                surface.SwapChain.Size.X,
                surface.SwapChain.Size.Y,
                TextureUsage.ColourTarget,
                Name: "backbuffer"
            ),
            ResourceState.Undefined,
            ResourceState.Present
        );

        // ⚠ Before the pass, not inside it. The atlas upload is a transfer and a layout transition,
        // and a render pass is the one place a Vulkan command list may not do either — which is why
        // `UiRenderer` splits `Upload` from `Record` at all.
        renderer.Upload(commands, frame, glyphs.Atlas);

        // ⚠ After `Upload` and outside the pass, for both of the reasons above and one more. A
        // translucent subtree that draws more than one thing is rendered into a surface of its own
        // and blended once — CSS Compositing 1 § 3 — and this is what renders those surfaces. It
        // opens a render pass per group, so it cannot be inside one; and it draws from the vertices
        // `Upload` just wrote, so it cannot be before it. Recording it onto `commands` here puts it
        // ahead of `graph.Execute` on the same list, which is the order the dependency runs in.
        //
        // ⚠ The same viewport and scale as `Record` below. A group's surface is viewport-sized and
        // drawn with the frame's own projection, so a different number here would place the subtree
        // somewhere its composite quad does not look for it.
        //
        // ⚠ And the colour the pass below clears to, which is what a `backdrop-filter` reads.
        // `Compose` can re-render the interface's own draw list and nothing else, so a backdrop
        // captured without this would be the panels above the element instead of the window under it
        // — and transparent where it should be opaque, which composites a blurred copy over the sharp
        // original instead of replacing it.
        //
        // ⚠ `Samples/02-HelloUi` did not call this at all, for months, and nothing failed: without a
        // composite the group's contents are drawn in place at *full* strength, so every disabled
        // control in the control gallery came out opaque rather than faded.
        renderer.Compose(commands, frame, viewport, scale, new UiBackdropSource(options.Ground));

        graph.AddPass("ui", pass => {
            pass.ColourAttachment(backbuffer, LoadAction.Clear, options.Ground);
            pass.SideEffect();

            // ⚠ The *logical* viewport and the DPI scale, not the swapchain's size. The geometry is
            // in device-independent units — the document is 1280×800 on a display whose framebuffer
            // is 2560×1600 — and the projection has to map those units, while the scissor has to come
            // out in framebuffer pixels. Passing the framebuffer for both draws the whole interface
            // into the top-left quarter of the window.
            pass.Execute(context => renderer.Record(context.CommandList, frame, viewport, scale));
        });

        graph.Execute(commands);
        graph.Reset();

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);
    }

    /// <summary>Builds everything GPU-shaped, once there is a surface to present to.</summary>
    /// <returns>Whether there is one.</returns>
    /// <remarks>
    ///     Lazy on purpose: a headless run never gets a surface, and the answer to that is to draw
    ///     nothing rather than to fail. It is also what lets
    ///     <see cref="UiApplicationOptions.Frames" /> mean something on a machine with no GPU at all.
    /// </remarks>
    bool EnsureDevice() {
        if (device is not null) {
            return true;
        }

        if (!window.Surface.Handle.CanPresent) {
            return false;
        }

        device = VulkanDevice.Create(new() { Surface = window.Surface.Handle });

        pool = new TransientResourcePool(device);
        graph = new RenderGraph(device, pool);

        // Once per device and shared by every window: a module is not a pipeline, and each
        // `UiWindowSurface` builds its own renderer from this one table.
        shaders = UiShaderLibrary.Load(device);

        return true;
    }

    void Release() {
        device?.WaitIdle();

        foreach (var surface in surfaces) {
            surface.Dispose();
        }

        pool?.Dispose();
        device?.Dispose();

        graph = null;
        pool = null;
        device = null;
        shaders = default;
    }

    /// <inheritdoc />
    public void Dispose() {
        // ⚠ First, and before the GPU is released. Anything still running is reporting into a queue
        // this loop has stopped draining, and a task whose delegate came from a plugin keeps that
        // plugin's assembly alive for as long as the queue holds the closure. Cancelling here is
        // what makes an application shutting down, or a plugin host tearing an application down,
        // stop being a leak — see `BackgroundTaskManager.Dispose`.
        Tasks.Dispose();

        Release();

        // Before the document, because closing a window removes its surface and a surface is part of
        // the document's tree. The other way round is a window host reaching into a disposed one.
        windows.Dispose();

        Document.Dispose();
    }
}

/// <summary>When a frame is happening, and how long the last one took.</summary>
/// <param name="Now">Time since the application started.</param>
/// <param name="Delta">How long the previous frame took.</param>
/// <remarks>
///     ⚠ <b>Both, because a caller needs each for a different thing and deriving one from the other
///     is where the copies went wrong.</b> An animation phase accumulates <see cref="Delta" />; a
///     toast expires against <see cref="Now" />, which is the same clock
///     <see cref="UiDocument.Tick" /> is given, so a caller that stamped one from
///     <c>Stopwatch.GetTimestamp</c> of its own would have two timelines that agree until the process
///     is paused under a debugger.
/// </remarks>
public readonly record struct UiFrame(TimeSpan Now, TimeSpan Delta);
