---
title: Weapons and hit claims
slug: gameplay/shooting
kind: guide
area: Gameplay
summary: The weapon model and the claim's validation rules — the two things that are new, because the lag compensation underneath is already built.
api: [T:Vixen.Gameplay.Shooting.WeaponDefinition, T:Vixen.Gameplay.Shooting.WeaponKind, T:Vixen.Gameplay.Shooting.FalloffDefinition, T:Vixen.Gameplay.Shooting.SpreadDefinition, T:Vixen.Gameplay.Shooting.RecoilDefinition, T:Vixen.Gameplay.Shooting.RecoilStepDefinition, T:Vixen.Gameplay.Shooting.PenetrationDefinition, T:Vixen.Gameplay.Shooting.WeaponTemplate, T:Vixen.Gameplay.Shooting.WeaponLibrary, T:Vixen.Gameplay.Shooting.ShotDirection, T:Vixen.Gameplay.Shooting.WeaponState, T:Vixen.Gameplay.Shooting.ShotFired, T:Vixen.Gameplay.Shooting.FireFailure, T:Vixen.Gameplay.Shooting.HitClaim, T:Vixen.Gameplay.Shooting.ClaimVerdict, T:Vixen.Gameplay.Shooting.ClaimRejection, T:Vixen.Gameplay.Shooting.ShotRecord, T:Vixen.Gameplay.Shooting.HitClaimValidator, T:Vixen.Gameplay.Shooting.RewindBudget, T:Vixen.Gameplay.Shooting.ShootingModule]
tags: [gameplay, shooting, weapons, fps, lag-compensation, anti-cheat]
since: 0.1
status: preview
related: [gameplay/combat, gameplay/randomness, gameplay/effects]
---

## What it is

A **weapon definition** is a rate, a magazine, a reload, a cone, a recoil pattern, a falloff curve and
a penetration rule. A **`WeaponState`** is one holder's copy of that — how many rounds are in it, how
wide the cone is right now, where the kick has taken it. A **`HitClaimValidator`** is the server side:
it decides whether what a client says its bullet did adds up.

## What it is for

Shooting, with server authority, over a connection that has latency. Doc 28's hit path is:

```
client fires → predicted locally (Vixen.Net.Prediction)
             → hit claim RPC with the client's tick
             → server rewinds colliders to that tick (Vixen.Net.Physics ColliderRollback)
             → validates
             → applies through the damage pipeline
```

Every arrow but one is already built. What this library is, is the weapon model and the **validation**
— and the budget that stops a rewound claim costing a rate limiter the same as an ordinary call.

## Using it

Give each holder a `WeaponState`, tick it, and fire it. Record each shot with the validator so the
claims that follow have something to be checked against. Feed accepted claims into
[`Vixen.Gameplay.Combat`](gameplay/combat)'s damage pipeline.

⚠ **`WeaponTemplate.Deviate` is a pure function of (shot, pellet, spread).** Both ends compute the
same cone, which is what makes a claim checkable at all: the server recomputes where the pellets
could have gone rather than believing a direction it was sent.

⚠ **The claim carries no penetration count.** The server counts the pellet's prior accepted claims
itself — a client that could say "penetrated nothing" would get full damage on every target it named.

⚠ **The pellet-uniqueness rule is the important one.** Without it a client hits one target and reports
the same pellet against forty, and every other check passes for each.

⚠ **This library never asks where anything is.** `ShotDirection` is two angles rather than a vector,
because there is no basis here to express one in; the caller applies them to its own aim.

⚠ **A rejection is not a cheat.** Most happen honestly — a claim outlives its shot, two claims race a
kill, a packet is lost. What to do about a *rate* of them is a realm's policy.

## Examples

A weapon:

```yaml
# Assets/Weapons/assault-rifle.vxdef
!WeaponDefinition
displayName: Assault Rifle
kind: Hitscan
damage: { school: Damage.Ballistic, amount: 25 }
roundsPerSecond: 10
automatic: true
range: 100
magazine: 30
reserve: 90
reloadTime: 2
falloff: { start: 30, end: 80, minimum: 0.5 }
spread: { base: 0.5, perShot: 0.4, maximum: 4, recovery: 2, movingMultiplier: 2, aimingMultiplier: 0.5 }
recoil:
  recovery: 20
  recoveryDelay: 0.2
  pattern:
    - { pitch: 1.0, yaw: 0.0 }
    - { pitch: 1.2, yaw: 0.3 }
    - { pitch: 1.4, yaw: -0.4 }
tags: [ Weapon.Rifle.Assault ]
```

Firing it, on either end:

```csharp compile
using System;
using System.Collections.Generic;
using Vixen.Gameplay.Shooting;

static class Trigger {
    public static IReadOnlyList<ShotDirection> Pull(WeaponState weapon, bool triggerDown) {
        if (weapon.TryFire(out var shot, triggerDown) != FireFailure.None) {
            return Array.Empty<ShotDirection>();
        }

        var pellets = new ShotDirection[shot.Pellets];

        for (var pellet = 0; pellet < pellets.Length; pellet++) {
            // The same call the server makes when it checks the claim.
            pellets[pellet] = WeaponTemplate.Deviate(shot.Shot, pellet, shot.Spread);
        }

        return pellets;
    }
}
```

Checking a claim, on the realm:

```csharp compile
using Vixen.Gameplay.Shooting;

static class Authority {
    public static ClaimVerdict Check(HitClaimValidator validator, in HitClaim claim, int tick, bool clearLine) =>
        // The trace is the caller's — this library has no scene. What it decides is whether the
        // claim's own numbers add up, and whether the shooter can afford the rewind.
        validator.Validate(claim, tick, clearLine);
}
```

The budget doc 16 asked for:

```csharp compile
using Vixen.Gameplay.Shooting;

static class Rewinds {
    // Charged per tick of rewind, so the price tracks the work: rolling back two ticks is cheap and
    // rolling back thirty is not.
    public static HitClaimValidator Guarded(WeaponLibrary weapons) =>
        new(weapons, window: 30) {
            Budget = new(capacity: 120f, refillPerSecond: 60f, costPerTick: 1f, minimumCost: 1f)
        };
}
```

## See also

- [Abilities and damage](gameplay/combat) — what an accepted claim is applied through.
- [Gameplay randomness](gameplay/randomness) — the stream a cone is drawn from, and why it is pure.
- [Effects](gameplay/effects) — what a weapon applies beyond damage.
