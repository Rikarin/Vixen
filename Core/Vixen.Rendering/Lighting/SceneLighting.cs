// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering.Lighting;

/// <summary>
///     What a scene's lighting is, as the names a pass's per-frame set is filled through.
/// </summary>
/// <remarks>
///     <para>
///         The step everything else was waiting for. <see cref="EnvironmentLight.Apply" /> and
///         <see cref="ReflectionProbe.Apply" /> have known how to write themselves for some time, and
///         <see cref="Features.ForwardLightingRenderFeature" /> writes a probe <em>index</em> per
///         object — into an array nothing was putting probes into. This walks the objects a scene
///         holds into <see cref="SceneConstants.Parameters" />, so that index points at something.
///     </para>
///     <para>
///         <strong>The array's length is the shader's, and the order is the selector's.</strong> The
///         length comes off the effect's own plan, because <c>ProbeCount</c> is a permutation that
///         sizes a binding; the order has to be
///         <see cref="ReflectionProbeSelector.Probes" />' own, because the index the per-object block
///         carries is a position in that list. Sorting them here — by weight, by priority, anything —
///         would silently point every object at a different probe than the one that was chosen for
///         it.
///     </para>
///     <para>
///         <strong>Every slot is filled, including the empty ones.</strong> A frame with two probes
///         and an array of four still owes the set four descriptors: the shader samples
///         <c>probes[clamp(probeIndex, 0, ProbeCount - 1)]</c> and only then weighs the result
///         against zero, so a slot with no descriptor is read rather than skipped. The spare slots
///         take the environment's own cube, which is the right answer as well as a valid one — it is
///         what a surface with no probe reflects.
///     </para>
///     <para>
///         What it does <em>not</em> write is as deliberate: the shadow map and its matrix belong to
///         whatever rendered the shadows, and the light and cluster buffers to the features that
///         filled them. Those are handles a frame produced, and their producers already hold them —
///         this turns the objects a <em>scene</em> holds into names, which is a different job with a
///         different lifetime.
///     </para>
/// </remarks>
public sealed class SceneLighting {
    /// <summary>The shader's name for the environment cube.</summary>
    const string EnvironmentName = "environment";

    /// <summary>The shader's name for how that cube is sampled.</summary>
    const string EnvironmentSamplerName = "environmentSampler";

    /// <summary>The shader's name for the probe array. Its length is the binding's.</summary>
    const string ProbesName = "probes";

    /// <summary>The shader's name for how the probes are sampled — one sampler for all of them.</summary>
    const string ProbeSamplerName = "probeSampler";

    /// <summary>The scene's sky, or null for a scene lit by lamps alone.</summary>
    public EnvironmentLight? Environment { get; set; }

    /// <summary>The probes to bind, in the order an object's index refers to them.</summary>
    /// <remarks>
    ///     The same instance <see cref="Features.ForwardLightingRenderFeature.Probes" /> selects from,
    ///     and it has to be: one writes a position in this list and the other fills the list. Two
    ///     selectors holding equal probes would agree until one of them was reordered.
    /// </remarks>
    public ReflectionProbeSelector? Probes { get; set; }

    /// <summary>Where the directional light comes from — the lighting feature, or a scene's own.</summary>
    public ISunSource? Sun { get; set; }

    /// <summary>
    ///     The camera the frame's froxel grid is built in, or null to write no grid parameters.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A camera in a type about lighting, because of what those four numbers are for: they are
    ///         how a fragment finds <em>which lights reach it</em>. The culling pass bins a light into
    ///         a froxel from the field of view, the aspect ratio and the two planes, and a fragment
    ///         looks itself up in the grid from the same four — so they are a property of the frame's
    ///         lighting rather than of its camera, and set 0 is where the shader reads them.
    ///     </para>
    ///     <para>
    ///         <see cref="ClusterGrid.Apply" /> is the one implementation, and both passes are filled
    ///         through it. Two derivations of one quantity is how a fragment ends up reading the list
    ///         that was culled for somewhere else.
    ///     </para>
    /// </remarks>
    public RenderCamera? Camera { get; set; }

    /// <summary>How many probe slots the last extract found the shader had.</summary>
    /// <remarks>Zero for a variant compiled with <c>UseReflectionProbe</c> off, which binds none.</remarks>
    public int Slots { get; private set; }

    /// <summary>How many of those slots a probe of its own filled.</summary>
    public int Bound { get; private set; }

    /// <summary>
    ///     How many probes the array had no room for.
    /// </summary>
    /// <remarks>
    ///     Not an error and not silent either. A scene with six probes and an array of four binds the
    ///     first four, and an object that selected the fifth carries an index the shader clamps — so
    ///     it reflects the wrong room rather than nothing, which is the kind of thing that gets
    ///     blamed on the artist. A host watching this raises <c>ProbeCount</c> or splits the scene.
    /// </remarks>
    public int Dropped { get; private set; }

    /// <summary>
    ///     Writes the scene's lighting into the names one pass reads it under.
    /// </summary>
    /// <param name="parameters">Where the values and handles go.</param>
    /// <param name="effect">The pass, whose plan says how long the probe array is and whose name qualifies every key.</param>
    /// <remarks>
    ///     Every frame, and it costs nothing when nothing moved:
    ///     <see cref="ParameterCollection.Set{T}(ParameterKey{T}, T)" /> does not bump the version for
    ///     a value that is already there, so a static scene re-asserts the same numbers and the block
    ///     is not re-uploaded.
    /// </remarks>
    public void Extract(ParameterCollection parameters, Effect effect) {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(effect);

        var shader = effect.Key.ShaderName;

        WriteSun(parameters, shader);
        WriteEnvironment(parameters, shader);
        WriteProbes(parameters, shader, effect);

        if (Camera is { } camera) {
            ClusterGrid.Apply(parameters, camera, shader);
        }
    }

    /// <summary>The directional light, or the absence of one.</summary>
    /// <remarks>
    ///     A sun that went away is written as black rather than left alone. The parameters outlive the
    ///     frame that filled them, so a scene whose sun was removed would otherwise keep being lit by
    ///     it — and the value that says "no sun" is one nobody would think to set.
    /// </remarks>
    void WriteSun(ParameterCollection parameters, string shader) {
        var sun = Sun?.Sun;

        parameters.Set(
            ParameterKeys.New<Vector3>($"{shader}.lightDirection"),
            sun is { } light ? light.Direction : Vector3.Zero
        );

        parameters.Set(
            ParameterKeys.New<Vector3>($"{shader}.lightColor"),
            sun is { } lit ? lit.Radiance : Vector3.Zero
        );
    }

    /// <summary>The sky: nine coefficients, a cube and how to sample it.</summary>
    void WriteEnvironment(ParameterCollection parameters, string shader) {
        if (Environment is not { } environment) {
            return;
        }

        environment.Apply(parameters, shader);

        if (environment.Prefiltered.IsValid) {
            parameters.Set(
                ParameterKeys.New<TextureViewHandle>($"{shader}.{EnvironmentName}"),
                environment.Prefiltered
            );
        }

        if (environment.Sampler.IsValid) {
            parameters.Set(
                ParameterKeys.New<SamplerHandle>($"{shader}.{EnvironmentSamplerName}"),
                environment.Sampler
            );
        }
    }

    /// <summary>Every probe the array has room for, and something valid in the slots left over.</summary>
    void WriteProbes(ParameterCollection parameters, string shader, Effect effect) {
        Slots = SlotsOf(effect);
        Bound = 0;
        Dropped = 0;

        if (Slots <= 0) {
            return;
        }

        var probes = Probes?.Probes;
        var count = probes?.Count ?? 0;

        Bound = Math.Min(count, Slots);
        Dropped = count - Bound;

        // The array's own name, which every slot with no probe of its own falls back to. Written
        // first: a slot that does have one overrides it, and a frame with no probes at all still owes
        // the set four cubes.
        if (Fallback() is { IsValid: true } spare) {
            parameters.Set(ParameterKeys.New<TextureViewHandle>($"{shader}.{ProbesName}"), spare);
        }

        if (Sampler() is { IsValid: true } sampler) {
            parameters.Set(ParameterKeys.New<SamplerHandle>($"{shader}.{ProbeSamplerName}"), sampler);
        }

        for (var index = 0; index < Bound; index++) {
            var probe = probes![index];

            // The volume goes in whether or not the cube does. A probe whose capture has not been
            // baked yet is still a volume an object can be inside, and its slot falls back to the
            // environment's cube — which is what that object would have reflected anyway.
            probe.Apply(parameters, index, shader);

            if (probe.Prefiltered.IsValid) {
                parameters.Set(
                    ParameterKeys.New<TextureViewHandle>(
                        $"{shader}.{ProbesName}[{index.ToString(CultureInfo.InvariantCulture)}]"
                    ),
                    probe.Prefiltered
                );
            }
        }
    }

    /// <summary>How long the shader's probe array is, or zero when it declares none.</summary>
    static int SlotsOf(Effect effect) {
        foreach (var binding in effect.Bindings) {
            if (string.Equals(binding.Name, ProbesName, StringComparison.Ordinal)) {
                return Math.Max(binding.Count, 1);
            }
        }

        return 0;
    }

    /// <summary>What an empty slot holds: the sky, or failing that the first probe that has a cube.</summary>
    TextureViewHandle Fallback() {
        if (Environment is { Prefiltered.IsValid: true } environment) {
            return environment.Prefiltered;
        }

        if (Probes is { } selector) {
            foreach (var probe in selector.Probes) {
                if (probe.Prefiltered.IsValid) {
                    return probe.Prefiltered;
                }
            }
        }

        return default;
    }

    /// <summary>
    ///     How the probes are sampled — one sampler for the whole array.
    /// </summary>
    /// <remarks>
    ///     The first probe that has one, and the environment's after that. The binding is a single
    ///     sampler because every probe is prefiltered the same way, so a probe carrying a sampler of
    ///     its own is describing something the shader has no way to honour — and picking the first
    ///     rather than the last makes which one it is a fact about the scene rather than about
    ///     iteration order.
    /// </remarks>
    SamplerHandle Sampler() {
        if (Probes is { } selector) {
            foreach (var probe in selector.Probes) {
                if (probe.Sampler.IsValid) {
                    return probe.Sampler;
                }
            }
        }

        return Environment?.Sampler ?? default;
    }
}
