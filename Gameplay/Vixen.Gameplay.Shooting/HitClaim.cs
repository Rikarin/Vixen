// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Shooting;

/// <summary>Why a hit claim was not believed.</summary>
/// <remarks>
///     ⚠ <b>A rejection is not the same as a cheat.</b> Most of these happen honestly — a claim
///     arrives after its shot has aged out, two claims race a kill, a client's pellet count and the
///     server's disagree by one because a packet was lost. What a realm does about a <em>rate</em> of
///     rejections is its own policy; what this reports is which rule the claim broke.
/// </remarks>
public enum ClaimRejection {
    /// <summary>It was believed.</summary>
    None = 0,

    /// <summary>The shooter never fired that shot, or it has aged out of the window.</summary>
    NoSuchShot,

    /// <summary>That pellet of that shot has already been claimed.</summary>
    AlreadyClaimed,

    /// <summary>The tick is outside the window the server is willing to rewind to.</summary>
    OutsideWindow,

    /// <summary>Further than the weapon reaches.</summary>
    OutOfRange,

    /// <summary>The server's own trace says there was a wall in the way.</summary>
    NoLineOfSight,

    /// <summary>Through more things than the weapon can penetrate.</summary>
    TooManyPenetrations,

    /// <summary>Outside the cone the shot could possibly have gone in.</summary>
    OutsideCone,

    /// <summary>The shooter has spent this window's rewind budget.</summary>
    BudgetExhausted
}

/// <summary>What a client says one of its pellets hit.</summary>
/// <param name="Shooter">Who fired, as the caller numbers them.</param>
/// <param name="Shot">Which of their shots.</param>
/// <param name="Pellet">Which pellet of it.</param>
/// <param name="Target">What they say it hit.</param>
/// <param name="Tick">The tick they were seeing when they fired.</param>
/// <param name="Distance">How far away the target was, in metres.</param>
/// <param name="Deviation">How far off the aim the pellet was, in degrees, as the client computed it.</param>
/// <remarks>
///     ⚠ <b>There is deliberately no "how many things it went through" on here.</b> A client that
///     could say its bullet had penetrated nothing would get full damage on every target it named;
///     the server counts the pellet's prior accepted claims itself, which is a number it already has
///     and a client cannot touch.
/// </remarks>
public readonly record struct HitClaim(
    ulong Shooter,
    uint Shot,
    int Pellet,
    ulong Target,
    int Tick,
    float Distance,
    float Deviation
);

/// <summary>What the server decided about a claim.</summary>
/// <param name="Rejection">Why not, or <see cref="ClaimRejection.None" />.</param>
/// <param name="Damage">What the pellet is worth, after falloff and penetration.</param>
/// <param name="RewindTicks">How far back the server had to look. What the budget is spent on.</param>
public readonly record struct ClaimVerdict(ClaimRejection Rejection, float Damage, int RewindTicks) {
    /// <summary>Whether it was believed.</summary>
    public bool Accepted => Rejection == ClaimRejection.None;
}

/// <summary>What the server knows about one shot it has been told about.</summary>
/// <param name="Shot">Which shot.</param>
/// <param name="Weapon">Which weapon fired it.</param>
/// <param name="Tick">The tick the server had when it was fired.</param>
/// <param name="Spread">The cone it went in, in degrees.</param>
/// <param name="Pellets">How many pellets it fired.</param>
public readonly record struct ShotRecord(uint Shot, DefId Weapon, int Tick, float Spread, int Pellets);

/// <summary>
///     Checks a client's hit claim against what the server knows, and against what the shot could
///     possibly have done.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 28 § Shooting's hit path, minus the networking.</b> The prediction, the claim RPC
///         and the collider rewind are all doc 16's and all built; what is new is the weapon model
///         and <em>the claim's validation rules</em>, which are arithmetic over numbers the caller
///         supplies. Line of sight and the rewind itself are the caller's — this is handed the
///         answers and decides whether they add up.
///     </para>
///     <para>
///         ⚠ <b>What it can prove and what it cannot, stated plainly.</b> It can prove a claim names
///         a shot that happened, that no pellet is claimed twice, that the tick is inside the window,
///         that the distance is inside the weapon's range, and that the deviation is inside the cone
///         the shot's own seed produces. It <em>cannot</em> prove the target was there — that is the
///         rewind, and the rewind is the expensive part, which is why the budget exists.
///     </para>
/// </remarks>
public sealed class HitClaimValidator {
    readonly Dictionary<ulong, Shooter> shooters = [];

    /// <summary>Makes a validator.</summary>
    /// <param name="weapons">Where weapon templates come from.</param>
    /// <param name="window">How many ticks back a claim may reach.</param>
    /// <param name="history">How many shots per shooter are remembered.</param>
    public HitClaimValidator(WeaponLibrary weapons, int window = 30, int history = 64) {
        ArgumentNullException.ThrowIfNull(weapons);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(window);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(history);

        Weapons = weapons;
        Window = window;
        History = history;
    }

    /// <summary>Where weapon templates come from.</summary>
    public WeaponLibrary Weapons { get; }

    /// <summary>How many ticks back a claim may reach.</summary>
    /// <remarks>
    ///     ⚠ <b>The window is a latency budget, not a fairness setting.</b> A wide one lets a player
    ///     on a bad connection hit what they saw; it also lets them hit somebody who has been behind
    ///     cover for a third of a second on everyone else's screen. Half a second at sixty ticks is
    ///     where most shooters land, and the number belongs in a game's configuration rather than in
    ///     this library, which is why it is a constructor parameter with a defensible default.
    /// </remarks>
    public int Window { get; }

    /// <summary>How many shots per shooter are remembered.</summary>
    public int History { get; }

    /// <summary>How much a claim's rewind costs, or null for unlimited.</summary>
    /// <remarks>
    ///     <b>Doc 16's owed "cost budget for rewinds", and doc 28 § Shooting says this library is the
    ///     reason to close it.</b> A rewound claim costs a physics scene rolled back and re-traced;
    ///     an ordinary RPC costs a dictionary lookup. A rate limiter that counts them the same lets a
    ///     client spend a server's frame budget with a packet flood that is, per packet, entirely
    ///     within its rate.
    /// </remarks>
    public RewindBudget? Budget { get; init; }

    /// <summary>Records a shot the server has accepted, so its claims can be checked against it.</summary>
    /// <param name="shooter">Who fired.</param>
    /// <param name="weapon">Which weapon.</param>
    /// <param name="shot">What <see cref="WeaponState.TryFire" /> reported.</param>
    /// <param name="tick">The server's tick when it was fired.</param>
    public void RecordShot(ulong shooter, DefId weapon, in ShotFired shot, int tick) {
        if (!shooters.TryGetValue(shooter, out var state)) {
            state = new(History);
            shooters.Add(shooter, state);
        }

        state.Record(new(shot.Shot, weapon, tick, shot.Spread, shot.Pellets));
    }

    /// <summary>Checks a claim.</summary>
    /// <param name="claim">What the client says happened.</param>
    /// <param name="tick">The server's tick now.</param>
    /// <param name="lineOfSight">Whether the server's own trace found a clear line. The caller does the trace.</param>
    /// <returns>What the server decided.</returns>
    /// <remarks>
    ///     ⚠ <b>An accepted claim consumes the pellet, so the same pellet cannot be claimed twice.</b>
    ///     Without that, a client that hits one target reports the same pellet against forty of them
    ///     and every check above passes for each — the shot happened, the tick is fine, the cone is
    ///     fine. It is the single cheapest and most valuable rule here.
    /// </remarks>
    public ClaimVerdict Validate(in HitClaim claim, int tick, bool lineOfSight = true) {
        if (!shooters.TryGetValue(claim.Shooter, out var state) || state.Find(claim.Shot) is not { } record) {
            return new(ClaimRejection.NoSuchShot, 0f, 0);
        }

        if (Weapons.Find(record.Weapon) is not { } weapon) {
            return new(ClaimRejection.NoSuchShot, 0f, 0);
        }

        if (claim.Pellet < 0 || claim.Pellet >= record.Pellets) {
            return new(ClaimRejection.NoSuchShot, 0f, 0);
        }

        var rewind = tick - claim.Tick;

        if (rewind < 0 || rewind > Window) {
            return new(ClaimRejection.OutsideWindow, 0f, rewind);
        }

        if (state.IsClaimed(claim.Shot, claim.Pellet, claim.Target)) {
            return new(ClaimRejection.AlreadyClaimed, 0f, rewind);
        }

        if (claim.Distance < 0f || claim.Distance > weapon.Range) {
            return new(ClaimRejection.OutOfRange, 0f, rewind);
        }

        // The server's own ledger, not the client's word: how many things this pellet has already
        // been believed to pass through.
        var order = state.ClaimsOf(claim.Shot, claim.Pellet);

        if (order > weapon.MaximumPenetrations) {
            return new(ClaimRejection.TooManyPenetrations, 0f, rewind);
        }

        // The cone the shot's own seed produced, recomputed rather than believed. A tenth of a degree
        // of slack, because the client computed it in single precision on another machine.
        var deviation = WeaponTemplate.Deviate(claim.Shot, claim.Pellet, record.Spread);
        var possible = MathF.Sqrt((deviation.Pitch * deviation.Pitch) + (deviation.Yaw * deviation.Yaw));

        if (claim.Deviation > possible + 0.1f) {
            return new(ClaimRejection.OutsideCone, 0f, rewind);
        }

        if (!lineOfSight) {
            return new(ClaimRejection.NoLineOfSight, 0f, rewind);
        }

        if (Budget is { } budget && !budget.TryConsume(claim.Shooter, rewind)) {
            return new(ClaimRejection.BudgetExhausted, 0f, rewind);
        }

        state.Claim(claim.Shot, claim.Pellet, claim.Target);

        return new(ClaimRejection.None, weapon.DamageAfter(claim.Distance, order), rewind);
    }

    /// <summary>Forgets a shooter — they left, they died, they respawned.</summary>
    /// <param name="shooter">Who.</param>
    /// <returns>Whether anything was remembered about them.</returns>
    public bool Forget(ulong shooter) => shooters.Remove(shooter);

    /// <summary>One shooter's recent shots, and which of their pellets have been spent.</summary>
    sealed class Shooter(int history) {
        readonly Queue<ShotRecord> shots = new(history);
        readonly HashSet<(uint Shot, int Pellet, ulong Target)> claimed = [];

        public void Record(in ShotRecord record) {
            shots.Enqueue(record);

            while (shots.Count > history) {
                var dropped = shots.Dequeue();

                // ⚠ The claims go with the shot they belong to. A set that only ever grew would be a
                // per-connection memory leak that a client can drive, which is the shape of every
                // "the server ran out of memory during a long match" bug.
                claimed.RemoveWhere(entry => entry.Shot == dropped.Shot);
            }
        }

        public ShotRecord? Find(uint shot) {
            foreach (var record in shots) {
                if (record.Shot == shot) {
                    return record;
                }
            }

            return null;
        }

        public bool IsClaimed(uint shot, int pellet, ulong target) => claimed.Contains((shot, pellet, target));

        /// <summary>How many things this pellet has already been believed to hit.</summary>
        public int ClaimsOf(uint shot, int pellet) {
            var count = 0;

            foreach (var entry in claimed) {
                if (entry.Shot == shot && entry.Pellet == pellet) {
                    count++;
                }
            }

            return count;
        }

        public void Claim(uint shot, int pellet, ulong target) => claimed.Add((shot, pellet, target));
    }
}
