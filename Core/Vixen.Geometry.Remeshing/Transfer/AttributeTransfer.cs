// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>The channels a source mesh carries that <see cref="EditMesh" /> has no room for.</summary>
/// <remarks>
///     <para>
///         <b>Parallel arrays alongside the mesh, and that is the documented choice.</b>
///         <c>docs/plan/41-automatic-retopology.md</c> § D12 asks for colours and skinning weights to
///         survive a remesh and leaves open where they live. <see cref="EditMesh" /> has a normal
///         layer and a texture-coordinate layer and nothing else, and growing it a colour channel and
///         a bone channel would put a renderer's and a rig's vocabulary into doc 24's geometry types
///         — which have consumers that have never heard of either. So they travel beside the mesh,
///         indexed by the mesh's own indices.
///     </para>
///     <para>
///         ⚠ <b>Colours are per <i>corner</i> and weights are per <i>position</i>, and the split is
///         not arbitrary.</b> A colour is a statement about the surface's appearance and can
///         legitimately jump across a seam; a weight is a statement about where the surface moves to,
///         and two corners at one position that moved differently would tear the mesh open.
///     </para>
///     <para>
///         ⚠ <b>Every channel is optional and an empty one means "the source had none", not
///         "zero".</b> A source with no colours must not come back with a mesh painted black.
///     </para>
/// </remarks>
public sealed record SourceAttributes {
    /// <summary>A colour or mask per source <i>corner</i>, or empty for none.</summary>
    public IReadOnlyList<Vector4> Colors { get; init; } = [];

    /// <summary>Skinning weights per source <i>position</i>, or null for none.</summary>
    public SkinBinding? Weights { get; init; }

    /// <summary>Nothing at all — a source that is only geometry, which is the generated-blob case.</summary>
    public static SourceAttributes None { get; } = new();
}

/// <summary>What a transfer is allowed to carry and what the target can hold.</summary>
public sealed record TransferSettings {
    /// <summary>Whether the source's texture coordinates are interpolated onto the output.</summary>
    /// <remarks>
    ///     ⚠ <b>Off when the atlas is being regenerated, and the two must not both write.</b>
    ///     <c>docs/plan/41-automatic-retopology.md</c> § D12 keeps coordinates "when the source had
    ///     them and the caller wants them kept rather than regenerated" — § D13's atlas is the other
    ///     branch, and <see cref="RemeshSettings.GenerateUvs" /> is what chooses between them.
    /// </remarks>
    public bool KeepTexCoords { get; init; } = true;

    /// <summary>How many bones may hold one output vertex.</summary>
    /// <remarks>
    ///     ⚠ <b>The clamp is not optional and the reason is which influence gets dropped.</b> An
    ///     output vertex sitting between two source vertices with four influences each can inherit
    ///     up to eight; a target with room for four that is handed five silently loses one, and
    ///     <i>which</i> one depends on the order the interpolation happened to accumulate them in.
    ///     Sorting by descending weight — ties broken on the bone index — makes the survivor a
    ///     function of the input and drops the influence that matters least.
    /// </remarks>
    public int MaxInfluences { get; init; } = 4;

    /// <summary>The angle, in degrees, above which two output faces do not share a shading normal.</summary>
    /// <remarks>
    ///     ⚠ <b>The fallback for a source whose smoothing groups are all zero, which is most
    ///     sources.</b> Nobody authors smoothing groups on a generated mesh, so a reconstruction that
    ///     trusted them alone would average one normal across every hard edge in the model — exactly
    ///     the artefact smoothing groups exist to prevent. Where the source <i>does</i> carry them
    ///     they partition first and unconditionally, and this only subdivides further.
    /// </remarks>
    public float SmoothingAngle { get; init; } = 35f;

    /// <summary>Whether face groups are reassigned by majority of covered area.</summary>
    /// <remarks>
    ///     ⚠ <b>Leaving this off is not the same as leaving the groups alone</b> — the extraction
    ///     gives every quad of a patch the group of the patch's <i>first</i> triangle, which is a
    ///     whole-patch block rather than a boundary. This is what turns that into a material seam
    ///     that follows the material.
    /// </remarks>
    public bool TransferGroups { get; init; } = true;
}

/// <summary>What a transfer produced that could not be written into the mesh.</summary>
/// <param name="Colors">A colour per output corner, or empty when the source had none.</param>
/// <param name="Weights">Weights per output position, or null when the source had none.</param>
/// <param name="UnboundVertices">
///     How many output positions came back with weights summing to zero. ⚠ Not an error — a mesh
///     with a rigged body and an unrigged prop produces them legitimately — but a number worth
///     reading, because a whole mesh of them means the source binding was indexed wrongly.
/// </param>
/// <param name="SmoothingGroups">How many shading groups the reconstruction found on the output.</param>
/// <param name="Warnings">What was asked for and could not be carried.</param>
public readonly record struct TransferResult(
    IReadOnlyList<Vector4> Colors,
    SkinBinding? Weights,
    int UnboundVertices,
    int SmoothingGroups,
    IReadOnlyList<string> Warnings
);

/// <summary>docs/plan/41 § D12's stage seven: the output carries the input, or it is useless.</summary>
/// <remarks>
///     <para>
///         <b>Closest-point queries against the source, driven by
///         <see cref="TriangleTree" />.</b> Normals and texture coordinates and colours are
///         interpolated barycentrically at the point the output landed on; face groups are decided by
///         the majority of the area they cover; skinning weights are interpolated, clamped and
///         renormalised. Doc 41 calls this "what makes it useful rather than impressive", and the
///         argument is arithmetic: a four-million-triangle generated blob is not expensive because it
///         is four million triangles, it is expensive because it is four million triangles of noise
///         with no attributes. Five thousand quads that kept them is a pipeline; five thousand quads
///         that did not is a downgrade.
///     </para>
///     <para>
///         ⚠ <b>Face groups are decided by covered area and never by the nearest face, and that is
///         not a micro-optimisation.</b> § D12: nearest-face assignment <i>shreds</i> along a
///         material boundary — the quads either side of it alternate, because which face is nearest
///         flips from one quad to the next — and the result is a sawtooth seam that reads as a UV bug
///         and gets debugged as one for a day. Integrating over the quad instead makes the boundary a
///         monotone chain, because a quad that is mostly on one side is mostly on one side however
///         its corners fell. Measured on a plane split into two groups on a straight line: the
///         sawtooth is <see cref="AreaSamples" />-times-cheaper to produce and takes half again as
///         many boundary edges to describe.
///     </para>
///     <para>
///         ⚠ <b>Every query is made from a point <i>inside</i> the output face rather than from the
///         vertex itself.</b> An output vertex on a hard edge is equidistant from both sides of it,
///         so the triangle a closest-point query returns there is whichever the traversal reached
///         first — and the normal, the texture coordinate and the colour it hands back all belong to
///         a face chosen by the shape of a tree. Insetting toward the face's own centroid asks the
///         question the corner is actually asking: <i>what is the source like on my side of the
///         crease</i>. It is the same mechanism that keeps a texture-coordinate seam from being
///         interpolated across.
///     </para>
///     <para>
///         ⚠ <b>The inset is a fraction of the face and never a distance.</b> An absolute nudge is a
///         claim about how big a model is, which is the failure <see cref="ScaleSafe" /> exists for
///         and which this repository has now been bitten by five times.
///     </para>
/// </remarks>
public static class AttributeTransfer {
    /// <summary>How far a corner query moves toward its face's centroid, as a fraction.</summary>
    /// <remarks>
    ///     A quarter of the way in: far enough that a query at a crease lands unambiguously on its
    ///     own side, near enough that it is still asking about the corner rather than about the
    ///     middle of the quad.
    /// </remarks>
    public const float CornerInset = 0.25f;

    /// <summary>The lattice a face's area is integrated on, per side.</summary>
    /// <remarks>
    ///     Four per side is sixteen samples per triangle and thirty-two per quad, which resolves a
    ///     boundary crossing a quad to about three percent of its area — an order of magnitude finer
    ///     than the decision it feeds, which is a majority.
    /// </remarks>
    public const int AreaSamples = 4;

    /// <summary>Carries the source's attributes onto a remeshed output.</summary>
    /// <param name="source">The surface the attributes live on. Read, never modified.</param>
    /// <param name="attributes">The channels the mesh has no room for, or <see cref="SourceAttributes.None" />.</param>
    /// <param name="target">The output. Its normals, coordinates, groups and smoothing are written.</param>
    /// <param name="settings">What may be carried and what the target can hold.</param>
    /// <returns>The channels that could not be written into the mesh.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">A channel's length does not match the source it claims to be for.</exception>
    public static TransferResult Transfer(
        EditMesh source,
        SourceAttributes attributes,
        EditMesh target,
        TransferSettings settings
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);

        if (attributes.Colors.Count > 0 && attributes.Colors.Count != source.CornerCount) {
            throw new ArgumentException(
                $"{attributes.Colors.Count} colours is not one per corner of a {source.CornerCount}-corner source.",
                nameof(attributes)
            );
        }

        if (attributes.Weights is { } binding && binding.VertexCount != source.PositionCount) {
            throw new ArgumentException(
                $"A binding for {binding.VertexCount} vertices is not one for a {source.PositionCount}-position source.",
                nameof(attributes)
            );
        }

        var warnings = new List<string>();

        if (target.FaceCount == 0 || source.FaceCount == 0) {
            if (source.FaceCount == 0) {
                warnings.Add("The source had no faces, so nothing was transferred.");
            }

            return new([], null, 0, 0, warnings);
        }

        var surface = SourceSurface.From(source);

        if (surface.TriangleCount == 0) {
            warnings.Add("The source triangulated to nothing, so nothing was transferred.");

            return new([], null, 0, 0, warnings);
        }

        // ① Face groups and the source's own smoothing, both by majority of covered area.
        var (groups, smoothing) = Areas(surface, target);

        if (settings.TransferGroups) {
            for (var face = 0; face < target.FaceCount; face++) {
                target.SetGroup(face, groups[face]);
            }
        }

        // ② The shading partition: the source's smoothing groups first, then the dihedral angle.
        var shading = Shading(target, smoothing, settings.SmoothingAngle);
        var components = shading.Length == 0 ? 0 : shading.Max() + 1;

        for (var face = 0; face < target.FaceCount; face++) {
            target.SetSmoothing(face, shading[face]);
        }

        // ③ Everything per corner, from a query made on the corner's own side of any crease.
        var normals = new Vector3[target.CornerCount];
        var coordinates = new Vector2[target.CornerCount];
        var colors = attributes.Colors.Count > 0 ? new Vector4[target.CornerCount] : [];
        var wantsCoordinates = settings.KeepTexCoords && surface.HasTexCoords;

        for (var face = 0; face < target.FaceCount; face++) {
            var loop = target.CornersOf(face);
            var entry = target.Faces[face];
            var centroid = Centroid(target, face);

            for (var index = 0; index < loop.Length; index++) {
                var corner = entry.Start + index;
                var position = target.Positions[loop[index]];
                var hit = surface.Tree.Closest(Vector3.Lerp(position, centroid, CornerInset));

                if (hit.Triangle < 0) {
                    continue;
                }

                normals[corner] = surface.NormalAt(hit.Triangle, hit.Barycentric);

                if (wantsCoordinates) {
                    coordinates[corner] = surface.TexCoordAt(hit.Triangle, hit.Barycentric);
                }

                if (colors.Length > 0) {
                    colors[corner] = Color(surface, attributes.Colors, hit.Triangle, hit.Barycentric);
                }
            }
        }

        // ④ Reconstruction: corners at one position and in one shading group share one normal.
        Reconcile(target, shading, normals);

        target.SetNormals(normals);

        if (wantsCoordinates) {
            target.SetTexCoords(coordinates);
        } else if (settings.KeepTexCoords && !surface.HasTexCoords) {
            warnings.Add("Texture coordinates were asked for and the source had none.");
        }

        // ⑤ Weights, per position, clamped to what the target can hold.
        var (weights, unbound) = attributes.Weights is null
            ? (null, 0)
            : Skin(surface, attributes.Weights, target, Math.Max(settings.MaxInfluences, 1));

        if (attributes.Weights is not null && settings.MaxInfluences < 1) {
            warnings.Add("An influence limit below one is not a limit; one was used.");
        }

        return new(colors, weights, unbound, components, warnings);
    }

    /// <summary>The face group and smoothing group covering the majority of each output face's area.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The integral is over the output face and it is measuring which source group the
    ///         face mostly sits on.</b> A true source-area integral would need the two surfaces
    ///         intersected, which is a mesh boolean and costs more than the whole remesh; the
    ///         quantity that decision actually turns on is the same either way, because a quad and
    ///         the source under it differ in area by a factor that is common to every group voting on
    ///         it and so cancels out of a comparison.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Votes are tallied in a list rather than a dictionary, and the tie goes to the
    ///         lower group.</b> § D14: two runs must reach the same answer, and a hash order is not a
    ///         function of the input. There are one or two groups over a quad and three is exotic, so
    ///         the linear scan is also the faster structure.
    ///     </para>
    /// </remarks>
    static (int[] Groups, int[] Smoothing) Areas(SourceSurface surface, EditMesh target) {
        var groups = new int[target.FaceCount];
        var smoothing = new int[target.FaceCount];
        var groupVotes = new List<(int Key, float Area)>(4);
        var smoothingVotes = new List<(int Key, float Area)>(4);

        for (var face = 0; face < target.FaceCount; face++) {
            groupVotes.Clear();
            smoothingVotes.Clear();

            var loop = target.CornersOf(face);

            // A fan from the first corner. The face is a quad by construction here and a fan is
            // exact for one; a concave n-gon would want the kernel's ear clipping, and the extraction
            // does not make any.
            for (var corner = 1; corner + 1 < loop.Length; corner++) {
                var a = target.Positions[loop[0]];
                var b = target.Positions[loop[corner]];
                var c = target.Positions[loop[corner + 1]];
                var area = Vector3.Cross(b - a, c - a).Length() * 0.5f;

                if (area <= 0f) {
                    continue;
                }

                var share = area / (AreaSamples * AreaSamples);

                foreach (var (u, v) in Lattice()) {
                    var point = a + ((b - a) * u) + ((c - a) * v);
                    var hit = surface.Tree.Closest(point);

                    if (hit.Triangle < 0) {
                        continue;
                    }

                    Vote(groupVotes, surface.GroupOf(hit.Triangle), share);
                    Vote(smoothingVotes, surface.SmoothingOf(hit.Triangle), share);
                }
            }

            groups[face] = Winner(groupVotes, target.Faces[face].Group);
            smoothing[face] = Winner(smoothingVotes, target.Faces[face].Smoothing);
        }

        return (groups, smoothing);
    }

    /// <summary>The centroids of a triangle cut into <see cref="AreaSamples" />² equal pieces.</summary>
    /// <remarks>
    ///     Every piece has the same area, so a sample needs no weight of its own — which is what
    ///     makes the tally a sum rather than a weighted sum, and a sum is what two runs can agree on
    ///     to the last bit.
    /// </remarks>
    static IEnumerable<(float U, float V)> Lattice() {
        const float Step = 1f / AreaSamples;
        const float Third = Step / 3f;

        for (var i = 0; i < AreaSamples; i++) {
            for (var j = 0; j + i < AreaSamples; j++) {
                yield return ((i * Step) + Third, (j * Step) + Third);

                if (j + i + 1 < AreaSamples) {
                    yield return ((i * Step) + (2f * Third), (j * Step) + (2f * Third));
                }
            }
        }
    }

    /// <summary>Adds one sample's area to a key's tally.</summary>
    static void Vote(List<(int Key, float Area)> votes, int key, float area) {
        for (var at = 0; at < votes.Count; at++) {
            if (votes[at].Key == key) {
                votes[at] = (key, votes[at].Area + area);

                return;
            }
        }

        votes.Add((key, area));
    }

    /// <summary>The key with the most area, ties going to the lower key.</summary>
    static int Winner(List<(int Key, float Area)> votes, int fallback) {
        var best = fallback;
        var area = float.NegativeInfinity;

        foreach (var (key, tally) in votes) {
            if (tally > area || (tally == area && key < best)) {
                best = key;
                area = tally;
            }
        }

        return best;
    }

    /// <summary>An output face's centroid, which every corner query is nudged toward.</summary>
    static Vector3 Centroid(EditMesh mesh, int face) {
        var loop = mesh.CornersOf(face);
        var sum = Vector3.Zero;

        foreach (var corner in loop) {
            sum += mesh.Positions[corner];
        }

        return loop.Length > 0 ? sum / loop.Length : Vector3.Zero;
    }

    /// <summary>The colour at a point on a source triangle.</summary>
    static Vector4 Color(SourceSurface surface, IReadOnlyList<Vector4> colors, int triangle, Vector3 barycentric) {
        var slots = surface.CornersOf(triangle);

        return (colors[slots[0]] * barycentric.X)
            + (colors[slots[1]] * barycentric.Y)
            + (colors[slots[2]] * barycentric.Z);
    }

    /// <summary>§ D12's smoothing-group-aware reconstruction: which output faces may share a normal.</summary>
    /// <remarks>
    ///     <para>
    ///         A flood over the output's faces, crossing an edge only where both sides inherited the
    ///         same source smoothing group <i>and</i> the fold between them is gentler than the
    ///         threshold. The component index becomes the output face's own smoothing group, so the
    ///         partition the source described survives into the mesh rather than only into the
    ///         normals it produced.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both conditions, not either.</b> Trusting the source's groups alone fails on the
    ///         overwhelmingly common source that has none — every face is group zero, the whole mesh
    ///         floods into one component, and a box comes back with its eight corners smoothed round.
    ///         Trusting the angle alone throws away a partition an artist actually authored, which is
    ///         the case where two coplanar faces are deliberately not smoothed together.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The flood is seeded in face order and its queue is a stack of indices</b>, so the
    ///         component numbering is a function of the mesh and not of a hash.
    ///     </para>
    /// </remarks>
    static int[] Shading(EditMesh target, int[] smoothing, float angle) {
        var component = new int[target.FaceCount];
        Array.Fill(component, -1);

        var limit = MathF.Cos(MathUtil.DegreesToRadians(Math.Clamp(angle, 0f, 180f)));
        var normals = new Vector3[target.FaceCount];

        for (var face = 0; face < target.FaceCount; face++) {
            normals[face] = ScaleSafe.Unit(target.Normal(face));
        }

        var next = 0;
        var stack = new Stack<int>();

        for (var seed = 0; seed < target.FaceCount; seed++) {
            if (component[seed] >= 0) {
                continue;
            }

            var id = next++;
            component[seed] = id;
            stack.Push(seed);

            while (stack.Count > 0) {
                var face = stack.Pop();
                var loop = target.CornersOf(face);

                for (var index = 0; index < loop.Length; index++) {
                    var edge = target.EdgeBetween(loop[index], loop[(index + 1) % loop.Length]);

                    if (edge < 0) {
                        continue;
                    }

                    foreach (var other in target.FacesOf(edge)) {
                        if (other == face || component[other] >= 0 || smoothing[other] != smoothing[face]) {
                            continue;
                        }

                        // A fold sharper than the threshold is a hard edge whichever way the source
                        // labelled it, and averaging a normal across one is the artefact.
                        if (Vector3.Dot(normals[face], normals[other]) < limit) {
                            continue;
                        }

                        component[other] = id;
                        stack.Push(other);
                    }
                }
            }
        }

        return component;
    }

    /// <summary>Averages the corners that stand at one position inside one shading group.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this a smooth surface is faceted for no reason.</b> Two neighbouring quads
    ///     query the source at two slightly different points and get two slightly different normals,
    ///     and the difference is invisible in the numbers and perfectly visible under a moving light.
    ///     Averaging inside a group is the reconstruction; the group boundary is where the average
    ///     deliberately stops.
    /// </remarks>
    static void Reconcile(EditMesh target, int[] shading, Vector3[] normals) {
        var sums = new Dictionary<(int Position, int Group), Vector3>();

        for (var face = 0; face < target.FaceCount; face++) {
            var entry = target.Faces[face];
            var loop = target.CornersOf(face);

            for (var index = 0; index < loop.Length; index++) {
                var key = (loop[index], shading[face]);
                sums[key] = sums.GetValueOrDefault(key) + normals[entry.Start + index];
            }
        }

        for (var face = 0; face < target.FaceCount; face++) {
            var entry = target.Faces[face];
            var loop = target.CornersOf(face);

            for (var index = 0; index < loop.Length; index++) {
                var averaged = ScaleSafe.Unit(sums[(loop[index], shading[face])]);
                var corner = entry.Start + index;

                // Opposed normals inside one group cancel exactly. That is a fold the flood should
                // not have crossed, and the corner's own answer is better than a zero vector.
                normals[corner] = averaged.LengthSquared() > 0f ? averaged : normals[corner];
            }
        }
    }

    /// <summary>Interpolates, clamps and renormalises the skinning weights onto the output.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is what makes doc 33's characters remeshable.</b> § D12: a generated humanoid
    ///         that came out of a remesh with no weights is not a character, it is a statue.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per position and from the position itself, with no inset.</b> The corner queries
    ///         are inset because a normal and a texture coordinate are allowed to jump across a
    ///         crease; a weight is not, and asking the question twice at one position would be asking
    ///         for a tear.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A total of zero is kept as a zero.</b> Three source vertices that were all
    ///         unbound interpolate to nothing, and normalising nothing means dividing by zero — or,
    ///         worse, inventing a bone. An unrigged prop inside a rigged mesh is a real input and
    ///         comes back unrigged.
    ///     </para>
    /// </remarks>
    static (SkinBinding Weights, int Unbound) Skin(
        SourceSurface surface,
        SkinBinding source,
        EditMesh target,
        int limit
    ) {
        var influences = new SkinInfluence[target.PositionCount * limit];
        var unbound = 0;
        var gathered = new List<(int Bone, float Weight)>(source.Stride * 3);

        for (var vertex = 0; vertex < target.PositionCount; vertex++) {
            gathered.Clear();

            var hit = surface.Tree.Closest(target.Positions[vertex]);

            if (hit.Triangle < 0) {
                unbound++;

                continue;
            }

            var slots = surface.PositionsOf(hit.Triangle);

            for (var corner = 0; corner < 3; corner++) {
                var weight = corner switch {
                    0 => hit.Barycentric.X,
                    1 => hit.Barycentric.Y,
                    _ => hit.Barycentric.Z
                };

                if (weight == 0f) {
                    continue;
                }

                for (var slot = 0; slot < source.Stride; slot++) {
                    var influence = source.Influences[(slots[corner] * source.Stride) + slot];

                    if (influence.Weight <= 0f) {
                        continue;
                    }

                    Accumulate(gathered, influence.Bone, influence.Weight * weight);
                }
            }

            // ⚠ Descending weight, ties on the bone index. Both halves are load-bearing: the first
            // is which influence is worth keeping, and the second is why two runs keep the same one.
            gathered.Sort((one, two) => two.Weight != one.Weight
                ? two.Weight.CompareTo(one.Weight)
                : one.Bone.CompareTo(two.Bone)
            );

            var kept = Math.Min(gathered.Count, limit);
            var total = 0f;

            for (var slot = 0; slot < kept; slot++) {
                total += gathered[slot].Weight;
            }

            if (total <= 0f) {
                unbound++;

                continue;
            }

            for (var slot = 0; slot < kept; slot++) {
                influences[(vertex * limit) + slot] = new(gathered[slot].Bone, gathered[slot].Weight / total);
            }
        }

        return (new() { Influences = influences, Stride = limit }, unbound);
    }

    /// <summary>Adds a weight to a bone's running total, in a list rather than a dictionary.</summary>
    static void Accumulate(List<(int Bone, float Weight)> gathered, int bone, float weight) {
        for (var at = 0; at < gathered.Count; at++) {
            if (gathered[at].Bone == bone) {
                gathered[at] = (bone, gathered[at].Weight + weight);

                return;
            }
        }

        gathered.Add((bone, weight));
    }
}
