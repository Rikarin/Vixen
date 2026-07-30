// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
    readonly record struct Attached(
        SceneViewport Pane,
        UiElement Bar,
        ToolbarPresenter Toolbar,
        TextBlock Stats
    );

    readonly List<Attached> attached = [];
    readonly EditorShell shell;

    /// <summary>Builds chrome over a shell's commands.</summary>
    /// <param name="shell">Where the buttons' ids are looked up.</param>
    public ViewportChrome(EditorShell shell) {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;
    }

    /// <summary>Forgets every pane, because they are about to be replaced.</summary>
    public void Forget() => attached.Clear();

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

        var band = overlay.Add<MarqueeOverlay>();
        band.Owner = pane;

        attached.Add(new Attached(pane, bar, toolbar, stats));
    }

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

    /// <summary>The show-flag ids with the grid's in front of them.</summary>
    /// <remarks>
    ///     ⚠ <b>The grid's toggle is <c>scene.toggle-grid</c> and always has been.</b> It is the one
    ///     show flag that had a command before there were show flags, and registering a second one
    ///     over the same state to make this list uniform is exactly the two-writers mistake doc 20
    ///     names. So the list is composed here instead.
    /// </remarks>
    static readonly string?[] ShowIds = ["scene.toggle-grid", .. EditorApplication.ViewportIds.ShowFlagIds];

    static readonly StringId ViewModeTitle = new("editor.viewport.view-mode", "View");
    static readonly StringId ShowTitle = new("editor.viewport.show", "Show");
    static readonly StringId SpeedTitle = new("editor.viewport.speed", "Speed");
    static readonly StringId LayoutTitle = new("editor.viewport.layout", "Panes");
}
