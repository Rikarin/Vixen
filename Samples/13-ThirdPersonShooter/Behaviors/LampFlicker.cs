// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Engine.Behaviors;
using Vixen.Rendering.Ecs;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>Breathes a lamp's intensity, so the indirect light has something to follow.</summary>
/// <remarks>
///     <para>
///         <b>The smallest behaviour in the project, and the one that proves the most.</b> Doc 19's
///         whole argument is that nothing is baked: a light that changes at run time changes the
///         bounce as well as the direct term, because the probe field is filled from the scene every
///         few frames rather than from a lightmap somebody built last week. Turn this off and the
///         lamps are static and the claim is untested.
///     </para>
///     <para>
///         <b>Attached to an entity the level created, not one the game did.</b> <c>PlayerRig</c>
///         hangs behaviours off things it made itself and knows the entity of; this one is attached by
///         a query over what the scene loaded — which is the other of the two ways a behaviour ever
///         gets onto an entity.
///     </para>
/// </remarks>
public sealed class LampFlicker : Behavior {
    float clock;

    /// <summary>The intensity to vary around. Taken from the level on the first frame.</summary>
    public float Baseline { get; private set; }

    /// <summary>How far it swings, as a fraction of the baseline.</summary>
    public float Depth { get; init; } = 0.12f;

    /// <summary>How fast, in cycles a second.</summary>
    public float Rate { get; init; } = 1.7f;

    /// <summary>Where in the cycle this lamp starts, so seven lamps do not pulse as one.</summary>
    public float Offset { get; init; }

    /// <inheritdoc />
    /// <remarks>
    ///     <c>Start</c> rather than <c>Awake</c>: the scene's components are all in place by the frame
    ///     after attachment, and reading the authored intensity is the whole reason this waits.
    /// </remarks>
    protected override void Start() {
        if (Has<Light>()) {
            Baseline = Read<Light>().Intensity;
        }
    }

    /// <inheritdoc />
    protected override void Update() {
        if (Baseline <= 0f || !Has<Light>()) {
            return;
        }

        clock += Time.DeltaSeconds;

        // Two waves at unrelated rates, so the pattern does not repeat on a beat anybody can hear in
        // their eyes. One sine reads as a machine; two read as a filament.
        var wave = (MathF.Sin((clock * Rate * MathF.Tau) + Offset) * 0.7f)
            + (MathF.Sin((clock * Rate * 2.7f * MathF.Tau) + Offset) * 0.3f);

        ref var light = ref Get<Light>();
        light.Intensity = Baseline * (1f + (Depth * wave));
    }
}
