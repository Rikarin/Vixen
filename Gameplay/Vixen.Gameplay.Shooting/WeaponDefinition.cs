// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Gameplay.Combat;

namespace Vixen.Gameplay.Shooting;

/// <summary>How a weapon's shot reaches what it hits.</summary>
public enum WeaponKind {
    /// <summary>It arrives the instant it is fired, and the server rewinds to check.</summary>
    Hitscan,

    /// <summary>It travels, and what it hits is decided where and when it gets there.</summary>
    Projectile
}

/// <summary>How much a shot loses over distance.</summary>
/// <remarks>
///     ⚠ <b>Every member is settable</b>, for the YAML binder's reason — see
///     <see cref="ModifierDefinition" />.
/// </remarks>
[DataContract("WeaponFalloff")]
public sealed class FalloffDefinition {
    /// <summary>How far a shot does full damage, in metres.</summary>
    public float Start { get; set; }

    /// <summary>How far it does <see cref="Minimum" /> of it. Zero disables falloff.</summary>
    public float End { get; set; }

    /// <summary>The fraction it never drops below.</summary>
    public float Minimum { get; set; } = 0.25f;
}

/// <summary>How wide a shot goes, and how that changes as somebody keeps firing.</summary>
[DataContract("WeaponSpread")]
public sealed class SpreadDefinition {
    /// <summary>The cone's half-angle when standing still and not firing, in degrees.</summary>
    public float Base { get; set; }

    /// <summary>How much each shot adds to it, in degrees.</summary>
    public float PerShot { get; set; }

    /// <summary>The widest it ever gets, in degrees.</summary>
    public float Maximum { get; set; }

    /// <summary>How fast it settles back, in degrees per second.</summary>
    public float Recovery { get; set; } = 10f;

    /// <summary>What moving multiplies it by.</summary>
    public float MovingMultiplier { get; set; } = 2f;

    /// <summary>What aiming down the sights multiplies it by.</summary>
    public float AimingMultiplier { get; set; } = 0.25f;
}

/// <summary>One step of a recoil pattern: where the shot after this one is aimed.</summary>
[DataContract("WeaponRecoilStep")]
public sealed class RecoilStepDefinition {
    /// <summary>How far up, in degrees.</summary>
    public float Pitch { get; set; }

    /// <summary>How far sideways, in degrees. Positive is right.</summary>
    public float Yaw { get; set; }
}

/// <summary>How a weapon climbs, and how it comes back down.</summary>
/// <remarks>
///     <b>A pattern rather than a random kick, because a pattern is learnable.</b> The thing that
///     makes a shooter's recoil feel fair is that the tenth bullet goes where the tenth bullet always
///     goes; randomness on top of it is <see cref="SpreadDefinition" />'s job, and keeping the two
///     apart is what lets a designer tune "hard to control" separately from "unpredictable".
/// </remarks>
[DataContract("WeaponRecoil")]
public sealed class RecoilDefinition {
    /// <summary>The pattern, one step per shot. The last step repeats once it runs out.</summary>
    public List<RecoilStepDefinition> Pattern { get; set; } = [];

    /// <summary>How fast the accumulated kick returns, in degrees per second.</summary>
    public float Recovery { get; set; } = 20f;

    /// <summary>How long after the last shot before recovery starts, in seconds.</summary>
    public float RecoveryDelay { get; set; } = 0.2f;
}

/// <summary>How far a shot carries on through what it hits.</summary>
[DataContract("WeaponPenetration")]
public sealed class PenetrationDefinition {
    /// <summary>How many things one shot may pass through. Zero stops at the first.</summary>
    public int MaximumTargets { get; set; }

    /// <summary>What fraction of the damage survives each thing it passes through.</summary>
    public float DamageFraction { get; set; } = 0.5f;
}

/// <summary>A weapon, as a designer wrote it.</summary>
/// <remarks>
///     <para>
///         Doc 28 § Shooting: hitscan and projectile weapons, spread and recoil patterns, ammunition
///         and reload state, penetration and falloff. What a hit <em>does</em> is
///         <c>Vixen.Gameplay.Combat</c>'s damage pipeline, because a headshot is a Crit-stage rule
///         and armour is a Mitigate one.
///     </para>
/// </remarks>
[DataContract("WeaponDefinition")]
public sealed record WeaponDefinition : Definition {
    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How its shot reaches what it hits.</summary>
    public WeaponKind Kind { get; set; }

    /// <summary>What one pellet does.</summary>
    public DamageDefinition? Damage { get; set; }

    /// <summary>How many pellets one shot fires. One is a rifle; eight is a shotgun.</summary>
    public int Pellets { get; set; } = 1;

    /// <summary>How many shots a second.</summary>
    public float RoundsPerSecond { get; set; } = 10f;

    /// <summary>Whether holding the trigger keeps firing.</summary>
    public bool Automatic { get; set; }

    /// <summary>How many shots one trigger pull fires. Zero or one is not a burst.</summary>
    public int Burst { get; set; }

    /// <summary>How far a shot reaches at all, in metres.</summary>
    public float Range { get; set; } = 100f;

    /// <summary>How fast a projectile travels, in metres per second. Ignored by a hitscan.</summary>
    public float ProjectileSpeed { get; set; } = 100f;

    /// <summary>How many rounds a magazine holds.</summary>
    public int Magazine { get; set; } = 30;

    /// <summary>How many rounds are carried beyond the magazine. Zero is unlimited.</summary>
    public int Reserve { get; set; }

    /// <summary>How long a reload takes, in seconds.</summary>
    public float ReloadTime { get; set; } = 2f;

    /// <summary>Whether a reload puts rounds in one at a time, so it can be cut short.</summary>
    public bool ReloadsPerRound { get; set; }

    /// <summary>How much it loses over distance, or null for none.</summary>
    public FalloffDefinition? Falloff { get; set; }

    /// <summary>How wide it goes, or null for perfectly accurate.</summary>
    public SpreadDefinition? Spread { get; set; }

    /// <summary>How it climbs, or null for none.</summary>
    public RecoilDefinition? Recoil { get; set; }

    /// <summary>How far it carries on, or null to stop at the first thing.</summary>
    public PenetrationDefinition? Penetration { get; set; }

    /// <summary>What this weapon is — <c>Weapon.Rifle.Assault</c>.</summary>
    public List<string> Tags { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var tag in Tags) {
            tags.Add(tag);
        }

        if (Damage is { School.Length: > 0 }) {
            tags.Add(Damage.School);
        }
    }
}
