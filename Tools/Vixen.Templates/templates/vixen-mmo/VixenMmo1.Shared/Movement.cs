using VixenMmo1.Contracts;

namespace VixenMmo1.Shared;

/// <summary>What a player pressed. The client sends these; the realm replays them.</summary>
public readonly record struct MoveInput(float Forward, float Strafe, float Facing);

/// <summary>The step both ends run, and neither end owns.</summary>
/// <remarks>
///     Deterministic and side-effect free on purpose: the client runs it to predict, the realm runs
///     it to decide, and rollback runs it again over a corrected state. Anything it read that was
///     not passed in — a random number, the wall clock, a component the client does not have —
///     would be a divergence the player sees as rubber-banding.
/// </remarks>
public static class Movement {
    /// <summary>Metres per second, flat. A real game reads this off the character's attributes.</summary>
    public const float Speed = 5f;

    /// <summary>Advances a pose by one step.</summary>
    /// <param name="pose">Where they were.</param>
    /// <param name="input">What they pressed.</param>
    /// <param name="delta">How long the step is, in seconds.</param>
    /// <returns>Where they are.</returns>
    public static Pose Step(Pose pose, MoveInput input, float delta) {
        var cos = MathF.Cos(input.Facing);
        var sin = MathF.Sin(input.Facing);

        return pose with {
            X = pose.X + ((input.Forward * cos) - (input.Strafe * sin)) * Speed * delta,
            Z = pose.Z + ((input.Forward * sin) + (input.Strafe * cos)) * Speed * delta,
            Facing = input.Facing
        };
    }
}
