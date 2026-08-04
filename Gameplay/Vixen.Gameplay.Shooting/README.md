# Vixen.Gameplay.Shooting

The weapon model and the hit claim's validation rules — the two things doc 28 says are actually new,
because the networking underneath is doc 16's and already built.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Combat and shooting, the second half
of **G2**.

## State

**Built: weapons, spread, recoil, ammunition and reload, falloff, penetration, the claim validator
and the rewind budget. 39 tests.** That closes G2 and, with it, doc 16's owed *"cost budget for
rewinds"*.

| | |
|---|---|
| `WeaponDefinition` and its parts | Kind, damage, pellets, rate, burst, range, magazine, reserve, reload, falloff, spread, recoil, penetration. |
| `WeaponTemplate` · `WeaponLibrary` | The compiled forms, plus `DamageAt`, `DamageAfter` and `Deviate`. |
| `ShotDirection` | Two angles, not a vector — this library has no idea which way is up. |
| `WeaponState` · `ShotFired` · `FireFailure` | One holder's weapon: ammunition, kick, cone, reload, and whether it can fire. |
| `HitClaim` · `ClaimVerdict` · `ClaimRejection` · `HitClaimValidator` | What a client says happened, and whether it adds up. |
| `RewindBudget` | What a rewound claim costs, so a rate limiter can stop counting it as an ordinary call. |
| `ShootingModule` | One definition type and a tag root, over `CombatModule`. |

## The five things worth knowing before reading the code

### Nothing here is new networking

Doc 28's hit path is: client fires → predicted locally (`Vixen.Net.Prediction`, built) → hit-claim RPC
with the client's tick → server rewinds colliders (`ColliderRollback`, built) → validates → applies
through the damage pipeline. Every arrow is already implemented somewhere else.

What is new is the weapon model and **the claim's validation rules**, which are arithmetic over
numbers the caller supplies. This library never traces a ray, never asks where anything is, and never
touches a socket.

### The cone is a pure function, and that is what makes a claim checkable

`WeaponTemplate.Deviate(shot, pellet, spread)` is seeded from the shot number and the pellet index and
nothing else. Both ends compute the same cone, so the server **recomputes where the client's pellets
could have gone** rather than believing a direction it was sent. A stream seeded from a clock or an
ambient random would make every claim unfalsifiable.

⚠ **Pellets are uniform in *area*, not in radius.** Uniform radius bunches them in the middle, which
reads as a shotgun far more accurate than its cone; the test measures the distribution rather than
trusting the formula.

### The client does not get to say how many things its bullet went through

`HitClaim` has no penetration count. The server counts the pellet's prior accepted claims itself —
a number it already has and a client cannot touch — and uses that for the falloff. Without it, a
client claims "penetrated nothing" against every target and gets full damage on all of them.

⚠ **The pellet-uniqueness rule is the cheapest and most valuable check here.** Without it, one client
hits one target and reports the same pellet against forty of them, and *every other check passes for
each*: the shot happened, the tick is fine, the cone is fine.

### Recoil is a pattern and spread is randomness, and they must stay apart

The thing that makes a shooter's recoil feel fair is that the tenth bullet goes where the tenth bullet
always goes. Randomness on top of it is the cone. A game that folds them together can only tune "hard
to control" and "unpredictable" together, which is why every weapon in such a game feels the same.

⚠ **The last step of a pattern repeats rather than wrapping.** Wrapping makes the twentieth bullet go
where the first one did, which reads as the weapon resetting itself mid-burst.

### A rewind costs more than a call, and now something says so

Doc 16 names *"a cost budget for rewinds"* as owed and doc 28 § Shooting says this library is the
reason to close it. A rewound claim costs a physics scene rolled back and re-traced; an ordinary RPC
costs a dictionary lookup. A limiter that counts them the same lets a client spend a whole server
frame with a flood that is, packet for packet, inside its rate.

`RewindBudget` charges **per tick of rewind**, so the price tracks the work and a player on a bad
connection pays more — which is correct, because they cost more. A refused claim costs nothing: charging
for the refusal would let a flood keep a connection permanently broke, which turns a defence against
one client into a way to disable somebody's hits.

**Policy here, enforcement in the router.** This library has no networking and must not grow any;
`RpcRouter`'s per-connection limiter is what should consult the budget, so there is one limiter rather
than two that disagree.

## What the validator can and cannot prove

It can prove: the shot happened, the pellet is not claimed twice, the tick is inside the window, the
distance is inside the weapon's range, the deviation is inside the cone that shot's seed produced, and
the shooter can afford the rewind.

It **cannot** prove the target was there. That is the rewind, and the rewind is the expensive part —
which is the entire reason the budget exists.

⚠ **A rejection is not a cheat.** Most happen honestly: a claim arrives after its shot has aged out,
two claims race a kill, a packet was lost. What a realm does about a *rate* of rejections is its own
policy.

## What is owed

- **Projectiles travel, and nothing here moves them.** `WeaponKind.Projectile` and `ProjectileSpeed`
  are declared and the damage, falloff and penetration all apply; simulating the flight needs
  `Vixen.Physics` and belongs beside the game's own projectile component.
- **Wiring the budget into `RpcRouter`.** The policy is here; the limiter that should consult it is
  `Vixen.Net`'s, and that edge is doc 16's to make.
- **Headshots and hit zones.** A hit zone is a Crit-stage or Mitigate-stage `IDamageRule` plus a
  collider name the caller supplies — the pipeline already takes it, and the collider naming is the
  game's.
- **Weapon attachments**, which are `Vixen.Gameplay.Items`' affixes pointed at a weapon's numbers, and
  land when the two meet.
