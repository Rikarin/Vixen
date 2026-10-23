// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>The scene panel's model: the panes while it is open, and what every pane is handed.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D2's rule for a panel's state, applied to the scene panel (#1294).</b> The
///         layout, the chrome drawn over each pane, the play overlays and <c>Configure</c> were
///         <c>EditorApplication</c>'s, and the panel's factory, the frame's per-pane memory and the
///         focused-view question read them there. They are this model's now; the application builds
///         it once over what only it holds and registers the panel it describes.
///     </para>
///     <para>
///         ⚠ <b>Several of those are asked for at each read rather than taken.</b> The picker and the
///         probe are <i>rebuilt</i> when the active scene changes (<c>SetActiveScene</c>), and the
///         preferences are <i>replaced</i> by a Revert, so a captured one would configure a rebuilt
///         pane with the scene that was open at start-up. The snap, the plane, the mesh edit, the play
///         keyboard and the bridges are the application's for its whole life, and are taken.
///     </para>
///     <para>
///         ⚠ <b>It is also <see cref="IActiveView" /></b>, which was an adapter holding the whole
///         application to read one property. The focused pane is this model's answer, so the
///         published contract is this object and nothing in between.
///     </para>
/// </remarks>
sealed class ScenePanes : IActiveView, IDisposable {
    /// <summary>The panel's id, which a saved arrangement carries it back by.</summary>
    public const string PanelId = "scene";

    readonly EditorShell shell;
    readonly string context;
    readonly World world;
    readonly Func<SceneDocument> scene;
    readonly Func<SceneViewState> viewState;
    readonly Func<ScenePicker> picker;
    readonly Func<SceneProbe> probe;
    readonly Func<IMeshSource> meshes;
    readonly Func<IReadOnlyList<LineVertex>> diagnostics;
    readonly IEditorRegistry extensions;
    readonly SnapContext snap;
    readonly WorkPlane plane;
    readonly MeshEdit editing;
    readonly PlayInput playInput;
    readonly IReadOnlyList<IComponentBridge> bridges;
    readonly Func<EditorPreferences> preferences;
    readonly Action writePreferences;
    readonly Func<float> renderScale;
    readonly Action registerViewModes;

    /// <summary>The toolbar, the stats readout and the rubber-band drawn over each pane.</summary>
    ViewportChrome? chrome;

    /// <summary>Builds the model over what the application holds.</summary>
    /// <param name="shell">The shell, whose context a press in the panel claims.</param>
    /// <param name="context">The context a press claims when the active mode has none of its own.</param>
    /// <param name="world">The editor's world, which an entity gizmo moves things in.</param>
    /// <param name="scene">The active scene, asked for at each read.</param>
    /// <param name="viewState">The workspace's per-view state for the scene panel (#1303).</param>
    /// <param name="picker">The picker, asked for at each read: it is rebuilt with the active scene.</param>
    /// <param name="probe">The probe, asked for at each read for the picker's reason.</param>
    /// <param name="meshes">The mesh source the scene's components resolve through.</param>
    /// <param name="diagnostics">The play session's overlay lines, the one list every pane draws.</param>
    /// <param name="extensions">This editor's registry, not the process-wide default.</param>
    /// <param name="snap">The one snap every gizmo and every drop reads.</param>
    /// <param name="plane">The work plane the grid draws.</param>
    /// <param name="editing">Which mesh's elements are being selected (doc 24's P2).</param>
    /// <param name="playInput">The play session's keyboard, ahead of the active mode.</param>
    /// <param name="bridges">The component bridges every pane's gizmos share.</param>
    /// <param name="preferences">The preferences, asked for at each read: a Revert replaces them.</param>
    /// <param name="writePreferences">Writes the preferences file.</param>
    /// <param name="renderScale">The display's scale, which belongs to the window.</param>
    /// <param name="registerViewModes">Registers the frame's trees against the panes that now exist.</param>
    public ScenePanes(
        EditorShell shell,
        string context,
        World world,
        Func<SceneDocument> scene,
        Func<SceneViewState> viewState,
        Func<ScenePicker> picker,
        Func<SceneProbe> probe,
        Func<IMeshSource> meshes,
        Func<IReadOnlyList<LineVertex>> diagnostics,
        IEditorRegistry extensions,
        SnapContext snap,
        WorkPlane plane,
        MeshEdit editing,
        PlayInput playInput,
        IReadOnlyList<IComponentBridge> bridges,
        Func<EditorPreferences> preferences,
        Action writePreferences,
        Func<float> renderScale,
        Action registerViewModes
    ) {
        this.shell = shell;
        this.context = context;
        this.world = world;
        this.scene = scene;
        this.viewState = viewState;
        this.picker = picker;
        this.probe = probe;
        this.meshes = meshes;
        this.diagnostics = diagnostics;
        this.extensions = extensions;
        this.snap = snap;
        this.plane = plane;
        this.editing = editing;
        this.playInput = playInput;
        this.bridges = bridges;
        this.preferences = preferences;
        this.writePreferences = writePreferences;
        this.renderScale = renderScale;
        this.registerViewModes = registerViewModes;
    }

    /// <summary>The panes the scene is drawn in, or <see langword="null" /> while the panel is closed.</summary>
    public ViewportLayout? Layout { get; private set; }

    /// <summary>The focused pane, or <see langword="null" /> while the panel is closed.</summary>
    public SceneViewport? Focused => Layout?.Focused;

    /// <summary>Every pane, in reading order. Empty while the panel is closed.</summary>
    public IReadOnlyList<SceneViewport> Panes => Layout?.Panes ?? [];

    /// <summary>A play session's overlay panels, laid out against the focused pane.</summary>
    public PaneOverlays Overlays { get; } = new();

    /// <summary>How the scene panel is split.</summary>
    /// <remarks>
    ///     The workspace's per-view state's, so it outlives the panel; the open panes follow it.
    ///     Moved from the application (#1296), so play's maximise can set it without the application.
    /// </remarks>
    public ViewportArrangement Arrangement {
        get => viewState().Arrangement;

        set {
            var state = viewState();

            if (state.Arrangement == value) {
                return;
            }

            state.Arrangement = value;

            // ⚠ Forgotten before the rebuild, not after. `ViewportLayout` raises `Rearranged` from
            // inside the setter below, and the handler is what restores these — so clearing
            // afterwards would restore last arrangement's cameras into this one's panes and then
            // throw the record away.
            state.ForgetCameras();

            if (Layout is { } layout) {
                layout.Arrangement = value;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Asked every time.</b> The focused pane changes as somebody clicks between four of them,
    ///     so a panel handed a view at activation would keep answering for whichever pane happened to
    ///     have focus when it loaded.
    /// </remarks>
    RenderView? IActiveView.Current => Focused?.View;

    SceneViewState ViewState => viewState();

    /// <summary>Describes the panel.</summary>
    /// <returns>The descriptor.</returns>
    public PanelDescriptor Describe() =>
        new(
            PanelId,
            EditorStrings.PanelScene,
            panel => {
                // ⚠ The panel must not scroll, and this is the case that would be a bug rather
                // than an annoyance. A viewport sizes its render target from its own laid-out box
                // and turns a pointer into a pick with `(x - AbsoluteLeft) * RenderScale` — so an
                // offset the panel applied and the viewport knew nothing about would move every
                // click a scroll's worth away from what the user aimed at, on a panel that never
                // needed to scroll because the viewport fills it by construction.
                panel.Scrolls = false;

                // ⚠ The one panel that does not report a constant, and the editor mode is why. A
                // mode is "a statement about what the viewport's input means right now" — doc
                // 20's A1 — and the way Blockout claims 1, 2, 3 and 4 from view-bookmark recall
                // without taking them from anywhere else is by being the context the pane reports
                // while it is active. A mode with no context of its own — Select — leaves the
                // pane reporting `SceneContext`, which is the editor exactly as it was. That is
                // why `WhenPressedIn` reads the claim on every press rather than capturing one.
                //
                // The other half is `RegisterModes`: entering a mode claims the context without
                // waiting for a press, because somebody who has just pressed the Blockout button
                // has aimed at the viewport and should not have to click it as well.
                panel.WhenPressedIn(() => shell.Context = shell.Modes.Context ?? context);

                var document = scene();

                // ⚠ A layout rather than a control, and every pane in it is a whole
                // `SceneViewport`. Doc 11 asks for "multiple simultaneous viewports with
                // independent cameras and render modes", and the second half of that is what
                // forces it: a view mode is stage state, so a pane that wanted its own would
                // silently change its neighbour's.
                var layout = new ViewportLayout(panel, document.Selection) {
                    Document = document,
                    // ⚠ The element modes take the gizmo over, and the factory is where that
                    // happens rather than in the pane: what a gizmo drags is exactly what a mode
                    // says is selected, and doc 24's P2 is that inside a mesh that is a set of
                    // its corners rather than the entity round them.
                    TargetsFactory = () =>
                        editing.IsActive && !editing.Selection.IsEmpty
                            ? [new MeshGizmoTarget(scene(), editing)]
                            : EntityGizmoTarget.For(world, scene().Selection),
                    Arrangement = ViewState.Arrangement
                };

                Layout = layout;

                // ⚠ Subscribed after the constructor and then called by hand, because the
                // constructor's own `Rebuild` raises this before there is anything to hear it —
                // and the panes it made are the ones that need configuring.
                layout.Rearranged += Configure;
                Configure(layout);
            }
        ) {
            // ⚠ The other half of the factory. A closed Scene tab takes every pane's control
            // with it, and `Update` walking them on the next frame asks a removed element for
            // its width — see `PanelDescriptor.Closed`.
            Closed = () => {
                Layout?.Dispose();
                Layout = null;
            }
        };

    /// <summary>Gives every pane of a rearranged layout what only the application can supply.</summary>
    /// <param name="layout">The layout whose panes were just made.</param>
    /// <remarks>
    ///     <para>
    ///         <b>Run on every rearrangement, because a rearrangement makes new panes.</b>
    ///         <c>ViewportLayout</c> pushes the document and the target factory into new panes itself
    ///         — they are its own properties — and everything here is something it has no business
    ///         knowing about: the picker, the probe, the display scale, where the camera was, and the
    ///         chrome drawn over the top.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The focus handler is added per pane and captures that pane.</b> A handler that
    ///         read the loop variable would give every pane the last one, which is a four-pane layout
    ///         where clicking any pane focuses the fourth — and the symptom is that three of the four
    ///         ignore the keyboard.
    ///     </para>
    /// </remarks>
    void Configure(ViewportLayout layout) {
        chrome ??= new ViewportChrome(shell);
        chrome.Forget();
        Overlays.Forget();

        for (var index = 0; index < layout.Panes.Count; index++) {
            var pane = layout.Panes[index];

            pane.Control.RenderScale = renderScale();

            // ⚠ Without the picker a click in the viewport selects nothing — the picking stage wants
            // a target driven by a render system the editor's viewport does not have, so the only way
            // to select an entity would be the hierarchy panel. Shared across panel rebuilds and
            // across panes, because its cache of shapes is worth keeping and a scene has one answer.
            var shared = picker();

            pane.Picker = shared;
            pane.Surfaces = probe();

            // ⚠ The same source the scene's mesh components already resolve through, so the selection
            // cage cannot disagree with the surface it is drawn round about how big that surface is.
            // Read by `SceneLines` and therefore by both presenters — see `SceneViewport.Meshes`.
            pane.Meshes = meshes();

            // ⚠ The one list, handed to every pane. What a play session's overlays drew is produced
            // once per frame by the loop, which does not know how many panes there are — so four
            // panes each asking a producer would draw a frame apiece. See `SceneViewport.Diagnostics`.
            pane.Diagnostics = diagnostics();

            // ⚠ This editor's registry, not the process-wide default the pane falls back to. A pane
            // reading a different registry from the one plugins were handed is a pane whose tool
            // list is empty however many tools were contributed — which is what this line was
            // written for, after exactly that.
            pane.Extensions = extensions;

            // ⚠ The same instance in both, and the same one every other pane has. A drop and a drag
            // onto the same ramp cannot disagree about whether the thing landing on it stands up,
            // because there is one answer — see `SnapContext`.
            pane.Gizmo.Snap = snap;
            pane.Placement.Snap = snap;

            // The grid is a view of the plane and the plane is the editor's, so a pane rebuilt by a
            // rearrangement comes up drawing the wall the designer was working on rather than the
            // ground.
            pane.Grid.Plane = plane;

            // The same object as the picker and a separate property on purpose — see
            // `ISubObjectPicker`. What it caches is per shape kind, so sharing it across panes is
            // what stops a four-pane layout welding a torus four times.
            pane.SubObjects = shared;

            // Doc 24's P2: which mesh's elements are being selected, shared for the reason above.
            pane.Editing = editing;

            // ⚠ The mode's first refusal on this pane's input, and the adapter is what joins two
            // assemblies that cannot see each other: `IEditorMode` is the shell's and `IViewportInput`
            // is the pane's, and the application is the only thing that knows about both. Shared
            // across panes and across rebuilds, because it holds nothing but the mode registry.
            // ⚠ Behind the play session's keyboard, which takes a key first while a session runs and
            // the pane has not let go of it with Escape, and hands everything else to the mode (#470).
            pane.Input = playInput;

            // ⚠ Restored, because this runs again every time the panel is reopened and a fresh
            // SceneViewport starts at the origin looking down −Z. Absent for a pane of an
            // arrangement nobody has looked at yet, which is what leaves the quad presets alone.
            if (index < SceneViewState.MaxPanes) {
                var remembered = ViewState.Panes[index];

                if (remembered.Camera is { } saved) {
                    pane.Camera.Restore(saved);
                }

                // ⚠ The workspace's slots, not the pane's own: a pane is rebuilt with the panel, and the
                // nine views saved in the last one went with it (#1303).
                pane.Bookmarks = remembered.Bookmarks;
            }

            // ⚠ And what it was drawing, which is the same restore one shelf along: the camera
            // survives a panel being reopened because it is kept in the workspace, and the show flags
            // survive a *restart* because they are kept in the preferences file. Both are read here
            // because this is the one place a freshly built pane passes through.
            preferences().RestoreView(index, pane);

            var focused = pane;

            pane.Control.AddHandler<FocusEvent>(
                (_, args) => {
                    if (args.Gained) {
                        layout.Focus(focused);
                    }

                    // ⚠ Both ways: a key held as the focus leaves is released where the game never
                    // hears it, so losing the focus has to let go of it here (#470).
                    playInput.Focus(args.Gained);
                },
                handledEventsToo: true
            );

            // ⚠ One `ComponentGizmos` per pane, sharing the one bridge list. The bridges are the
            // expensive half and they are the same for every pane; what is per-pane is the show flag
            // and the line list, both of which live on the pane already.
            pane.Gizmos = new(bridges, extensions);

            chrome.Attach(pane, extensions);

            // ⚠ After the chrome, so a play session's overlay panel is stroked over the toolbar as a
            // game's HUD is over its picture. Every pane gets one and only the focused pane's paints
            // — see `PaneOverlays` (#1264).
            Overlays.Attach(pane);
        }

        // ⚠ After the loop and not inside it, because the panes are what it walks. A rearrangement
        // builds new `SceneViewport`s and therefore new `ViewModes`, which know nothing about the
        // trees the renderer built before they existed — see `RegisterViewModes`.
        registerViewModes();
    }

    /// <summary>Brings every pane up to date, and keeps what each is looking at.</summary>
    /// <param name="delta">How long the last frame took.</param>
    /// <returns>Whether a gizmo is being dragged in any pane, which the inspector follows.</returns>
    public bool Update(TimeSpan delta) {
        if (Layout is not { } layout) {
            return false;
        }

        // ⚠ Every pane, not only the focused one. A pane nobody has clicked in still has a camera
        // that has to be brought up to date before its render view is read, and a four-pane layout
        // where three of the panes only redraw once somebody has clicked in them is one where the
        // other three look frozen. Which pane *flies* is decided by the focus, inside the pane.
        for (var index = 0; index < layout.Panes.Count; index++) {
            var pane = layout.Panes[index];

            pane.Update(delta);
            pane.Stats.Sample(delta);
            chrome?.Refresh(pane, ReferenceEquals(pane, layout.Focused));

            // ⚠ Kept every frame, not on the way out. A panel's factory runs again when it is
            // reopened and the SceneViewport goes with the old one, so there is no teardown hook to
            // read the camera in — and a bookmark taken once at startup would restore the origin
            // every time.
            if (index < SceneViewState.MaxPanes) {
                ViewState.Panes[index].Camera = pane.Camera.Bookmark("current");
            }

            if (preferences().RememberView(index, pane)) {
                writePreferences();
            }
        }

        return layout.Panes.Any(static pane => pane.Gizmo.IsDragging);
    }

    /// <summary>Rebuilds the contributed overlays of one kind over every pane.</summary>
    /// <param name="kind">The overlay type whose contribution changed.</param>
    public void Refreshed(Type kind) => chrome?.Refreshed(kind);

    /// <inheritdoc />
    public void Dispose() => Layout?.Dispose();
}
