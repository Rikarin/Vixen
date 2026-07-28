// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering.Lighting;

/// <summary>
///     A local capture of the surroundings, standing in for the sky where it applies.
/// </summary>
/// <remarks>
///     <para>
///         An environment map answers "what is around this scene"; a probe answers "what is around
///         <em>here</em>", which is the difference between a room whose mirrors reflect the room and
///         one whose mirrors reflect the sky through the walls.
///     </para>
///     <para>
///         Prefiltered exactly as the environment is — same chain, same roughness-to-level mapping —
///         because a surface blending between the two at a probe's edge would otherwise change
///         apparent roughness as it crossed.
///     </para>
/// </remarks>
public sealed class ReflectionProbe {
    /// <summary>Where the cube was captured from.</summary>
    /// <remarks>
    ///     Not necessarily the middle of <see cref="Bounds" />: a probe for a room is usually captured
    ///     at eye height rather than at the centroid of the volume. The parallax correction re-aims
    ///     from this point, so it has to be where the capture actually happened.
    /// </remarks>
    public Vector3 CapturePosition { get; set; }

    /// <summary>The volume the probe describes, in world space.</summary>
    public BoundingBox Bounds { get; set; }

    /// <summary>The radius of a spherical probe, or zero for the box.</summary>
    /// <remarks>
    ///     A corridor is a box and a clearing is a sphere, and the choice is the room's rather than a
    ///     preference: a box's corners place reflections on geometry that is not there.
    /// </remarks>
    public float Radius { get; set; }

    /// <summary>Over what distance the probe fades out at its boundary, in metres.</summary>
    /// <remarks>
    ///     Zero makes a probe pop as a surface crosses its edge, which is the artefact probes are
    ///     otherwise famous for. The fade is measured inward from the boundary, so a probe never
    ///     reaches beyond the volume it describes.
    /// </remarks>
    public float BlendDistance { get; set; } = 1f;

    /// <summary>Which probe wins where two overlap. Higher takes precedence.</summary>
    /// <remarks>
    ///     Needed because probes nest: a room inside a building inside a district. Without it the
    ///     answer would depend on the order they were registered in, which is not a thing an author
    ///     can see.
    /// </remarks>
    public int Priority { get; set; }

    /// <summary>The prefiltered radiance.</summary>
    public TextureViewHandle Prefiltered { get; set; }

    /// <summary>How it is sampled.</summary>
    public SamplerHandle Sampler { get; set; }

    /// <summary>How many levels the prefiltered chain has.</summary>
    public int MipCount { get; set; } = 1;

    /// <summary>Whether a point is inside the probe's volume at all.</summary>
    public bool Contains(Vector3 position) =>
        Radius > 0f
            ? Vector3.DistanceSquared(position, CapturePosition) <= Radius * Radius
            : Bounds.Contains(position);

    /// <summary>
    ///     How much of this probe a point takes, from one at its centre to zero outside it.
    /// </summary>
    /// <remarks>
    ///     Measured from the boundary inward rather than from the centre outward, so a probe covering
    ///     a long corridor is at full strength along all of it rather than only in the middle. The
    ///     falloff is linear in distance, which is what makes two overlapping probes sum to something
    ///     sensible without either knowing about the other.
    /// </remarks>
    public float WeightAt(Vector3 position) {
        var inside = Radius > 0f ? Radius - Vector3.Distance(position, CapturePosition) : DistanceInsideBox(position);

        if (inside <= 0f) {
            return 0f;
        }

        return BlendDistance <= 0f ? 1f : Math.Clamp(inside / BlendDistance, 0f, 1f);
    }

    /// <summary>Writes this probe into the parameters a pass reads.</summary>
    /// <param name="parameters">Where the values go.</param>
    /// <param name="weight">How much of it shows, from <see cref="WeightAt" />.</param>
    /// <param name="shaderName">The pass whose keys to write.</param>
    public void Apply(ParameterCollection parameters, float weight, string shaderName = "ForwardPlus") {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(shaderName);

        parameters.Set(ParameterKeys.New<float>($"{shaderName}.probeWeight"), Math.Clamp(weight, 0f, 1f));
        parameters.Set(ParameterKeys.New<float>($"{shaderName}.probeMipCount"), MipCount);
        parameters.Set(ParameterKeys.New<float>($"{shaderName}.probeRadius"), Radius);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.probeBox.minimum"), Bounds.Minimum);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.probeBox.maximum"), Bounds.Maximum);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.probeBox.center"), CapturePosition);
    }

    /// <summary>How far inside the box a point is, or a negative number when it is outside.</summary>
    float DistanceInsideBox(Vector3 position) {
        var minimum = position - Bounds.Minimum;
        var maximum = Bounds.Maximum - position;

        return Math.Min(
            Math.Min(Math.Min(minimum.X, minimum.Y), minimum.Z),
            Math.Min(Math.Min(maximum.X, maximum.Y), maximum.Z)
        );
    }
}

/// <summary>
///     Which probe lights a point, and how much of it.
/// </summary>
/// <remarks>
///     <para>
///         Kept apart from any render feature on purpose: choosing a probe is a question about
///         positions and volumes, and everything hard about it — nesting, ties, fading at the edges —
///         is decidable without a device, a frame or a descriptor set.
///     </para>
///     <para>
///         ⚠ <strong>What is not here is the per-object binding</strong>, and what it needs is not
///         what this used to say. A descriptor set per probe bound per draw is the shape to avoid,
///         not the shape to build: it is a set per object in all but name, which is the cost the
///         four-set convention exists to refuse. The right shape is an <em>array</em> of probe cubes
///         bound once and an index in the per-object block — which is a block that already exists,
///         already written per object, already bound with a dynamic offset. A probe then costs an
///         <c>int</c>, and nothing extra is bound anywhere.
///     </para>
///     <para>
///         That was blocked on the compiler rather than on the renderer, and silently: Raven put an
///         array of textures <em>inside the uniform block</em>, which no backend can express and both
///         emitted anyway. Fixed — see <c>ArrayTypeSymbol.ResourceKind</c> — so
///         <c>var probes: TextureCube[N]</c> is now one binding with a count. What remains is the
///         work in <c>ForwardPlus.rvn</c> and a feature to write the index.
///     </para>
///     <para>
///         Until then a host applies the probe a group of objects shares — a room, a corridor — which
///         is how probes are authored anyway.
///     </para>
/// </remarks>
public sealed class ReflectionProbeSelector {
    readonly List<ReflectionProbe> probes = [];

    /// <summary>The probes this selector chooses between.</summary>
    public IList<ReflectionProbe> Probes => probes;

    /// <summary>
    ///     The probe that applies at a point, and how much of it shows.
    /// </summary>
    /// <returns>The probe and its weight, or null when the point is inside none of them.</returns>
    /// <remarks>
    ///     Highest priority first, then the strongest weight, then the smallest volume: a probe for a
    ///     cupboard inside a probe for a room should win inside the cupboard, and it is smaller. Ties
    ///     resolved by something the author can see rather than by registration order, which they
    ///     cannot.
    /// </remarks>
    public (ReflectionProbe Probe, float Weight)? Select(Vector3 position) {
        ReflectionProbe? best = null;
        var bestWeight = 0f;

        foreach (var probe in probes) {
            var weight = probe.WeightAt(position);

            if (weight <= 0f) {
                continue;
            }

            if (best is null || Beats(probe, weight, best, bestWeight)) {
                best = probe;
                bestWeight = weight;
            }
        }

        return best is null ? null : (best, bestWeight);
    }

    static bool Beats(ReflectionProbe candidate, float weight, ReflectionProbe incumbent, float incumbentWeight) {
        if (candidate.Priority != incumbent.Priority) {
            return candidate.Priority > incumbent.Priority;
        }

        if (!MathUtil.IsZero(weight - incumbentWeight)) {
            return weight > incumbentWeight;
        }

        return Volume(candidate) < Volume(incumbent);
    }

    static float Volume(ReflectionProbe probe) {
        if (probe.Radius > 0f) {
            return probe.Radius * probe.Radius * probe.Radius;
        }

        var extent = probe.Bounds.Maximum - probe.Bounds.Minimum;
        return extent.X * extent.Y * extent.Z;
    }
}
