// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Shooting;

/// <summary>How much rewinding one connection may cost the server, per second.</summary>
/// <remarks>
///     <para>
///         <b>Doc 16's owed "cost budget for rewinds", closed here because doc 28 § Shooting says
///         this library is the reason to close it.</b> A rewound hit claim costs a physics scene
///         rolled back and re-traced; an ordinary remote call costs a dictionary lookup. A rate
///         limiter that counts them the same lets a client spend a whole server frame with a flood
///         that is, packet for packet, entirely inside its rate.
///     </para>
///     <para>
///         ⚠ <b>A deeper rewind costs more, and that is the whole reason this is not just a second
///         rate limit.</b> Rolling back two ticks is cheap and rolling back thirty is not, so charging
///         per tick makes the price track the work. A player on a bad connection legitimately pays
///         more — which is correct, because they legitimately cost more — and the refill rate is
///         where a game decides how much of that it is willing to fund.
///     </para>
///     <para>
///         <b>Policy here, enforcement in the router.</b> This library has no networking and must not
///         grow any; what it owns is the arithmetic of what a claim costs. <c>RpcRouter</c>'s
///         per-connection limiter is what should consult it, which keeps one limiter rather than two
///         that disagree.
///     </para>
/// </remarks>
public sealed class RewindBudget {
    readonly Dictionary<ulong, float> remaining = [];

    /// <summary>Makes a budget.</summary>
    /// <param name="capacity">The most a connection may bank.</param>
    /// <param name="refillPerSecond">How fast it comes back.</param>
    /// <param name="costPerTick">What one tick of rewind costs.</param>
    /// <param name="minimumCost">What a claim costs even when it rewinds nothing.</param>
    public RewindBudget(
        float capacity = 120f,
        float refillPerSecond = 60f,
        float costPerTick = 1f,
        float minimumCost = 1f
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(refillPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(costPerTick);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumCost);

        Capacity = capacity;
        RefillPerSecond = refillPerSecond;
        CostPerTick = costPerTick;
        MinimumCost = minimumCost;
    }

    /// <summary>The most a connection may bank.</summary>
    public float Capacity { get; }

    /// <summary>How fast it comes back.</summary>
    public float RefillPerSecond { get; }

    /// <summary>What one tick of rewind costs.</summary>
    public float CostPerTick { get; }

    /// <summary>What a claim costs even when it rewinds nothing.</summary>
    public float MinimumCost { get; }

    /// <summary>What a rewind of this depth costs.</summary>
    /// <param name="rewindTicks">How far back.</param>
    /// <returns>The cost.</returns>
    public float CostOf(int rewindTicks) => MathF.Max(MinimumCost, Math.Max(0, rewindTicks) * CostPerTick);

    /// <summary>How much one connection has left.</summary>
    /// <param name="connection">Which one.</param>
    /// <returns>Its remaining budget.</returns>
    public float RemainingFor(ulong connection) =>
        remaining.TryGetValue(connection, out var left) ? left : Capacity;

    /// <summary>Spends a rewind's cost, if there is room for it.</summary>
    /// <param name="connection">Which connection.</param>
    /// <param name="rewindTicks">How far back it wants to look.</param>
    /// <returns>Whether it could be afforded.</returns>
    /// <remarks>
    ///     ⚠ <b>A refused claim costs nothing, deliberately.</b> Charging for the refusal would let a
    ///     flood of unaffordable claims keep a connection permanently broke, which turns a defence
    ///     against one client into a way for that client to disable its own hits — and, in a game
    ///     where anybody can spoof a shooter id, somebody else's.
    /// </remarks>
    public bool TryConsume(ulong connection, int rewindTicks) {
        var cost = CostOf(rewindTicks);
        var left = RemainingFor(connection);

        if (left < cost) {
            return false;
        }

        remaining[connection] = left - cost;

        return true;
    }

    /// <summary>Refills every connection.</summary>
    /// <param name="delta">How much time passed, in seconds.</param>
    public void Tick(float delta) {
        if (delta <= 0f || RefillPerSecond <= 0f) {
            return;
        }

        var refill = RefillPerSecond * delta;

        foreach (var connection in remaining.Keys.ToArray()) {
            var left = remaining[connection] + refill;

            if (left >= Capacity) {
                // Full is the same as never having spent anything, so the entry goes rather than
                // sitting at capacity for ever — a per-connection dictionary that only grows is the
                // leak this would otherwise be.
                remaining.Remove(connection);
            } else {
                remaining[connection] = left;
            }
        }
    }

    /// <summary>Forgets a connection — it left.</summary>
    /// <param name="connection">Which one.</param>
    /// <returns>Whether anything was remembered about it.</returns>
    public bool Forget(ulong connection) => remaining.Remove(connection);
}
