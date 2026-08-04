// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Combat;
using Xunit;

namespace Vixen.Gameplay.Shooting.Tests;

/// <summary>A rifle, a shotgun and a sniper: automatic, multi-pellet and penetrating.</summary>
public static class Content {
    public const string Rifle = "weapons/assault-rifle";
    public const string Shotgun = "weapons/shotgun";
    public const string Sniper = "weapons/sniper";

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .Add(
                Rifle,
                new WeaponDefinition {
                    DisplayName = "Assault Rifle",
                    Kind = WeaponKind.Hitscan,
                    Damage = new() { School = "Damage.Ballistic", Amount = 25f },
                    RoundsPerSecond = 10f,
                    Automatic = true,
                    Range = 100f,
                    Magazine = 30,
                    Reserve = 90,
                    ReloadTime = 2f,
                    Falloff = new() { Start = 30f, End = 80f, Minimum = 0.5f },
                    Spread = new() {
                        Base = 0.5f,
                        PerShot = 0.4f,
                        Maximum = 4f,
                        Recovery = 2f,
                        MovingMultiplier = 2f,
                        AimingMultiplier = 0.5f
                    },
                    Recoil = new() {
                        Pattern = [
                            new() { Pitch = 1f, Yaw = 0f },
                            new() { Pitch = 1.2f, Yaw = 0.3f },
                            new() { Pitch = 1.4f, Yaw = -0.4f }
                        ],
                        Recovery = 20f,
                        RecoveryDelay = 0.2f
                    },
                    Tags = ["Weapon.Rifle.Assault"]
                }
            )
            .Add(
                Shotgun,
                new WeaponDefinition {
                    DisplayName = "Shotgun",
                    Kind = WeaponKind.Hitscan,
                    Damage = new() { School = "Damage.Ballistic", Amount = 12f },
                    Pellets = 8,
                    RoundsPerSecond = 1f,
                    Range = 40f,
                    Magazine = 6,
                    Reserve = 24,
                    ReloadTime = 3f,
                    ReloadsPerRound = true,
                    Spread = new() { Base = 3f, PerShot = 0f, Maximum = 3f },
                    Tags = ["Weapon.Shotgun.Pump"]
                }
            )
            .Add(
                Sniper,
                new WeaponDefinition {
                    DisplayName = "Sniper",
                    Kind = WeaponKind.Hitscan,
                    Damage = new() { School = "Damage.Ballistic", Amount = 100f },
                    RoundsPerSecond = 1f,
                    Range = 300f,
                    Magazine = 5,
                    ReloadTime = 3f,
                    Penetration = new() { MaximumTargets = 2, DamageFraction = 0.5f },
                    Tags = ["Weapon.Rifle.Sniper"]
                }
            )
            .Build();

    public static WeaponLibrary Weapons() => WeaponLibrary.Compile(Catalog());

    public static WeaponState State(string address) => new(Weapons().Get(DefId.From(address)));
}

public class WeaponLibraryTests {
    [Fact]
    public void ACleanCatalogCompilesWithNothingToReport() {
        Assert.Empty(Content.Weapons().Problems);
        Assert.Equal(3, Content.Weapons().Count);
    }

    [Fact]
    public void ADefinitionThatCannotWorkIsReported() {
        var catalog = new DefinitionCatalogBuilder()
            .Add("weapons/broken", new WeaponDefinition { RoundsPerSecond = 0f })
            .Add(
                "weapons/backwards",
                new WeaponDefinition {
                    Damage = new() { Amount = 1f },
                    Spread = new() { Base = 5f, Maximum = 2f },
                    Magazine = 30,
                    Reserve = 10
                }
            )
            .Build();

        var problems = WeaponLibrary.Compile(catalog).Problems;

        Assert.Contains(problems, problem => problem.Contains("no rounds a second", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("no damage", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("widest before it has fired", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("never be fully reloaded", StringComparison.Ordinal));
    }

    [Fact]
    public void FalloffIsLinearBetweenItsTwoDistancesAndNeverBelowItsFloor() {
        var rifle = Content.Weapons().Get(DefId.From(Content.Rifle));

        Assert.Equal(25f, rifle.DamageAt(0f));
        Assert.Equal(25f, rifle.DamageAt(30f));
        Assert.Equal(18.75f, rifle.DamageAt(55f), 3);
        Assert.Equal(12.5f, rifle.DamageAt(80f), 3);
        Assert.Equal(12.5f, rifle.DamageAt(500f), 3);
    }

    [Fact]
    public void AWeaponWithNoFalloffDoesTheSameAtEveryDistance() {
        var sniper = Content.Weapons().Get(DefId.From(Content.Sniper));

        Assert.Equal(100f, sniper.DamageAt(1f));
        Assert.Equal(100f, sniper.DamageAt(299f));
    }

    [Fact]
    public void PenetrationHalvesPerThingAndStopsAtTheLimit() {
        var sniper = Content.Weapons().Get(DefId.From(Content.Sniper));

        Assert.Equal(100f, sniper.DamageAfter(10f, 0));
        Assert.Equal(50f, sniper.DamageAfter(10f, 1));
        Assert.Equal(25f, sniper.DamageAfter(10f, 2));
        Assert.Equal(0f, sniper.DamageAfter(10f, 3));

        // A weapon with no penetration stops at the first thing.
        Assert.Equal(0f, Content.Weapons().Get(DefId.From(Content.Rifle)).DamageAfter(10f, 1));
    }
}

public class SpreadTests {
    [Fact]
    public void BothEndsComputeTheSameConeFromTheSameShot() {
        // The whole basis of a checkable hit claim: the server recomputes where the pellets could
        // have gone rather than taking the client's word for it.
        for (var shot = 1u; shot < 200; shot++) {
            for (var pellet = 0; pellet < 8; pellet++) {
                Assert.Equal(
                    WeaponTemplate.Deviate(shot, pellet, 3f),
                    WeaponTemplate.Deviate(shot, pellet, 3f)
                );
            }
        }
    }

    [Fact]
    public void EveryPelletOfOneShotGoesSomewhereDifferent() {
        var seen = new HashSet<ShotDirection>();

        for (var pellet = 0; pellet < 8; pellet++) {
            Assert.True(seen.Add(WeaponTemplate.Deviate(7, pellet, 3f)));
        }
    }

    [Fact]
    public void NoPelletLeavesTheCone() {
        for (var shot = 1u; shot < 500; shot++) {
            var deviation = WeaponTemplate.Deviate(shot, 0, 3f);
            var distance = MathF.Sqrt((deviation.Pitch * deviation.Pitch) + (deviation.Yaw * deviation.Yaw));

            Assert.InRange(distance, 0f, 3.0001f);
        }
    }

    [Fact]
    public void PelletsFillTheDiscRatherThanBunchingInTheMiddle() {
        // A uniform angle and radius puts half the pellets inside the inner *quarter* of the area,
        // which reads as a shotgun far more accurate than its cone. Uniform in area puts half of
        // them inside 1/√2 of the radius.
        var inner = 0;
        const int Shots = 4000;

        for (var shot = 1u; shot <= Shots; shot++) {
            var deviation = WeaponTemplate.Deviate(shot, 0, 4f);
            var distance = MathF.Sqrt((deviation.Pitch * deviation.Pitch) + (deviation.Yaw * deviation.Yaw));

            if (distance <= 2f) {
                inner++;
            }
        }

        // A quarter of the area is inside half the radius, so a quarter of the pellets should be.
        Assert.InRange(inner / (double)Shots, 0.22, 0.28);
    }

    [Fact]
    public void NoSpreadIsPerfectAccuracy() {
        Assert.Equal(default, WeaponTemplate.Deviate(1, 0, 0f));
    }
}

public class WeaponStateTests {
    [Fact]
    public void AnAutomaticWeaponKeepsFiringAndASemiAutomaticDoesNot() {
        var rifle = Content.State(Content.Rifle);

        Assert.Equal(FireFailure.None, rifle.TryFire(out _));
        rifle.Tick(0.2f);
        Assert.Equal(FireFailure.None, rifle.TryFire(out _));

        var catalog = new DefinitionCatalogBuilder()
            .Add(
                "weapons/pistol",
                new WeaponDefinition {
                    Damage = new() { Amount = 30f },
                    RoundsPerSecond = 5f,
                    Automatic = false,
                    Magazine = 12
                }
            )
            .Build();

        var pistol = new WeaponState(WeaponLibrary.Compile(catalog).Get(DefId.From("weapons/pistol")));

        Assert.Equal(FireFailure.None, pistol.TryFire(out _));
        pistol.Tick(1f);
        Assert.Equal(FireFailure.NotAutomatic, pistol.TryFire(out _));

        pistol.ReleaseTrigger();
        Assert.Equal(FireFailure.None, pistol.TryFire(out _));
    }

    [Fact]
    public void TheFireIntervalIsHonoured() {
        var rifle = Content.State(Content.Rifle);

        Assert.Equal(FireFailure.None, rifle.TryFire(out _));
        Assert.Equal(FireFailure.TooSoon, rifle.TryFire(out _));

        rifle.Tick(0.05f);
        Assert.Equal(FireFailure.TooSoon, rifle.TryFire(out _));

        rifle.Tick(0.06f);
        Assert.Equal(FireFailure.None, rifle.TryFire(out _));
    }

    [Fact]
    public void AMagazineEmptiesAndAReloadFillsItFromTheReserve() {
        var rifle = Content.State(Content.Rifle);

        for (var shot = 0; shot < 30; shot++) {
            Assert.Equal(FireFailure.None, rifle.TryFire(out _));
            rifle.Tick(0.1f);
        }

        Assert.Equal(0, rifle.Magazine);
        Assert.Equal(FireFailure.Empty, rifle.TryFire(out _));

        Assert.True(rifle.BeginReload());
        Assert.Equal(FireFailure.Reloading, rifle.TryFire(out _));

        rifle.Tick(2f);

        Assert.Equal(30, rifle.Magazine);
        Assert.Equal(60, rifle.Reserve);
    }

    [Fact]
    public void AReloadThatCannotHappenIsRefusedRatherThanStarted() {
        var rifle = Content.State(Content.Rifle);

        Assert.False(rifle.BeginReload());

        rifle.TryFire(out _);
        Assert.True(rifle.BeginReload());
        Assert.False(rifle.BeginReload());
    }

    [Fact]
    public void APerRoundReloadKeepsWhatItHasAlreadyLoaded() {
        // The whole mechanic: a shotgun cut short mid-reload is a shotgun with some shells in it.
        var shotgun = Content.State(Content.Shotgun);

        for (var shot = 0; shot < 6; shot++) {
            Assert.Equal(FireFailure.None, shotgun.TryFire(out _));
            shotgun.ReleaseTrigger();
            shotgun.Tick(1f);
        }

        Assert.Equal(0, shotgun.Magazine);
        Assert.True(shotgun.BeginReload());

        // Half a second per shell.
        shotgun.Tick(0.5f);
        Assert.Equal(1, shotgun.Magazine);

        shotgun.Tick(0.5f);
        Assert.Equal(2, shotgun.Magazine);

        Assert.True(shotgun.CancelReload());
        shotgun.Tick(5f);

        Assert.Equal(2, shotgun.Magazine);
        Assert.Equal(22, shotgun.Reserve);
    }

    [Fact]
    public void SpreadWidensAsSomebodyKeepsFiringAndSettlesBack() {
        var rifle = Content.State(Content.Rifle);

        Assert.Equal(0.5f, rifle.EffectiveSpread, 3);

        for (var shot = 0; shot < 5; shot++) {
            rifle.TryFire(out _);
            rifle.Tick(0.1f);
        }

        // Five shots at 0.4° each, minus two degrees a second of recovery over half a second.
        Assert.InRange(rifle.EffectiveSpread, 1.2f, 2.2f);

        rifle.Tick(5f);
        Assert.Equal(0.5f, rifle.EffectiveSpread, 3);
    }

    [Fact]
    public void SpreadIsCappedAndMovingAndAimingMultiplyIt() {
        var rifle = Content.State(Content.Rifle);

        for (var shot = 0; shot < 30; shot++) {
            rifle.TryFire(out _);
            rifle.Tick(0.1f);
        }

        Assert.Equal(4f, rifle.EffectiveSpread, 3);

        rifle.Tick(10f);
        rifle.IsMoving = true;
        Assert.Equal(1f, rifle.EffectiveSpread, 3);

        rifle.IsMoving = false;
        rifle.IsAiming = true;
        Assert.Equal(0.25f, rifle.EffectiveSpread, 3);
    }

    [Fact]
    public void RecoilFollowsItsPatternAndTheLastStepRepeats() {
        var rifle = Content.State(Content.Rifle);

        rifle.TryFire(out _);
        Assert.Equal(1f, rifle.Recoil.Pitch, 3);

        rifle.Tick(0.1f);
        rifle.TryFire(out _);
        Assert.Equal(2.2f, rifle.Recoil.Pitch, 3);
        Assert.Equal(0.3f, rifle.Recoil.Yaw, 3);

        rifle.Tick(0.1f);
        rifle.TryFire(out _);
        Assert.Equal(3.6f, rifle.Recoil.Pitch, 3);

        // The fourth shot repeats the third step rather than wrapping to the first, which would read
        // as the weapon resetting itself mid-burst.
        rifle.Tick(0.1f);
        rifle.TryFire(out _);
        Assert.Equal(5f, rifle.Recoil.Pitch, 3);
    }

    [Fact]
    public void RecoilRecoversAfterItsDelayAndNotBefore() {
        var rifle = Content.State(Content.Rifle);

        rifle.TryFire(out _);
        var kicked = rifle.Recoil.Pitch;

        rifle.Tick(0.1f);
        Assert.Equal(kicked, rifle.Recoil.Pitch, 3);

        rifle.Tick(0.2f);
        Assert.True(rifle.Recoil.Pitch < kicked);

        rifle.Tick(5f);
        Assert.Equal(0f, rifle.Recoil.Pitch, 3);
    }

    [Fact]
    public void ABurstFiresItsCountAndThenWantsTheTriggerReleased() {
        var catalog = new DefinitionCatalogBuilder()
            .Add(
                "weapons/burst",
                new WeaponDefinition {
                    Damage = new() { Amount = 20f },
                    RoundsPerSecond = 20f,
                    Burst = 3,
                    Magazine = 30
                }
            )
            .Build();

        var weapon = new WeaponState(WeaponLibrary.Compile(catalog).Get(DefId.From("weapons/burst")));

        for (var shot = 0; shot < 3; shot++) {
            Assert.Equal(FireFailure.None, weapon.TryFire(out _));
            weapon.Tick(0.1f);
        }

        Assert.Equal(FireFailure.BurstFinished, weapon.TryFire(out _));

        weapon.ReleaseTrigger();
        Assert.Equal(FireFailure.None, weapon.TryFire(out _));
    }

    [Fact]
    public void ARespawnPutsEverythingBack() {
        var rifle = Content.State(Content.Rifle);

        for (var shot = 0; shot < 10; shot++) {
            rifle.TryFire(out _);
            rifle.Tick(0.1f);
        }

        rifle.Refill();

        Assert.Equal(30, rifle.Magazine);
        Assert.Equal(90, rifle.Reserve);
        Assert.Equal(0.5f, rifle.EffectiveSpread, 3);
        Assert.Equal(default, rifle.Recoil);
    }

    [Fact]
    public void AWeaponWithUnlimitedSparesNeverRunsOut() {
        var sniper = Content.State(Content.Sniper);

        for (var round = 0; round < 3; round++) {
            for (var shot = 0; shot < 5; shot++) {
                Assert.Equal(FireFailure.None, sniper.TryFire(out _));
                sniper.ReleaseTrigger();
                sniper.Tick(1f);
            }

            Assert.True(sniper.BeginReload());
            sniper.Tick(3f);
            Assert.Equal(5, sniper.Magazine);
        }
    }
}

public class HitClaimValidatorTests {
    static HitClaimValidator Validator(out WeaponState rifle, RewindBudget? budget = null) {
        rifle = Content.State(Content.Rifle);

        return new(Content.Weapons(), window: 30) { Budget = budget };
    }

    static HitClaim Claim(in ShotFired shot, int pellet = 0, ulong target = 2, int tick = 100, float distance = 10f) {
        var deviation = WeaponTemplate.Deviate(shot.Shot, pellet, shot.Spread);

        return new(
            1,
            shot.Shot,
            pellet,
            target,
            tick,
            distance,
            MathF.Sqrt((deviation.Pitch * deviation.Pitch) + (deviation.Yaw * deviation.Yaw))
        );
    }

    [Fact]
    public void AnHonestClaimIsBelievedAndPaysTheWeaponsDamage() {
        var validator = Validator(out var rifle);

        rifle.TryFire(out var shot);
        validator.RecordShot(1, DefId.From(Content.Rifle), shot, 100);

        var verdict = validator.Validate(Claim(shot), 105);

        Assert.True(verdict.Accepted);
        Assert.Equal(25f, verdict.Damage);
        Assert.Equal(5, verdict.RewindTicks);
    }

    [Fact]
    public void AClaimAboutAShotNobodyFiredIsRefused() {
        var validator = Validator(out var rifle);

        rifle.TryFire(out var shot);
        validator.RecordShot(1, DefId.From(Content.Rifle), shot, 100);

        Assert.Equal(
            ClaimRejection.NoSuchShot,
            validator.Validate(Claim(shot) with { Shot = 99 }, 105).Rejection
        );

        Assert.Equal(
            ClaimRejection.NoSuchShot,
            validator.Validate(Claim(shot) with { Shooter = 7 }, 105).Rejection
        );

        // And a pellet the shot never had.
        Assert.Equal(ClaimRejection.NoSuchShot, validator.Validate(Claim(shot, pellet: 4), 105).Rejection);
    }

    [Fact]
    public void OnePelletCannotBeClaimedAgainstTwoTargetsWhenItCannotPenetrate() {
        // The cheapest and most valuable rule here: without it a client hits one target and reports
        // the same pellet against forty of them, and every other check passes for each.
        var validator = Validator(out var rifle);

        rifle.TryFire(out var shot);
        validator.RecordShot(1, DefId.From(Content.Rifle), shot, 100);

        Assert.True(validator.Validate(Claim(shot, target: 2), 101).Accepted);
        Assert.Equal(ClaimRejection.AlreadyClaimed, validator.Validate(Claim(shot, target: 2), 101).Rejection);
        Assert.Equal(
            ClaimRejection.TooManyPenetrations,
            validator.Validate(Claim(shot, target: 3), 101).Rejection
        );
    }

    [Fact]
    public void APenetratingWeaponMayClaimOnePelletAgainstSeveralAndTheDamageFalls() {
        var validator = new HitClaimValidator(Content.Weapons(), window: 30);
        var sniper = Content.State(Content.Sniper);

        sniper.TryFire(out var shot);
        validator.RecordShot(1, DefId.From(Content.Sniper), shot, 100);

        Assert.Equal(100f, validator.Validate(Claim(shot, target: 2), 101).Damage);
        Assert.Equal(50f, validator.Validate(Claim(shot, target: 3), 101).Damage);
        Assert.Equal(25f, validator.Validate(Claim(shot, target: 4), 101).Damage);

        // Three is the limit, and the client never got to say how many it had gone through.
        Assert.Equal(
            ClaimRejection.TooManyPenetrations,
            validator.Validate(Claim(shot, target: 5), 101).Rejection
        );
    }

    [Fact]
    public void ATickOutsideTheWindowIsRefusedInBothDirections() {
        var validator = Validator(out var rifle);

        rifle.TryFire(out var shot);
        validator.RecordShot(1, DefId.From(Content.Rifle), shot, 100);

        Assert.Equal(ClaimRejection.OutsideWindow, validator.Validate(Claim(shot, tick: 60), 100).Rejection);
        Assert.Equal(ClaimRejection.OutsideWindow, validator.Validate(Claim(shot, tick: 120), 100).Rejection);
        Assert.True(validator.Validate(Claim(shot, tick: 71), 100).Accepted);
    }

    [Fact]
    public void ADistanceTheWeaponCannotReachIsRefused() {
        var validator = Validator(out var rifle);

        rifle.TryFire(out var shot);
        validator.RecordShot(1, DefId.From(Content.Rifle), shot, 100);

        Assert.Equal(ClaimRejection.OutOfRange, validator.Validate(Claim(shot, distance: 500f), 101).Rejection);
        Assert.Equal(ClaimRejection.OutOfRange, validator.Validate(Claim(shot, distance: -1f), 101).Rejection);
    }

    [Fact]
    public void APelletClaimedOutsideItsOwnConeIsRefused() {
        var validator = new HitClaimValidator(Content.Weapons(), window: 30);
        var shotgun = Content.State(Content.Shotgun);

        shotgun.TryFire(out var shot);
        validator.RecordShot(1, DefId.From(Content.Shotgun), shot, 100);

        var honest = Claim(shot, pellet: 3);

        Assert.True(validator.Validate(honest, 101).Accepted);

        // The same pellet, claimed as though it had gone a long way off the aim — which is what a
        // client asking for a hit it could not have made looks like.
        Assert.Equal(
            ClaimRejection.OutsideCone,
            validator.Validate(Claim(shot, pellet: 4) with { Deviation = 20f }, 101).Rejection
        );
    }

    [Fact]
    public void TheServersOwnTraceHasTheLastWord() {
        var validator = Validator(out var rifle);

        rifle.TryFire(out var shot);
        validator.RecordShot(1, DefId.From(Content.Rifle), shot, 100);

        Assert.Equal(
            ClaimRejection.NoLineOfSight,
            validator.Validate(Claim(shot), 101, lineOfSight: false).Rejection
        );

        // And the refused claim did not spend the pellet.
        Assert.True(validator.Validate(Claim(shot), 101).Accepted);
    }

    [Fact]
    public void AnAgedOutShotIsForgottenAlongWithItsClaims() {
        var validator = new HitClaimValidator(Content.Weapons(), window: 1000, history: 4);
        var rifle = Content.State(Content.Rifle);

        var shots = new List<ShotFired>();

        for (var index = 0; index < 6; index++) {
            rifle.TryFire(out var shot);
            validator.RecordShot(1, DefId.From(Content.Rifle), shot, 100);
            rifle.Tick(0.2f);
            shots.Add(shot);
        }

        Assert.Equal(ClaimRejection.NoSuchShot, validator.Validate(Claim(shots[0]), 101).Rejection);
        Assert.True(validator.Validate(Claim(shots[5]), 101).Accepted);
    }

    [Fact]
    public void ForgettingAShooterForgetsTheirShots() {
        var validator = Validator(out var rifle);

        rifle.TryFire(out var shot);
        validator.RecordShot(1, DefId.From(Content.Rifle), shot, 100);

        Assert.True(validator.Forget(1));
        Assert.Equal(ClaimRejection.NoSuchShot, validator.Validate(Claim(shot), 101).Rejection);
    }
}

public class RewindBudgetTests {
    [Fact]
    public void ADeeperRewindCostsMore() {
        var budget = new RewindBudget(capacity: 100f, refillPerSecond: 0f, costPerTick: 1f, minimumCost: 2f);

        Assert.Equal(2f, budget.CostOf(0));
        Assert.Equal(2f, budget.CostOf(1));
        Assert.Equal(30f, budget.CostOf(30));
    }

    [Fact]
    public void AConnectionRunsOutAndRefills() {
        var budget = new RewindBudget(capacity: 60f, refillPerSecond: 60f, costPerTick: 1f, minimumCost: 1f);

        Assert.True(budget.TryConsume(1, 30));
        Assert.True(budget.TryConsume(1, 30));
        Assert.False(budget.TryConsume(1, 30));
        Assert.Equal(0f, budget.RemainingFor(1));

        budget.Tick(0.5f);
        Assert.Equal(30f, budget.RemainingFor(1), 3);
        Assert.True(budget.TryConsume(1, 30));

        budget.Tick(10f);
        Assert.Equal(60f, budget.RemainingFor(1));
    }

    [Fact]
    public void ARefusedClaimCostsNothing() {
        // Charging for the refusal would let a flood keep a connection permanently broke, which turns
        // a defence against one client into a way to disable somebody's hits.
        var budget = new RewindBudget(capacity: 10f, refillPerSecond: 0f, costPerTick: 1f, minimumCost: 1f);

        Assert.False(budget.TryConsume(1, 30));
        Assert.Equal(10f, budget.RemainingFor(1));
    }

    [Fact]
    public void OneConnectionsFloodDoesNotSpendAnothers() {
        var budget = new RewindBudget(capacity: 30f, refillPerSecond: 0f);

        Assert.True(budget.TryConsume(1, 30));
        Assert.False(budget.TryConsume(1, 30));
        Assert.True(budget.TryConsume(2, 30));
    }

    [Fact]
    public void AValidatorWithABudgetRefusesWhatItCannotAfford() {
        var budget = new RewindBudget(capacity: 20f, refillPerSecond: 0f, costPerTick: 1f, minimumCost: 1f);
        var validator = new HitClaimValidator(Content.Weapons(), window: 30) { Budget = budget };
        var rifle = Content.State(Content.Rifle);

        for (var index = 0; index < 3; index++) {
            rifle.TryFire(out var shot);
            validator.RecordShot(1, DefId.From(Content.Rifle), shot, 100);
            rifle.Tick(0.2f);

            var deviation = WeaponTemplate.Deviate(shot.Shot, 0, shot.Spread);

            var claim = new HitClaim(
                1,
                shot.Shot,
                0,
                (ulong)(index + 2),
                100,
                10f,
                MathF.Sqrt((deviation.Pitch * deviation.Pitch) + (deviation.Yaw * deviation.Yaw))
            );

            var verdict = validator.Validate(claim, 110);

            // Ten ticks a claim out of twenty: the third cannot be afforded.
            if (index < 2) {
                Assert.True(verdict.Accepted);
            } else {
                Assert.Equal(ClaimRejection.BudgetExhausted, verdict.Rejection);
            }
        }
    }

    [Fact]
    public void ABudgetForgetsAConnectionThatLeft() {
        var budget = new RewindBudget(capacity: 30f, refillPerSecond: 0f);

        budget.TryConsume(1, 10);
        Assert.True(budget.Forget(1));
        Assert.Equal(30f, budget.RemainingFor(1));
    }
}

public class ShootingModuleTests {
    [Fact]
    public void ShootingNeedsCombatAndTheKernel() {
        Assert.Throws<InvalidOperationException>(() => new GameplayConfig().Use<ShootingModule>().Build());

        var composition = new GameplayConfig()
            .Use<GameplayKernelModule>()
            .Use<CombatModule>()
            .Use<ShootingModule>()
            .Build();

        Assert.Contains(composition.Definitions, entry => entry.Tag == "WeaponDefinition");
        Assert.Contains(ShootingModule.WeaponRoot, composition.Tags);
    }
}
