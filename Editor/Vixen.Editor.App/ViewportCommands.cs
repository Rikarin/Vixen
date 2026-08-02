// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>What one pane can be told to do, beyond the gizmo and the camera.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's E2, as commands rather than as a second control panel.</b> The view mode, the
///         show flags, the pane count, the camera speed, the bookmarks and maximise are every one of
///         them registry entries — so each appears in the palette, each can be rebound, each greys
///         itself out when there is no viewport, and the Scene menu and the viewport's own overlay
///         toolbar are two views over one list rather than two lists that have to agree.
///     </para>
///     <para>
///         ⚠ <b>Every one of them acts on the <i>focused</i> pane.</b> That is what makes a four-pane
///         layout mean anything: pressing the wireframe key changes the pane you are working in and
///         not its three neighbours. <see cref="EditorApplication.Viewport" /> is the focused pane and
///         <c>ViewportLayout</c> tracks it from the controls' own focus.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>The ids the Scene menu and the viewport toolbar are both built from.</summary>
    /// <remarks>
    ///     ⚠ <b>Composed rather than written out, in one place.</b> A menu naming a command nothing
    ///     registered is silently skipped — which is the behaviour that lets the shell name
    ///     <c>file.save</c> without owning it, and which would quietly empty three submenus if a
    ///     literal here and a literal at the registration drifted apart by a hyphen.
    /// </remarks>
    internal static class ViewportIds {
        /// <summary>The id of the command that puts a pane into a view mode.</summary>
        public static string ViewMode(ViewMode mode) => "scene.view-mode-" + ViewShading.SlugOf(mode);

        /// <summary>The id of the command that toggles a show flag.</summary>
        public static string Show(SceneShow flag) => "scene.show-" + ShowFlags.SlugOf(flag);

        /// <summary>The id of the command that sets the pane count.</summary>
        public static string Arrangement(ViewportArrangement value) =>
            "scene.panes-" + value switch {
                ViewportArrangement.SideBySide => "side-by-side",
                ViewportArrangement.Stacked => "stacked",
                ViewportArrangement.Quad => "quad",
                _ => "single"
            };

        /// <summary>The id of the command that sets the camera's speed.</summary>
        public static string Speed(int index) => "scene.speed-" + Speeds[index].Name;

        /// <summary>The id of the command that saves the camera into a numbered slot.</summary>
        public static string SetBookmark(int slot) => "scene.bookmark-set-" + (slot + 1);

        /// <summary>And of the one that goes back to it.</summary>
        public static string GoBookmark(int slot) => "scene.bookmark-go-" + (slot + 1);

        /// <summary>The id of the command that toggles one snap element.</summary>
        public static string SnapElement(SnapElements element) =>
            "scene.snap-" + element switch {
                SnapElements.Vertex => "vertex",
                SnapElements.Edge => "edge",
                SnapElements.EdgeCentre => "edge-centre",
                _ => "surface"
            };

        /// <summary>The id of the command that chooses what a snap moves onto the point.</summary>
        public static string SnapBase(SnapBase value) => "scene.snap-from-" + value.ToString().ToLowerInvariant();

        /// <summary>The id of the command that toggles one snap modifier.</summary>
        public static string SnapModifier(SnapModifiers modifier) =>
            "scene.snap-" + modifier switch {
                SnapModifiers.AlignToTarget => "align",
                SnapModifiers.ProjectFromView => "project",
                _ => "ignore-self"
            };

        /// <summary>Everything on the snap dropdown, in order, with the rules between the groups.</summary>
        /// <remarks>
        ///     ⚠ <b>Three groups because doc 24's D4 says they are three orthogonal things</b> — what
        ///     you land on, what of yours lands on it, and everything true of a snap without being
        ///     either. A flat list of eleven ticks says none of that.
        /// </remarks>
        public static string?[] SnapIds { get; } = [
            "scene.toggle-snap",
            null,
            SnapElement(SnapElements.Vertex),
            SnapElement(SnapElements.Edge),
            SnapElement(SnapElements.EdgeCentre),
            SnapElement(SnapElements.Face),
            null,
            SnapBase(SceneView.SnapBase.Origin),
            SnapBase(SceneView.SnapBase.Centre),
            SnapBase(SceneView.SnapBase.Active),
            SnapBase(SceneView.SnapBase.Pointer),
            null,
            SnapModifier(SnapModifiers.AlignToTarget),
            SnapModifier(SnapModifiers.ProjectFromView),
            SnapModifier(SnapModifiers.IgnoreSelf)
        ];

        /// <summary>The id of the command that puts a scale reference in the pane.</summary>
        public static string Reference(ReferenceVolume volume) =>
            "scene.reference-" + volume.Name.ToLowerInvariant();

        /// <summary>Everything on the work-plane dropdown, in order.</summary>
        public static string?[] WorkPlaneIds { get; } = [
            "scene.work-plane-to-face",
            "scene.work-plane-to-selection",
            "scene.work-plane-to-world",
            null,
            "scene.work-plane-raise",
            "scene.work-plane-lower",
            null,
            "scene.grid-step-double",
            "scene.grid-step-halve",
            "scene.grid-step-auto"
        ];

        /// <summary>And on the precision one: the tape measure and the scale references.</summary>
        public static string?[] PrecisionIds { get; } = [
            "scene.measure",
            "scene.measure-clear",
            null,
            .. ReferenceVolumes.All.Select(Reference),
            "scene.reference-clear"
        ];

        /// <summary>Every view-mode id, in menu order.</summary>
        public static string[] ViewModes { get; } = [.. ViewShading.All.Select(ViewMode)];

        /// <summary>Every show-flag id except the grid's, in menu order.</summary>
        /// <remarks>
        ///     ⚠ <b>The grid's is <c>scene.toggle-grid</c> and is deliberately not in here.</b> It is
        ///     the one flag that had a command before there were show flags, and a second command over
        ///     the same state — registered only so that this list could be uniform — is two menu lines
        ///     that can disagree about what is on.
        /// </remarks>
        public static string[] ShowFlagIds { get; } = [.. ShowFlags.All.Where(static flag => flag != SceneShow.Grid)
            .Select(Show)];

        /// <summary>Every pane-count id, in menu order.</summary>
        public static string[] Arrangements { get; } = [
            Arrangement(ViewportArrangement.Single),
            Arrangement(ViewportArrangement.SideBySide),
            Arrangement(ViewportArrangement.Stacked),
            Arrangement(ViewportArrangement.Quad)
        ];

        /// <summary>Every camera-speed id, in menu order.</summary>
        public static string[] SpeedIds { get; } = [.. Enumerable.Range(0, Speeds.Length).Select(Speed)];

        /// <summary>The nine recall ids.</summary>
        public static string[] GoBookmarks { get; } =
            [.. Enumerable.Range(0, SceneViewport.BookmarkSlots).Select(GoBookmark)];

        /// <summary>The nine save ids.</summary>
        public static string[] SetBookmarks { get; } =
            [.. Enumerable.Range(0, SceneViewport.BookmarkSlots).Select(SetBookmark)];
    }

    /// <summary>The fly speeds the Camera Speed menu offers, as multiples of the default.</summary>
    /// <remarks>
    ///     ⚠ <b>Multiplicative and coarse, which is what a speed picker is for.</b>
    ///     <c>EditorCamera.Fly</c> already scales by the distance to the pivot, so this is the
    ///     multiplier on top of that — the thing somebody reaches for when a level turns out to be ten
    ///     times bigger than the last one. A linear slider of thirty values is a control nobody can
    ///     aim; five steps a factor of two apart cover three orders of magnitude.
    /// </remarks>
    static readonly (string Name, string Label, float Factor)[] Speeds = [
        ("slowest", "Slowest (0.25×)", 0.25f),
        ("slow", "Slow (0.5×)", 0.5f),
        ("normal", "Normal (1×)", 1f),
        ("fast", "Fast (2×)", 2f),
        ("fastest", "Fastest (4×)", 4f)
    ];

    /// <summary>What <c>EditorCamera.FlySpeed</c> is at a factor of one.</summary>
    /// <remarks>
    ///     <see cref="EditorCamera.FlySpeed" />'s own default, repeated because the multipliers above
    ///     have to be multipliers <i>of</i> something and a pane whose speed has been changed no
    ///     longer remembers what it started at.
    /// </remarks>
    const float BaseFlySpeed = 4f;

    /// <summary>What arrangement play mode's maximise goes back to.</summary>
    /// <remarks>
    ///     ⚠ <b>Held rather than assumed to be Quad.</b> Maximise-on-play is a toggle, and one that
    ///     came back to a fixed arrangement would turn somebody's two-pane layout into four panes the
    ///     first time they pressed Play.
    /// </remarks>
    ViewportArrangement? restore;

    /// <summary>The window's arrangement while the Scene panel is maximised over it.</summary>
    /// <remarks>
    ///     ⚠ <b>The saved YAML rather than a flag, because a maximise throws the arrangement away.</b>
    ///     What comes back has to be the splitter positions, the tab order and the panels that were
    ///     open — all of which live in the layout the docking host serialises, and none of which
    ///     survives being rebuilt from a preset. Null means the panel is not maximised, which is what
    ///     the command's tick reads.
    /// </remarks>
    string? unmaximised;

    /// <summary>Whether entering play mode maximises the scene panel.</summary>
    bool maximiseOnPlay;

    void ViewportCommands() {
        ViewModeCommands();
        ShowFlagCommands();
        ArrangementCommands();
        SpeedCommands();
        BookmarkCommands();
        SnapCommands();
        WorkPlaneCommands();
        PrecisionCommands();

        // ⚠ Not a `Pane` command, and that is the fix rather than a detail. This used to set the
        // *pane count* to Single and remember what it had been — so pressing it in the default
        // layout, which is already Single, asked the arrangement to become what it already was and
        // `Arrangement`'s setter returned without doing anything. The button did nothing, every
        // time, for the only layout most people ever use.
        //
        // What maximise means in every editor that has one is the panel filling the window: the
        // hierarchy, the inspector and the console go away and the scene gets the whole frame. That
        // is an arrangement, so it is the workspace's to do and not a viewport's.
        Shell.Commands.Add(
            new EditorCommand(
                "scene.maximise",
                Label("scene.maximise", "Maximise Viewport"),
                ToggleMaximised
            ) {
                Category = CategoryScene,

                // Enabled from the panel rather than from a pane, for `ArrangementCommands`' reason:
                // maximising a closed panel is meaningless, and coming *back* has to stay reachable
                // even in the state where the Scene tab is the only thing on screen.
                Enablement = () => unmaximised is not null || Shell.Workspace.IsOpen(ScenePanel),
                Checked = () => unmaximised is not null
            }
        );

        Shell.Keys.SetDefault("scene.maximise", new KeyChord(InputKey.Space, ModifierKeys.Shift));
    }

    /// <summary>What the Scene panel is called in an arrangement.</summary>
    internal const string ScenePanel = "scene";

    /// <summary>Gives the Scene panel the whole window, or gives the window back.</summary>
    /// <remarks>
    ///     ⚠ <b>The arrangement is saved before it is replaced and applied whole afterwards.</b> A
    ///     maximise that came back by re-applying a preset would be one that silently threw away
    ///     every splitter the user had dragged and every panel they had opened — which is the same
    ///     complaint, one press later.
    /// </remarks>
    void ToggleMaximised() {
        if (unmaximised is { } saved) {
            unmaximised = null;
            Shell.Workspace.Load(saved);

            return;
        }

        if (!Shell.Workspace.IsOpen(ScenePanel)) {
            return;
        }

        unmaximised = Shell.Workspace.Save();

        // ⚠ Closed rather than left to the new arrangement, because `DockingHost.SetLayout` keeps a
        // panel the layout does not name — it tabs it into the first group, deliberately, so that
        // applying a preset does not silently throw away an open document. Here that is exactly
        // wrong: it would give the Scene tab the window with the inspector and the console stacked
        // behind it, which is not a maximise, it is a rearrangement.
        foreach (var id in Shell.Workspace.Host.Panels.Keys.Where(id => id != ScenePanel).ToList()) {
            Shell.Workspace.Close(id);
        }

        Shell.Workspace.Show(LayoutPresets.Single(ScenePanel));
    }

    /// <summary>Records what to come back to, and answers Single.</summary>
    ViewportArrangement Remember() {
        restore = arrangement;
        return ViewportArrangement.Single;
    }

    /// <summary>Answers what was recorded, and forgets it.</summary>
    /// <remarks>
    ///     ⚠ <b>Forgotten on the way back, so the tick goes off.</b> A maximise that stayed "on" after
    ///     it had been undone is a toggle whose state is a lie, and the next press would come back to
    ///     the arrangement it was already in.
    /// </remarks>
    ViewportArrangement Take(ViewportArrangement previous) {
        restore = null;
        return previous;
    }

    /// <summary>One command per view mode, ticked, with the unsupported two declared and disabled.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Overdraw and light complexity are registered and greyed rather than absent.</b> Doc
    ///         20's first bar is that a verb which is not implemented is <i>visibly</i> not implemented
    ///         — and the alternative for a view mode is worse than absence: a mode with no compositor
    ///         falls back to shaded, so the menu line would draw the same picture as the line above it
    ///         and read as the editor ignoring the click. See <see cref="ViewShading" />.
    ///     </para>
    ///     <para>
    ///         ✅ <b>Roughness was a third and enabled itself.</b> Its excuse was that the tool renderer
    ///         had no material to read a roughness off, and it has one now — nothing here changed, and
    ///         nothing needed to, because the enablement is <see cref="ViewShading.IsSupported" />'s
    ///         answer rather than a list written out twice.
    ///     </para>
    /// </remarks>
    void ViewModeCommands() {
        foreach (var value in ViewShading.All) {
            var mode = value;

            if (!ViewShading.IsSupported(mode)) {
                Planned(
                    ViewportIds.ViewMode(mode),
                    Label(ViewportIds.ViewMode(mode), ViewShading.NameOf(mode)),
                    CategoryScene,
                    Excuse(mode)
                );

                continue;
            }

            Pane(
                ViewportIds.ViewMode(mode),
                ViewShading.NameOf(mode),
                pane => pane.Modes.Current = mode,
                on: pane => pane.Modes.Current == mode,
                radioGroup: "scene.view-mode"
            );
        }

        static string Excuse(ViewMode mode) =>
            mode switch {
                SceneView.ViewMode.Overdraw =>
                    "Overdraw needs an additive pipeline with the depth test off, which the editor's tool "
                    + "renderer does not carry. It arrives with the compositor-driven viewport in Phase 7.",
                _ =>
                    "Light complexity is a count off the clustered light list, which the editor's viewport does "
                    + "not run. It arrives with the compositor-driven viewport in Phase 7."
            };
    }

    /// <summary>One ticked command per show flag.</summary>
    /// <remarks>
    ///     ⚠ <b>The grid's is <c>scene.toggle-grid</c> and is not registered twice.</b> A second
    ///     command over the same flag is two menu lines that can disagree about what is on, which is
    ///     doc 20's rule about two writers to one setting in its smallest possible form.
    /// </remarks>
    void ShowFlagCommands() {
        foreach (var value in ShowFlags.All) {
            var flag = value;

            if (flag == SceneShow.Grid) {
                continue;
            }

            Pane(
                ViewportIds.Show(flag),
                ShowFlags.NameOf(flag),
                pane => pane.Show ^= flag,
                on: pane => (pane.Show & flag) != 0
            );
        }
    }

    /// <summary>One ticked command per pane count.</summary>
    void ArrangementCommands() {
        Split("Single", ViewportArrangement.Single, InputKey.Number1);
        Split("Side by Side", ViewportArrangement.SideBySide, InputKey.Number2);
        Split("Stacked", ViewportArrangement.Stacked, InputKey.Number3);
        Split("Four Panes", ViewportArrangement.Quad, InputKey.Number4);

        void Split(string label, ViewportArrangement value, InputKey key) {
            var id = ViewportIds.Arrangement(value);

            Shell.Commands.Add(
                new EditorCommand(id, Label(id, label), () => Arrangement = value) {
                    Category = CategoryScene,
                    RadioGroup = "scene.panes",

                    // ⚠ Enabled from the panel rather than from a pane, unlike everything else here.
                    // Splitting a closed panel is meaningless, and a pane count is a property of the
                    // panel — so this is the one viewport command that does not go through `Pane`.
                    Enablement = () => viewports is not null,
                    Checked = () => arrangement == value
                }
            );

            Shell.Keys.SetDefault(id, new KeyChord(key, ModifierKeys.Alt));
        }
    }

    /// <summary>One ticked command per camera speed.</summary>
    void SpeedCommands() {
        for (var index = 0; index < Speeds.Length; index++) {
            var (_, label, factor) = Speeds[index];
            var wanted = BaseFlySpeed * factor;

            Pane(
                ViewportIds.Speed(index),
                label,
                pane => pane.Camera.FlySpeed = wanted,

                // ⚠ Compared with a tolerance rather than for equality. The speed is a float a
                // multiplication produced, and a tick that only lit up when two of them were bit-wise
                // identical is one that flickers off for reasons nobody can see.
                on: pane => MathF.Abs(pane.Camera.FlySpeed - wanted) < 0.001f,
                radioGroup: "scene.speed"
            );
        }
    }

    /// <summary>Doc 24's D4: the element, the base and the three modifiers, as commands.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without these the geometry elements were declared and unreachable, which is doc
    ///         24's B5 in the form it survived in.</b> <c>SnapToVertex</c> has been honoured by the
    ///         viewport for a while; what there was no way to do was turn it on. <c>scene.toggle-snap</c>
    ///         moves the increment, the angle and the scale together and says nothing about the four
    ///         that need geometry.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>They act on the focused pane's context, which is the editor's one context.</b>
    ///         <c>EditorApplication</c> hands the same <c>SnapContext</c> to every pane's gizmo and to
    ///         every pane's placement, so "the focused pane's" is a way of reaching it rather than a
    ///         claim that panes have their own.
    ///     </para>
    /// </remarks>
    void SnapCommands() {
        Element(SnapElements.Vertex, "Snap to Vertex");
        Element(SnapElements.Edge, "Snap to Edge");
        Element(SnapElements.EdgeCentre, "Snap to Edge Centre");
        Element(SnapElements.Face, "Snap to Surface");

        Base(SnapBase.Origin, "Snap from Origin");
        Base(SnapBase.Centre, "Snap from Centre");
        Base(SnapBase.Active, "Snap from Active");
        Base(SnapBase.Pointer, "Snap from Grabbed Point");

        Modifier(SnapModifiers.AlignToTarget, "Align to Target");
        Modifier(SnapModifiers.ProjectFromView, "Search from the View");
        Modifier(SnapModifiers.IgnoreSelf, "Ignore What Is Being Dragged");

        void Element(SnapElements element, string label) =>
            Pane(
                ViewportIds.SnapElement(element),
                label,
                pane => pane.Gizmo.Snap.Toggle(element, !pane.Gizmo.Snap.Has(element)),
                on: pane => pane.Gizmo.Snap.Has(element)
            );

        // ⚠ A radio group, because the base is a choice of one and three ticks is not how a choice
        // reads. See `EditorCommand.RadioGroup`.
        void Base(SnapBase value, string label) =>
            Pane(
                ViewportIds.SnapBase(value),
                label,
                pane => pane.Gizmo.Snap.Base = value,
                on: pane => pane.Gizmo.Snap.Base == value,
                radioGroup: "scene.snap-base"
            );

        void Modifier(SnapModifiers modifier, string label) =>
            Pane(
                ViewportIds.SnapModifier(modifier),
                label,
                pane => pane.Gizmo.Snap.Toggle(modifier, !pane.Gizmo.Snap.Is(modifier)),
                on: pane => pane.Gizmo.Snap.Is(modifier)
            );
    }

    /// <summary>Doc 24's D5: the grid is a plane you can move, tilt, offset and re-step.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>"To face" asks the scene under the pointer rather than the selection.</b> The
    ///         gesture D5 describes is "select a wall, press a key, the grid is on the wall", and until
    ///         there are faces to select the honest reading of "the face" is the surface the pointer is
    ///         over — which is a question <c>SceneProbe</c> already answers exactly, normal included.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The step doubles and halves from whatever the grid is drawing.</b> Until somebody
    ///         presses one of these there is no step — the spacing is a function of how far away the
    ///         camera is — so the first press has to be told what was on screen, or it would double a
    ///         number the user has never seen. Powers of two from there, so every level is a
    ///         sub-lattice of the last and a 0.25 m object is still on the 4 m grid's lines.
    ///     </para>
    /// </remarks>
    void WorkPlaneCommands() {
        Pane(
            "scene.work-plane-to-face",
            "Work Plane to Face",
            pane => {
                if (pane.Surfaces is { } probe && probe.Raycast(pane.PointerRay(), [], out var hit)) {
                    pane.Grid.Plane.SetTo(hit.Point, hit.Normal);
                }
            },
            enabled: pane => pane.Surfaces is not null,
            key: InputKey.G,
            modifiers: ModifierKeys.Shift
        );

        Pane(
            "scene.work-plane-to-selection",
            "Work Plane to Selection",
            pane => pane.Grid.Plane.SetTo(SelectionCentre(), Vector3.UnitY),
            enabled: _ => !scene.Selection.IsEmpty
        );

        Pane(
            "scene.work-plane-to-world",
            "Work Plane to World",
            pane => pane.Grid.Plane.Reset(),
            enabled: pane => !pane.Grid.Plane.IsGround
        );

        // ⚠ Along the plane's own normal rather than along world Y, which is D5's third gesture:
        // building the second floor at three metres without doing arithmetic, and doing the same
        // thing one wall-thickness out when the plane is on a wall.
        Pane(
            "scene.work-plane-raise",
            "Raise Work Plane",
            pane => pane.Grid.Plane.Offset(Step(pane)),
            key: InputKey.Equals,
            modifiers: ModifierKeys.Shift
        );

        Pane(
            "scene.work-plane-lower",
            "Lower Work Plane",
            pane => pane.Grid.Plane.Offset(-Step(pane)),
            key: InputKey.Minus,
            modifiers: ModifierKeys.Shift
        );

        Pane(
            "scene.grid-step-double",
            "Double Grid Step",
            pane => pane.Grid.Plane.Coarsen(Spacing(pane)),
            key: InputKey.RightBracket
        );

        Pane(
            "scene.grid-step-halve",
            "Halve Grid Step",
            pane => pane.Grid.Plane.Refine(Spacing(pane)),
            key: InputKey.LeftBracket
        );

        Pane(
            "scene.grid-step-auto",
            "Grid Step Follows the Camera",
            pane => pane.Grid.Plane.Auto(),
            on: pane => pane.Grid.Plane.Step is null
        );

        float Spacing(SceneViewport pane) => pane.Grid.Spacing(pane.Camera, pane.Control.RenderHeight);

        float Step(SceneViewport pane) => pane.Grid.Plane.Effective(Spacing(pane));
    }

    /// <summary>The middle of what is selected, which is where "to selection" puts the plane.</summary>
    Vector3 SelectionCentre() {
        var total = Vector3.Zero;
        var count = 0;

        foreach (var entity in scene.Selection) {
            if (scene.World.Has<WorldTransform>(entity)) {
                total += scene.World.Read<WorldTransform>(entity).Value.Translation;
                count++;
            }
        }

        return count == 0 ? Vector3.Zero : total / count;
    }

    /// <summary>Doc 24's precision group: the tape measure and the scale references.</summary>
    /// <remarks>
    ///     ⚠ <b>Neither of these is a mode and neither creates anything.</b> A measurement and a
    ///     reference volume are drawn in the pane and are gone when they are cleared — see
    ///     <c>ReferenceVolumes</c> for why "drawn and not shipped" is the whole point of them.
    /// </remarks>
    void PrecisionCommands() {
        Pane(
            "scene.measure",
            "Measure",
            pane => {
                pane.Measure.IsActive = !pane.Measure.IsActive;

                if (!pane.Measure.IsActive) {
                    pane.Measure.Clear();
                }
            },
            on: pane => pane.Measure.IsActive,
            key: InputKey.M,
            modifiers: ModifierKeys.Shift
        );

        Pane(
            "scene.measure-clear",
            "Clear Measurement",
            pane => pane.Measure.Clear(),
            enabled: pane => pane.Measure.Points.Count > 0
        );

        foreach (var volume in ReferenceVolumes.All) {
            var placed = volume;

            Pane(
                ViewportIds.Reference(placed),
                "Reference: " + placed.Name,

                // On the work plane under the pointer, which is where the designer is building — and
                // the reason a reference volume knows where its own box sits relative to that point:
                // a person stands on the floor and a corridor is a hole you are inside.
                pane => pane.References.Add(placed, pane.PointerOnPlane())
            );
        }

        Pane(
            "scene.reference-clear",
            "Clear References",
            pane => pane.References.Clear(),
            enabled: pane => !pane.References.IsEmpty
        );
    }

    /// <summary>Nine slots to save a view into and nine to come back from.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>Ctrl+1..9</c> sets and <c>1..9</c> recalls</b>, which is the pair both reference
    ///         editors ship and which people arrive already knowing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A recall of an empty slot is <i>disabled</i>, not a no-op.</b> A key that does
    ///         nothing and a key that does nothing <i>yet</i> look identical while you are pressing
    ///         it; the enablement is what makes the menu say which of the nine have anything in them.
    ///     </para>
    /// </remarks>
    void BookmarkCommands() {
        for (var index = 0; index < SceneViewport.BookmarkSlots; index++) {
            var slot = index;
            var number = (slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            // ⚠ The number row, not the keypad, and counted from `Number1` because the nine are
            // consecutive there. `Keypad1..9` are the six axis views and the four orbit steps, which
            // is the whole reason the bookmarks are on the other set of digits.
            var key = (InputKey) ((ushort) InputKey.Number1 + slot);

            Pane(ViewportIds.SetBookmark(slot), "Set View " + number, pane => pane.SaveBookmark(slot));

            Pane(
                ViewportIds.GoBookmark(slot),
                "View " + number,
                pane => pane.RestoreBookmark(slot),
                enabled: pane => pane.HasBookmark(slot)
            );

            Shell.Keys.SetDefault(ViewportIds.SetBookmark(slot), new KeyChord(key, ModifierKeys.Control));
            Shell.Keys.SetDefault(ViewportIds.GoBookmark(slot), new KeyChord(key, ModifierKeys.None));
        }
    }

    /// <summary>The category every viewport command is filed under.</summary>
    static readonly StringId CategoryScene = new("editor.category.scene", "Scene");

    /// <summary>A localisation id for a viewport command's label, from its own id.</summary>
    static StringId Label(string id, string text) => new("editor.command." + id, text);

    /// <summary>Registers a command that acts on the focused pane.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="label">What it is called.</param>
    /// <param name="action">What it does to the pane.</param>
    /// <param name="on">Whether it is ticked, or null when it is not a toggle.</param>
    /// <param name="enabled">An extra condition beyond there being a pane.</param>
    /// <param name="radioGroup">Which choice it belongs to, so it draws as one segment.</param>
    /// <param name="key">A default binding.</param>
    /// <param name="modifiers">Its modifiers.</param>
    /// <remarks>
    ///     ⚠ <b><c>Checked</c> is left null when there is no predicate rather than answering
    ///     false.</b> <c>MenuPresenter</c> grows a tick column only for the commands that have one, so
    ///     a lambda here would indent every line of every viewport submenu by an empty tick.
    /// </remarks>
    void Pane(
        string id,
        string label,
        Action<SceneViewport> action,
        Func<SceneViewport, bool>? on = null,
        Func<SceneViewport, bool>? enabled = null,
        string? radioGroup = null,
        InputKey key = InputKey.Unknown,
        ModifierKeys modifiers = ModifierKeys.None
    ) {
        Shell.Commands.Add(
            new EditorCommand(id, Label(id, label), () => {
                    if (Viewport is { } pane) {
                        action(pane);
                    }
                }
            ) {
                Category = CategoryScene,
                RadioGroup = radioGroup,
                Enablement = () => Viewport is { } pane && (enabled is null || enabled(pane)),
                Checked = on is null ? null : () => Viewport is { } pane && on(pane)
            }
        );

        if (key != InputKey.Unknown) {
            Shell.Keys.SetDefault(id, new KeyChord(key, modifiers));
        }
    }
}
