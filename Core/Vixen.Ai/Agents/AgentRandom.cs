// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>Random numbers keyed on the agent, so a replay makes the same choice.</summary>
/// <remarks>
///     <para>
///         <b>Stateless, and keyed on identity rather than on a slot.</b> A weighted-random selector
///         reading <c>Random.Shared</c> is a desync per NPC per second: two machines running the same
///         build would draw in whatever order their threads happened to reach the agents, and the
///         choices would diverge within a frame. A value here is a pure function of <i>who is
///         asking, on which stream, for what</i>, so any thread can compute any agent's number at any
///         time without having computed anybody else's first.
///     </para>
///     <para>
///         ⚠ <b>The salt is not optional.</b> Two uses of randomness on one agent must not agree with
///         each other, or a <c>RandomSelector</c> and a service's random deviation would draw the
///         same number and every agent that took the first branch would also tick early — a
///         correlation that looks like behaviour and is not. The salt is what the number is
///         <i>for</i>: a node's execution index, an action's index, a named constant.
///     </para>
///     <para>
///         The mixing function is <c>VfxRandom</c>'s — Chris Wellons' <c>lowbias32</c>, three
///         multiplies and three xor-shifts, no avalanche anomalies — and it is copied rather than
///         shared because the two live in sibling assemblies and neither may reference the other.
///         When a third caller wants it, the hash is what moves to <c>Vixen.Core</c>, not this type:
///         a particle's stream is keyed on an identifier and a graph operation, an agent's on an
///         entity and a node, and the two contracts are not the same one.
///     </para>
/// </remarks>
public static class AgentRandom {
    /// <summary>A stream seed for an entity, stable across runs and machines.</summary>
    /// <param name="entity">The agent.</param>
    /// <returns>Its seed.</returns>
    /// <remarks>
    ///     Deliberately <i>not</i> the entity id on its own: consecutive ids would give agents spawned
    ///     together consecutive seeds, and a hash of a small integer run through one more round is
    ///     what makes a wave of guards look like a crowd rather than a sequence.
    /// </remarks>
    public static uint SeedOf(Entity entity) => Hash((uint)entity.Id ^ ((uint)entity.Version << 16));

    /// <summary>Mixes one integer.</summary>
    /// <param name="value">The input.</param>
    /// <returns>Its hash.</returns>
    public static uint Hash(uint value) {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        value ^= value >> 16;

        return value;
    }

    /// <summary>Mixes an agent, a stream and a use.</summary>
    /// <param name="entity">The agent.</param>
    /// <param name="seed">Its stream.</param>
    /// <param name="salt">What the number is for.</param>
    /// <returns>The hash.</returns>
    /// <remarks>
    ///     ⚠ <b>The entity and the seed are combined with <c>+</c> and not with <c>^</c>, and that is
    ///     not a stylistic difference.</b> The seed a caller has is almost always
    ///     <see cref="SeedOf" />'s, which is <c>Hash(id)</c> for a freshly created entity — so
    ///     <c>Hash(id) ^ seed</c> is <c>Hash(id) ^ Hash(id)</c>, which is <b>zero for every agent in
    ///     the world</b>. Every guard then drew the same number from its supposedly private stream: one
    ///     shuffled selector picked the same child in a thousand agents, and a jittered interval put
    ///     the whole population on one frame. P3 found it by spreading forty listeners across ten
    ///     frames and watching all forty land on frame five.
    /// </remarks>
    public static uint Hash(Entity entity, uint seed, uint salt) =>
        Hash(Hash(Hash((uint)entity.Id) + seed) ^ salt);

    /// <summary>A number in <c>[0,1)</c>.</summary>
    /// <param name="entity">The agent.</param>
    /// <param name="seed">Its stream.</param>
    /// <param name="salt">What the number is for.</param>
    /// <returns>The number.</returns>
    public static float Value(Entity entity, uint seed, uint salt) => ToFloat(Hash(entity, seed, salt));

    /// <summary>A number in a range.</summary>
    /// <param name="entity">The agent.</param>
    /// <param name="seed">Its stream.</param>
    /// <param name="salt">What the number is for.</param>
    /// <param name="minimum">The bottom.</param>
    /// <param name="maximum">The top, exclusive.</param>
    /// <returns>The number.</returns>
    public static float Range(Entity entity, uint seed, uint salt, float minimum, float maximum) =>
        minimum + ((maximum - minimum) * Value(entity, seed, salt));

    /// <summary>An index into a set.</summary>
    /// <param name="entity">The agent.</param>
    /// <param name="seed">Its stream.</param>
    /// <param name="salt">What the number is for.</param>
    /// <param name="count">How many there are.</param>
    /// <returns>An index in <c>[0, count)</c>, or <c>-1</c> when there are none.</returns>
    public static int Index(Entity entity, uint seed, uint salt, int count) =>
        count <= 0 ? -1 : (int)(Hash(entity, seed, salt) % (uint)count);

    /// <summary>
    ///     The top 24 bits as a float in <c>[0,1)</c>, which is exact in both directions.
    /// </summary>
    static float ToFloat(uint value) => (value >> 8) * (1f / 16777216f);
}
