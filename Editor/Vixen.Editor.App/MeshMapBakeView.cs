// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.MeshMaps;
using Vixen.Geometry.Remeshing;
using Measurements = Vixen.Geometry.Remeshing.MeshMaps;

namespace Vixen.Editor.App;

/// <summary>Doc 48 § D12's bake panel: what a mesh-map bake measures, at what size, and what it did.</summary>
/// <remarks>
///     <para>
///         The panel is <c>MeshMapBakeView.vxml</c>; this file is the type declaration the emitter's
///         partial pairs with, plus the settings behind it and the row its result list is made of —
///         the same arrangement <see cref="BuildSettingsView" /> has, deliberately.
///     </para>
///     <para>
///         <b>What § D12's last bullet asks for, in one sentence: a bake somebody chose.</b> The verb
///         baked at a hard-coded 1024 with every map on and <c>BakeSettings</c>'s default sixty-four
///         rays, and its own comment said the constant was "a preview size until § D12's bake panel
///         exists". A hero asset wants 4K and several hundred rays, and nobody wants to discover that
///         by picking a menu item.
///     </para>
///     <para>
///         ⚠ <b>And to see what came back, which is half of why the maps are project assets at
///         all.</b> <c>BakedMaps.Warnings</c> has always carried what a bake could not do — a texel
///         no ray reached, a search radius too short for the cage — and the only place it reached was
///         the detail line of a toast that is gone in four seconds. The result list here is what
///         makes "the curvature map looks wrong" answerable without re-running the bake.
///     </para>
///     <para>
///         ⚠ <b>One settings object, two views</b>, which is doc 20's A4 rule and the reason the menu
///         line opens this rather than baking: a verb that baked with its own constants and a panel
///         that baked with its fields would be two answers to "what does Bake do", and the one
///         somebody got would be whichever they used last.
///     </para>
///     <para>
///         ⚠ <b>Nothing here is bound, for <see cref="BuildSettingsView" />'s reason.</b> An effect
///         runs at the frame's flush, and this panel's tests read <c>BakeButton.Disabled</c> back on
///         the line after <c>Rebuild()</c>.
///     </para>
/// </remarks>
sealed partial class MeshMapBakeView;

/// <summary>What a mesh-map bake is set to measure, and how finely.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The editor's, not the project's, and not persisted — a limitation rather than a
///         decision.</b> Every other settings surface in the editor is a <c>[DataContract]</c> under
///         <c>ProjectSettings/</c>; this one is a field on <see cref="EditorApplication" /> because
///         a bake setting is not yet something a checkout has to agree about. It means a resolution
///         somebody raised is back at 1024 next session.
///     </para>
///     <para>
///         ⚠ <b><see cref="SearchRadius" /> is a fraction of the source's bounding-box diagonal and
///         never a distance</b> — <c>BakeSettings</c>'s own remark, restated here because this is
///         where a person types the number. A cage in metres is a claim about how big a model is,
///         and the same character exported in centimetres finds nothing.
///     </para>
///     <para>
///         ⚠ <b>The panel asks in <see cref="MeshMapUsage" /> and this answers in the bake's flags,
///         and that seam is here on purpose.</b> They are two enums for the reason
///         <see cref="MeshMapUsage" />'s own remarks give — one says what to spend rays on, the other
///         names one file — and a panel that spoke the bake's flags would be a panel that has to know
///         which two of the nine are not in them.
///     </para>
/// </remarks>
sealed class MeshMapBakeSettings {
    /// <summary>How big a map, on a side.</summary>
    public int Resolution { get; set; } = 1024;

    /// <summary>How many texels the gutter is dilated by, past the charts' edges.</summary>
    public int Gutter { get; set; } = 4;

    /// <summary>How far a ray looks for the source, as a fraction of its bounding-box diagonal.</summary>
    public float SearchRadius { get; set; } = 0.05f;

    /// <summary>How many rays the hemisphere at a texel is sampled with.</summary>
    public int OcclusionSamples { get; set; } = 64;

    /// <summary>Which of § D12's seven to measure. The normal and the displacement are not optional.</summary>
    public Measurements Maps { get; set; } = Measurements.All;

    /// <summary>The seven a person can turn off, in the order § D12's table lists them.</summary>
    public static IReadOnlyList<MeshMapUsage> Optional { get; } = [
        MeshMapUsage.AmbientOcclusion,
        MeshMapUsage.BentNormal,
        MeshMapUsage.Curvature,
        MeshMapUsage.Thickness,
        MeshMapUsage.Position,
        MeshMapUsage.WorldNormal,
        MeshMapUsage.Id
    ];

    /// <summary>How many maps a bake with these settings would write.</summary>
    /// <remarks>The two that are always baked, plus whichever of the seven are on.</remarks>
    public int Count {
        get {
            var many = MeshMapBake.Always.Count;

            foreach (var usage in Optional) {
                if (Wants(usage)) {
                    many++;
                }
            }

            return many;
        }
    }

    /// <summary>Whether a bake would measure one of the seven.</summary>
    /// <param name="usage">Which map.</param>
    /// <returns>Whether it is on. Anything not in <see cref="Optional" /> is not.</returns>
    public bool Wants(MeshMapUsage usage) => Flag(usage) is { } flag && Maps.HasFlag(flag);

    /// <summary>Turns one of the seven on or off.</summary>
    /// <param name="usage">Which map.</param>
    /// <param name="on">Whether to measure it.</param>
    /// <remarks>A usage that is not one of the seven is ignored: the other two are not optional.</remarks>
    public void Want(MeshMapUsage usage, bool on) {
        if (Flag(usage) is not { } flag) {
            return;
        }

        Maps = on ? Maps | flag : Maps & ~flag;
    }

    /// <summary>These settings as the bake's own.</summary>
    /// <returns>The settings <c>MapBaker.Bake</c> takes.</returns>
    /// <remarks>
    ///     ⚠ <b><c>Space</c> and <c>OcclusionRadius</c> are deliberately left at their defaults</b>
    ///     rather than put on the panel. Tangent space is what a material samples and an
    ///     object-space bake is a different workflow with a different consumer; the occlusion radius
    ///     is in the same units trap as <see cref="SearchRadius" /> and has no second reader yet.
    ///     Both are fields this type can grow the day something reads them — which is the bar doc 20
    ///     sets for a shipped setting.
    /// </remarks>
    public BakeSettings ToBake() =>
        new() {
            Resolution = Resolution,
            Gutter = Gutter,
            SearchRadius = SearchRadius,
            OcclusionSamples = OcclusionSamples,
            Maps = Maps
        };

    /// <summary>Which ray budget a usage is spent out of, or null where it is not one of the seven.</summary>
    static Measurements? Flag(MeshMapUsage usage) => usage switch {
        MeshMapUsage.AmbientOcclusion => Measurements.AmbientOcclusion,
        MeshMapUsage.BentNormal => Measurements.BentNormal,
        MeshMapUsage.Curvature => Measurements.Curvature,
        MeshMapUsage.Thickness => Measurements.Thickness,
        MeshMapUsage.Position => Measurements.Position,
        MeshMapUsage.WorldNormal => Measurements.WorldNormal,
        MeshMapUsage.Id => Measurements.Id,
        _ => null
    };
}

/// <summary>One row of the panel's "what the last bake produced" list.</summary>
/// <param name="Map">What it measures, as the file name's own suffix says it.</param>
/// <param name="File">Its file name, with no directory.</param>
sealed record BakedMapRow(string Map, string File);
