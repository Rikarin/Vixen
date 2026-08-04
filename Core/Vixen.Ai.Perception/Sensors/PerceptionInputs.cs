// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Perception.Ecs;
using Vixen.Core.Mathematics;

namespace Vixen.Ai.Perception.Sensors;

/// <summary>What an agent's senses say, as a utility consideration and as a sensor.</summary>
/// <remarks>
///     <para>
///         doc 37 § Part 4's ⚠ on <see cref="IUtilityInput" />: <b>a perceived count is
///         <c>Vixen.Ai.Perception</c>'s, because <c>Vixen.Ai</c> may not see a sense.</b> It is the
///         second implementation of that seam that is not a delegate and not a blackboard key — which
///         is the point of the rule, since a seam whose only implementations read a key is a seam
///         shaped like a key.
///     </para>
///     <para>
///         ⚠ <b>An input is normalised and a sensor is not.</b> "Three of them" is what a blackboard
///         key wants and "0.6 of the most I care about" is what a curve wants, so the same question
///         has two front ends: <see cref="PerceivedCount" /> for a consideration and
///         <see cref="CountSensor" /> for a key. Normalising in the sensor would make the key useless
///         to a GOAP condition, which counts.
///     </para>
/// </remarks>
public static class PerceptionInputs {
    /// <summary>How many things an agent currently senses, as a fraction of a maximum.</summary>
    /// <param name="perception">The system that senses for it.</param>
    /// <param name="senses">Which senses count.</param>
    /// <param name="most">The count that reads as one.</param>
    /// <returns>The input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="perception" /> is null.</exception>
    public static IUtilityInput PerceivedCount(
        PerceptionSystem perception,
        SenseMask senses = SenseMask.All,
        int most = 4
    ) => new PerceivedCountInput(perception, senses, most);

    /// <summary>How far the nearest currently-sensed thing is, as a fraction of a range.</summary>
    /// <param name="perception">The system that senses for it.</param>
    /// <param name="senses">Which senses count.</param>
    /// <param name="range">The distance that reads as one. Nothing sensed also reads as one.</param>
    /// <returns>The input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="perception" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Nothing sensed reads as <i>far</i> and not as near.</b> With the zero rule, "how close
    ///     is the threat" inverted is a veto, and an agent that treated an empty perceived list as a
    ///     threat at zero metres would flee from nothing for ever.
    /// </remarks>
    public static IUtilityInput NearestPerceived(
        PerceptionSystem perception,
        SenseMask senses = SenseMask.Sight,
        float range = 20f
    ) => new NearestPerceivedInput(perception, senses, range);

    /// <summary>How many things an agent senses, as a count on a numeric key.</summary>
    /// <param name="perception">The system that senses for it.</param>
    /// <param name="senses">Which senses count.</param>
    /// <returns>The sensor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="perception" /> is null.</exception>
    public static ILocalWorldSensor CountSensor(PerceptionSystem perception, SenseMask senses = SenseMask.All) =>
        new PerceivedCountSensor(perception, senses);

    /// <summary>The nearest thing an agent senses, as a place and a thing.</summary>
    /// <param name="perception">The system that senses for it.</param>
    /// <param name="senses">Which senses count.</param>
    /// <param name="currentOnly">Whether a remembered target counts, or only one sensed right now.</param>
    /// <returns>The sensor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="perception" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Nearest and not freshest.</b> "Shoot whichever one is about to reach me" and "react to
    ///     what just happened" are different questions, and <c>NearestPerceivedService</c> already made
    ///     this choice for the tree front end — the two agree because they are the same question.
    /// </remarks>
    public static ITargetSensor NearestSensor(
        PerceptionSystem perception,
        SenseMask senses = SenseMask.Sight,
        bool currentOnly = true
    ) => new NearestPerceivedSensor(perception, senses, currentOnly);
}

/// <summary>How many things an agent senses, normalised.</summary>
sealed class PerceivedCountInput(PerceptionSystem perception, SenseMask senses, int most) : IUtilityInput {
    readonly PerceptionSystem perception = perception ?? throw new ArgumentNullException(nameof(perception));
    readonly float most = Math.Max(1, most);

    public float Read(in AgentContext context) {
        if (perception.PerceivedBy(context.World, context.Entity) is not { } perceived) {
            return 0f;
        }

        var count = 0;

        foreach (var target in perceived.Targets) {
            if (target.Current && Senses.Has(senses, target.Sense)) {
                count++;
            }
        }

        return Math.Clamp(count / most, 0f, 1f);
    }
}

/// <summary>How far the nearest sensed thing is, normalised. Nothing sensed reads as far.</summary>
sealed class NearestPerceivedInput(PerceptionSystem perception, SenseMask senses, float range) : IUtilityInput {
    readonly PerceptionSystem perception = perception ?? throw new ArgumentNullException(nameof(perception));
    readonly float range = MathF.Max(1e-3f, range);

    public float Read(in AgentContext context) {
        if (perception.PerceivedBy(context.World, context.Entity) is not { } perceived) {
            return 1f;
        }

        var here = PerceptionSystem.PositionOf(context.World, context.Entity);

        return perceived.TryNearest(senses, here, out var target)
            ? Math.Clamp((target.LastKnownLocation - here).Length() / range, 0f, 1f)
            : 1f;
    }
}

/// <summary>The count, unnormalised, on a key.</summary>
sealed class PerceivedCountSensor(PerceptionSystem perception, SenseMask senses) : ILocalWorldSensor {
    readonly PerceptionSystem perception = perception ?? throw new ArgumentNullException(nameof(perception));

    public void Sense(in AgentContext context, Blackboard blackboard, BlackboardKey key) {
        var count = 0;

        if (perception.PerceivedBy(context.World, context.Entity) is { } perceived) {
            foreach (var target in perceived.Targets) {
                if (target.Current && Senses.Has(senses, target.Sense)) {
                    count++;
                }
            }
        }

        if (blackboard.Layout[key].Type == BlackboardValueType.Int) {
            blackboard.SetInt(key, count);
        } else {
            blackboard.SetFloat(key, count);
        }
    }
}

/// <summary>The nearest sensed thing, as a place and a thing.</summary>
sealed class NearestPerceivedSensor(PerceptionSystem perception, SenseMask senses, bool currentOnly) : ITargetSensor {
    readonly PerceptionSystem perception = perception ?? throw new ArgumentNullException(nameof(perception));

    public SensorTarget Sense(in AgentContext context) {
        if (perception.PerceivedBy(context.World, context.Entity) is not { } perceived) {
            return SensorTarget.None;
        }

        var here = PerceptionSystem.PositionOf(context.World, context.Entity);

        return perceived.TryNearest(senses, here, out var target, currentOnly)
            ? SensorTarget.Of(target.Source, target.LastKnownLocation)
            : SensorTarget.None;
    }
}
