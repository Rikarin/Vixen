// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Ai;

/// <summary>Where a consideration's number comes from.</summary>
/// <remarks>
///     <para>
///         doc 37 § Part 4's seam, and the one a project replaces most often: "how hungry am I", "how
///         much ammo is left", "how many of them are there" are all a game's own question.
///     </para>
///     <para>
///         ⚠ <b>It returns a number in <c>[0,1]</c> and the normalisation is the input's job, not the
///         curve's.</b> A curve whose domain were "0 to whatever this game's maximum health is" could
///         not be drawn, could not be shared between two considerations, and would have to be
///         re-authored the day somebody changed the maximum. Normalising here is what makes the six
///         shapes in <see cref="ResponseCurveKind" /> mean the same thing everywhere.
///     </para>
/// </remarks>
public interface IUtilityInput {
    /// <summary>Reads the world.</summary>
    /// <param name="context">The agent.</param>
    /// <returns>A number in <c>[0,1]</c>. Anything outside is clamped by the caller.</returns>
    float Read(in AgentContext context);
}

/// <summary>An input written as a lambda.</summary>
/// <param name="context">The agent.</param>
/// <returns>A number in <c>[0,1]</c>.</returns>
public delegate float UtilityReading(in AgentContext context);

/// <summary>The inputs that ship.</summary>
public static class UtilityInputs {
    /// <summary>An input from a lambda.</summary>
    /// <param name="reading">What it does.</param>
    /// <returns>The input.</returns>
    public static IUtilityInput From(UtilityReading reading) => new DelegateUtilityInput(reading);

    /// <summary>A constant, for a consideration that is only there to carry a weight.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The input.</returns>
    public static IUtilityInput Constant(float value) => new ConstantUtilityInput(value);
}

/// <summary>A numeric blackboard key, normalised between two bounds.</summary>
/// <param name="key">The key. A bool reads as 0 or 1; an int and a float read as themselves.</param>
/// <param name="minimum">What maps to zero.</param>
/// <param name="maximum">What maps to one.</param>
/// <remarks>
///     ⚠ <b>An unset key reads as zero, not as <paramref name="minimum" />.</b> "Nobody has written
///     this yet" and "this is at its lowest" are different facts, and a consideration that treated
///     them the same would make an agent act on a value it has never been given — which with the zero
///     rule is the safe direction: the action is vetoed until something writes the key.
/// </remarks>
public sealed class BlackboardUtilityInput(BlackboardKey key, float minimum = 0f, float maximum = 1f)
    : IUtilityInput {
    readonly float span = MathF.Abs(maximum - minimum) < 1e-6f ? 1f : maximum - minimum;

    /// <inheritdoc />
    public float Read(in AgentContext context) {
        var blackboard = context.Blackboard;

        if (!key.IsValid || key.Index >= blackboard.Layout.Count || !blackboard.IsSet(key)) {
            return 0f;
        }

        var value = blackboard.Layout[key].Type switch {
            BlackboardValueType.Bool => blackboard.GetBool(key) ? 1f : 0f,
            BlackboardValueType.Int => blackboard.GetInt(key),
            BlackboardValueType.Float => blackboard.GetFloat(key),
            _ => 0f
        };

        return Math.Clamp((value - minimum) / span, 0f, 1f);
    }
}

/// <summary>How far the agent is from what a key names, as a fraction of a maximum.</summary>
/// <param name="key">A <c>Vector3</c> key, or an <c>Entity</c> key with a position on it.</param>
/// <param name="range">The distance that maps to one. Anything further reads as one.</param>
/// <param name="positionOf">How to find an entity's position, or null to read <c>Vector3</c> keys only.</param>
/// <remarks>
///     ⚠ <b>The position lookup is a delegate, and that is the layering.</b> Where an entity is lives
///     in <c>Vixen.Engine</c>, which this assembly may not reference — doc 37's whole argument for
///     putting the planners in <c>Core/</c>. A game or <c>Vixen.Ai.Nodes</c> passes
///     <c>AgentTarget.TryPositionOf</c> and gets the entity form; a game with no transforms at all
///     passes nothing and gets the <c>Vector3</c> form, which still works.
/// </remarks>
public sealed class DistanceUtilityInput(
    BlackboardKey key,
    float range = 20f,
    DistanceUtilityInput.PositionLookup? positionOf = null
) : IUtilityInput {
    /// <summary>How to find where an entity is.</summary>
    /// <param name="context">The agent, for its world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="position">Where to put it.</param>
    /// <returns>Whether it has a position.</returns>
    public delegate bool PositionLookup(in AgentContext context, Entity entity, out Vector3 position);

    /// <inheritdoc />
    public float Read(in AgentContext context) {
        var blackboard = context.Blackboard;

        if (!key.IsValid || key.Index >= blackboard.Layout.Count || !blackboard.IsSet(key)) {
            return 1f;
        }

        Vector3 target;

        switch (blackboard.Layout[key].Type) {
            case BlackboardValueType.Vector3:
                target = blackboard.GetVector3(key);

                break;

            case BlackboardValueType.Entity when positionOf is not null:
                if (!positionOf(in context, blackboard.GetEntity(key), out target)) {
                    return 1f;
                }

                break;

            default:
                return 1f;
        }

        // The agent's own position comes from the same lookup, so an assembly that cannot see a
        // transform cannot half-answer this: either both ends resolve or the distance is "far".
        if (positionOf is null || !positionOf(in context, context.Entity, out var here)) {
            return 1f;
        }

        return Math.Clamp((target - here).Length() / MathF.Max(1e-3f, range), 0f, 1f);
    }
}

/// <summary>Always the same number.</summary>
sealed class ConstantUtilityInput(float value) : IUtilityInput {
    readonly float value = Math.Clamp(value, 0f, 1f);

    public float Read(in AgentContext context) => value;
}

/// <summary>An input that is a lambda.</summary>
sealed class DelegateUtilityInput(UtilityReading reading) : IUtilityInput {
    readonly UtilityReading reading = reading ?? throw new ArgumentNullException(nameof(reading));

    public float Read(in AgentContext context) => reading(in context);
}
