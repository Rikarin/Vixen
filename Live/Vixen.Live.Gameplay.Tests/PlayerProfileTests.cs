// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Loot;
using Xunit;

namespace Vixen.Live.Gameplay.Tests;

/// <summary>A section a test can drive, standing in for a library's codec.</summary>
sealed class Slice(string name) : IProfileSection {
    public ProfileSectionId Id { get; } = ProfileSectionId.From(name);

    public byte[] Bytes { get; set; } = [];

    public ReadOnlyMemory<byte> Save() => Bytes;

    public void Load(ReadOnlyMemory<byte> bytes) => Bytes = bytes.ToArray();
}

public class PlayerProfileTests {
    static readonly ProfileSectionId Quests = ProfileSections.Quests;
    static readonly ProfileSectionId Fog = ProfileSections.Exploration;

    readonly PlayerProfile profile = new();

    // ── The container ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASliceComesBackAsItWasWritten() {
        profile.Set(Quests, new byte[] { 1, 2, 3 });

        var round = PlayerProfile.Read(profile.Write());

        Assert.True(round.TryGet(Quests, out var bytes));
        Assert.Equal([1, 2, 3], bytes.ToArray());
    }

    [Fact]
    public void AFreshCharacterHasNothingRatherThanBeingAnError() {
        var fresh = PlayerProfile.Read(default);

        Assert.Equal(0, fresh.Count);
        Assert.False(fresh.TryGet(Quests, out _));
    }

    [Fact]
    public void ASectionNothingInThisBuildKnowsAboutSurvivesARoundTrip() {
        // ⚠ The rule the whole container exists for. Doc 27 § Upgrades fragments a population by
        // version on purpose, so during a rollout an old realm and a new realm both write the same
        // character — and an old realm that dropped the new one's section would lose it silently,
        // and only for players who zoned the wrong way.
        profile.Set(ProfileSectionId.From("something-from-next-patch"), new byte[] { 9, 9 });
        profile.Set(Quests, new byte[] { 1 });

        var loaded = PlayerProfile.Read(profile.Write());

        // A build that only knows about quests reads and rewrites, touching nothing else.
        var binder = new ProfileBinder().Add(new Slice("quests"));

        binder.Load(loaded);
        binder.Save(loaded);

        var again = PlayerProfile.Read(loaded.Write());

        Assert.True(again.TryGet(ProfileSectionId.From("something-from-next-patch"), out var kept));
        Assert.Equal([9, 9], kept.ToArray());
    }

    [Fact]
    public void TheSameStateWritesTheSameBytesWhateverOrderItWasBuiltIn() {
        // Or every checkpoint looks like a change and the row is rewritten on a cadence for ever.
        var one = new PlayerProfile();
        var two = new PlayerProfile();

        one.Set(Quests, new byte[] { 1 });
        one.Set(Fog, new byte[] { 2 });

        two.Set(Fog, new byte[] { 2 });
        two.Set(Quests, new byte[] { 1 });

        Assert.Equal(one.Write().ToArray(), two.Write().ToArray());
    }

    [Fact]
    public void WritingTheSameBytesBackIsNotAChange() {
        Assert.True(profile.Set(Quests, new byte[] { 1, 2 }));

        var revision = profile.Revision;

        Assert.False(profile.Set(Quests, new byte[] { 1, 2 }));
        Assert.Equal(revision, profile.Revision);

        Assert.True(profile.Set(Quests, new byte[] { 1, 3 }));
        Assert.Equal(revision + 1, profile.Revision);
    }

    [Fact]
    public void AnEmptySliceRemovesItRatherThanStoringNothing() {
        profile.Set(Quests, new byte[] { 1 });

        Assert.True(profile.Set(Quests, default));
        Assert.Equal(0, profile.Count);
    }

    [Fact]
    public void BytesThatAreNotAProfileAreRefusedRatherThanRead() {
        Assert.Throws<ProfileFormatException>(() => PlayerProfile.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
    }

    [Fact]
    public void ATruncatedProfileIsRefused() {
        profile.Set(Quests, new byte[] { 1, 2, 3, 4 });

        var bytes = profile.Write().ToArray();

        Assert.Throws<ProfileFormatException>(() => PlayerProfile.Read(bytes.AsMemory(0, bytes.Length - 2)));
    }

    // ── The binder ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachSectionLoadsAndSavesItsOwn() {
        var quests = new Slice("quests") { Bytes = [1] };
        var fog = new Slice("exploration") { Bytes = [2, 2] };
        var binder = new ProfileBinder().Add(quests).Add(fog);

        binder.Save(profile);

        var loaded = PlayerProfile.Read(profile.Write());
        var second = new ProfileBinder().Add(new Slice("quests")).Add(new Slice("exploration"));

        second.Load(loaded);

        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void ASectionThisBuildDidNotRegisterIsLoadedAsNothing() {
        // A game that declined a library never registers its section, and the character it loads is
        // one without that state rather than one that fails to load.
        profile.Set(Fog, new byte[] { 7 });

        var quests = new Slice("quests") { Bytes = [1] };

        new ProfileBinder().Add(quests).Load(profile);

        Assert.Empty(quests.Bytes);
    }

    [Fact]
    public void TwoSectionsOnOneIdAreRefused() {
        // ⚠ Last-wins would be one of them silently reading the other's bytes, which presents as a
        // character whose quests are full of somebody's fog.
        var binder = new ProfileBinder().Add(new Slice("quests"));

        Assert.Throws<InvalidOperationException>(() => binder.Add(new Slice("quests")));
    }

    [Fact]
    public void ASectionWithNoNameIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => new ProfileBinder().Add(new Slice("")));

    // ── The checkpoint ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACleanCharacterIsNotWritten() {
        var policy = new CheckpointPolicy(TimeSpan.FromMinutes(5));

        policy.Loaded(DateTimeOffset.UnixEpoch);

        Assert.False(policy.Due(DateTimeOffset.UnixEpoch.AddHours(1), out var reason));
        Assert.Equal(CheckpointReason.None, reason);
    }

    [Fact]
    public void TheCadenceComesRoundAfterSomethingChanges() {
        var policy = new CheckpointPolicy(TimeSpan.FromMinutes(5));

        policy.Loaded(DateTimeOffset.UnixEpoch);
        policy.Touch();

        Assert.False(policy.Due(DateTimeOffset.UnixEpoch.AddMinutes(4), out _));
        Assert.True(policy.Due(DateTimeOffset.UnixEpoch.AddMinutes(5), out var reason));
        Assert.Equal(CheckpointReason.Cadence, reason);
    }

    [Fact]
    public void ATransferWritesWithoutWaitingForTheCadence() {
        var policy = new CheckpointPolicy(TimeSpan.FromMinutes(5));

        policy.Loaded(DateTimeOffset.UnixEpoch);
        policy.Touch();
        policy.Force(CheckpointReason.Transfer);

        Assert.True(policy.Due(DateTimeOffset.UnixEpoch, out var reason));
        Assert.Equal(CheckpointReason.Transfer, reason);
    }

    [Fact]
    public void ATransferOnACleanCharacterWritesNothing() {
        // ⚠ "Always on transfer" reads as unconditional and should not be: the bytes stored are
        // already the bytes we have, and a round trip to say so is one spent inside L2's overlap.
        var policy = new CheckpointPolicy(TimeSpan.FromMinutes(5));

        policy.Loaded(DateTimeOffset.UnixEpoch);
        policy.Force(CheckpointReason.Transfer);

        Assert.False(policy.Due(DateTimeOffset.UnixEpoch, out _));
    }

    [Fact]
    public void AFailedWriteStaysDirtyAndDoesNotRestartTheClock() {
        // ⚠ Clearing the flag loses the interval for good; restarting the clock turns a five-second
        // outage into five minutes of lost progress.
        var policy = new CheckpointPolicy(TimeSpan.FromMinutes(5));
        var at = DateTimeOffset.UnixEpoch;

        policy.Loaded(at);
        policy.Touch();

        Assert.True(policy.Due(at.AddMinutes(5), out _));

        policy.Failed();

        Assert.True(policy.IsDirty);
        Assert.True(policy.Due(at.AddMinutes(5), out var reason));
        Assert.Equal(CheckpointReason.Cadence, reason);
        Assert.Equal(1, policy.Failures);
    }

    [Fact]
    public void AFailedForcedWriteKeepsItsReason() {
        var policy = new CheckpointPolicy(TimeSpan.FromMinutes(5));

        policy.Loaded(DateTimeOffset.UnixEpoch);
        policy.Touch();
        policy.Force(CheckpointReason.Logout);
        policy.Failed();

        Assert.True(policy.Due(DateTimeOffset.UnixEpoch, out var reason));
        Assert.Equal(CheckpointReason.Logout, reason);
    }

    [Fact]
    public void ASuccessfulWriteClearsEverything() {
        var policy = new CheckpointPolicy(TimeSpan.FromMinutes(5));
        var at = DateTimeOffset.UnixEpoch;

        policy.Loaded(at);
        policy.Touch();
        policy.Force(CheckpointReason.Transfer);
        policy.Failed();
        policy.Wrote(at.AddMinutes(1));

        Assert.False(policy.IsDirty);
        Assert.Equal(0, policy.Failures);
        Assert.Equal(1, policy.Writes);
        Assert.False(policy.Due(at.AddMinutes(2), out _));
    }

    // ── Pity ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APityCountSurvivesARoundTrip() {
        // ⚠ Doc 28: "a pity counter that resets on a realm crash is a support ticket".
        var policy = new CheckpointPolicy(TimeSpan.FromMinutes(5));
        var store = new ProfilePityStore(policy);
        var key = new PityKey(7, DefId.From("loot/skarr"));

        for (var attempt = 0; attempt < 12; attempt++) {
            store.Record(key, hit: false);
        }

        new ProfileBinder().Add(store).Save(profile);

        var loaded = new ProfilePityStore();

        new ProfileBinder().Add(loaded).Load(PlayerProfile.Read(profile.Write()));

        Assert.Equal(12, loaded.AttemptsOf(key));
    }

    [Fact]
    public void AHitClearsTheRunRatherThanDecrementingIt() {
        // ⚠ Pity bounds a *run* of bad luck. Decrementing carries a hundred failures into the next
        // hundred attempts and makes the guarantee unbounded.
        var store = new ProfilePityStore();
        var key = new PityKey(7, DefId.From("loot/skarr"));

        store.Record(key, hit: false);
        store.Record(key, hit: false);
        store.Record(key, hit: true);

        Assert.Equal(0, store.AttemptsOf(key));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ARecordedAttemptMakesTheCharacterDirty() {
        var policy = new CheckpointPolicy(TimeSpan.FromMinutes(5));
        var store = new ProfilePityStore(policy);

        policy.Loaded(DateTimeOffset.UnixEpoch);

        Assert.False(policy.IsDirty);

        store.Record(new(7, DefId.From("loot/skarr")), hit: false);

        Assert.True(policy.IsDirty);
    }

    [Fact]
    public void ACharacterWithNoRunsWritesNoSection() {
        var store = new ProfilePityStore();

        new ProfileBinder().Add(store).Save(profile);

        Assert.Equal(0, profile.Count);
    }

    [Fact]
    public void TwoStoresHoldingTheSameCountsWriteTheSameBytes() {
        var one = new ProfilePityStore();
        var two = new ProfilePityStore();
        var first = new PityKey(7, DefId.From("loot/skarr"));
        var second = new PityKey(9, DefId.From("loot/gravewarden"));

        one.Record(first, false);
        one.Record(second, false);

        two.Record(second, false);
        two.Record(first, false);

        Assert.Equal(one.Save().ToArray(), two.Save().ToArray());
    }
}
