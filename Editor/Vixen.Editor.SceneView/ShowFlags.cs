// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.SceneView;

/// <summary>What a viewport draws, as one bitset per pane.</summary>
/// <remarks>
///     <para>
///         <b>Unreal's Show flags and Unity's scene-view toggles, and the reason both exist is the
///         same.</b> A viewport that draws everything is unreadable the moment a scene has lights,
///         parents and bounds in it, and every one of those is something somebody needs to look at on
///         its own for ten minutes and never again. A bitset the collectors read is the smallest thing
///         that gives them that.
///     </para>
///     <para>
///         ⚠ <b>Per pane, not per editor.</b> The point of a four-pane layout is that the panes
///         disagree — a wireframe top view beside a shaded perspective one is the whole reason
///         somebody asked for four — so this lives on <see cref="SceneViewport" /> beside the camera
///         and the view mode rather than on the application.
///     </para>
///     <para>
///         ⚠ <b>Only flags with something behind them are here.</b> Doc 20's checklist names
///         colliders, audio sources and navigation as well; the editor has no collider component, no
///         audio-source component and no navigation mesh to draw, so a tick for any of them would be
///         a control that does nothing — which is doc 20's own second bar ("nothing they find is a
///         toy") failed by the menu that was meant to satisfy it. They arrive with the subsystems, and
///         adding one here is a line in this enum and a branch in <see cref="SceneLines" />.
///     </para>
/// </remarks>
[Flags]
public enum SceneShow {
    /// <summary>An empty pane: the clear colour and nothing else.</summary>
    None = 0,

    /// <summary>The floor grid.</summary>
    Grid = 1 << 0,

    /// <summary>The three-axis cross at every entity.</summary>
    Markers = 1 << 1,

    /// <summary>The faded line from an entity to its parent.</summary>
    Parents = 1 << 2,

    /// <summary>What each light reaches: the cone, the rings, the rays.</summary>
    Lights = 1 << 3,

    /// <summary>The surfaces of shaped entities.</summary>
    Meshes = 1 << 4,

    /// <summary>The transform handles.</summary>
    Gizmos = 1 << 5,

    /// <summary>A box round each shaped entity, in its own axes.</summary>
    Bounds = 1 << 6,

    /// <summary>The rim drawn round whatever is selected.</summary>
    Outline = 1 << 7,

    /// <summary>What a pane comes up with.</summary>
    /// <remarks>
    ///     Everything except <see cref="Bounds" />, which is a diagnostic rather than a picture: a box
    ///     round every object is the one flag that makes a busy scene less legible rather than more.
    /// </remarks>
    Default = Grid | Markers | Parents | Lights | Meshes | Gizmos | Outline
}

/// <summary>The show flags as a list, so a menu is generated rather than written twice.</summary>
/// <remarks>
///     ⚠ <b>Generated from this, the same bargain <c>MeshShapes.All</c> makes.</b> A flag added to
///     <see cref="SceneShow" /> and to <see cref="All" /> appears in the Show menu, in the viewport's
///     overlay popover and in the palette without any of the three being edited — and a flag added to
///     the enum alone appears in none of them, which is the failure that is visible immediately rather
///     than the one that is not.
/// </remarks>
public static class ShowFlags {
    /// <summary>Every flag a user can toggle, in the order they are offered.</summary>
    /// <remarks>
    ///     <see cref="SceneShow.None" /> and <see cref="SceneShow.Default" /> are deliberately absent:
    ///     they are not things to draw, they are the two ends of the set.
    /// </remarks>
    public static IReadOnlyList<SceneShow> All { get; } = [
        SceneShow.Grid,
        SceneShow.Markers,
        SceneShow.Parents,
        SceneShow.Lights,
        SceneShow.Meshes,
        SceneShow.Gizmos,
        SceneShow.Bounds,
        SceneShow.Outline
    ];

    /// <summary>What a flag is called in a menu.</summary>
    /// <param name="flag">The flag.</param>
    /// <returns>Its label.</returns>
    public static string NameOf(SceneShow flag) =>
        flag switch {
            SceneShow.Grid => "Grid",
            SceneShow.Markers => "Entity Markers",
            SceneShow.Parents => "Parent Links",
            SceneShow.Lights => "Light Shapes",
            SceneShow.Meshes => "Meshes",
            SceneShow.Gizmos => "Gizmos",
            SceneShow.Bounds => "Bounds",
            SceneShow.Outline => "Selection Outline",
            _ => flag.ToString()
        };

    /// <summary>What a flag is called in a command id.</summary>
    /// <param name="flag">The flag.</param>
    /// <returns>A lower-case, hyphenated name.</returns>
    public static string SlugOf(SceneShow flag) =>
        flag switch {
            SceneShow.Markers => "markers",
            SceneShow.Parents => "parents",
            SceneShow.Lights => "lights",
            SceneShow.Meshes => "meshes",
            SceneShow.Gizmos => "gizmos",
            SceneShow.Bounds => "bounds",
            SceneShow.Outline => "outline",
            _ => "grid"
        };
}
