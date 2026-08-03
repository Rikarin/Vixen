// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>What is drawn over a scene pane: a toolbar, a stats readout and the rubber-band.</summary>
/// <remarks>
///     <para>
///         <b>Chrome, not rendering.</b> Every part of this is an ordinary element in
///         <c>Viewport.Overlay</c> — styled by the cascade, laid out by the layout pass, drawn in the
///         same list as every panel — which is what the overlay was put on the control for. Nothing
///         here reaches into a render target and nothing here knows what a device is.
///     </para>
///     <para>
///         ⚠ <b>One toolbar is visible at a time, and it is the focused pane's.</b> Every scene
///         command acts on <c>ViewportLayout.Focused</c>, so a toolbar over an unfocused pane would
///         be showing that pane's neighbour's gizmo mode, its neighbour's view mode and its
///         neighbour's show flags — four strips of controls of which three are lying. Showing the
///         focused pane's only means what is on screen is always what the buttons do, and clicking in
///         a pane focuses it, so the strip follows the work.
///     </para>
///     <para>
///         ⚠ <b>Rebuilt per rearrangement, and the records are dropped first.</b> A rearrangement
///         throws every pane away and makes new ones; a chrome that kept the old entries would refresh
///         controls belonging to elements that had been removed from the document.
///     </para>
/// </remarks>
sealed class ViewportChrome {
    /// <summary>What is attached to one pane.</summary>
    /// <param name="Pane">The pane.</param>
    /// <param name="Bar">Its overlay toolbar's host element.</param>
    /// <param name="Toolbar">The presenter over that host.</param>
    /// <param name="Stats">Where the counts and the frame time are written.</param>
    /// <param name="Readout">Where a drag's own magnitude and a measurement are written.</param>
    /// <param name="Overlays">Where the contributed overlays' corner columns go.</param>
    readonly record struct Attached(
        SceneViewport Pane,
        UiElement Bar,
        ToolbarPresenter Toolbar,
        TextBlock Stats,
        TextBlock Readout,
        UiElement Overlays
    );

    readonly List<Attached> attached = [];
    readonly EditorShell shell;
    IEditorRegistry? extensions;

    /// <summary>Builds chrome over a shell's commands.</summary>
    /// <param name="shell">Where the buttons' ids are looked up.</param>
    public ViewportChrome(EditorShell shell) {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;
    }

    /// <summary>Forgets every pane, because they are about to be replaced.</summary>
    public void Forget() => attached.Clear();

    /// <summary>Rebuilds every pane's contributed overlays.</summary>
    /// <param name="kind">The contribution kind that changed.</param>
    /// <remarks>
    ///     ⚠ <b>Without this, an overlay works in a plugin and not in a project script, and nothing
    ///     about either says why.</b> <see cref="Attach" /> reads the registry when the panes are
    ///     arranged; a packaged plugin registers at start-up, which is before that, and a project's
    ///     `Editor/` script registers on its first build, which is after — so the same declaration in
    ///     the same words appeared in one tier and silently not in the other. A plugin enabled from the
    ///     manager, or reloaded, lands on the same side as the script.
    ///     <para>
    ///         `EditorWorlds.RefreshAssetKinds` is the same subscription for the same reason, which is
    ///         what makes this the arrangement's rule rather than a special case: a contribution
    ///         registry is read whenever it changes, not once.
    ///     </para>
    /// </remarks>
    public void Refreshed(Type kind) {
        if (kind != typeof(SceneOverlay) || extensions is null) {
            return;
        }

        foreach (var entry in attached) {
            foreach (var corner in entry.Overlays.Children.ToArray()) {
                corner.Remove();
            }

            Contributed(entry.Overlays, entry.Pane, extensions);
        }
    }

    /// <summary>Puts the chrome over one pane.</summary>
    /// <param name="pane">The pane.</param>
    /// <param name="editor">The application, for what the buttons ask about.</param>
    public void Attach(SceneViewport pane, EditorApplication editor) {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(editor);

        var overlay = pane.Control.Overlay;

        // ⚠ First, so that `ToolbarPresenter`'s reserved slot is the top of the overlay. Its
        // constructor records where in the host the strip will go, and everything added after it —
        // the stats readout, the band — would otherwise take that place instead.
        var bar = overlay.Add<UiElement>("viewport-bar");
        var toolbar = new ToolbarPresenter(bar, shell.Commands, shell.Keys);

        toolbar.Show(
            new ToolbarGroup(["scene.translate", "scene.rotate", "scene.scale"]),
            new ToolbarSeparator(),
            new ToolbarButton("scene.toggle-space"),
            new ToolbarButton("scene.toggle-pivot"),
            new ToolbarButton("scene.toggle-snap"),

            // ⚠ Beside the toggle rather than instead of it, and doc 20's A1 asks for exactly this
            // shape: "snap (with a dropdown per snap value)". The button is the thing people press
            // twenty times an hour and the popover is where the four geometry elements, the base and
            // the three modifiers live — every one of which was declared and unreachable before.
            new ToolbarDropdown(SnapTitle, null, EditorApplication.ViewportIds.SnapIds),
            new ToolbarDropdown(PlaneTitle, null, EditorApplication.ViewportIds.WorkPlaneIds),
            new ToolbarDropdown(PrecisionTitle, null, EditorApplication.ViewportIds.PrecisionIds),
            new ToolbarSeparator(),
            new ToolbarDropdown(ViewModeTitle, null, [.. EditorApplication.ViewportIds.ViewModes]),
            new ToolbarDropdown(ShowTitle, null, ShowIds),
            new ToolbarDropdown(SpeedTitle, null, [.. EditorApplication.ViewportIds.SpeedIds]),
            new ToolbarSeparator(),
            new ToolbarButton("scene.toggle-projection"),
            new ToolbarDropdown(LayoutTitle, null, [.. EditorApplication.ViewportIds.Arrangements]),
            new ToolbarButton("scene.maximise")
        );

        var stats = overlay.Add<TextBlock>("viewport-stats");

        // ⚠ Its own element rather than a line of the stats readout, because it is in the middle of
        // the pane and the stats are in a corner. Doc 24 is precise about why it exists: "the extent
        // in metres, on screen, while resizing — both reference editors make you read a details
        // panel", and a number in the corner of a four-pane layout is a details panel with fewer
        // steps.
        var readout = overlay.Add<TextBlock>("viewport-readout");
        readout.AddClass("hidden");

        var band = overlay.Add<MarqueeOverlay>();
        band.Owner = pane;

        // ⚠ Its own host rather than straight into the pane's overlay, so that a later contribution
        // can rebuild the overlays without touching the toolbar, the stats or the band — which are
        // siblings there and would be swept up by anything that cleared it.
        var overlays = overlay.Add<UiElement>();

        extensions = editor.Extensions;
        Contributed(overlays, pane, editor.Extensions);

        attached.Add(new Attached(pane, bar, toolbar, stats, readout, overlays));
    }

    /// <summary>Puts every contributed overlay over one pane, in the corner it asked for.</summary>
    /// <param name="overlay">The pane's overlay host.</param>
    /// <param name="pane">The pane, which is what each overlay is built for.</param>
    /// <param name="extensions">Where the <see cref="SceneOverlay" /> contributions are.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One corner element per corner that is used, and none for the ones that are
    ///         not.</b> Four empty absolutely-positioned columns over every pane in a four-pane layout
    ///         is sixteen elements the layout pass walks to draw nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An overlay that throws is refused, not swallowed.</b> It is built inside the pane
    ///         arrangement rather than inside the plugin's <c>Activate</c>, so there is no registration
    ///         scope to roll back here — what a throw would do without this is take down the whole
    ///         rearrangement and leave the editor with no panes at all. A named diagnostic and a
    ///         missing panel is the lesser failure, and it says whose panel it was.
    ///     </para>
    /// </remarks>
    void Contributed(UiElement overlay, SceneViewport pane, IEditorRegistry extensions) {
        var corners = new Dictionary<OverlayCorner, UiElement>();

        foreach (var contributed in extensions.All<SceneOverlay>().OrderBy(static entry => entry.Order)) {
            if (!corners.TryGetValue(contributed.Corner, out var corner)) {
                corner = overlay.Add<UiElement>("viewport-corner");
                corner.AddClass(Named(contributed.Corner));
                corners[contributed.Corner] = corner;
            }

            var panel = corner.Add<UiElement>("viewport-panel");
            var title = panel.Add<TextBlock>();

            title.AddClass("panel-title");
            title.Text = contributed.Title;

            var body = panel.Add<UiElement>();

            try {
                contributed.Build(body, pane);
            } catch (Exception failure) when (failure is not OutOfMemoryException) {
                panel.AddClass("hidden");
                shell.Notifications.Error(contributed.Id, $"The overlay threw while building: {failure.Message}");
            }
        }
    }

    /// <summary>The style class for a corner.</summary>
    static string Named(OverlayCorner corner) =>
        corner switch {
            OverlayCorner.TopLeft => "top-left",
            OverlayCorner.TopRight => "top-right",
            OverlayCorner.BottomLeft => "bottom-left",
            _ => "bottom-right"
        };

    /// <summary>Brings one pane's chrome up to date, once a frame.</summary>
    /// <param name="pane">The pane.</param>
    /// <param name="focused">Whether it is the pane the scene commands act on.</param>
    /// <remarks>
    ///     ⚠ <b>The toolbar's enablement is refreshed only for the strip that is visible.</b>
    ///     <c>ToolbarPresenter.Refresh</c> asks every command's predicate, and asking three hidden
    ///     strips' worth of predicates per frame is three quarters of the work for none of the
    ///     picture.
    /// </remarks>
    public void Refresh(SceneViewport pane, bool focused) {
        foreach (var entry in attached) {
            if (!ReferenceEquals(entry.Pane, pane)) {
                continue;
            }

            if (focused) {
                entry.Bar.RemoveClass("hidden");
                entry.Toolbar.Refresh();
            } else {
                entry.Bar.AddClass("hidden");
            }

            entry.Stats.Text = Describe(pane);

            if (Readout(pane) is { } text) {
                entry.Readout.RemoveClass("hidden");
                entry.Readout.Text = text;
            } else {
                entry.Readout.AddClass("hidden");
            }

            return;
        }
    }

    /// <summary>The readout in the corner of a pane.</summary>
    /// <remarks>
    ///     ⚠ <b>Frame time and frames a second together, because neither answers on its own.</b>
    ///     Sixteen milliseconds is the number a budget is expressed in and sixty is the number people
    ///     recognise, and a readout with only the second cannot say how much room is left.
    /// </remarks>
    static string Describe(SceneViewport pane) {
        var stats = pane.Stats;
        var culture = CultureInfo.InvariantCulture;

        return string.Create(
            culture,
            $"{stats.Entities} obj  {stats.Triangles} tris  {stats.Draws} draws  {stats.FrameMilliseconds:0.0} ms ({stats.FramesPerSecond:0} fps)"
        );
    }

    /// <summary>What the middle-of-the-pane readout says, or null when it should not be there.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three things share one line and they cannot happen at once.</b> A typed transform
    ///         is a drag, a drag's magnitude is a drag, and a measurement is taken with no drag in
    ///         flight — so the readout is never two answers, and giving each of them its own element
    ///         would be three elements of which at most one is ever visible.
    ///     </para>
    ///     <para>
    ///         The typed text wins over the dragged number, because a user midway through typing
    ///         <c>1.</c> wants to see <c>1.</c> and not the 1.0 metres it currently means.
    ///     </para>
    /// </remarks>
    static string? Readout(SceneViewport pane) {
        if (pane.Typing.IsActive) {
            return pane.Typing.Text;
        }

        if (pane.Gizmo.IsDragging) {
            var (kind, offset, scalar) = pane.Gizmo.Dragged;
            var culture = CultureInfo.CurrentCulture;

            return kind switch {
                GizmoMode.Rotate => scalar.ToString("0.0", culture) + "°",
                GizmoMode.Scale => "×" + scalar.ToString("0.000", culture),

                // Per axis as well as the length, because "two metres" says nothing about which way
                // and a drag along one arm is the case this is most used for.
                _ => string.Create(
                    culture,
                    $"{offset.X:0.00}, {offset.Y:0.00}, {offset.Z:0.00} m   ({scalar:0.00} m)"
                )
            };
        }

        return pane.Measure.Describe();
    }

    /// <summary>The show-flag ids with the grid's in front of them.</summary>
    /// <remarks>
    ///     ⚠ <b>The grid's toggle is <c>scene.toggle-grid</c> and always has been.</b> It is the one
    ///     show flag that had a command before there were show flags, and registering a second one
    ///     over the same state to make this list uniform is exactly the two-writers mistake doc 20
    ///     names. So the list is composed here instead.
    /// </remarks>
    static readonly string?[] ShowIds = ["scene.toggle-grid", .. EditorApplication.ViewportIds.ShowFlagIds];

    static readonly StringId SnapTitle = new("editor.viewport.snap", "Snap");
    static readonly StringId PlaneTitle = new("editor.viewport.work-plane", "Plane");
    static readonly StringId PrecisionTitle = new("editor.viewport.precision", "Measure");
    static readonly StringId ViewModeTitle = new("editor.viewport.view-mode", "View");
    static readonly StringId ShowTitle = new("editor.viewport.show", "Show");
    static readonly StringId SpeedTitle = new("editor.viewport.speed", "Speed");
    static readonly StringId LayoutTitle = new("editor.viewport.layout", "Panes");
}
