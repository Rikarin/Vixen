// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Vixen.Geometry.Uv;

namespace Vixen.Editor.Blockout;

/// <summary>What one island looks like in a panel: where it is, and how badly it is stretched.</summary>
/// <param name="Island">Which island, by index into <see cref="BlockoutUvPanel.Islands" />.</param>
/// <param name="Minimum">Its lower corner in the atlas, after packing.</param>
/// <param name="Maximum">Its upper corner.</param>
/// <param name="Distortion">Its worst triangle's stretch, one being none.</param>
/// <param name="Flipped">How many of its triangles came out wound the wrong way.</param>
public readonly record struct UvIslandView(
    int Island,
    Vector2 Minimum,
    Vector2 Maximum,
    float Distortion,
    int Flipped
) {
    /// <summary>Whether this island is the one a heat map should shout about.</summary>
    /// <remarks>
    ///     ⚠ <b>A flipped triangle is a correctness failure wearing a metric's clothes</b> —
    ///     docs/plan/42's report says <c>FlippedTriangles</c> "must be zero" — so an island with one is
    ///     bad however low its stretch is, and the two conditions are deliberately or-ed rather than
    ///     averaged into a single score that would hide it.
    /// </remarks>
    public bool IsBad => Flipped > 0 || Distortion > BlockoutUvPanel.BadDistortion;
}

/// <summary>docs/plan/42 § D13's editor UV panel, as the half that runs without a device.</summary>
/// <remarks>
///     <para>
///         <b>"A UV panel: islands, distortion as a heat map, seam display, and the three verbs
///         separately."</b> That is the whole of the requirement and this is the whole of the state it
///         needs: which islands there are, where each one sits, how stretched each one is, which edges
///         are seams, and a verb apiece for chart, flatten and pack. The drawing — the atlas rectangle,
///         the island outlines, the colour ramp — reads these and adds nothing to them.
///     </para>
///     <para>
///         ⚠ <b>It is not a drag-a-vertex-in-UV-space tool, and docs/plan/42's "What this does not
///         become" item 2 says so in as many words.</b> Every verb here replaces the whole layout;
///         nothing moves one coordinate. Editing an island by hand is doc 20's surface and a different
///         document, and a panel that grew one gesture in that direction would have to grow an undo
///         model, a selection model and a snapping model with it.
///     </para>
///     <para>
///         ⚠ <b>The three stages are separate calls because they are separately useful.</b> § D1: an
///         artist who cut seams elsewhere wants those seams kept and the islands rearranged, so
///         <see cref="Pack" /> runs on whatever <see cref="Islands" /> currently holds rather than
///         re-deriving it — which is the same property <c>vixen uv pack</c> exists to expose.
///     </para>
///     <para>
///         ⚠ <b>A refusal leaves the previous state alone.</b> The packer throws when the islands did
///         not fit and could not be made to; catching it here and keeping the last good layout is what
///         stops "I raised the margin too far" from emptying the panel.
///     </para>
/// </remarks>
public sealed class BlockoutUvPanel {
    /// <summary>Above this stretch an island is drawn as a problem rather than as a colour.</summary>
    /// <remarks>
    ///     The same number <see cref="UvSettings.DistortionThreshold" /> defaults to, because the
    ///     charter splits a chart when it exceeds that — so an island still above it after flattening
    ///     is one the recursion could not fix, which is exactly what a heat map is for.
    /// </remarks>
    public const float BadDistortion = 1.15f;

    EditMesh? mesh;

    /// <summary>What the panel is looking at, or null.</summary>
    /// <remarks>Setting it clears everything derived, because none of it describes the new mesh.</remarks>
    public EditMesh? Mesh {
        get => mesh;
        set {
            mesh = value;

            Charts = [];
            Islands = [];
            Placements = [];
            Views = [];
            Report = default;
            Messages = [];
        }
    }

    /// <summary>Where to cut and how flat to make it.</summary>
    public UvSettings Settings { get; set; } = new();

    /// <summary>Where the islands go.</summary>
    public PackSettings Packing { get; set; } = new() { Resolution = 1024 };

    /// <summary>One chart index per face, or empty before <see cref="Chart" /> has run.</summary>
    public IReadOnlyList<int> Charts { get; private set; } = [];

    /// <summary>The flattened islands, or empty before <see cref="Flatten" /> has run.</summary>
    public IReadOnlyList<UvIsland> Islands { get; private set; } = [];

    /// <summary>Where the packer put each island, or empty before <see cref="Pack" /> has run.</summary>
    public IReadOnlyList<UvPlacement> Placements { get; private set; } = [];

    /// <summary>What to draw: one entry per island, in the atlas's coordinates.</summary>
    public IReadOnlyList<UvIslandView> Views { get; private set; } = [];

    /// <summary>The last report any stage produced.</summary>
    public UvReport Report { get; private set; }

    /// <summary>What to show a person about the last thing that ran.</summary>
    public IReadOnlyList<string> Messages { get; private set; } = [];

    /// <summary>Raised whenever anything above changed, so a view can redraw.</summary>
    public event Action<BlockoutUvPanel>? Changed;

    /// <summary>Runs the charting stage: where to cut.</summary>
    /// <returns>Whether it produced charts.</returns>
    public bool Chart() {
        if (Mesh is not { IsEmpty: false } source) {
            return Fail("There is no mesh to chart.");
        }

        try {
            Charts = UvUnwrap.Charts(source, Settings, out var report);
            Report = report;
            Messages = [.. report.Warnings, $"{report.ChartCount} charts, seam length {report.SeamLengthNormalized:0.###}."];
        } catch (Exception failure) when (failure is InvalidOperationException or ArgumentException) {
            return Fail($"Charting refused: {failure.Message}");
        }

        Islands = [];
        Placements = [];
        Views = [];

        Changed?.Invoke(this);

        return Charts.Count > 0;
    }

    /// <summary>Runs the flattening stage over the current charts, charting first if it has to.</summary>
    /// <returns>Whether it produced islands.</returns>
    public bool Flatten() {
        if (Mesh is not { IsEmpty: false } source) {
            return Fail("There is no mesh to flatten.");
        }

        if (Charts.Count != source.FaceCount && !Chart()) {
            return false;
        }

        try {
            Islands = UvUnwrap.Flatten(source, Charts, Settings, out var report);
            Report = report;

            Messages = [
                .. report.Warnings,
                $"{Islands.Count} islands, angular distortion {report.Distortion.Angular:0.###}, "
                + $"{report.Distortion.Flipped} flipped."
            ];
        } catch (Exception failure) when (failure is InvalidOperationException or ArgumentException) {
            return Fail($"Flattening refused: {failure.Message}");
        }

        Placements = [];
        Views = Describe(Islands, []);

        Changed?.Invoke(this);

        return Islands.Count > 0;
    }

    /// <summary>Runs the packing stage over the current islands, flattening first if it has to.</summary>
    /// <returns>Whether it placed them.</returns>
    public bool Pack() {
        if (Islands.Count == 0 && !Flatten()) {
            return false;
        }

        IReadOnlyList<UvPlacement> placed;
        UvReport report;

        try {
            placed = UvUnwrap.Pack(Islands, Packing, out report);
        } catch (Exception failure) when (failure is InvalidOperationException or ArgumentException) {
            // ⚠ Before anything is written, so a pack that refused leaves the last one on screen.
            return Fail($"Packing refused: {failure.Message}");
        }

        Placements = placed;
        Report = report;

        Messages = [
            .. report.Warnings,
            $"{report.EffectiveEfficiency:P1} of the atlas used after a {Packing.Margin}-texel margin "
            + $"at {Packing.Resolution}."
        ];

        Views = Describe(Islands, placed);

        Changed?.Invoke(this);

        return placed.Count > 0;
    }

    /// <summary>Every edge the current charting cut, as pairs of positions.</summary>
    /// <returns>Two positions per seam edge, in the mesh's own space.</returns>
    /// <remarks>
    ///     <b>§ D13's "seam display".</b> A seam is an edge whose two faces landed in different charts,
    ///     which is a question the chart-per-face array answers directly — so the panel does not need
    ///     the charter to hand back a seam set, and cannot disagree with it about what one is.
    /// </remarks>
    public IReadOnlyList<(Vector3 A, Vector3 B)> Seams() {
        if (Mesh is not { } source || Charts.Count != source.FaceCount) {
            return [];
        }

        var seams = new List<(Vector3, Vector3)>();

        for (var edge = 0; edge < source.Edges.Count; edge++) {
            var faces = source.FacesOf(edge);

            // An open boundary is a seam by construction and is counted as one: it is where the
            // atlas is cut whether anybody chose it or not, and a display that hid it would show a
            // closed island outline around a chart that has a hole in it.
            if (faces.Length == 1 || (faces.Length == 2 && Charts[faces[0]] != Charts[faces[1]])) {
                seams.Add((source.Positions[source.Edges[edge].A], source.Positions[source.Edges[edge].B]));
            }
        }

        return seams;
    }

    /// <summary>The heat map's ramp position for an island, in <c>[0, 1]</c>.</summary>
    /// <param name="view">The island.</param>
    /// <returns>Zero for undistorted, one at twice the bad threshold and above.</returns>
    /// <remarks>
    ///     ⚠ <b>Anchored at one rather than at the minimum observed.</b> A ramp normalised over what
    ///     happens to be in this atlas makes the least bad island green on a mesh where every island is
    ///     terrible, which is the one case a heat map exists to catch.
    /// </remarks>
    public static float Heat(in UvIslandView view) =>
        Math.Clamp((view.Distortion - 1f) / Math.Max(BadDistortion - 1f, 1e-6f) * 0.5f, 0f, 1f);

    /// <summary>Islands as the panel draws them, placed if there are placements and raw if not.</summary>
    static List<UvIslandView> Describe(IReadOnlyList<UvIsland> islands, IReadOnlyList<UvPlacement> placements) {
        var views = new List<UvIslandView>(islands.Count);
        var placed = new Dictionary<int, UvPlacement>();

        foreach (var placement in placements) {
            placed[placement.Island] = placement;
        }

        for (var index = 0; index < islands.Count; index++) {
            var island = islands[index];
            var low = island.Minimum;
            var high = island.Maximum;

            if (placed.TryGetValue(index, out var placement)) {
                low = placement.Apply(island, island.Minimum);
                high = placement.Apply(island, island.Maximum);

                // A quarter turn can put the "upper" corner below the lower one, and a rectangle with
                // a negative size draws as nothing at all.
                (low, high) = (Vector2.Min(low, high), Vector2.Max(low, high));
            }

            var (stretch, flipped) = Stretch(island);

            views.Add(new(index, low, high, stretch, flipped));
        }

        return views;
    }

    /// <summary>An island's worst triangle stretch, and how many of them are inside out.</summary>
    /// <remarks>
    ///     ⚠ <b>Shape rather than size, and that is what makes the number comparable between
    ///     islands.</b> The measure is the longest side against the area, normalised so an equilateral
    ///     triangle scores one — which is invariant under the uniform scale the packer is allowed to
    ///     apply. A measure that compared UV area against world area would call every island the packer
    ///     shrank a distorted one.
    /// </remarks>
    static (float Stretch, int Flipped) Stretch(in UvIsland island) {
        var worst = 1f;
        var flipped = 0;

        for (var corner = 0; corner + 2 < island.Coordinates.Count; corner += 3) {
            var a = island.Coordinates[corner];
            var b = island.Coordinates[corner + 1];
            var c = island.Coordinates[corner + 2];
            var cross = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

            if (cross < 0f) {
                flipped++;
            }

            var longest = MathF.Max((b - a).Length(), MathF.Max((c - b).Length(), (a - c).Length()));
            var area = MathF.Abs(cross) * 0.5f;

            if (area <= 0f || longest <= 0f) {
                continue;
            }

            // Against the equilateral ideal, where side² = 4√3 · area — so this is one for a perfect
            // triangle and grows with how far from one it is, at whatever size it happens to be.
            worst = MathF.Max(worst, longest * longest / (area * 4f * MathF.Sqrt(3f)));
        }

        return (worst, flipped);
    }

    bool Fail(string message) {
        Messages = [message];
        Changed?.Invoke(this);

        return false;
    }
}
