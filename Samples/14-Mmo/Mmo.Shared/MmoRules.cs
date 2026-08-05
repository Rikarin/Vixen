// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Combat;
using Vixen.Gameplay.Progression;
using Vixen.Samples.Mmo.Contracts;

namespace Vixen.Samples.Mmo.Rules;

/// <summary>What a player pressed. The client sends these; the realm replays them.</summary>
/// <param name="Forward">−1 to 1.</param>
/// <param name="Strafe">−1 to 1.</param>
/// <param name="Facing">Radians.</param>
/// <param name="Mounted">Whether they are on something, which changes the speed and nothing else.</param>
public readonly record struct MoveInput(float Forward, float Strafe, float Facing, bool Mounted);

/// <summary>The rules both ends run, and neither end owns.</summary>
/// <remarks>
///     <para>
///         <b>Doc 27 puts this in its own assembly for one reason:</b> <em>"this is where 'the client
///         predicts what the server will do' is made literal"</em>. Two implementations of one rule
///         drift, and doc 16's <c>MispredictionCount</c> is the number that catches the drift — one
///         assembly is what prevents it.
///     </para>
///     <para>
///         ⚠ <b>It is much smaller than that framing suggests, and the reason is the interesting
///         part.</b> Most of what a naive <c>.Shared</c> would hold is <em>already</em> shared,
///         because the gameplay libraries are linked by both ends: <c>AbilityTemplate.BaseAmount</c>
///         is the damage formula, <c>RequirementSet.IsMetBy</c> is what greys a button out and what
///         refuses a packet, and neither wants a wrapper here. Re-implementing them in this assembly
///         would create exactly the second implementation it exists to prevent.
///     </para>
///     <para>
///         What is left is the arithmetic the <em>game</em> owns — how fast a mount is, which
///         attribute a class spends — and it is left here because nothing in the engine could have
///         guessed it.
///     </para>
///     <para>
///         ⚠ <b>Deterministic and side-effect free, and it has to be.</b> The client runs this to
///         predict, the realm runs it to decide, and rollback runs it again over corrected state.
///         Anything read that was not passed in — a random number, the wall clock, a component the
///         client does not have — is a divergence the player feels as rubber-banding.
///     </para>
/// </remarks>
public static class MmoRules {
    /// <summary>Metres per second on foot.</summary>
    public const float WalkSpeed = 5f;

    /// <summary>What a class spends. Vanguards rage, Emberwrights mana, Marksmen focus.</summary>
    /// <remarks>
    ///     ⚠ <b>One wire field and three names, which is why this function exists.</b>
    ///     <c>Vitals.Resource</c> is a number; which resource it is follows from the specialisation
    ///     the receiver already has. Sending three fields would send two zeroes about every character
    ///     in view, every time any of them spent anything.
    /// </remarks>
    /// <param name="specialisation">Theirs.</param>
    /// <returns>The attribute, or <c>Mana</c> for somebody with no specialisation yet.</returns>
    public static AttributeId ResourceOf(DefId specialisation) =>
        specialisation == MmoAddresses.Vanguard ? Rage
        : specialisation == MmoAddresses.Marksman ? Focus
        : Mana;

    /// <summary>Rage.</summary>
    public static AttributeId Rage { get; } = AttributeId.From("Rage");

    /// <summary>Mana.</summary>
    public static AttributeId Mana { get; } = AttributeId.From("Mana");

    /// <summary>Focus.</summary>
    public static AttributeId Focus { get; } = AttributeId.From("Focus");

    /// <summary>Advances a pose by one step.</summary>
    /// <param name="pose">Where they were.</param>
    /// <param name="input">What they pressed.</param>
    /// <param name="mountSpeed">How fast the thing they are on is, from its <c>Vehicle</c>.</param>
    /// <param name="delta">How long the step is, in seconds.</param>
    /// <returns>Where they are.</returns>
    /// <remarks>
    ///     ⚠ <b>The mount's speed is passed in rather than looked up.</b> A step that read a library
    ///     would be a step whose answer depends on which build compiled the library, and a client one
    ///     patch behind would predict a different position for every mounted player on screen.
    /// </remarks>
    public static Pose Step(Pose pose, MoveInput input, float mountSpeed, float delta) {
        var speed = input.Mounted ? mountSpeed : WalkSpeed;
        var cos = MathF.Cos(input.Facing);
        var sin = MathF.Sin(input.Facing);

        return pose with {
            X = pose.X + (((input.Forward * cos) - (input.Strafe * sin)) * speed * delta),
            Z = pose.Z + (((input.Forward * sin) + (input.Strafe * cos)) * speed * delta),
            Facing = input.Facing
        };
    }

    /// <summary>What a character's wire vitals are, given their progression and their stats.</summary>
    /// <param name="state">Their progression.</param>
    /// <param name="subject">Their attributes, after every modifier.</param>
    /// <param name="health">What they currently have.</param>
    /// <param name="resource">What they currently have of it.</param>
    /// <returns>The struct to replicate.</returns>
    /// <remarks>
    ///     The client predicts its own bar with this and the realm fills the packet with it, which is
    ///     the whole reason it is one function. A health bar that jumps on every server correction is
    ///     two formulae disagreeing about the maximum.
    /// </remarks>
    public static Vitals VitalsOf(ProgressionState state, GameplaySubject subject, int health, int resource) {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(subject);

        // ⚠ TryGetValue rather than an indexer, because an attribute nothing has modified is
        // *absent* rather than zero — and a maximum of zero clamps the bar to empty, which reads as
        // a dead player rather than as a missing stat.
        var maximumHealth = subject.TryGetValue(MaximumHealth, out var pool) ? (int)pool : 0;
        var maximumResource = subject.TryGetValue(ResourceOf(state.Specialisation), out var value) ? (int)value : 0;

        return new() {
            Health = Math.Clamp(health, 0, maximumHealth),
            MaximumHealth = maximumHealth,
            Resource = Math.Clamp(resource, 0, maximumResource),
            MaximumResource = maximumResource
        };
    }

    /// <summary>What a character's pool of health is called.</summary>
    public static AttributeId MaximumHealth { get; } = AttributeId.From("MaximumHealth");
}
