// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Shooting;

/// <summary>Why a shot did not happen.</summary>
public enum FireFailure {
    /// <summary>It did.</summary>
    None = 0,

    /// <summary>Not enough time has passed since the last one.</summary>
    TooSoon,

    /// <summary>The magazine is empty.</summary>
    Empty,

    /// <summary>It is being reloaded.</summary>
    Reloading,

    /// <summary>The trigger is held on a weapon that needs releasing.</summary>
    NotAutomatic,

    /// <summary>The burst it is in has finished and the trigger has not been released.</summary>
    BurstFinished
}

/// <summary>One shot that happened.</summary>
/// <param name="Shot">Which shot this holder has fired, counting from one. Part of every pellet's seed.</param>
/// <param name="Spread">The cone's half-angle when it was fired, in degrees.</param>
/// <param name="Recoil">Where the weapon was kicked to, cumulative.</param>
/// <param name="Pellets">How many pellets it fired.</param>
public readonly record struct ShotFired(uint Shot, float Spread, ShotDirection Recoil, int Pellets);

/// <summary>What one holder's weapon is doing: ammunition, heat, kick, and whether it can fire.</summary>
/// <remarks>
///     <para>
///         <b>Deterministic and side-effect free about geometry.</b> Firing produces a shot number and
///         a cone width; where the pellets went is <see cref="WeaponTemplate.Deviate" />, a pure
///         function both ends compute. That is what makes the server's check of a client's claim
///         possible at all — it recomputes the cone rather than believing one.
///     </para>
///     <para>
///         ⚠ <b>Spread and recoil are separate, and keeping them apart is the design.</b> Recoil is a
///         learnable pattern — the tenth bullet goes where the tenth bullet always goes — and spread
///         is the randomness. A game that folds them together can tune "hard to control" and
///         "unpredictable" only together, which is why every weapon in such a game feels the same.
///     </para>
/// </remarks>
public sealed class WeaponState {
    float sinceLastShot;
    float sinceRecoil;
    float reloadRemaining;
    int burstFired;
    bool triggerHeld;

    /// <summary>Makes a state for a weapon, with a full magazine.</summary>
    /// <param name="weapon">Which weapon.</param>
    public WeaponState(WeaponTemplate weapon) {
        ArgumentNullException.ThrowIfNull(weapon);

        Weapon = weapon;
        Magazine = weapon.Magazine;
        Reserve = weapon.Definition.Reserve;
        sinceLastShot = float.MaxValue;
    }

    /// <summary>Which weapon.</summary>
    public WeaponTemplate Weapon { get; }

    /// <summary>How many rounds are in it.</summary>
    public int Magazine { get; private set; }

    /// <summary>How many are carried beyond it. Zero when the weapon carries unlimited spares.</summary>
    public int Reserve { get; private set; }

    /// <summary>How many shots this holder has fired with it. Part of every pellet's seed.</summary>
    public uint Shots { get; private set; }

    /// <summary>Whether it is being reloaded.</summary>
    public bool IsReloading => reloadRemaining > 0f;

    /// <summary>How much of the reload is left, in seconds.</summary>
    public float ReloadRemaining => reloadRemaining;

    /// <summary>Where the weapon has been kicked to, cumulative and in degrees.</summary>
    public ShotDirection Recoil { get; private set; }

    /// <summary>How wide the cone is right now, in degrees.</summary>
    public float Spread { get; private set; }

    /// <summary>Whether the holder is moving. Widens the cone.</summary>
    public bool IsMoving { get; set; }

    /// <summary>Whether the holder is aiming down the sights. Narrows it.</summary>
    public bool IsAiming { get; set; }

    /// <summary>The cone the next shot would use, with the movement and aim multipliers applied.</summary>
    public float EffectiveSpread {
        get {
            if (Weapon.Definition.Spread is not { } spread) {
                return 0f;
            }

            var cone = spread.Base + Spread;

            if (IsMoving) {
                cone *= MathF.Max(0f, spread.MovingMultiplier);
            }

            if (IsAiming) {
                cone *= MathF.Max(0f, spread.AimingMultiplier);
            }

            return spread.Maximum > 0f ? MathF.Min(cone, spread.Maximum) : cone;
        }
    }

    /// <summary>Whether a shot could happen right now.</summary>
    /// <param name="triggerDown">Whether the trigger is being held.</param>
    /// <returns>Why not, or <see cref="FireFailure.None" />.</returns>
    public FireFailure CanFire(bool triggerDown = true) {
        if (IsReloading) {
            return FireFailure.Reloading;
        }

        if (Magazine <= 0) {
            return FireFailure.Empty;
        }

        if (sinceLastShot < Weapon.FireInterval) {
            return FireFailure.TooSoon;
        }

        if (!triggerHeld || !triggerDown) {
            return FireFailure.None;
        }

        if (Weapon.Definition.Burst > 1) {
            return burstFired >= Weapon.Definition.Burst ? FireFailure.BurstFinished : FireFailure.None;
        }

        return Weapon.Definition.Automatic ? FireFailure.None : FireFailure.NotAutomatic;
    }

    /// <summary>Fires one shot.</summary>
    /// <param name="shot">What happened, when it did.</param>
    /// <param name="triggerDown">Whether the trigger is being held.</param>
    /// <returns>Why not, or <see cref="FireFailure.None" />.</returns>
    public FireFailure TryFire(out ShotFired shot, bool triggerDown = true) {
        shot = default;

        var failure = CanFire(triggerDown);

        if (failure != FireFailure.None) {
            triggerHeld |= triggerDown;

            return failure;
        }

        Shots++;
        Magazine--;
        sinceLastShot = 0f;
        sinceRecoil = 0f;
        burstFired = triggerHeld ? burstFired + 1 : 1;
        triggerHeld = triggerDown;

        var cone = EffectiveSpread;

        if (Weapon.Definition.Spread is { } spread) {
            Spread += MathF.Max(0f, spread.PerShot);
        }

        Kick();

        shot = new(Shots, cone, Recoil, Weapon.Pellets);

        return FireFailure.None;
    }

    /// <summary>Says the trigger was let go, which is what ends a burst and a semi-automatic's shot.</summary>
    public void ReleaseTrigger() {
        triggerHeld = false;
        burstFired = 0;
    }

    /// <summary>Starts a reload.</summary>
    /// <returns>Whether one started.</returns>
    /// <remarks>
    ///     Refused when the magazine is already full or nothing is carried, so a caller can bind it to
    ///     a key without checking.
    /// </remarks>
    public bool BeginReload() {
        if (IsReloading || Magazine >= Weapon.Magazine) {
            return false;
        }

        var unlimited = Weapon.Definition.Reserve <= 0;

        if (!unlimited && Reserve <= 0) {
            return false;
        }

        reloadRemaining = Weapon.Definition.ReloadsPerRound
            ? Weapon.ReloadTime / Weapon.Magazine
            : Weapon.ReloadTime;

        return true;
    }

    /// <summary>Stops a reload where it is.</summary>
    /// <returns>Whether one was running.</returns>
    /// <remarks>
    ///     ⚠ <b>A per-round reload keeps the rounds it has already loaded.</b> That is the whole
    ///     mechanic — a shotgun cut short mid-reload is a shotgun with some shells in it — and a
    ///     cancel that discarded them would make the mechanic pointless.
    /// </remarks>
    public bool CancelReload() {
        if (!IsReloading) {
            return false;
        }

        reloadRemaining = 0f;

        return true;
    }

    /// <summary>Advances the fire interval, the reload, the spread's recovery and the recoil's.</summary>
    /// <param name="delta">How much time passed, in seconds.</param>
    public void Tick(float delta) {
        if (delta <= 0f) {
            return;
        }

        sinceLastShot = sinceLastShot > float.MaxValue - delta ? float.MaxValue : sinceLastShot + delta;
        sinceRecoil += delta;

        if (reloadRemaining > 0f) {
            reloadRemaining -= delta;

            if (reloadRemaining <= 0f) {
                reloadRemaining = 0f;
                Load();
            }
        }

        if (Weapon.Definition.Spread is { } spread && Spread > 0f) {
            Spread = MathF.Max(0f, Spread - (MathF.Max(0f, spread.Recovery) * delta));
        }

        if (Weapon.Definition.Recoil is not { } recoil || sinceRecoil < recoil.RecoveryDelay) {
            return;
        }

        var recovered = MathF.Max(0f, recoil.Recovery) * delta;

        Recoil = new(Approach(Recoil.Pitch, recovered), Approach(Recoil.Yaw, recovered));
    }

    /// <summary>Puts everything back: full magazine, no kick, no spread. What a respawn does.</summary>
    public void Refill() {
        Magazine = Weapon.Magazine;
        Reserve = Weapon.Definition.Reserve;
        Spread = 0f;
        Recoil = default;
        reloadRemaining = 0f;
        burstFired = 0;
        triggerHeld = false;
        sinceLastShot = float.MaxValue;
    }

    void Load() {
        var unlimited = Weapon.Definition.Reserve <= 0;
        var wanted = Weapon.Definition.ReloadsPerRound ? 1 : Weapon.Magazine - Magazine;
        var taken = unlimited ? wanted : Math.Min(wanted, Reserve);

        Magazine += taken;

        if (!unlimited) {
            Reserve -= taken;
        }

        // A per-round reload carries on by itself until the magazine is full or the trigger, a
        // sprint or a weapon swap cancels it — which is what makes cancelling it a decision.
        if (Weapon.Definition.ReloadsPerRound && Magazine < Weapon.Magazine && (unlimited || Reserve > 0)) {
            reloadRemaining = Weapon.ReloadTime / Weapon.Magazine;
        }
    }

    void Kick() {
        if (Weapon.RecoilPattern.Length == 0) {
            return;
        }

        // The last step repeats, so a pattern shorter than a magazine does not wrap round to the
        // first — a wrap would make the twentieth bullet go where the first one did, which reads as
        // the weapon resetting itself mid-burst.
        var step = Weapon.RecoilPattern[Math.Min((int)Shots - 1, Weapon.RecoilPattern.Length - 1)];

        Recoil = new(Recoil.Pitch + step.Pitch, Recoil.Yaw + step.Yaw);
    }

    static float Approach(float value, float amount) =>
        value > 0f ? MathF.Max(0f, value - amount) : MathF.Min(0f, value + amount);
}
