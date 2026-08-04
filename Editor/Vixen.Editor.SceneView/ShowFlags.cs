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

    // ⚠ 1 << 7 was `Outline`, the rim drawn round whatever is selected, and the gap is deliberate.
    // The bit is not reused: a saved pane state from a build that had the flag would otherwise come
    // back with whatever now occupies that bit switched on, which is a viewport that silently changes
    // what it draws on upgrade. What replaced the rim is that a selected surface is already tinted
    // amber — see `SceneMeshes.SelectedColour` — so the outline was a second answer to a question that
    // already had one, drawn in a different colour.

    /// <summary>Each post-process volume's box, and a second one at its blend radius.</summary>
    /// <remarks>
    ///     ⚠ <b>Two boxes rather than one, because the falloff is the part somebody gets wrong.</b> A
    ///     volume that looks right and does nothing is usually one whose blend radius the camera never
    ///     enters, and a number in an inspector cannot show that. The outer box is where it starts to
    ///     apply and the inner one is where it fully applies.
    /// </remarks>
    Volumes = 1 << 8,

    /// <summary>Whatever a component's own gizmo draws for it.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own flag rather than <see cref="Gizmos" />, which is the transform handles.</b>
    ///     Turning the handles off is somebody saying "stop putting an arrow over the thing I am
    ///     looking at"; it is not them saying a trigger volume should become invisible. The two are one
    ///     word apart and sharing a switch would have been wrong every time either was used.
    /// </remarks>
    Components = 1 << 9,

    /// <summary>What a pane comes up with.</summary>
    /// <remarks>
    ///     Everything except <see cref="Bounds" />, which is a diagnostic rather than a picture: a box
    ///     round every object is the one flag that makes a busy scene less legible rather than more.
    /// </remarks>
    Default = Grid | Markers | Parents | Lights | Meshes | Gizmos | Volumes | Components
}

/// <summary>The show flags as a list, so a menu is generated rather than written twice.</summary>
/// <remarks>
///     ⚠ <b>Generated from this, the same bargain <c>PrimitiveShapes.All</c> makes.</b> A flag added to
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
        SceneShow.Volumes,
        SceneShow.Components
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
            SceneShow.Volumes => "Post-process Volumes",
            SceneShow.Meshes => "Meshes",
            SceneShow.Gizmos => "Gizmos",
            SceneShow.Bounds => "Bounds",
            SceneShow.Components => "Component Gizmos",
            _ => flag.ToString()
        };

    /// <summary>The flags a set of slugs names, ignoring any it does not recognise.</summary>
    /// <param name="slugs">What <see cref="SlugOf" /> wrote.</param>
    /// <returns>The set.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Slugs and not the enum's own bits, which is what makes a saved pane survive this
    ///         file being edited.</b> The numbers are positions in a bitset that has already lost a
    ///         member once — see <see cref="SceneShow" />'s gap at 1 &lt;&lt; 7 — so a preferences
    ///         file holding <c>388</c> would come back meaning something different the next time a
    ///         flag is added or removed. The same bargain <c>EditorPreferences.ProjectTileSize</c>
    ///         makes for the same reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unknown slug is skipped rather than refused.</b> It is either a flag a newer
    ///         build had, or one an older build has not got yet — a user who moves between two of
    ///         them keeps the flags both agree on rather than losing the lot, and a plugin's flag that
    ///         is not loaded today is not thrown out of the file.
    ///     </para>
    /// </remarks>
    public static SceneShow Parse(IEnumerable<string> slugs) {
        ArgumentNullException.ThrowIfNull(slugs);

        var found = SceneShow.None;

        foreach (var slug in slugs) {
            foreach (var flag in All) {
                if (string.Equals(SlugOf(flag), slug, StringComparison.OrdinalIgnoreCase)) {
                    found |= flag;
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>A set of flags as the slugs <see cref="Parse" /> reads back.</summary>
    /// <param name="show">The set.</param>
    /// <returns>The slugs, in <see cref="All" />'s order.</returns>
    public static List<string> Slugs(SceneShow show) =>
        [.. All.Where(flag => (show & flag) != 0).Select(SlugOf)];

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
            SceneShow.Volumes => "volumes",
            SceneShow.Components => "component-gizmos",
            _ => "grid"
        };
}
