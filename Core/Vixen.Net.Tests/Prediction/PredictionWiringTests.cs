// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Motion;
using Vixen.Net.Prediction;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Rules;
using Vixen.Net.Sessions;
using Vixen.Net.Time;
using Xunit;

namespace Vixen.Net.Tests.Prediction;

/// <summary>The wiring around prediction: what is predicted, how far ahead, and what a player sees.</summary>
public sealed class PredictionWiringTests : IDisposable {
    static readonly PlayerId Mine = new(1);
    static readonly PlayerId Theirs = new(2);

    readonly World world = new("prediction-wiring");
    readonly NetworkOwnership ownership = new();
    readonly NetworkRulesRegistry rules;

    public PredictionWiringTests() => rules = new(ownership);

    public void Dispose() => world.Dispose();

    /// <summary>What a client predicts is what the rules let it decide.</summary>
    /// <remarks>
    ///     Inventing a second notion of "mine" beside <c>NetworkRules.Write</c> is how the two come to
    ///     disagree — and the day they do, the client predicts something the server overrules on every
    ///     tick.
    /// </remarks>
    [Fact]
    public void WhatIsPredictedIsWhatTheRulesLetThisClientDecide() {
        var system = new PredictedOwnershipSystem { Rules = rules, Local = Mine };

        var mine = world.Create(new NetworkId(1));
        var theirs = world.Create(new NetworkId(2));
        var servers = world.Create(new NetworkId(3));

        ownership.SetOwner(new(1), Mine);
        ownership.SetOwner(new(2), Theirs);
        rules.Set(new(1), NetworkRules.OwnerAuthoritative);
        rules.Set(new(2), NetworkRules.OwnerAuthoritative);

        system.Update(world);

        Assert.True(world.Has<Predicted>(mine));
        Assert.False(world.Has<Predicted>(theirs));
        Assert.False(world.Has<Predicted>(servers));
        Assert.Equal(1, system.PredictedCount);
        Assert.Equal(1, system.AddedCount);
    }

    /// <summary>An object somebody else takes is one this client stops predicting.</summary>
    /// <remarks>
    ///     Ownership is transferable, and a predicted object whose prediction is never confirmed is a
    ///     correction on every snapshot for as long as it lives.
    /// </remarks>
    [Fact]
    public void AnObjectSomebodyElseTakesStopsBeingPredicted() {
        var system = new PredictedOwnershipSystem { Rules = rules, Local = Mine };
        var vehicle = world.Create(new NetworkId(1));

        ownership.SetOwner(new(1), Mine);
        rules.Set(new(1), NetworkRules.OwnerAuthoritative);
        system.Update(world);

        Assert.True(world.Has<Predicted>(vehicle));

        ownership.SetOwner(new(1), Theirs);
        system.Update(world);

        Assert.False(world.Has<Predicted>(vehicle));
        Assert.Equal(1, system.RemovedCount);
    }

    /// <summary>With no rules, nothing is predicted.</summary>
    /// <remarks>
    ///     The safe answer. Predicting by default would mean a game that never configured this
    ///     predicting every object on the map against a server that overrules all of them.
    /// </remarks>
    [Fact]
    public void WithNoRulesNothingIsPredicted() {
        var system = new PredictedOwnershipSystem { Local = Mine };
        var entity = world.Create(new NetworkId(1));

        system.Update(world);

        Assert.False(world.Has<Predicted>(entity));
    }

    /// <summary>Starvation makes the client run further ahead; a deep buffer gives it back.</summary>
    /// <remarks>
    ///     The loop that was missing: the buffer measured, <c>LeadBias</c> adjusted, and nothing
    ///     carried one to the other.
    /// </remarks>
    [Fact]
    public void StarvationPushesTheClientFurtherAhead() {
        var ticks = Synchronized();
        var controller = new TickLeadController { PatienceToGrow = 2, PatienceToShrink = 3 };
        var before = ticks.LeadTicks;

        // One report is not enough — the lead moves every input not yet sent, so reacting to one
        // starved tick is how a controller spends its life oscillating.
        Assert.Equal(0, controller.Apply(ticks, new(Depth: 0, Starved: 1, Late: 0)));
        Assert.Equal(before, ticks.LeadTicks);

        Assert.Equal(1, controller.Apply(ticks, new(Depth: 0, Starved: 2, Late: 0)));
        Assert.Equal(before + 1, ticks.LeadTicks);

        // And a buffer that is comfortably deep gives it back — more slowly, because being too far
        // ahead costs a little latency and being too far behind costs corrections a player sees.
        Assert.Equal(0, controller.Apply(ticks, new(Depth: 8, Starved: 0, Late: 0)));
        Assert.Equal(0, controller.Apply(ticks, new(Depth: 8, Starved: 0, Late: 0)));
        Assert.Equal(-1, controller.Apply(ticks, new(Depth: 8, Starved: 0, Late: 0)));
        Assert.Equal(before, ticks.LeadTicks);
    }

    /// <summary>A buffer that is doing what it should moves nothing.</summary>
    [Fact]
    public void AHealthyBufferMovesNothing() {
        var ticks = Synchronized();
        var controller = new TickLeadController { TargetDepth = 2 };
        var before = ticks.LeadTicks;

        for (var report = 0; report < 20; report++) {
            Assert.Equal(0, controller.Apply(ticks, new(Depth: 2, Starved: 0, Late: 0)));
        }

        Assert.Equal(before, ticks.LeadTicks);
        Assert.Equal(0, controller.GrewCount);
        Assert.Equal(0, controller.ShrankCount);
    }

    /// <summary>The bias is bounded, because past it the estimate is wrong by more than this hides.</summary>
    [Fact]
    public void TheBiasIsBounded() {
        var ticks = Synchronized();
        var controller = new TickLeadController { PatienceToGrow = 1, MaxBias = 3 };

        for (var report = 0; report < 50; report++) {
            controller.Apply(ticks, new(Depth: 0, Starved: 1, Late: 1));
        }

        Assert.Equal(3, ticks.LeadBias);
    }

    /// <summary>A report survives the wire, and saturates rather than wrapping.</summary>
    [Fact]
    public void AReportRoundTrips() {
        var router = new Messaging.BroadcastRouter();
        PredictionHealth? received = null;
        router.Subscribe<PredictionHealth>((_, message) => received = message);

        Assert.True(router.TryEncode(new PredictionHealth(2, 1, 0), out var payload));
        Assert.True(router.Receive(Mine, payload));
        Assert.Equal(new PredictionHealth(2, 1, 0), received);

        // A depth past what six bits can say is a client so far ahead that the exact number stopped
        // being information, and clamping is what stops it reading as zero.
        Assert.True(router.TryEncode(new PredictionHealth(9999, 9999, 9999), out payload));
        Assert.True(router.Receive(Mine, payload));
        Assert.Equal(new PredictionHealth(63, 63, 63), received);
    }

    /// <summary>The reporter sends deltas, not totals, and not every tick.</summary>
    [Fact]
    public void TheReporterSendsDeltasAndNotEveryTick() {
        var reporter = new PredictionHealthReporter { Period = 3 };

        Assert.False(reporter.TryAdvance(new(2, 4, 1, 0), out _));
        Assert.False(reporter.TryAdvance(new(2, 4, 1, 0), out _));
        Assert.True(reporter.TryAdvance(new(2, 4, 1, 0), out var first));

        // Four starved since the buffer was made, which is four since the first report.
        Assert.Equal(new PredictionHealth(2, 4, 1), first);

        Assert.False(reporter.TryAdvance(new(3, 6, 1, 0), out _));
        Assert.False(reporter.TryAdvance(new(3, 6, 1, 0), out _));
        Assert.True(reporter.TryAdvance(new(3, 6, 1, 0), out var second));

        // Two more since the last report, not six since the beginning — "has starved four times since
        // the match began" is not a thing to steer by.
        Assert.Equal(new PredictionHealth(3, 2, 0), second);
    }

    /// <summary>A correction is taken by the simulation at once and by the picture slowly.</summary>
    /// <remarks>
    ///     The split is the whole idea: what the server will judge is already right, and what the
    ///     player sees glides there. Blending the simulation instead would mean predicting on from a
    ///     position the server has already disagreed with.
    /// </remarks>
    [Fact]
    public void ACorrectionIsHiddenFromTheEyeAndNotFromTheSimulation() {
        var smoother = new PredictionSmoother { SnapDistance = 10f };
        var id = new NetworkId(1);

        smoother.Take([new(id, new(0f, 0f, 0f), new(1f, 0f, 0f))]);

        Assert.Equal(1, smoother.CorrectionCount);

        // Drawn where it was, near enough, while the simulation has already moved on.
        var drawn = smoother.Draw(id, new(1f, 0f, 0f));
        Assert.True(drawn.X < 0.5f, $"The correction was not hidden: drawn at {drawn.X}.");

        // And it works itself off, after which the object is forgotten rather than kept.
        for (var frame = 0; frame < 60; frame++) {
            smoother.Advance(TimeSpan.FromMilliseconds(16));
        }

        Assert.Equal(0, smoother.Count);
        Assert.Equal(new Vector3(1f, 0f, 0f), smoother.Draw(id, new(1f, 0f, 0f)));
    }

    /// <summary>A correction too large to hide is shown.</summary>
    /// <remarks>
    ///     Past the snap distance the object did not drift, it was moved — a respawn, a teleport, a
    ///     shove. Dragging a camera across that is worse than arriving.
    /// </remarks>
    [Fact]
    public void ACorrectionTooLargeToHideIsShown() {
        var smoother = new PredictionSmoother { SnapDistance = 2f };
        var id = new NetworkId(1);

        smoother.Take([new(id, Vector3.Zero, new(50f, 0f, 0f))]);

        Assert.Equal(1, smoother.SnapCount);
        Assert.Equal(new Vector3(50f, 0f, 0f), smoother.Draw(id, new(50f, 0f, 0f)));
    }

    /// <summary>Two objects corrected differently are smoothed differently.</summary>
    /// <remarks>
    ///     One shared error would be wrong the moment a player and the vehicle they are driving are
    ///     corrected by different amounts, which is the normal case rather than the exotic one.
    /// </remarks>
    [Fact]
    public void TwoObjectsAreSmoothedSeparately() {
        var smoother = new PredictionSmoother { SnapDistance = 10f };

        smoother.Take([
            new(new(1), Vector3.Zero, new(1f, 0f, 0f)),
            new(new(2), Vector3.Zero, new(0f, 3f, 0f))
        ]);

        Assert.Equal(2, smoother.Count);

        var first = smoother.Draw(new(1), new(1f, 0f, 0f));
        var second = smoother.Draw(new(2), new(0f, 3f, 0f));

        Assert.True(first.X < 1f);
        Assert.Equal(0f, first.Y, 3);
        Assert.True(second.Y < 3f);
        Assert.Equal(0f, second.X, 3);
    }

    static TickManager Synchronized() {
        var ticks = new TickManager(TickRate.Default);
        ticks.Synchronize(new(100), TimeSpan.FromMilliseconds(60));

        return ticks;
    }
}
