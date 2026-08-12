// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Engine.Players;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>
///     A player driven by a written script instead of by a device, so a headless capture can be
///     taken of a camera that is <i>moving</i>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> <c>VIXEN_SPAWN</c> sets a start pose and nothing drives the camera
///         afterwards, so every headless picture this repository has ever taken is of a still frame —
///         and every temporal defect in the renderer is a defect <i>under motion</i>. TAA's whole job
///         is reprojection, motion vectors are zero when nothing moves, and the virtual shadow map
///         refits its finest level every 0.31 m of walking. Two investigations have now ended at "the
///         mechanism most likely to produce this lives under motion, and I could not measure it".
///     </para>
///     <para>
///         <b>⚠ It has no clock of its own, and that is the whole of its correctness.</b> The only
///         time this class knows about is the <c>deltaTime</c> <c>PlayerInputSystem</c> hands to
///         <see cref="Sample" />, which under <c>--vixen-fixed-step</c> — implied by
///         <c>--vixen-capture</c> at a sixtieth — is a constant. A <c>Stopwatch</c> here would make
///         the walk a function of how fast the machine happened to render, which is exactly the
///         wall-clock non-reproducibility that flag was added to remove, and exactly the bug that was
///         found hiding in <c>TerrainSceneSource</c>'s grass wind. <see cref="Elapsed" /> is the sum
///         of the deltas handed in and nothing else, so two runs walk identically.
///     </para>
///     <para>
///         <b>It is a sample facility and not an engine one.</b>
///         <see cref="IPlayerInputSource" /> is documented as the seam a planner, a replay or a test
///         takes, so a scripted walk needs no new engine surface at all — everything downstream of
///         the controller is already unable to tell what is driving it. Putting a scripted camera in
///         the host would be the first half of a cutscene or replay system, which is a much larger
///         decision than a diagnostic needs to make.
///     </para>
///     <para>
///         <b>The script is legs, and a leg is a stick held still.</b>
///         <c>seconds:forward:strafe:yawRate:pitchRate</c>, several separated by <c>;</c>. Forward and
///         strafe are <see cref="MoveIntent.Move" /> — a magnitude of one is
///         <c>CharacterMovement.WalkSpeed</c>, 4.5 m/s in this sample — and the two rates are degrees
///         a second of aim. Everything after the duration is optional and defaults to walking
///         straight ahead, so <c>VIXEN_WALK=14</c> is "walk forward for fourteen seconds", which is
///         out of a gate and thirty metres onto the terrain.
///     </para>
///     <para>
///         Durations rather than waypoints, deliberately. A duration times the fixed step is an exact
///         number of frames, so "the gate is crossed at frame 427" is arithmetic; a waypoint is a
///         feedback loop against a character that accelerates, slides along walls and climbs slopes,
///         and it would arrive at a different frame on a level somebody edited.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     VIXEN_SPAWN=0,0.2,0,180 VIXEN_WALK=14 dotnet … --vixen-frames 840 --vixen-capture ./shots
///     VIXEN_SPAWN=0,0.2,0,145 VIXEN_WALK="2:0:0:0;6:1:0:20" …
///     </code>
/// </example>
public sealed class ScriptedWalk : IPlayerInputSource {
    /// <summary>The environment variable a run is scripted with.</summary>
    public const string Variable = "VIXEN_WALK";

    readonly Leg[] legs;

    /// <summary>Builds a walk from its legs.</summary>
    /// <param name="legs">The legs, in order. An empty list stands still forever.</param>
    /// <exception cref="ArgumentNullException"><paramref name="legs" /> is null.</exception>
    public ScriptedWalk(IEnumerable<Leg> legs) {
        ArgumentNullException.ThrowIfNull(legs);
        this.legs = [.. legs];

        foreach (var leg in this.legs) {
            Duration += leg.Seconds;
        }
    }

    /// <summary>One leg: a stick held still for a while.</summary>
    /// <param name="Seconds">How long, in simulated seconds.</param>
    /// <param name="Forward">Throttle along the aim. One is the character's full walk speed.</param>
    /// <param name="Strafe">Throttle across it. Positive is right.</param>
    /// <param name="YawRate">Degrees a second of turn. Positive turns left, as
    ///     <c>ControlRotation.Turn</c> does.</param>
    /// <param name="PitchRate">Degrees a second of tilt. Positive looks up.</param>
    public readonly record struct Leg(
        float Seconds,
        float Forward = 1f,
        float Strafe = 0f,
        float YawRate = 0f,
        float PitchRate = 0f
    );

    /// <summary>How much simulated time this walk has been handed.</summary>
    /// <remarks>
    ///     <para>
    ///         The sum of every <c>deltaTime</c> passed to <see cref="Sample" />, and nothing else. It
    ///         is deliberately not a wall clock: see the remarks on the class.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Accumulated in <see cref="double" /> although every delta is a float</b>, and the
    ///         first version of this was not. Sixty additions of <c>1f / 60f</c> in single precision
    ///         come to 0.99999994, so a one-second leg ran a sixty-first frame — reproducibly, which
    ///         is worse than randomly: the harness would have been quietly off by one frame at every
    ///         leg boundary and the arithmetic in the log line would have said otherwise. A
    ///         capture run adds nine hundred of these.
    ///     </para>
    /// </remarks>
    public double Elapsed { get; private set; }

    /// <summary>How long the whole script lasts, in simulated seconds.</summary>
    public double Duration { get; }

    /// <summary>Whether the script has run out, after which the player stands still.</summary>
    public bool Finished => Elapsed >= Duration;

    /// <summary>The legs, in order.</summary>
    public IReadOnlyList<Leg> Legs => legs;

    /// <summary>Reads <c>VIXEN_WALK</c>, or <see langword="null" /> if it is not set.</summary>
    /// <returns>The walk, or <see langword="null" />.</returns>
    /// <exception cref="FormatException">The variable is set and does not parse.</exception>
    /// <remarks>
    ///     A malformed script throws rather than being ignored. A capture harness that silently took
    ///     a still picture because a colon was a semicolon would be answering a question nobody
    ///     asked — and the whole point of the run is that the camera moved.
    /// </remarks>
    public static ScriptedWalk? FromEnvironment() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } spec ? Parse(spec) : null;

    /// <summary>Parses a script.</summary>
    /// <param name="spec">
    ///     Legs separated by <c>;</c>, each <c>seconds[:forward[:strafe[:yawRate[:pitchRate]]]]</c>.
    /// </param>
    /// <returns>The walk.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="spec" /> is null.</exception>
    /// <exception cref="FormatException">A leg is malformed or has a duration that is not positive.</exception>
    public static ScriptedWalk Parse(string spec) {
        ArgumentNullException.ThrowIfNull(spec);

        List<Leg> legs = [];

        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var fields = part.Split(':', StringSplitOptions.TrimEntries);

            if (fields.Length > 5) {
                throw new FormatException(
                    $"'{part}' has {fields.Length} fields; a leg is "
                    + "seconds[:forward[:strafe[:yawRate[:pitchRate]]]]."
                );
            }

            var seconds = Number(fields, 0, part);

            if (!(seconds > 0f)) {
                throw new FormatException($"'{part}' lasts {seconds} seconds; a leg has to last some.");
            }

            legs.Add(
                new(
                    seconds,
                    fields.Length > 1 ? Number(fields, 1, part) : 1f,
                    fields.Length > 2 ? Number(fields, 2, part) : 0f,
                    fields.Length > 3 ? Number(fields, 3, part) : 0f,
                    fields.Length > 4 ? Number(fields, 4, part) : 0f
                )
            );
        }

        if (legs.Count == 0) {
            throw new FormatException($"'{spec}' holds no legs; a walk is at least one duration.");
        }

        return new(legs);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The leg is picked from the time at the <em>start</em> of the frame and holds for the whole
    ///     of it, so a boundary lands on a frame rather than inside one. That is what makes "the
    ///     script changes at frame N" a statement about a picture.
    /// </remarks>
    public void Sample(ref ControlRotation rotation, ref MoveIntent intent, float deltaTime) {
        var leg = At(Elapsed);

        Elapsed += deltaTime;

        if (leg is null) {
            // Past the end. Cleared rather than frozen, for PlayerInputSystem's own reason: what the
            // script was holding is stale the moment it stops, and where it was looking is not.
            intent.Move = Vector2.Zero;
            intent.Buttons = MoveButtons.None;
            intent.Yaw = rotation.Yaw;
            intent.Pitch = rotation.Pitch;

            return;
        }

        var turn = leg.Value;

        if (turn.YawRate != 0f || turn.PitchRate != 0f) {
            rotation.Turn(
                MathUtil.DegreesToRadians(turn.YawRate) * deltaTime,
                MathUtil.DegreesToRadians(turn.PitchRate) * deltaTime
            );
        }

        intent.Move = new(turn.Strafe, turn.Forward);
        intent.Yaw = rotation.Yaw;
        intent.Pitch = rotation.Pitch;
        intent.Buttons = MoveButtons.None;
    }

    /// <summary>Which leg a moment falls in, or <see langword="null" /> past the end.</summary>
    /// <param name="time">The moment, in simulated seconds since the walk began.</param>
    /// <returns>The leg, or <see langword="null" />.</returns>
    public Leg? At(double time) {
        var start = 0d;

        foreach (var leg in legs) {
            if (time < start + leg.Seconds) {
                return leg;
            }

            start += leg.Seconds;
        }

        return null;
    }

    /// <summary>The script, written back out, for the line a run logs about itself.</summary>
    /// <returns>The script in <see cref="Parse" />'s own syntax.</returns>
    public override string ToString() {
        var text = new StringBuilder();

        foreach (var leg in legs) {
            if (text.Length > 0) {
                text.Append(';');
            }

            text.Append(
                CultureInfo.InvariantCulture,
                $"{leg.Seconds:0.###}:{leg.Forward:0.###}:{leg.Strafe:0.###}:{leg.YawRate:0.###}:{leg.PitchRate:0.###}"
            );
        }

        return text.ToString();
    }

    static float Number(string[] fields, int index, string part) =>
        float.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"'{fields[index]}' in leg '{part}' is not a number.");
}
