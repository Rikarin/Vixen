// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Where every position and corner of a reflected mesh came from in the half that made it.</summary>
/// <remarks>
///     ⚠ <b>Built by <see cref="SymmetryPass" /> as it reflects, and never recovered afterwards.</b>
///     The correspondence is exactly what docs/plan/41 § D11 promises and it is free at the moment the
///     mirror is emitted; reconstructing it later means matching positions, which is the tolerance
///     weld the whole pass exists to avoid.
/// </remarks>
sealed class MirrorCorrespondence {
    /// <summary>Which half position each built position came from.</summary>
    public required int[] PositionSource { get; init; }

    /// <summary>Whether each built position is the reflected copy rather than the original.</summary>
    public required bool[] PositionIsMirror { get; init; }

    /// <summary>Whether each built position sits on the plane and is therefore its own mirror.</summary>
    public required bool[] PositionIsSeam { get; init; }

    /// <summary>Which half corner each built corner came from.</summary>
    public required int[] CornerSource { get; init; }

    /// <summary>Whether each built corner belongs to a reflected face.</summary>
    public required bool[] CornerIsMirror { get; init; }
}

/// <summary>§ D11's mirror applied to what § D12 carried, which is the half of it that is not mechanical.</summary>
/// <remarks>
///     <para>
///         <b>Colours and coordinates copy, normals reflect, and skin weights change which bone they
///         name.</b> That last one is the whole reason this file exists.
///         <see cref="SkinInfluence" /> is <c>(int Bone, float Weight)</c> — an index with no name —
///         so nothing here can work out that the mirror of bone 14 is bone 27. The caller says, in
///         <see cref="SourceAttributes.BoneMirror" />, and without it the transfer is refused.
///     </para>
///     <para>
///         ⚠ <b>Attributes are mirrored rather than transferred twice, and the difference is the
///         point of symmetry mode.</b> Running the transfer against the whole source once the mesh is
///         reflected needs no bone map at all — the right half of the source carries right-half bones
///         already — and it produces an <i>asymmetric</i> result, because a sculpt is only
///         approximately symmetric and two independent closest-point queries land in two different
///         places. § D11's promise is that vertex <i>k</i> and its mirror are the same vertex twice;
///         a rig that is the same rig twice is the same promise, and it is the one an animator
///         notices.
///     </para>
///     <para>
///         ⚠ <b>The consequence, stated rather than hidden: asymmetric <i>detail</i> in the source's
///         attributes is discarded.</b> A scar painted on one cheek comes back on both cheeks or
///         neither, because only the kept half was ever read. That is what turning symmetry on asks
///         for, and it is why the setting is not on by default.
///     </para>
///     <para>
///         ⚠ <b>A vertex on the plane is <i>symmetrised</i>, not left alone.</b> It is one vertex
///         standing in both halves, so its weights have to be invariant under the bone mirror or the
///         seam moves differently from the surface either side of it. Averaging a vertex's weights
///         with its own mirror is what makes it invariant — and it is also the one place an influence
///         count can grow, because two four-bone sets average to as many as eight.
///     </para>
/// </remarks>
static class AttributeMirror {
    /// <summary>Why the transfer cannot be mirrored, or null when it can.</summary>
    /// <param name="attributes">What the caller handed in.</param>
    /// <returns>A warning naming what is wrong, or null.</returns>
    /// <remarks>
    ///     ⚠ <b>Every one of these is a caller error and every one is named rather than repaired.</b>
    ///     Clamping an out-of-range entry, or filling a short map with identity, turns a rig that is
    ///     wrong into a rig that is quietly wrong in one limb — which is strictly harder to find than
    ///     a refusal that says which bone.
    /// </remarks>
    public static string? Refusal(SourceAttributes attributes) {
        if (attributes.Weights is not { } binding || binding.Stride <= 0) {
            return null;
        }

        if (attributes.BoneMirror is not { } bones || bones.Count == 0) {
            return "Symmetry was requested for a mesh with skinning weights and no "
                + "SourceAttributes.BoneMirror, so nothing was transferred. A mirrored vertex's weights "
                + "belong to the mirrored bone and a bone index carries no name to work that out from; "
                + "mirroring them onto the bone they already named would drive the right leg from the "
                + "left arm. docs/plan/41 § D11.";
        }

        // ⚠ Two passes, and the order is what makes the message the useful one. A map with an
        // out-of-range entry is also, necessarily, not its own inverse — so a single pass reports
        // whichever fault it happened to reach first, and sends the caller looking at the wrong bone.
        for (var bone = 0; bone < bones.Count; bone++) {
            if ((uint) bones[bone] >= (uint) bones.Count) {
                return $"SourceAttributes.BoneMirror sends bone {bone} to {bones[bone]}, which is not a "
                    + $"bone in a map of {bones.Count}, so nothing was transferred.";
            }
        }

        for (var bone = 0; bone < bones.Count; bone++) {
            var other = bones[bone];

            // A mirror is its own inverse. A map that is not one has no consistent reading — the
            // mirror of the mirror would be a third bone — and it is very cheap to say so here.
            if (bones[other] != bone) {
                return $"SourceAttributes.BoneMirror sends bone {bone} to {other} and bone {other} to "
                    + $"{bones[other]}, so it is not its own inverse and nothing was transferred.";
            }
        }

        for (var slot = 0; slot < binding.Influences.Count; slot++) {
            var influence = binding.Influences[slot];

            if (influence.Weight > 0f && (uint) influence.Bone >= (uint) bones.Count) {
                return $"The source binding gives weight to bone {influence.Bone} and "
                    + $"SourceAttributes.BoneMirror covers {bones.Count} bones, so that bone has no "
                    + "mirror and nothing was transferred.";
            }
        }

        return null;
    }

    /// <summary>Reflects the half's per-corner mesh layers onto the built mesh.</summary>
    /// <param name="half">The mesh the transfer wrote into.</param>
    /// <param name="built">The reflected mesh, whose layers are written.</param>
    /// <param name="map">Where each of its corners came from.</param>
    /// <param name="plane">The mirror plane.</param>
    /// <param name="axis">Which axis it is, or −1 for a general plane.</param>
    /// <remarks>
    ///     ⚠ <b>Without this a symmetric remesh comes back with no normals and no coordinates at
    ///     all</b>, because the reflection builds a fresh <see cref="EditMesh" /> and a fresh one has
    ///     no layers. It looks like a shading bug and it is a bookkeeping one.
    /// </remarks>
    public static void Layers(EditMesh half, EditMesh built, MirrorCorrespondence map, Plane plane, int axis) {
        if (half.Normals.Length == half.CornerCount && half.CornerCount > 0) {
            var normals = new Vector3[built.CornerCount];

            for (var corner = 0; corner < normals.Length; corner++) {
                var from = half.Normals[map.CornerSource[corner]];

                // ⚠ A direction, so the plane's distance term takes no part. Reflecting a normal as
                // if it were a point translates it by twice the plane's offset, which on a plane
                // through the origin is invisible and on any other is a lighting bug that only
                // appears when somebody moves their model off zero.
                normals[corner] = map.CornerIsMirror[corner] ? SymmetryPass.MirrorDirection(from, plane, axis) : from;
            }

            built.SetNormals(normals);
        }

        if (half.TexCoords.Length == half.CornerCount && half.CornerCount > 0) {
            var coordinates = new Vector2[built.CornerCount];

            for (var corner = 0; corner < coordinates.Length; corner++) {
                // Copied rather than reflected: the mirrored half lands on the same island as the
                // half that made it, which is the stacked layout a symmetric character wants and is
                // what doc 42 § UvStacking detects after the fact on meshes that did not arrive
                // that way.
                coordinates[corner] = half.TexCoords[map.CornerSource[corner]];
            }

            built.SetTexCoords(coordinates);
        }
    }

    /// <summary>Reflects the channels the mesh had no room for.</summary>
    /// <param name="half">What the transfer produced on the kept half.</param>
    /// <param name="map">Where each built position and corner came from.</param>
    /// <param name="bones">The bone mirror map, which <see cref="Refusal" /> has already accepted.</param>
    /// <returns>The same channels over the whole mesh.</returns>
    public static TransferResult Reflect(TransferResult half, MirrorCorrespondence map, IReadOnlyList<int>? bones) {
        var warnings = new List<string>(half.Warnings);
        var colors = half.Colors.Count == 0 ? [] : new Vector4[map.CornerSource.Length];

        for (var corner = 0; corner < colors.Length; corner++) {
            colors[corner] = half.Colors[map.CornerSource[corner]];
        }

        if (half.Weights is not { } binding || binding.Stride <= 0 || bones is null) {
            return new(colors, half.Weights is null ? null : Renumber(half.Weights, map), half.UnboundVertices, half.SmoothingGroups, warnings);
        }

        var stride = binding.Stride;
        var influences = new SkinInfluence[map.PositionSource.Length * stride];
        var gathered = new List<(int Bone, float Weight)>(stride * 2);
        var unbound = 0;
        var crowded = 0;

        for (var vertex = 0; vertex < map.PositionSource.Length; vertex++) {
            var from = map.PositionSource[vertex];

            gathered.Clear();

            if (map.PositionIsSeam[vertex]) {
                Gather(gathered, binding, from, bones, 0.5f, mirrored: false);
                Gather(gathered, binding, from, bones, 0.5f, mirrored: true);
            } else {
                Gather(gathered, binding, from, bones, 1f, map.PositionIsMirror[vertex]);
            }

            // ⚠ Descending weight, ties on the bone index — the same order the transfer itself sorts
            // in, and for the same two reasons: which influence is worth keeping, and why two runs
            // keep the same one. § D14.
            gathered.Sort(static (one, two) => two.Weight != one.Weight
                ? two.Weight.CompareTo(one.Weight)
                : one.Bone.CompareTo(two.Bone)
            );

            var kept = Math.Min(gathered.Count, stride);
            var whole = 0f;
            var total = 0f;

            for (var slot = 0; slot < gathered.Count; slot++) {
                whole += gathered[slot].Weight;

                if (slot < kept) {
                    total += gathered[slot].Weight;
                }
            }

            if (total <= 0f) {
                unbound++;

                continue;
            }

            if (gathered.Count > stride) {
                crowded++;
            }

            // ⚠ Scaled by what was dropped rather than normalised to one, which is what makes the
            // ordinary mirror bit-exact. Nothing is dropped there, so the factor is exactly 1f and a
            // multiply by 1f returns every weight unchanged — a rigged mesh round-trips its binding
            // through a reflection rather than merely landing near it. Normalising to one instead
            // would rewrite the last bit of every weight in the model and quietly impose a
            // convention on a caller whose binding did not sum to one.
            var scale = whole / total;

            for (var slot = 0; slot < kept; slot++) {
                influences[(vertex * stride) + slot] = new(gathered[slot].Bone, gathered[slot].Weight * scale);
            }
        }

        if (crowded > 0) {
            warnings.Add(
                $"{crowded} vertices on the symmetry plane inherited more than {stride} influences once "
                + "their weights were symmetrised, and the smallest were dropped and the rest rescaled."
            );
        }

        return new(colors, new() { Influences = influences, Stride = stride }, unbound, half.SmoothingGroups, warnings);
    }

    /// <summary>One vertex's influences added to a tally, optionally through the bone mirror.</summary>
    static void Gather(
        List<(int Bone, float Weight)> gathered,
        SkinBinding binding,
        int vertex,
        IReadOnlyList<int> bones,
        float share,
        bool mirrored
    ) {
        for (var slot = 0; slot < binding.Stride; slot++) {
            var influence = binding.Influences[(vertex * binding.Stride) + slot];

            if (influence.Weight <= 0f) {
                continue;
            }

            var bone = mirrored ? bones[influence.Bone] : influence.Bone;

            Accumulate(gathered, bone, influence.Weight * share);
        }
    }

    /// <summary>Adds a weight to a bone's running total, in a list rather than a dictionary.</summary>
    /// <remarks>
    ///     ⚠ A dictionary would iterate in an order that is a function of the runtime rather than of
    ///     the mesh, which § D14 makes a gate. There are at most twice the stride of them.
    /// </remarks>
    static void Accumulate(List<(int Bone, float Weight)> gathered, int bone, float weight) {
        for (var at = 0; at < gathered.Count; at++) {
            if (gathered[at].Bone == bone) {
                gathered[at] = (bone, gathered[at].Weight + weight);

                return;
            }
        }

        gathered.Add((bone, weight));
    }

    /// <summary>A binding spread over the built positions with no bone changed at all.</summary>
    /// <remarks>
    ///     The stride-zero and no-map cases, which <see cref="Refusal" /> has already decided are not
    ///     worth refusing over: there is nothing to mirror, so the mirrored half simply repeats what
    ///     the kept half held.
    /// </remarks>
    static SkinBinding Renumber(SkinBinding binding, MirrorCorrespondence map) {
        var stride = Math.Max(binding.Stride, 1);
        var influences = new SkinInfluence[map.PositionSource.Length * stride];

        for (var vertex = 0; vertex < map.PositionSource.Length; vertex++) {
            var from = map.PositionSource[vertex];

            for (var slot = 0; slot < binding.Stride; slot++) {
                influences[(vertex * stride) + slot] = binding.Influences[(from * binding.Stride) + slot];
            }
        }

        return new() { Influences = influences, Stride = stride };
    }
}
