// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using Vixen.Core.Mathematics;
using Vixen.Editor.Profiler;
using Vixen.Editor.SceneView;
using Vixen.Editor.ShaderGraph;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Platform;
using Vixen.Platform.Ui;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Terrain;
using Vixen.Shaders.Generated;
using Vixen.Ui;
using Vixen.Ui.Desktop;
using Vixen.Ui.Reactive;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Editor.App;

/// <summary>The windows, the device, and the four steps of a frame.</summary>
/// <remarks>
///     <para>
///         The loop is four steps and they are worth naming: pump the platform's events into the
///         document, run the layout and draw passes, turn the draw lists into geometry, and record
///         that geometry into a frame. Only the last of the four knows what a GPU is — which is why
///         <c>--frames N</c> means something on a machine with no Vulkan at all.
///     </para>
///     <para>
///         <b>Windows, plural, and only the last step multiplies.</b> A panel torn out of the
///         arrangement onto the desktop is a second <c>UiSurface</c> of the same document — so it is
///         laid out by the same pass, styled by the same cascade and reached by the same reparent
///         that moved it. What it needs of its own is a swapchain, a renderer and an extent, and a
///         <see cref="UiWindowSurface" /> is those three.
///     </para>
///     <para>
///         ⚠ <b>That type is this file's own <c>EditorPane</c>, moved into
///         <c>Platform/Vixen.Ui.Desktop</c> rather than copied out of it.</b> The repository carried
///         three frame loops — this one, <c>Samples/02-HelloUi</c>'s and the <c>vixen-app</c>
///         template's — and this was the only one that could open a second window, republish its
///         granted colour gamut per surface, and skip tessellating a frame whose drawing had not
///         changed. So the sample and the template inherit those three things; the editor keeps its
///         own loop, because what is below is a compositor, a scene presenter per pane and a GPU
///         profiler, and none of that is what an application whose content is an interface does.
///     </para>
///     <para>
///         ⚠ <b>It draws every frame rather than when something changes.</b> An editor that redrew
///         only on input is the right end state and is not free: it needs every animation, every
///         toast expiry and every background task's progress to say so, and one that forgets leaves
///         a progress bar frozen at forty per cent. Said out loud rather than left to be discovered
///         on a laptop battery.
///     </para>
/// </remarks>
sealed class EditorHost : IDisposable {
    /// <summary>What a window is cleared to, and so what a <c>backdrop-filter</c> starts from.</summary>
    /// <remarks>
    ///     ⚠ <b>One constant for two call sites that have to agree, and nothing else can check that
    ///     they do.</b> The interface's pass clears the backbuffer to this, and <c>UiRenderer.Compose</c>
    ///     is told the same colour so that a captured backdrop begins from the ground the frame will
    ///     actually be drawn on — see <see cref="UiBackdropSource" />. Two literals a few hundred lines
    ///     apart would drift into a rectangle of the wrong shade under every glass panel, which reads
    ///     as the blur being tinted rather than as a mismatch.
    ///     ⚠ Alpha one, which is what makes the clear and the capture the same picture. See
    ///     <see cref="UiBackdropSource.Colour" />.
    /// </remarks>
    static readonly Color4 Ground = new(0.06f, 0.07f, 0.09f, 1f);

    readonly IPlatform platform;
    readonly IWindow window;
    readonly EditorApplication editor;
    readonly PlatformWindowHost windows;
    readonly PlatformTextInput textInput;

    readonly GlyphFieldCache glyphs = new(new GlyphAtlas(1024, 1024));
    readonly List<UiWindowSurface> panes = [];

    /// <summary>One presenter per scene pane, made on demand and kept while the count holds.</summary>
    /// <remarks>
    ///     ⚠ <b>Indexed by pane, and each one owns a target and an image id of its own.</b> Four panes
    ///     sharing a presenter would share a render target, so all four would show whichever camera
    ///     wrote it last — and sharing the <i>id</i> alone would do the same thing one layer up, in
    ///     the interface's image registry. See <c>ScenePresenter.Image</c>.
    /// </remarks>
    readonly List<ScenePresenter> scenes = [];

    /// <summary>The compositor-driven presenter for each pane, or null for a pane that has no use for one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Parallel to <see cref="scenes" /> and made on demand</b>, because which kind of
    ///         presenter a pane wants is its view mode's answer and a mode changes mid-session. A pane
    ///         that has been composed keeps its presenter while the arrangement holds — the target and
    ///         the tool pipelines are what it costs, and rebuilding them on every trip through the
    ///         View menu would be a device allocation per menu click.
    ///     </para>
    ///     <para>
    ///         ✅ <b>Every one of them may draw in a frame, where at most one used to.</b> The limit
    ///         was that <c>EditorWorldRenderer</c> held one <see cref="Vixen.Rendering.RenderView" />
    ///         and one <c>GraphicsCompositor</c> with one set of imports; it holds a view, a colour, a
    ///         depth and a sub-frame per pane now, and they are composed by a single build. See
    ///         <see cref="Composes" />, and <c>EditorWorldRenderer.ViewOf</c> for why the build is one.
    ///     </para>
    /// </remarks>
    readonly List<FramePresenter?> frames = [];

    /// <summary>The panes' targets this frame, for the interface's pass to declare that it reads.</summary>
    readonly List<GraphTexture> sampled = [];

    /// <summary>The panes a compositor draws this frame, in reading order.</summary>
    /// <remarks>
    ///     ⚠ <b>Gathered before any of them uploads, because the frame is built once for all of
    ///     them.</b> Reused rather than allocated: this runs once per window per frame.
    /// </remarks>
    readonly List<(FramePresenter Presenter, SceneViewport Viewport)> composing = [];

    /// <summary>And the trees they contribute, which is what one build is handed.</summary>
    readonly List<SceneRenderer> trees = [];

    VulkanDevice? device;
    TransientResourcePool? pool;
    RenderGraph? graph;

    /// <summary>What turns a decoded thumbnail into a texture, once there is a device and a renderer.</summary>
    ThumbnailSurface? thumbnails;

    /// <summary>What draws a shader graph's node previews, once there is a device and a renderer.</summary>
    ShaderGraphPreviewRenderer? previews;
    UiShaders shaders;

    /// <summary>What writes the frame's timestamps, once there is a device that can be timed.</summary>
    /// <remarks>
    ///     ⚠ <b>Owned here rather than by the application, because the object that records the
    ///     timestamps has to be the one recording the frame.</b> A GPU profiler in
    ///     <c>EditorApplication</c> would have no command list to write into — the application is
    ///     deliberately the half of the editor that does not know what a GPU is.
    /// </remarks>
    GpuProfiler? gpu;

    readonly bool running = true;
    bool lost;
    bool resized;

    /// <param name="platform">The platform.</param>
    /// <param name="window">The main window.</param>
    /// <param name="projectRoot">Which project to open, or null for the last one.</param>
    /// <param name="styleDirectory">
    ///     A directory of <c>.vcss</c> files to reload as they are saved, or null — which is every
    ///     run but a developer's. See <c>Program</c>'s <c>--hot-reload</c>.
    /// </param>
    public EditorHost(
        IPlatform platform,
        IWindow window,
        string? projectRoot = null,
        string? styleDirectory = null
    ) {
        this.platform = platform;
        this.window = window;

        editor = new EditorApplication(
            window.FramebufferSize.X / Scale,
            window.FramebufferSize.Y / Scale,
            platform.FileSystem.DataDirectory,
            projectRoot,

            // ⚠ Capabilities rather than the platform. What the application is handed is "there is a
            // file picker" and "there is a browser", both answered at run time — so Open Scene greys
            // itself out on a platform without pickers instead of being absent, which is the rule
            // `view.float-panel` already follows for a second window.
            EditorServices.Of(platform),

            // ⚠ Doc 36 § P3: the features this editor ships, named here because this is the only
            // assembly that can name them. `Vixen.Editor.App` knows that some `IEditorPlugin`s exist
            // and what they are called, and nothing else about any of them.
            extensions: null,
            modules: EditorModules.Standard()
        ) {
            RenderScale = Scale
        };

        // ⚠ Only the host asks the editor to greet, which is what keeps the startup Project Browser
        // out of every test that builds an application. See `EditorApplication.Greets`.
        editor.Greets = true;

        // ⚠ Pushed on change rather than set once. The title carries the scene's name and its dirty
        // marker — `<scene>* — <project> — Vixen` — and it is the only affordance that answers
        // "which project is this window" when three of them are open. The shell composes it and
        // raises nothing unless the composed string differs, so this runs once per actual change.
        window.Title = editor.Shell.Title;
        editor.Shell.TitleChanged += title => window.Title = title;

        // ⚠ Installed on the document, which is what makes a torn-off dock group a real window
        // rather than a rectangle drawn inside this one. Nothing in `Vixen.Ui.Controls.Advanced`
        // names this type — the docking host asks the document, the document asks `IUiWindowHost`,
        // and this is the only assembly in the chain allowed to know what a window is.
        windows = new PlatformWindowHost(platform, editor.Shell.Document, window);
        textInput = new PlatformTextInput(platform.TextInput);

        Fonts.Install(editor.Shell.Document);

        // ⚠ After the font, because how a shortcut should be written depends on what the face can
        // draw. macOS's ⌘ ⇧ ⌥ ⌃ are missing from Arial — which is what `Fonts` finds there — and an
        // unmapped codepoint resolves to glyph zero rather than to a box, so the bar read "L+S" for
        // Save. The shell decides again now that there is something to ask.
        editor.Shell.RefreshShortcutFormat();

        // ⚠ Last, and only when asked. Everything above is what every run does; this opens a
        // `FileSystemWatcher` and loads sheets on top of the five the editor ships, which is a
        // development mode and not a default. `--frames N` never passes it — see `Program`.
        if (styleDirectory is { Length: > 0 } styles) {
            editor.WatchStyles(styles);
        }
    }

    /// <summary>A command to run once, on the first frame, and then forget.</summary>
    /// <remarks>
    ///     ⚠ <b>For CI, and it goes through the command registry rather than round it.</b> The point
    ///     of proving an import or a content build from here is that it is the <i>editor's</i> path —
    ///     enablement, background task, notification and all. Calling the underlying pipeline
    ///     directly would prove only what the CLI already proves.
    /// </remarks>
    public string? Command { get; set; }

    /// <summary>Which project the editor should be rebuilt over, or <see langword="null" /> to stop.</summary>
    /// <remarks>
    ///     ⚠ <b>Read by <c>Program</c> after <see cref="Run" /> returns, which is what makes Open
    ///     Project work without a restart.</b> Doc 20 filed swapping a project as "a world, a scene,
    ///     an asset database and every open document" — and it is, which is why nothing is swapped:
    ///     this host is disposed and another is built over the same window, so the new editor is
    ///     assembled by exactly the code that assembles it at launch. See
    ///     <c>EditorApplication.RequestProject</c>.
    /// </remarks>
    public string? NextProject => editor.PendingProject;

    /// <summary>Runs until the window closes, or for a fixed number of frames.</summary>
    /// <param name="frames">How many, or zero for as many as it takes.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///     ⚠ <b>The twin of the claim in <see cref="UiApplication" />'s loop, and it has to be here
    ///     too because the editor never runs that loop.</b> <c>ReactiveGraph.OwningThread</c> is
    ///     process-wide and was assigned by nothing in the tree, so every
    ///     <c>AssertOwningThread</c> in the reactive layer was inert in the editor as well —
    ///     including on <c>Strings</c>, the one static reactive node every panel in the shell
    ///     attaches an effect to. Restored on the way out because <c>Program</c> builds a second
    ///     host over the same window when a project is swapped.
    /// </remarks>
    public int Run(int frames) {
        var previousOwner = ReactiveGraph.OwningThread;
        ReactiveGraph.OwningThread = Thread.CurrentThread;

        try {
            return Loop(frames);
        } finally {
            ReactiveGraph.OwningThread = previousOwner;
        }
    }

    int Loop(int frames) {
        var clock = Stopwatch.StartNew();
        var previous = TimeSpan.Zero;
        var drawn = 0;

        // The appearance the machine already had. No event is posted for it — there is nothing to
        // notice — so a host that only handled the change would never see the first one.
        PlatformInput.ApplyColorScheme(editor.Shell.Document, platform.ColorScheme);

        while (running && (frames == 0 || drawn < frames)) {
            var now = clock.Elapsed;
            var delta = now - previous;
            previous = now;

            // ⚠ Advanced whether or not anybody is sampling. `BeginFrame` is an interlocked
            // increment and nothing else — an editor that only counted frames while the profiler
            // was open would attribute a whole capture to frame zero, which is exactly the axis a
            // flame chart is drawn against.
            Vixen.Core.Diagnostics.Profiler.BeginFrame();

            // The scope covering everything below, so a chart of the editor has one bar per frame
            // with the four phases nested under it rather than four unrelated bars.
            using var frame = Vixen.Core.Diagnostics.Profiler.Begin(EditorApplication.EditorKeys.Frame);

            using (Vixen.Core.Diagnostics.Profiler.Begin(EditorApplication.EditorKeys.Pump)) {
                Pump();
            }

            if (!running || editor.IsClosing) {
                break;
            }

            // ⚠ Once per frame, however many resize events arrived. A window opened maximised on a
            // 4K display produces a burst of them, and handling each one where it arrives means a
            // `vkDeviceWaitIdle` and a full swapchain rebuild several times before a single frame is
            // drawn — every rebuild handing the compositor images whose contents are undefined,
            // which is what the flicker is. Coalescing also keeps the layout and the geometry in
            // step: both are read from the same framebuffer size, once, before anything uses it.
            //
            // Only the main window's, because only the main window's size is the shell's. Every
            // other surface was resized by the window host as the event arrived, and its swapchain
            // is rebuilt from the size comparison in `UiWindowSurface.Recreate`.
            if (resized) {
                resized = false;

                editor.Shell.Resize(window.FramebufferSize.X / Scale, window.FramebufferSize.Y / Scale);
                editor.RenderScale = Scale;
            }

            editor.Shell.Tick(now, delta);

            using (Vixen.Core.Diagnostics.Profiler.Begin(EditorApplication.EditorKeys.Document)) {
                editor.Shell.Document.Update();
            }

            // ⚠ After the update, because the cursor is a computed style and the hover the pointer
            // moved this frame is what decides whose. One of the two places this call has to be —
            // the other is `UiApplication` — and a host that forgets it is a host where every
            // `cursor-*` class in every theme resolves correctly and shows nothing.
            //
            // ⚠ The gap here was the loop rather than the line, and the loop is covered now:
            // `EditorHostTests` builds this host over a headless platform and runs frames through
            // it, so every step above and below is reached by a test. What is still asserted only
            // against the *other* host is what this call hands the window —
            // `UiApplicationTests.TheLoopTellsTheWindowWhatThePointerIsOver`, and
            // `PlatformCursorTests`' class remarks say why the assertion is there and not here.
            PlatformCursor.Apply(windows);

            // ⚠ The second host, wired in the same frame position as the first. A wire added to one
            // of the two and not the other is this repository's standing defect, and here it would
            // read as the editor's own fields being the ones an input method cannot be used in.
            textInput.Apply(windows);

            // ⚠ Between the two, and it is not arbitrary. A viewport measures itself in render pixels
            // from a box the layout pass is what produces, and the axis cross it draws comes from the
            // camera this brings up to date — so either side of this pair puts the picture a frame
            // behind whatever the user just did with the mouse.
            using (Vixen.Core.Diagnostics.Profiler.Begin(EditorApplication.EditorKeys.Update)) {
                editor.Update(delta);
            }

            using (Vixen.Core.Diagnostics.Profiler.Begin(EditorApplication.EditorKeys.Document)) {
                editor.Shell.Document.Draw();
            }

            Sync();

            using (Vixen.Core.Diagnostics.Profiler.Begin(EditorApplication.EditorKeys.Geometry)) {
                Build();
            }

            using (Vixen.Core.Diagnostics.Profiler.Begin(EditorApplication.EditorKeys.Present)) {
                Present();
            }

            drawn++;

            // ⚠ After the first frame rather than before the loop, so the command runs against a
            // shell that has laid itself out and a project that has finished opening — which is the
            // state a person clicking a menu item is in, and the only state worth proving works.
            if (drawn == 1 && Command is { Length: > 0 } once) {
                Command = null;

                if (!editor.Shell.Commands.Execute(once)) {
                    Console.Error.WriteLine($"There is no enabled command called '{once}'.");
                    return 2;
                }
            }
        }

        device?.WaitIdle();

        // ⚠ Before the document goes, and it is the reason this is not in `Dispose`. Persisting
        // reads the arrangement out of the docking host, and a host that had already been disposed
        // would write an empty layout over the one the user spent the afternoon arranging.
        editor.Persist();

        // The window's own geometry, which `Program` reads back before the next window exists. On
        // the way down rather than on every resize, for the reason the layout is: a file written per
        // frame of a corner drag is the noisiest thing on the disk.
        WindowPlacement.Save(platform.FileSystem.DataDirectory, window);

        return 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        Release();

        // Before the document, because closing a window removes its surface and a surface is part of
        // the document's tree. The other way round is a window host reaching into a disposed one.
        windows.Dispose();

        editor.Dispose();
    }

    void Pump() {
        foreach (var platformEvent in platform.PumpEvents()) {
            switch (platformEvent.Kind) {
                // ⚠ Through the same request the close button goes through, and the difference is
                // an afternoon's work. This used to stop the loop where it stood — no prompt, no
                // save, and every unsaved document gone. That is not a rare path: ⌘Q is how a
                // macOS application is quit, and SDL raises this rather than a window close for it;
                // on every platform it is also what the window manager sends when the session ends.
                //
                // ⚠ And the platform's own flag is cleared, because backing out of the prompt has to
                // leave the editor running. `DesktopLifecycle` latches `IsQuitRequested` — a host
                // that left it set would be one where the *next* quit is already half-answered.
                case PlatformEventKind.Quit:
                    editor.RequestClose();

                    if (!editor.IsClosing) {
                        platform.Lifecycle.CancelQuit();
                    }

                    // ⚠ Not `return`. The rest of this pump is the frame's input — a click, a
                    // keystroke, the resize that arrived with it — and dropping it was what made a
                    // close request that arrived in the same batch as a quit never reach the editor.
                    break;

                case PlatformEventKind.WindowCloseRequested:
                    // ⚠ Asked first, and the answer decides whose close this is. A torn-off panel's
                    // window is handled by the host — it becomes a request the docking host answers
                    // by bringing the panels home — and only a close nobody claimed is the editor
                    // being asked to quit.
                    if (windows.Handle(platformEvent)) {
                        break;
                    }

                    // ⚠ A request, not a close, and this is the whole of save-on-close. The editor
                    // asks about unsaved work and sets `IsClosing` when it has an answer — which the
                    // loop reads on the next frame — so backing out of the prompt leaves the window
                    // open. Setting `running` here instead is what made the close button lose an
                    // afternoon, and doc 20 is blunt about what that costs.
                    editor.RequestClose();
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
                    // ⚠ Wired here even though the editor's theme is the *class* dark-mode strategy
                    // and does not read the media query — the second host is where a wire added to
                    // one of the two silently does nothing, and this repository has that defect
                    // often enough to spend two lines on. A panel or plug-in loading a sheet whose
                    // theme uses the `media` strategy gets the same answer the framework host gives.
                    PlatformInput.ApplyColorScheme(editor.Shell.Document, platform.ColorScheme);
                    break;

                default:
                    // ⚠ Routed by the window the event names. Two windows do not share a coordinate
                    // space, so an event delivered to the wrong surface lands at the right numbers in
                    // the wrong place — which looks exactly like a hit-testing bug and is a routing
                    // one. A window this host does not know about is one that has just been closed,
                    // and its last few events are dropped rather than sent somewhere arbitrary.
                    if (windows.TryResolve(platformEvent.WindowId, out var surface)) {
                        PlatformInput.Dispatch(editor.Shell.Document, surface, platformEvent);
                    }

                    break;
            }
        }
    }

    /// <summary>Brings the panes into line with the document's surfaces.</summary>
    /// <remarks>
    ///     Once a frame rather than on an event, because a surface appears and disappears from inside
    ///     the docking host — a tab dragged onto the desktop, a window closed — and a host that had
    ///     to be told would be a second place that has to agree with the first.
    /// </remarks>
    void Sync() {
        // ⚠ Installed here rather than beside the device, because it needs the *renderer* as well —
        // an `Image` number is resolved against the one that draws the surface it appears on, and
        // the main pane's is made lazily on its first frame. Once, on the frame it becomes possible.
        if (thumbnails is null && panes.Count > 0 && panes[0].Renderer is { } renderer && device is { } ready) {
            thumbnails = new ThumbnailSurface(ready, renderer);
            editor.ThumbnailSurface = thumbnails;

            // ⚠ The same moment and for the same two reasons: a preview needs a device to draw on and
            // a renderer to be named by. Handing it over reaches the graphs that are already open —
            // a restored session opens its tabs before the first frame — as well as the later ones.
            previews = new ShaderGraphPreviewRenderer(ready, ShaderNodeLibrary.Create(), new UiPreviewImages(renderer));
            editor.ShaderGraphPreviews = previews;
        }

        // ⚠ NOT after a wait, and the comment that claimed otherwise cost task #364. The only
        // `WaitIdle` in this method belongs to the loop below and runs on the frames a pane is
        // removed — a window being closed — which is not the frames a thumbnail is evicted on. This
        // call happens between `EndFrame` and the next `BeginFrame` with the last frame still on the
        // GPU, and what makes it safe is that `IGraphicsDevice.Destroy` defers. See
        // `VulkanDevice.Retire`, where that deferral used to be zero frames wide for exactly this
        // caller.
        thumbnails?.Retire();

        for (var i = panes.Count - 1; i >= 0; i--) {
            if (!panes[i].Surface.IsRemoved) {
                continue;
            }

            // The images this pane's swapchain owns may still be in flight. A window closed while a
            // frame referencing it is queued is a use-after-free the validation layers will name and
            // the driver will not.
            device?.WaitIdle();

            panes[i].Dispose();
            panes.RemoveAt(i);
        }

        foreach (var surface in editor.Shell.Document.Surfaces) {
            if (Pane(surface) is not null || !windows.TryWindow(surface, out var opened)) {
                continue;
            }

            panes.Add(new UiWindowSurface(surface, opened));
        }
    }

    UiWindowSurface? Pane(UiSurface surface) {
        foreach (var pane in panes) {
            if (ReferenceEquals(pane.Surface, surface)) {
                return pane;
            }
        }

        return null;
    }

    /// <summary>Turns each window's draw list into vertices.</summary>
    /// <remarks>
    ///     ⚠ <b>Built whether or not there is a device.</b> On a headless run — no surface, no
    ///     Vulkan — everything above the RHI still executes, which is what makes <c>--frames</c> a
    ///     smoke test of the editor rather than only of the backend, torn-off windows included.
    /// </remarks>
    void Build() {
        foreach (var pane in panes) {
            var list = pane.Surface.Drawing;
            var extent = pane.Extent;

            // ⚠ The glyph atlas is the third input and it is why `AtlasChanged` is checked too. A
            // label that brought a new glyph in can repack the texture, which moves every region
            // already baked into last frame's vertices — so a frame that skipped after a repack
            // would draw the right letters read out of the wrong places.
            if (pane.Built == (list.Version, extent) && !pane.Geometry.AtlasChanged) {
                continue;
            }

            pane.Frame = pane.Geometry.Build(list, glyphs, extent);
            pane.Built = (list.Version, extent);
        }
    }

    /// <summary>How many physical pixels one device-independent one is, never zero.</summary>
    float Scale => window.DpiScale <= 0f ? 1f : window.DpiScale;

    void Present() {
        if (lost || !EnsureDevice()) {
            return;
        }

        device!.BeginFrame();

        // ⚠ Inside the frame and before anything records, because a preview draws into a target the
        // interface samples in this same frame — submits on one queue run in order, so the pass has
        // finished by the time the panes' lists read it. `RebuildsPerUpdate` is what stops a graph
        // that was just pasted into compiling twenty shaders between two frames.
        previews?.Update();

        // ⚠ Here for the same reason, and it is the reason the upload is split in two.
        // `ThumbnailCache.Pump` runs from `editor.Update` above, which is outside this pair — a
        // command list recorded there comes from the pool of the slot `BeginFrame` has just reset,
        // so submitting it where it is recorded races the next frame's reset of that same pool.
        // `Upload` makes the texture and hands out its number; this is what copies the pixels in,
        // ordered ahead of the panes that sample them.
        thumbnails?.Flush();

        foreach (var pane in panes) {
            pane.IsDrawing = false;

            if (!pane.Ensure(device, shaders)) {
                continue;
            }

            pane.Recreate(device);

            if (!Acquire(pane, out var view)) {
                continue;
            }

            pane.Acquired = view;
            pane.IsDrawing = true;

            Record(pane);
        }

        // ⚠ Ended even when nothing was drawn, and this is not tidiness. `BeginFrame` waits on this
        // slot's fence and resets it; `EndFrame` is what submits the signal that makes the wait
        // return. Leaving without it means the frame counter never advances, so the next frame waits
        // on the same reset fence with no submission behind it — `vkWaitForFences` with no timeout,
        // which is a hang rather than a dropped frame.
        device.EndFrame();

        foreach (var pane in panes) {
            if (!pane.IsDrawing) {
                continue;
            }

            switch (pane.SwapChain!.Present()) {
                case SwapChainStatus.OutOfDate:
                    pane.Recreate(device, force: true);
                    break;

                // ⚠ Suboptimal is a hint, and rebuilding on it unconditionally is the flicker. It
                // means "this still presents correctly, but the surface would prefer other
                // parameters" — and a compositor that keeps saying so, which a scaled 4K surface
                // does, then gets a `vkDeviceWaitIdle` and a fresh set of undefined images every
                // single frame. Honoured only when the window has actually changed size, which
                // `Recreate` is what decides.
                case SwapChainStatus.Suboptimal:
                    pane.Recreate(device);
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
    void Record(UiWindowSurface pane) {
        var scale = pane.Scale;
        var extent = pane.Extent;

        using var commands = device!.BeginCommandList(QueueKind.Graphics, "ui");

        // ⚠ Only the primary window's frame is timed, and it is a decision rather than a limitation.
        // A torn-off panel is a second submission on the same queue, and interleaving two windows'
        // regions in one pool would produce a timeline whose bars overlap for reasons that are about
        // window management rather than about rendering.
        var timing = gpu is not null && pane.IsPrimary;

        if (timing) {
            // The CPU profiler's frame counter, so a GPU bar and a CPU bar labelled "frame 812" are
            // the same frame. A counter of the host's own would be a second number that agreed by
            // accident until somebody early-returned out of the loop.
            gpu!.BeginFrame(commands, Vixen.Core.Diagnostics.Profiler.FrameIndex);
        }

        // ⚠ Attached per frame and cleared per frame, because `timing` is per *window*. A torn-off
        // panel shares this graph and must not write into a pool whose frame the primary window
        // opened — leaving the sink attached would interleave two windows' regions and produce a
        // timeline whose bars overlap for reasons that are about window management.
        graph!.Profiler = timing ? gpu : null;

        var backbuffer = graph!.ImportTexture(
            pane.SwapChain!.CurrentTexture,
            pane.Acquired,
            new(
                pane.SwapChain.Format,
                pane.SwapChain.Size.X,
                pane.SwapChain.Size.Y,
                TextureUsage.ColourTarget,
                Name: "backbuffer"
            ),
            ResourceState.Undefined,
            ResourceState.Present
        );

        var renderer = pane.Renderer!;

        // ⚠ Before the pass, not inside it. The atlas upload is a transfer and a layout transition,
        // and a render pass is the one place a Vulkan command list may not do either. The scene's
        // lines are a buffer write and are here for the same reason.
        renderer.Upload(commands, pane.Frame, glyphs.Atlas);

        // ⚠ <b>After `Upload` and outside the graph, and both halves of that are load-bearing.</b>
        // After, because a group's surface is drawn from the vertices `Upload` wrote and through the
        // descriptor sets it advanced the ring to — composing first would render this frame's groups
        // from the last frame's geometry. Outside, because `Compose` opens a render pass per group
        // and the graph's own pass is not somewhere another one can begin; recording them onto
        // `commands` here puts them before `graph.Execute` on the same list, which is exactly the
        // order the dependency runs in — the interface's pass samples what these wrote.
        //
        // ⚠ The same surface and scale as `Record` below, and not the swapchain's size. `Compose`
        // draws a group with the frame's own projection into a viewport-sized surface — see
        // `UiLayer` — so a different number here would put the group's contents at a different place
        // in its surface than the composite quad expects to find them, and the subtree would land
        // offset by whatever the two disagreed by.
        //
        // ⚠ <b>And the same colour the interface's own pass clears to, which is what
        // <c>backdrop-filter</c> reads and is not optional.</b> `Compose` can re-render the
        // interface's draw list and nothing else, so a backdrop captured from that alone would be
        // both the wrong picture — the panels above the element instead of the window under it — and
        // a *translucent* one, which composites over the sharp original rather than replacing it.
        // The window's ground is a flat clear, so a colour is the whole of what this host has to
        // hand: the scene panes are drawn into targets of their own that the interface samples as
        // ordinary images, and a glass panel over one of them blurs the interface's copy along with
        // everything else. See `UiBackdropSource`.
        //
        // ⚠ It has to be the *same* colour as the `LoadAction.Clear` below, and there is nothing that
        // checks it. A disagreement is a rectangle of the wrong shade under every glass panel, which
        // reads as the blur being tinted.
        renderer.Compose(
            commands,
            pane.Frame,
            new Int2((int) MathF.Round(extent.Width), (int) MathF.Round(extent.Height)),
            scale,
            new UiBackdropSource(Ground)
        );

        // ⚠ Declared before the interface's pass, so the graph orders the two from the read: the
        // interface samples what the scene wrote, and the barrier between them is derived rather
        // than placed by hand.
        //
        // ⚠ One of these per pane of the scene panel, and the list is reused rather than allocated:
        // this runs once per window per frame.
        sampled.Clear();

        if (pane.IsPrimary) {
            var panes = editor.Viewports;

            Ensure(panes.Count);

            // ⚠ Decided before the loop, because "which panes compose" is a fact about the
            // arrangement rather than about a pane, and because every one of them has to have lent
            // the frame its imports before the one build reads them.
            composing.Clear();

            for (var index = 0; index < panes.Count && index < scenes.Count; index++) {
                var viewport = panes[index];

                if (Composes(viewport) && Frames(index) is { } composition) {
                    // ⚠ Resized here rather than in the group below, because a pane whose target
                    // could not be made — a collapsed dock, the frame before the first layout — is
                    // not a pane that contributes an import naming a view nothing resized.
                    if (composition.Resize(viewport, renderer)) {
                        composing.Add((composition, viewport));
                        continue;
                    }
                }

                var presenter = scenes[index];

                if (!presenter.Resize(viewport, renderer)) {
                    continue;
                }

                presenter.Upload(commands, editor.Scene, viewport);

                if (presenter.Declare(graph, viewport, out var target)) {
                    sampled.Add(target);
                }
            }

            if (composing.Count > 0 && editor.Frame is { } world) {
                // ⚠ Once, before any pane, and never per pane. `WorldRenderer.Draw` opens with the
                // per-frame descriptor pool's boundary, which recycles every set handed out since
                // the last call — a second call between two panes hands the second pane sets the
                // first pane's passes are still going to bind when the graph executes.
                world.Begin(commands);

                trees.Clear();

                var reference = Int2.Zero;

                foreach (var (composition, viewport) in composing) {
                    composition.Upload(commands, editor.Scene, viewport);

                    if (composition.Prepare(viewport, out var tree)) {
                        trees.Add(tree);
                    }

                    // The largest pane, because a resource an authored document declares as a
                    // fraction of the frame has to be at least as large as what any pane attaches.
                    reference = new(
                        Math.Max(reference.X, composition.Width),
                        Math.Max(reference.Y, composition.Height)
                    );
                }

                if (trees.Count > 0) {
                    // ⚠ One build for every pane, because a view's index is assigned per collect and
                    // the work lists a pass records are looked up by that index when the graph
                    // executes — which is after all of them have built. A build per pane is four
                    // panes drawing whichever view collected last. See `EditorWorldRenderer.ViewOf`.
                    var composed = world.Compose(graph, trees, reference, device!.WaitIdle);

                    foreach (var (composition, _) in composing) {
                        if (composition.Take(composed, out var composedTarget)) {
                            sampled.Add(composedTarget);
                        }
                    }
                }

                // ⚠ Every frame, because the reasons are per build and a node can start declining
                // halfway through a session — a document reload, a device that lost a capability.
                // The application is what compares them against last frame's, so this is a call
                // rather than three thousand console lines a minute.
                editor.ReportDegradations(world.Degradations);
            } else {
                // ⚠ Reported as empty when no pane is composed, so the last composed pane's reasons
                // stop being the console's most recent word on a frame nothing is drawing any more.
                editor.ReportDegradations([]);
            }
        }

        graph.AddPass(
            "ui",
            pass => {
                pass.ColourAttachment(backbuffer, LoadAction.Clear, Ground);
                pass.SideEffect();

                // ⚠ No timestamp pair here any more. The graph brackets every pass it runs with a
                // scope named after the pass, so this one is timed as "ui" without asking — and a
                // hand-rolled pair inside the body would be a second bar measuring the same work,
                // nested one level deeper than the pass that contains it.

                // ⚠ The scene's target is sampled through a descriptor set, which the graph cannot
                // see. Saying so here is what orders the scene's pass before this one and puts the
                // layout transition between them — without it the target is still a colour
                // attachment when the fragment shader reads it. Every pane's, because a four-pane
                // layout is four targets this one pass samples.
                foreach (var target in sampled) {
                    pass.Reads(target);
                }

                // ⚠ The logical surface and the DPI scale, not the swapchain's size. The geometry is
                // in device-independent units and the scissor comes out in framebuffer pixels;
                // passing the framebuffer for both draws the whole interface into the top-left
                // quarter of the window. The scale is this window's, which on a second display is
                // not the main window's.
                pass.Execute(
                    context => renderer.Record(
                        context.CommandList,
                        pane.Frame,
                        new Int2((int) MathF.Round(extent.Width), (int) MathF.Round(extent.Height)),
                        scale
                    )
                );
            }
        );

        graph.Execute(commands);
        graph.Reset();

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        // ⚠ After the submit, and it reads a pool from several frames ago rather than this one.
        // `TryResolveQueries` never waits — see its remarks — so the first few frames after the
        // panel opens report nothing, which is correct and is why the view says it is waiting.
        if (timing && gpu!.Resolve()) {
            editor.GpuFrame = gpu.Latest;
        }
    }

    /// <summary>Takes the next image, rebuilding once if the swapchain has gone stale.</summary>
    /// <returns>Whether there is an image to draw into.</returns>
    /// <remarks>
    ///     ⚠ <b>It retries rather than dropping the frame.</b> `OutOfDate` arrives on the first
    ///     acquire after every resize, and returning here would present nothing that frame — the
    ///     compositor shows whatever was there before, which during a maximise or a drag is the
    ///     window visibly blinking. Rebuilding and asking again costs one stall and puts a correct
    ///     frame on the screen.
    /// </remarks>
    bool Acquire(UiWindowSurface pane, out TextureViewHandle view) {
        // ⚠ The retry and the rebuild are `UiWindowSurface.Acquire`'s; what stays here is the *lost*
        // verdict, because losing the device is the whole host's problem rather than one window's —
        // a pane that answered for it would have to reach back into the field that stops the loop.
        switch (pane.Acquire(device!, out view)) {
            case null:
                lost = true;
                return false;

            case { } answer:
                return answer;
        }
    }

    /// <summary>Makes the pool of presenters match how many panes the scene panel has.</summary>
    /// <param name="wanted">How many.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Grown and shrunk here rather than when the arrangement changes.</b> The
    ///         application splits the panel and knows nothing about devices; this is the frame loop,
    ///         which is the only place that can be sure no frame is in flight over the target it is
    ///         about to destroy. Comparing two counts once per frame is not a cost.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The device is idled before one goes.</b> A presenter's colour target may be
    ///         referenced by a frame the driver has not finished with, and destroying it is a
    ///         use-after-free the validation layers name and the driver does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Image ids are one-based and there are two per pane.</b> Zero means "no target" to
    ///         <c>Viewport.RenderTarget</c>, which draws the placeholder instead — so a pane numbered
    ///         zero would be a pane that never shows the scene. See <see cref="Frames" /> for why a
    ///         pane's two presenters may not share one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The composed presenters shrink with them.</b> A pane's frame presenter owns a
    ///         colour target, a depth target and three pipelines; left behind when the arrangement
    ///         shrank, it would keep an image number registered against a view of a texture nothing
    ///         resizes any more.
    ///     </para>
    /// </remarks>
    void Ensure(int wanted) {
        if (scenes.Count > wanted) {
            device?.WaitIdle();

            for (var index = scenes.Count - 1; index >= wanted; index--) {
                scenes[index].Dispose();
                scenes.RemoveAt(index);
            }

            for (var index = frames.Count - 1; index >= wanted; index--) {
                frames[index]?.Dispose();
                frames.RemoveAt(index);
            }
        }

        while (scenes.Count < wanted) {
            var presenter = Presenter(SceneImage(scenes.Count));

            // ⚠ Every pane, and every pane created later. A source set on the first presenter only is
            // a split view where one half draws the level and the other half draws the grid, which
            // reads as a broken pane rather than as missing wiring.
            presenter.Surfaces.Meshes = editor.SceneGeometry;
            presenter.Surfaces.Surfaces = editor.SceneSurfaces;

            // ⚠ The terrain's two stages come out of the library rather than out of Shaders/*.rvn,
            // and they are the first modules here that do. `./build.sh CheckShaders` compiles them
            // from Raven/Library/Terrain/Terrain.rvn with its import closure and commits the bytes;
            // a build whose resources do not carry them draws no terrain and nothing else changes.
            presenter.TerrainStages = TerrainModules();
            presenter.TerrainScene = editor.TerrainScene;

            // The vegetation rides the same two seams: the modules from the library build, the
            // painted volume from whichever module contributed one. Either absent is a pane that
            // draws less, not a pane that fails.
            presenter.GrassStages = GrassModules();
            presenter.VegetationScene = editor.VegetationScene;

            // ⚠ And the water, which needs no modules at all: the preview surface is evaluated on the
            // CPU by the same `WaterQuery` a game's vertex stage samples — docs/plan/35 § D2 — so
            // there is no library stage to be missing and no build in which a lake silently does not
            // draw. Absent a contributing module the seam is null, which is a pane with no water in
            // it and nothing else different.
            presenter.WaterScene = editor.WaterScene;

            scenes.Add(presenter);
        }
    }

    /// <summary>Whether a compositor draws this pane, rather than the tool renderer.</summary>
    /// <param name="viewport">The pane.</param>
    /// <returns>Whether its current mode has a tree of its own.</returns>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="ViewModes" /> decides, and this is the whole of the decision.</b> A mode
    ///         with a tree registered for it is a mode the compositor draws; a mode with none is the
    ///         tool renderer's, which is where wireframe on a device without <c>fillModeNonSolid</c>,
    ///         albedo, normals and roughness all still live. <c>Registered</c> rather than
    ///         <c>Resolve</c>, because <c>Resolve</c> falls back to the shaded tree — right for
    ///         picking a tree to draw, wrong for asking whether this mode is a compositor's at all.
    ///     </para>
    ///     <para>
    ///         ✅ <b>Every pane, where this used to answer with one.</b> The limit was the render
    ///         view: one <c>RenderView</c>, one <c>GraphicsCompositor</c>, one set of imports and one
    ///         reference size, so two panes composing in a frame would both draw the second one's
    ///         camera into the second one's target. <c>EditorWorldRenderer</c> now holds a view and a
    ///         sub-frame per pane, and the panes are composed by one build — see
    ///         <c>EditorWorldRenderer.ViewOf</c> for why that build may not be split.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A pane whose modes are empty is still ordinary.</b> A pane past the document's
    ///         slots and every pane but the first under an authored <c>.vxcompositor</c> have no tree
    ///         registered, and they keep the tool presenter, which draws.
    ///     </para>
    /// </remarks>
    bool Composes(SceneViewport viewport) {
        if (editor.Frame is null) {
            return false;
        }

        var modes = viewport.Modes;

        return modes.Registered.Contains(modes.Current);
    }

    /// <summary>The compositor-driven presenter for a pane, built the first frame it needs one.</summary>
    /// <param name="index">Which pane.</param>
    /// <returns>It, or null when the renderer has not been built.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Lazily, because the renderer is the application's and arrives with the device.</b>
    ///         <c>EnsureDevice</c> hands the device over and <c>EditorApplication.AttachRenderer</c>
    ///         builds the frame's renderer inside that assignment — but a project whose shaders do not
    ///         parse leaves it null, which is an ordinary state and not one this may throw on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two image numbers per pane, interleaved, because a pane has two presenters and
    ///         both register.</b> <c>ScenePresenter.Resize</c> re-registers only when the size
    ///         changed, so a pane whose two presenters shared a number would keep showing the one that
    ///         registered last however the mode was switched — the split-view failure one layer up.
    ///         Odd is the tool presenter's, even is the frame's, and zero stays "no target".
    ///     </para>
    /// </remarks>
    FramePresenter? Frames(int index) {
        // ⚠ A pane past the document's slots has no view bound by name and no sub-frame, so there is
        // nothing for a presenter to draw into. It keeps the tool presenter, which draws.
        if (index >= EditorWorldRenderer.MaxPanes) {
            return null;
        }

        while (frames.Count <= index) {
            frames.Add(null);
        }

        if (frames[index] is { } existing) {
            return existing;
        }

        if (editor.Frame is not { } world) {
            return null;
        }

        var presenter = new FramePresenter(
            device!,
            world,
            new LineShaders(
                device!.CreateShader(ShaderStage.Vertex, Module("LineVertex.vert.spv"), "line vertex"),
                device.CreateShader(ShaderStage.Fragment, Module("LineFragment.frag.spv"), "line fragment")
            ) {
                Locations = new(LineVertexKeys.PositionLocation, LineVertexKeys.VertexColourLocation)
            },
            new MeshShaders(
                device.CreateShader(ShaderStage.Vertex, Module("Mesh.vert.spv"), "mesh vertex"),
                device.CreateShader(ShaderStage.Fragment, Module("Mesh.frag.spv"), "mesh fragment")
            ) {
                Locations = new(MeshKeys.PositionLocation, MeshKeys.NormalLocation, MeshKeys.VertexColourLocation)
            },
            FramePresenter.ColourFormat,
            FrameImage(index),
            index
        );

        frames[index] = presenter;

        return presenter;
    }

    /// <summary>What the interface calls a pane's tool target.</summary>
    static ulong SceneImage(int index) => ((ulong) index * 2) + 1;

    /// <summary>And its composed one.</summary>
    static ulong FrameImage(int index) => ((ulong) index * 2) + 2;

    /// <summary>Builds one pane's presenter.</summary>
    /// <param name="image">What the interface calls its target.</param>
    /// <remarks>
    ///     ⚠ <b>A colour format the swapchain's is not.</b> The scene is sampled by the interface
    ///     rather than presented, so it wants a linear target — a UNorm-sRGB one would be decoded on
    ///     the way in and encoded again on the way out, which is a scene visibly washed out next to
    ///     the panels around it.
    /// </remarks>
    ScenePresenter Presenter(ulong image) =>
        new(
            device!,
            new LineShaders(
                device!.CreateShader(ShaderStage.Vertex, Module("LineVertex.vert.spv"), "line vertex"),
                device.CreateShader(ShaderStage.Fragment, Module("LineFragment.frag.spv"), "line fragment")
            ) {
                Locations = new(LineVertexKeys.PositionLocation, LineVertexKeys.VertexColourLocation)
            },
            new MeshShaders(
                device.CreateShader(ShaderStage.Vertex, Module("Mesh.vert.spv"), "mesh vertex"),
                device.CreateShader(ShaderStage.Fragment, Module("Mesh.frag.spv"), "mesh fragment")
            ) {
                Locations = new(MeshKeys.PositionLocation, MeshKeys.NormalLocation, MeshKeys.VertexColourLocation)
            },
            new MeshInstanceShaders(
                device.CreateShader(ShaderStage.Vertex, Module("MeshInstanced.vert.spv"), "mesh instance vertex"),
                device.CreateShader(ShaderStage.Fragment, Module("MeshInstanced.frag.spv"), "mesh instance fragment")
            ) {
                // ⚠ Thirteen, in the renderer's own attribute order: the shape's pair first, then the
                // entity's eleven. A location short would leave that attribute bound to nothing and
                // the stage reading whatever the driver left there — see `VertexLocations`, which is
                // why these are read out of Raven's reflection rather than written down. That is also
                // why adding the two material attributes cost two lines here and nothing else: three
                // new streams pushed every one of these locations up by three, and no number in this
                // file had to know it.
                Locations = new(
                    MeshInstancedKeys.PositionLocation,
                    MeshInstancedKeys.NormalLocation,
                    MeshInstancedKeys.Model0Location,
                    MeshInstancedKeys.Model1Location,
                    MeshInstancedKeys.Model2Location,
                    MeshInstancedKeys.Model3Location,
                    MeshInstancedKeys.Normal0Location,
                    MeshInstancedKeys.Normal1Location,
                    MeshInstancedKeys.Normal2Location,
                    MeshInstancedKeys.TintLocation,
                    MeshInstancedKeys.StyleLocation,
                    MeshInstancedKeys.SurfaceLocation,
                    MeshInstancedKeys.EmissiveLocation
                )
            },
            PixelFormat.Rgba8UNorm,
            image
        );

    /// <summary>Builds everything GPU-shaped, once there is a surface to present to.</summary>
    /// <returns>Whether there is one.</returns>
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

        // ⚠ Handed to the application so the panel can say *why* there is no timeline. A device that
        // reports no timestamp queries — MoltenVK on an older Metal, a driver with no valid bits on
        // the graphics family — is a real configuration, and a GPU panel that drew nothing on it
        // would be indistinguishable from one whose frame had no passes.
        editor.GraphicsDevice = device;

        if (device.Features.HasTimestampQueries) {
            gpu = new GpuProfiler(device);
        }

        // Compiled once and handed to every window's renderer — once per process rather than once
        // per window, because a module is not a pipeline and the two panes each build their own
        // `UiRenderer` from this one table.
        //
        // ⚠ **The library's eight, not a table written out here, and the difference is three
        // stages the editor used to be missing outright.** This host hand-rolled five modules from
        // its own copy of `Ui.rvn` until 2026-09-04, and the copy did not declare `UiBlur`,
        // `UiColour` or `UiMask` — so `filter: blur()` drew sharp, `filter: grayscale(1)` drew in
        // full colour and `mask-image` did nothing, in the one application whose stylesheets are
        // this repository's own. None of the three is a failure: `UiRenderer` composites the group
        // through `Image` and returns a correct-looking picture, which is why it survived. See
        // `UiShaderLibrary`, whose own remark is that a host naming eight modules is a host where
        // somebody names four.
        shaders = UiShaderLibrary.Load(device);

        // The presenters themselves are made on demand, one per pane — see `Ensure`, which is also
        // where they go when the panel is split the other way.
        return true;
    }

    void Release() {
        device?.WaitIdle();

        // Before the device, and before the application is told, so that nothing hands out an image
        // number that resolves against a renderer which is going.
        editor.ThumbnailSurface = null;
        thumbnails?.Dispose();
        thumbnails = null;

        // The same rule again: the previews hold targets the interface's registry names, and every
        // open graph is told they are gone before they go.
        editor.ShaderGraphPreviews = null;
        previews?.Dispose();
        previews = null;

        // The same rule, for the same reason: the query pools are the device's resources, and the
        // wait above is what makes it safe to take them back — a frame still in flight is one whose
        // command buffer still names them.
        gpu?.Dispose();
        gpu = null;

        // ⚠ Before the application is told, like the thumbnails above: each holds a target the
        // interface's registry names and a tool pass wrapping a tree the renderer owns, and the
        // renderer goes down inside that assignment.
        foreach (var pane in frames) {
            pane?.Dispose();
        }

        frames.Clear();

        editor.GraphicsDevice = null;

        foreach (var presenter in scenes) {
            presenter.Dispose();
        }

        scenes.Clear();

        foreach (var pane in panes) {
            pane.Dispose();
        }

        pool?.Dispose();
        device?.Dispose();

        graph = null;
        pool = null;
        device = null;
    }

    /// <summary>Reads an embedded SPIR-V module.</summary>
    /// <remarks>
    ///     ⚠ Found by suffix rather than named outright: the manifest name is the root namespace
    ///     plus the folder plus the file, so it is
    ///     <c>Vixen.Editor.App.Shaders.UiVertex.vert.spv</c> rather than anything a reader would
    ///     guess — and it changes if the assembly is renamed.
    /// </remarks>
    /// <summary>The terrain's two stages, or default when the modules are not embedded.</summary>
    /// <remarks>
    ///     ⚠ <b>Absent is a viewport with no terrain in it, not a viewport that fails to start.</b>
    ///     These are the only modules here that come from the shader library rather than from a
    ///     standalone source beside them, and a working tree in which <c>CheckShaders</c> has never
    ///     run is an ordinary state to meet. Everything else in the pane still draws.
    /// </remarks>
    TerrainShaders TerrainModules() {
        if (device is not { } graphics || !HasModule("Terrain.vert.spv") || !HasModule("Terrain.frag.spv")) {
            return default;
        }

        return new(
            graphics.CreateShader(ShaderStage.Vertex, Module("Terrain.vert.spv"), "terrain vertex"),
            graphics.CreateShader(ShaderStage.Fragment, Module("Terrain.frag.spv"), "terrain fragment")
        );
    }

    /// <summary>The grass's five modules, or default when any is not embedded.</summary>
    /// <remarks>
    ///     <see cref="TerrainModules" />'s convention, over more files: the draw pair from
    ///     <c>Grass.rvn</c> and three permutations of <c>GrassScatter.rvn</c> — layer-bound, unbound,
    ///     and the argument phase — because a permutation is a separate module on a device. All five
    ///     or none: a scatter without its argument phase draws last frame's counts for ever.
    /// </remarks>
    GrassShaderSet GrassModules() {
        string[] wanted = [
            "Grass.vert.spv",
            "Grass.frag.spv",
            "GrassScatter.comp.spv",
            "GrassScatterUnbound.comp.spv",
            "GrassScatterArguments.comp.spv"
        ];

        if (device is not { } graphics || wanted.Any(module => !HasModule(module))) {
            return default;
        }

        return new(
            new(
                graphics.CreateShader(ShaderStage.Vertex, Module("Grass.vert.spv"), "grass vertex"),
                graphics.CreateShader(ShaderStage.Fragment, Module("Grass.frag.spv"), "grass fragment")
            ),
            graphics.CreateShader(ShaderStage.Compute, Module("GrassScatter.comp.spv"), "grass scatter"),
            graphics.CreateShader(ShaderStage.Compute, Module("GrassScatterUnbound.comp.spv"), "grass scatter unbound"),
            graphics.CreateShader(ShaderStage.Compute, Module("GrassScatterArguments.comp.spv"), "grass arguments")
        );
    }

    /// <summary>Whether a module is embedded, asked without throwing for one that is not.</summary>
    static bool HasModule(string name) =>
        Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Any(entry => entry.EndsWith(name, StringComparison.Ordinal));

    static byte[] Module(string name) {
        var assembly = Assembly.GetExecutingAssembly();

        var resource = assembly.GetManifestResourceNames()
                .SingleOrDefault(entry => entry.EndsWith(name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{name}' is not embedded in this assembly.");

        using var stream = assembly.GetManifestResourceStream(resource)!;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return memory.ToArray();
    }
}
