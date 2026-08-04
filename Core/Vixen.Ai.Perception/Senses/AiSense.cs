// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ai.Perception;

/// <summary>One way of finding out that something is there.</summary>
/// <remarks>
///     <para>
///         Unreal's five, minus one: <b>prediction is not a sense</b> and is not here. Predicting
///         where something will be is a query somebody asks — whatever is aiming, leading a shot or
///         cutting a corner — and it needs a velocity and a time, neither of which a listener has. A
///         sense answers "is it there"; a prediction answers "where will it be", and putting the
///         second behind the first would make every agent pay for a question almost none of them ask.
///     </para>
///     <para>
///         ⚠ <b>The order is the tie-break order, and it is deliberate.</b> When two senses report
///         the same entity in one pass the earlier one wins, so a target that is both seen and heard
///         is recorded as seen — which is what "last known location" has to mean, because sight knows
///         where something is and hearing knows only where a noise was.
///     </para>
/// </remarks>
public enum AiSense : byte {
    /// <summary>Saw it: inside a radius, inside a cone, and with nothing solid in the way.</summary>
    Sight,

    /// <summary>Heard it: a noise somebody made, inside a radius, through anything.</summary>
    Hearing,

    /// <summary>Was hurt by it. Reported by whatever applies damage; there is no radius.</summary>
    Damage,

    /// <summary>Touched it, or was touched by it.</summary>
    Touch,

    /// <summary>Was told about it by an ally who did one of the above.</summary>
    Team
}

/// <summary>Which senses something is visible to, or which a listener runs.</summary>
/// <remarks>
///     A mask rather than five bools, because it is the thing a broad phase filters on and both ends
///     of a perception test carry one: a source says "I can be seen and heard but not felt", a
///     listener says "I see and I hear", and the pair is one <c>and</c> away from an answer.
/// </remarks>
[Flags]
public enum SenseMask : byte {
    /// <summary>Nothing. A source with this is invisible to everything, which is how a disguise works.</summary>
    None = 0,

    /// <summary><see cref="AiSense.Sight" />.</summary>
    Sight = 1 << 0,

    /// <summary><see cref="AiSense.Hearing" />.</summary>
    Hearing = 1 << 1,

    /// <summary><see cref="AiSense.Damage" />.</summary>
    Damage = 1 << 2,

    /// <summary><see cref="AiSense.Touch" />.</summary>
    Touch = 1 << 3,

    /// <summary><see cref="AiSense.Team" />.</summary>
    Team = 1 << 4,

    /// <summary>All five.</summary>
    All = Sight | Hearing | Damage | Touch | Team
}

/// <summary>Turning a sense into its mask bit and back.</summary>
public static class Senses {
    /// <summary>How many there are.</summary>
    public const int Count = 5;

    /// <summary>The mask bit for a sense.</summary>
    /// <param name="sense">The sense.</param>
    /// <returns>Its bit.</returns>
    public static SenseMask Bit(AiSense sense) => (SenseMask)(1 << (int)sense);

    /// <summary>Whether a mask carries a sense.</summary>
    /// <param name="mask">The mask.</param>
    /// <param name="sense">The sense.</param>
    /// <returns>Whether it is in there.</returns>
    public static bool Has(this SenseMask mask, AiSense sense) => (mask & Bit(sense)) != 0;
}
