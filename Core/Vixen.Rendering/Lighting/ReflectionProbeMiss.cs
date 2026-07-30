// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Reflections;
using Vixen.Shaders;

namespace Vixen.Rendering.Lighting;

/// <summary>Doc 06's reflection probes in the miss seat — the caveat's retirement, both halves.</summary>
/// <remarks>
///     <para>
///         <b>This is the arrangement that ends "blended against the sky".</b> A probe used to be
///         the whole reflection and faded into nothing but sky at its edge; behind the traced
///         reflections it answers only what the trace <i>missed</i> — the far field it is actually
///         good at — and the fade is a fade between two kinds of far field rather than a reflection
///         disappearing. The near field never fades at all, because it never came from the probe.
///     </para>
///     <para>
///         <b>One class, two halves.</b> As an <see cref="IReflectionFallback" /> it answers on the
///         CPU, sampling each probe's <see cref="CubeImage" /> through the same parallax correction
///         and the same inward-measured weight the shader uses — the reference the kernel's
///         <c>ReflectionProbeMissSource</c> is compared against. <see cref="Apply" /> writes the
///         device half: the cubes, the volumes and the count, under the slot's qualified names.
///     </para>
///     <para>
///         <b>Probes are consulted in the order they were added, first non-zero weight wins.</b>
///         The caller sorts by priority — the same contract <c>SceneLighting</c> keeps for the
///         forward pass's array, so the two selections cannot disagree about which probe a
///         position belongs to.
///     </para>
/// </remarks>
public sealed class ReflectionProbeMiss : IReflectionFallback {
    /// <summary>How many probes the shader's arrays hold — its fixed binding count.</summary>
    /// <remarks>Four, and not selectable from a host, for <c>GlobalDistanceField.LevelCount</c>'s
    ///     exact reason: the compiler does not surface a composed shader's permutations.</remarks>
    public const int ProbeLimit = 4;

    readonly List<(ReflectionProbe Probe, CubeImage Radiance)> probes = [];

    /// <summary>What lies behind every probe — the sky, one colour.</summary>
    public Vector3 FarColour { get; set; }

    /// <summary>The probes in residence, in consultation order.</summary>
    public int Count => probes.Count;

    /// <summary>Adds a probe and the image its cube was prefiltered from.</summary>
    /// <param name="probe">The probe — bounds, capture point, blend, and the device view.</param>
    /// <param name="radiance">The same capture, CPU-side, for the reference to sample.</param>
    /// <exception cref="ArgumentNullException">There is no probe or no image.</exception>
    /// <exception cref="InvalidOperationException">The shader's arrays are full.</exception>
    public void Add(ReflectionProbe probe, CubeImage radiance) {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(radiance);

        if (probes.Count >= ProbeLimit) {
            throw new InvalidOperationException(
                $"the miss source holds {ProbeLimit} probes — the shader's array size — and choosing "
                + "which four cover a frame is the caller's selection to make, not a fifth slot's"
            );
        }

        probes.Add((probe, radiance));
    }

    /// <inheritdoc />
    public Vector3 Miss(Vector3 position, Vector3 direction, float roughness) {
        foreach (var (probe, radiance) in probes) {
            var weight = probe.WeightAt(position);

            if (weight <= 0f) {
                continue;
            }

            var corrected = probe.Radius > 0f
                ? CorrectSphere(direction, position, probe.CapturePosition, probe.Radius)
                : CorrectBox(direction, position, probe.Bounds, probe.CapturePosition);

            // The image's own nearest fetch rather than the device's bilinear, stated: the
            // comparison fixtures paint each face one colour, where the two filters are the same
            // answer everywhere but the face seams — exactly where a fixture must not put its
            // expectations.
            return Vector3.Lerp(FarColour, radiance.Sample(corrected), weight);
        }

        return FarColour;
    }

    /// <summary>Writes the device half — cubes, volumes, count — under the slot's qualified names.</summary>
    /// <param name="parameters">Where the consuming pass reads its set 0 from.</param>
    /// <param name="shaderName">The slot's qualified name —
    ///     <c>ReflectionTrace.ReflectionProbeMissSource</c> for the kernel.</param>
    /// <param name="sampler">How the cubes are sampled — one sampler for the set, the frame's.</param>
    /// <exception cref="ArgumentNullException">There are no parameters.</exception>
    /// <exception cref="ArgumentException">There is no shader name.</exception>
    /// <remarks>⚠ Every slot of the array is written, the spare ones with the last probe's cube —
    ///     the shader samples a slot before it weighs it, the forward pass's own rule, and a
    ///     descriptor left empty binds nothing at all.</remarks>
    public void Apply(ParameterCollection parameters, string shaderName, SamplerHandle sampler) {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(shaderName);

        if (probes.Count == 0) {
            throw new InvalidOperationException("a miss source with no probes has nothing to bind — compose the sky instead");
        }

        for (var index = 0; index < ProbeLimit; index++) {
            var (probe, _) = probes[Math.Min(index, probes.Count - 1)];
            var slot = $"{shaderName}.missProbeVolumes[{index.ToString(CultureInfo.InvariantCulture)}]";

            parameters.Set(
                ParameterKeys.New<TextureViewHandle>(
                    $"{shaderName}.missProbes[{index.ToString(CultureInfo.InvariantCulture)}]"
                ),
                probe.Prefiltered
            );

            parameters.Set(ParameterKeys.New<Vector3>($"{slot}.minimum"), probe.Bounds.Minimum);
            parameters.Set(ParameterKeys.New<Vector3>($"{slot}.maximum"), probe.Bounds.Maximum);
            parameters.Set(ParameterKeys.New<Vector3>($"{slot}.center"), probe.CapturePosition);
            parameters.Set(ParameterKeys.New<float>($"{slot}.radius"), probe.Radius);
            parameters.Set(ParameterKeys.New<float>($"{slot}.mipCount"), probe.MipCount);
            parameters.Set(ParameterKeys.New<float>($"{slot}.blendDistance"), probe.BlendDistance);
        }

        parameters.Set(ParameterKeys.New<SamplerHandle>($"{shaderName}.missProbeSampler"), sampler);
        parameters.Set(ParameterKeys.New<int>($"{shaderName}.missProbeCount"), probes.Count);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.missFarColor"), FarColour);
    }

    /// <summary>The box correction — <c>Ibl.ParallaxCorrect</c>'s arithmetic, mirrored exactly.</summary>
    static Vector3 CorrectBox(Vector3 reflected, Vector3 position, BoundingBox box, Vector3 centre) {
        const float Epsilon = 0.0001f;

        var guarded = Vector3.Max(reflected, new(Epsilon));
        var toMax = (box.Maximum - position) / guarded;
        var toMin = (box.Minimum - position) / guarded;
        var furthest = Vector3.Max(toMax, toMin);
        var distance = MathF.Min(furthest.X, MathF.Min(furthest.Y, furthest.Z));

        return Vector3.Normalize(position + (reflected * distance) - centre);
    }

    /// <summary>The sphere correction — <c>Ibl.ParallaxCorrectSphere</c>'s, mirrored exactly.</summary>
    static Vector3 CorrectSphere(Vector3 reflected, Vector3 position, Vector3 centre, float radius) {
        var toCentre = centre - position;
        var along = Vector3.Dot(toCentre, reflected);
        var half = MathF.Sqrt(MathF.Max((radius * radius) - (Vector3.Dot(toCentre, toCentre) - (along * along)), 0f));

        return Vector3.Normalize(position + (reflected * (along + half)) - centre);
    }

}
