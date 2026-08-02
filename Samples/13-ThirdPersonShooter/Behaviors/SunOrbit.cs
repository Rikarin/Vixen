// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Transforms;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>Sweeps the sun around the level, so the shadows can be watched moving.</summary>
/// <remarks>
///     <para>
///         <b>Azimuth only — the elevation the level authored is kept.</b> A full tumble would put
///         the sun under the floor for half of every cycle and light the level from beneath, which is
///         a fine thing to watch once and useless for looking at shadows. Turning it about the world
///         up axis keeps it at its seven degrees and sweeps every shadow through a complete circle,
///         which is the thing being looked at.
///     </para>
///     <para>
///         ⚠ <b>The sky does not follow it, and cannot cheaply.</b> The environment cube is a CPU
///         bake — Preetham's model at sixty-four importance samples per texel per face per level,
///         which is most of this project's load time — so the ambient, the horizon and the sun's own
///         colour stay where <see cref="ArenaFrame.Bake" /> left them. What moves is the directional
///         light and everything downstream of it: the cascades, the caster pass, the shading term.
///         So the shadows sweep under a sky that does not, and the two disagree by design rather than
///         by accident.
///     </para>
///     <para>
///         Attached by a query over what the level loaded, exactly as <see cref="LampFlicker" /> is —
///         the game has never seen this entity and has no handle to it, so a query is the only thing
///         that can find it.
///     </para>
/// </remarks>
public sealed class SunOrbit : Behavior {
    float clock;

    /// <summary>Where the level pointed it, which the sweep turns about the up axis.</summary>
    public Quaternion Baseline { get; private set; } = Quaternion.Identity;

    /// <summary>How long one full turn takes, in seconds.</summary>
    public float Period { get; init; } = 30f;

    /// <inheritdoc />
    /// <remarks>
    ///     <c>Start</c> rather than <c>Awake</c>, on <see cref="LampFlicker" />'s terms: the scene's
    ///     components are all in place by the frame after attachment, and reading the authored
    ///     rotation is the whole reason this waits.
    /// </remarks>
    protected override void Start() {
        if (Has<LocalTransform>()) {
            Baseline = Read<LocalTransform>().Rotation;
        }
    }

    /// <inheritdoc />
    protected override void Update() {
        if (!Has<LocalTransform>() || Period <= 0f) {
            return;
        }

        clock += Time.DeltaSeconds;

        // Wrapped rather than left to grow, so a session left running overnight has the same
        // precision in its angle as one a minute old.
        clock %= Period;

        var turn = Quaternion.FromAxisAngle(Vector3.Up, clock / Period * MathF.Tau);

        ref var transform = ref Get<LocalTransform>();
        transform.Rotation = Quaternion.Normalize(turn * Baseline);
    }
}
