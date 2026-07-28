// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Assets;
using Vixen.Audio.Devices;
using Vixen.Audio.Mixing;
using Vixen.Audio.Music;
using Vixen.Audio.Parameters;
using Vixen.Core.Serialization;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>The clock, and the arithmetic that turns "now" into "the top of the next bar".</summary>
public sealed class MusicTransportTests {
    static MusicTransport Transport(float bpm = 120f, int beatsPerBar = 4) {
        var transport = new MusicTransport(48_000);
        transport.Start(0, new MusicTempo { BeatsPerMinute = bpm, BeatsPerBar = beatsPerBar });
        return transport;
    }

    /// <summary>
    ///     A bar at 128 in four is 90 000 frames at 48 kHz exactly. Held in seconds it is 1.8749999,
    ///     and the error compounds every bar until a four-minute track has drifted off its loop.
    /// </summary>
    [Fact]
    public void ABarIsAWholeNumberOfFrames() {
        var tempo = new MusicTempo { BeatsPerMinute = 128f, BeatsPerBar = 4 };

        Assert.Equal(22_500, tempo.FramesPerBeat(48_000));
        Assert.Equal(90_000, tempo.FramesPerBar(48_000));

        // And a hundred bars is exactly a hundred times one, which rounding at the bar would not give.
        Assert.Equal(tempo.FramesPerBar(48_000) * 100, tempo.FramesPerBeat(48_000) * 400);
    }

    [Fact]
    public void PositionIsCountedFromTheOrigin() {
        var transport = new MusicTransport(48_000);
        transport.Start(1_000_000, new MusicTempo());

        Assert.Equal(0, transport.PositionAt(1_000_000));
        Assert.Equal(24_000, transport.PositionAt(1_024_000));
        Assert.Equal(1, transport.BeatAt(1_024_000));
        Assert.Equal(0, transport.BarAt(1_024_000));
        Assert.Equal(1, transport.BarAt(1_096_000));
    }

    /// <summary>A segment scheduled a bar ahead is asked about frames before it starts, every frame.</summary>
    [Fact]
    public void PositionsBeforeTheOriginCountBackwardsRatherThanClampingToZero() {
        var transport = new MusicTransport(48_000);
        transport.Start(100_000, new MusicTempo());

        Assert.Equal(-1, transport.BeatAt(100_000 - 1));
        Assert.Equal(-1, transport.BarAt(100_000 - 1));
        Assert.Equal(-2, transport.BeatAt(100_000 - 24_001));
    }

    [Fact]
    public void TheNextBeatAndBarAreWhereTheyShouldBe() {
        var transport = Transport();

        // 120 in four: a beat is 24 000 frames and a bar is 96 000.
        Assert.Equal(24_000, transport.NextBoundary(1, MusicQuantize.Beat));
        Assert.Equal(24_000, transport.NextBoundary(23_999, MusicQuantize.Beat));
        Assert.Equal(96_000, transport.NextBoundary(1, MusicQuantize.Bar));
        Assert.Equal(96_000, transport.NextBoundary(95_999, MusicQuantize.Bar));
        Assert.Equal(192_000, transport.NextBoundary(96_001, MusicQuantize.Bar));
    }

    /// <summary>
    ///     Otherwise a transition asked for exactly on the beat is a whole bar late — which is the one
    ///     case anybody notices, because it is the case where they timed it.
    /// </summary>
    [Fact]
    public void ARequestExactlyOnABoundaryLandsOnThatBoundary() {
        var transport = Transport();

        Assert.Equal(96_000, transport.NextBoundary(96_000, MusicQuantize.Bar));
        Assert.Equal(24_000, transport.NextBoundary(24_000, MusicQuantize.Beat));
        Assert.Equal(0, transport.NextBoundary(0, MusicQuantize.Bar));
    }

    [Fact]
    public void ImmediateIsNow() {
        var transport = Transport();
        Assert.Equal(12_345, transport.NextBoundary(12_345, MusicQuantize.Immediate));
    }

    [Fact]
    public void SegmentIsWhereTheCurrentOneRunsOut() {
        var transport = Transport();

        Assert.Equal(500_000, transport.NextBoundary(1, MusicQuantize.Segment, 500_000));
        Assert.Equal(500_000, transport.NextBoundary(500_000, MusicQuantize.Segment, 500_000));

        // Past its end, so the next whole pass of it.
        Assert.Equal(1_000_000, transport.NextBoundary(500_001, MusicQuantize.Segment, 500_000));
    }

    /// <summary>Because "when this ends" has no answer for something that does not.</summary>
    [Fact]
    public void ASegmentWithNoLengthFallsBackToTheBar() {
        var transport = Transport();
        Assert.Equal(96_000, transport.NextBoundary(1, MusicQuantize.Segment));
    }

    [Fact]
    public void AWaltzIsThreeBeatsToTheBar() {
        var transport = Transport(120f, 3);
        Assert.Equal(72_000, transport.NextBoundary(1, MusicQuantize.Bar));
    }
}

/// <summary>Segments, loops, transitions and the sample the join lands on.</summary>
public sealed class MusicPlayerTests : IDisposable {
    const int Block = 64;

    readonly AudioEngine engine;
    readonly NullAudioDevice device;

    public MusicPlayerTests() {
        var backend = new NullAudioBackend();

        device = (NullAudioDevice)backend.OpenDevice(new AudioDeviceOptions {
            Format = new AudioFormat(48_000, 2),
            BufferFrames = Block
        });

        engine = new(device, new AudioEngineOptions {
            VoiceCapacity = 8,
            StreamOnOwnThread = false,
            MasterLimiter = false
        });
    }

    public void Dispose() => engine.Dispose();

    /// <summary>A segment whose clip is a ramp, so where its playhead is can be read off a sample.</summary>
    static MusicSegment Segment(string name, int frames = 96_000, string next = "", int loops = -1) => new() {
        Name = name,
        Clip = AudioTestData.Ramp(frames),
        Tempo = new() { BeatsPerMinute = 120f, BeatsPerBar = 4 },
        LoopCount = loops,
        Next = next
    };

    /// <summary>Renders whole blocks, updating as a frame loop would.</summary>
    /// <remarks>
    ///     Both updates, in the order a game does them: the engine collects and steps, then the player
    ///     reads the clock. Leaving the engine out is how a test ends up asserting against statistics
    ///     from before it started.
    /// </remarks>
    void Advance(MusicPlayer player, int blocks) {
        for (var i = 0; i < blocks; i++) {
            AudioTestData.Render(device, Block);
            engine.Update(0f);
            player.Update();
        }
    }

    [Fact]
    public void PlayingASegmentStartsItAndTheTransportFollows() {
        var player = new MusicPlayer(engine);
        player.Add(Segment("Explore"));

        Assert.True(player.Play("Explore"));
        Assert.Equal("Explore", player.Current?.Name);
        Assert.True(player.IsPlaying);

        Advance(player, 8);
        Assert.True(AudioTestData.Peak(AudioTestData.Render(device, Block)) > 0f);
    }

    [Fact]
    public void AnUnknownSegmentIsRefusedRatherThanThrowing() {
        var player = new MusicPlayer(engine);

        Assert.False(player.Play("Nothing"));
        Assert.False(player.TransitionTo("Nothing"));
        Assert.Null(player.Current);
    }

    /// <summary>The claim the whole subsystem exists for.</summary>
    [Fact]
    public void ATransitionIsScheduledForTheExactFrameOfTheNextBarLine() {
        var player = new MusicPlayer(engine);
        player.Add(Segment("Explore"));
        player.Add(Segment("Combat"));

        player.Play("Explore");

        // A bar at 120 in four is 96 000 frames. Ask part way through the first one.
        Advance(player, 100);
        Assert.True(player.TransitionTo("Combat", MusicQuantize.Bar));

        Assert.Equal("Combat", player.Queued?.Name);
        Assert.Equal(96_000, player.QueuedAtFrame);
        Assert.Equal("Explore", player.Current?.Name);
    }

    /// <summary>
    ///     Scheduled the instant it is asked for, so nothing between the request and the boundary — a
    ///     long frame, a level load, a breakpoint — can make it late.
    /// </summary>
    [Fact]
    public void TheIncomingVoiceIsStartedImmediatelyAndWaits() {
        var player = new MusicPlayer(engine);
        player.Add(Segment("Explore"));
        player.Add(Segment("Combat"));

        player.Play("Explore");
        Advance(player, 10);

        var before = engine.Statistics.ActiveVoices;
        player.TransitionTo("Combat", MusicQuantize.Bar);
        Advance(player, 1);

        // Two voices, one of them silent and waiting on its start frame — and its bar line is a long
        // way off, so nothing has promoted it.
        Assert.Equal(1, before);
        Assert.Equal(2, engine.Statistics.ActiveVoices);
        Assert.Equal("Explore", player.Current?.Name);
    }

    [Fact]
    public void TheQueuedSegmentBecomesCurrentWhenItsMomentArrives() {
        var player = new MusicPlayer(engine) { CrossfadeSeconds = 0f };
        player.Add(Segment("Explore"));
        player.Add(Segment("Combat"));

        player.Play("Explore");

        // Part way into the first bar, because a request made exactly on a bar line lands on that
        // bar line — which is right, and is not what this is about.
        Advance(player, 10);
        player.TransitionTo("Combat", MusicQuantize.Bar);
        Assert.Equal(96_000, player.QueuedAtFrame);

        // 96 000 frames from the origin, ten blocks of which have already gone by.
        Advance(player, (96_000 / Block) - 10 - 1);
        Assert.Equal("Explore", player.Current?.Name);

        Advance(player, 1);
        Assert.Equal("Combat", player.Current?.Name);
        Assert.Null(player.Queued);
        Assert.Equal(96_000, player.Transport.Origin);
    }

    /// <summary>
    ///     A cut that lands off the beat is heard as a mistake by people who cannot name a beat. At a
    ///     tempo whose beat is not a whole number of blocks, a scheduler that could only start voices
    ///     on block boundaries would be wrong by up to a block every time.
    /// </summary>
    [Fact]
    public void TheJoinLandsOnASampleThatIsNotABlockBoundary() {
        var player = new MusicPlayer(engine) { CrossfadeSeconds = 0f };

        player.Add(new MusicSegment {
            Name = "Odd",
            Clip = AudioTestData.Ramp(96_000),
            Tempo = new() { BeatsPerMinute = 137f, BeatsPerBar = 4 }
        });

        player.Add(Segment("Next"));
        player.Play("Odd");

        var origin = player.Transport.Origin;
        Advance(player, 5);
        player.TransitionTo("Next", MusicQuantize.Beat);

        // 48 000 × 60 / 137 is 21 021.9…, rounded once to 21 022 — which is not a multiple of 64.
        Assert.Equal(21_022, player.QueuedAtFrame - origin);
        Assert.NotEqual(0, 21_022 % Block);
    }

    /// <summary>What a scheduled start actually does to the buffer, measured against a ramp.</summary>
    [Fact]
    public void AScheduledVoiceIsSilentUntilItsFrameAndThenStartsFromTheTop() {
        // A hundred frames in, which is part way through the second block.
        var handle = engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings { StartFrame = 100 });

        Assert.True(handle.IsValid);

        var first = AudioTestData.Render(device, Block);
        Assert.Equal(0f, AudioTestData.Peak(first));

        var second = AudioTestData.Render(device, Block);

        // Frames 64..99 silent, then it begins at frame 100 — index 36 of this block.
        Assert.Equal(0f, MathF.Abs(second[35 * 2]), 1e-6f);
        Assert.True(MathF.Abs(second[36 * 2]) > 0f);
    }

    [Fact]
    public void ASegmentThatRunsOutMovesOnToWhatItNames() {
        var player = new MusicPlayer(engine) { CrossfadeSeconds = 0f };
        player.Add(Segment("Intro", frames: 6_400, next: "Loop", loops: 0));
        player.Add(Segment("Loop"));

        player.Play("Intro");
        Assert.Equal("Intro", player.Current?.Name);

        // 6 400 frames is a hundred blocks.
        Advance(player, 102);
        Assert.Equal("Loop", player.Current?.Name);
    }

    [Fact]
    public void ASegmentThatLoopsForeverNeverMovesOn() {
        var player = new MusicPlayer(engine);
        player.Add(Segment("Loop", frames: 6_400, next: "Never"));
        player.Add(Segment("Never"));

        player.Play("Loop");
        Advance(player, 300);

        Assert.Equal("Loop", player.Current?.Name);
    }

    [Fact]
    public void StoppingImmediatelyStopsAndStoppingOnABarWaits() {
        var player = new MusicPlayer(engine);
        player.Add(Segment("Explore"));

        player.Play("Explore");
        Advance(player, 10);

        player.Stop(MusicQuantize.Bar);
        Assert.Equal("Explore", player.Current?.Name);

        Advance(player, 1_500);
        Assert.Null(player.Current);
        Assert.False(player.IsPlaying);

        player.Play("Explore");
        player.Stop();
        Assert.Null(player.Current);
    }

    /// <summary>Which is what makes a light flash on the downbeat.</summary>
    [Fact]
    public void BeatsAndBarsAreRaisedAsThePlayheadCrossesThem() {
        var player = new MusicPlayer(engine);
        player.Add(Segment("Explore"));

        var beats = new List<long>();
        var bars = new List<long>();
        player.BeatPassed += beats.Add;
        player.BarPassed += bars.Add;

        player.Play("Explore");

        // Two bars at 120 in four is 192 000 frames, which is 3 000 blocks.
        Advance(player, 3_001);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], beats);
        Assert.Equal([0, 1, 2], bars);
    }

    /// <summary>A frame that ran long must not quietly skip nineteen of them.</summary>
    [Fact]
    public void ALongFrameRaisesEveryBeatItSkippedOver() {
        var player = new MusicPlayer(engine);
        player.Add(Segment("Explore"));

        var beats = new List<long>();
        player.BeatPassed += beats.Add;

        player.Play("Explore");

        // One enormous render, as a level load would produce.
        AudioTestData.Render(device, 24_000 * 5);
        player.Update();

        Assert.Equal([0, 1, 2, 3, 4, 5], beats);
    }

    [Fact]
    public void MarkersAreRaisedInOrderAndOnlyOnce() {
        var player = new MusicPlayer(engine);

        player.Add(new MusicSegment {
            Name = "Explore",
            Clip = AudioTestData.Ramp(192_000),
            Tempo = new() { BeatsPerMinute = 120f, BeatsPerBar = 4 },
            Markers = [new("drop", 2f), new("swell", 5f)]
        });

        var seen = new List<string>();
        player.MarkerPassed += seen.Add;

        player.Play("Explore");
        Advance(player, 800);
        Assert.Equal(["drop"], seen);

        Advance(player, 1_500);
        Assert.Equal(["drop", "swell"], seen);
    }

    [Fact]
    public void SegmentStartedIsRaisedWhenItActuallyBeginsRatherThanWhenItIsScheduled() {
        var player = new MusicPlayer(engine) { CrossfadeSeconds = 0f };
        player.Add(Segment("Explore"));
        player.Add(Segment("Combat"));

        var started = new List<string>();
        player.SegmentStarted += segment => started.Add(segment.Name);

        player.Play("Explore");
        Advance(player, 10);
        player.TransitionTo("Combat", MusicQuantize.Bar);

        Assert.Equal(["Explore"], started);

        Advance(player, 96_000 / Block);
        Assert.Equal(["Explore", "Combat"], started);
    }

    /// <summary>
    ///     What makes the rule content rather than a switch statement in whichever system noticed the
    ///     fight start.
    /// </summary>
    [Fact]
    public void ATransitionCanBeDrivenByAParameterInsteadOfACall() {
        engine.LoadParameters([
            new AudioBusParameterDefinition { Name = "intensity", Minimum = 0f, Maximum = 1f }
        ], out _);

        var player = new MusicPlayer(engine) { CrossfadeSeconds = 0f };
        player.Add(Segment("Explore"));
        player.Add(Segment("Combat"));

        player.AddTransition(new MusicTransition {
            From = "Explore",
            To = "Combat",
            Quantize = MusicQuantize.Bar,
            Parameter = "intensity",
            Minimum = 0.7f,
            Maximum = 1f
        });

        player.Play("Explore");
        Advance(player, 10);
        Assert.Null(player.Queued);

        engine.Parameters!.Set("intensity", 0.9f);
        engine.Update(0f);
        player.Update();

        Assert.Equal("Combat", player.Queued?.Name);

        Advance(player, 1_500);
        Assert.Equal("Combat", player.Current?.Name);
    }

    [Fact]
    public void ATransitionWhoseParameterIsOutOfRangeIsNotTaken() {
        engine.LoadParameters([new AudioBusParameterDefinition { Name = "intensity" }], out _);

        var player = new MusicPlayer(engine);
        player.Add(Segment("Explore"));
        player.Add(Segment("Combat"));

        player.AddTransition(new MusicTransition {
            From = "Explore",
            To = "Combat",
            Parameter = "intensity",
            Minimum = 0.7f,
            Maximum = 1f
        });

        player.Play("Explore");
        engine.Parameters!.Set("intensity", 0.2f);
        engine.Update(0f);
        Advance(player, 20);

        Assert.Null(player.Queued);
        Assert.Equal("Explore", player.Current?.Name);
    }

    [Fact]
    public void AnAssetBuildsAPlayerAndReportsWhatDidNotResolve() {
        var music = engine.CreateBus("Music");

        var player = MusicBuilder.Build(engine, new MusicAsset {
            Name = "Level",
            Bus = "Music",
            Start = "Explore",
            CrossfadeSeconds = 0.1f,
            Segments = [
                new() {
                    Name = "Explore",
                    Clip = new ContentReference<AudioClip>(default, AudioTestData.Ramp(96_000)),
                    BeatsPerMinute = 128f
                },
                new() { Name = "Broken" }
            ],
            Transitions = [
                new() { From = "Explore", To = "Combat", Parameter = "intensity" }
            ]
        }, out var problems);

        Assert.Equal(music.Index, player.Bus);
        Assert.Equal(0.1f, player.CrossfadeSeconds);
        Assert.Equal("Explore", player.Current?.Name);
        Assert.Equal(128f, player.Transport.Tempo.BeatsPerMinute);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("Broken", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Combat", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAssetWithAnUnknownBusOrStartSaysSo() {
        var player = MusicBuilder.Build(engine, new MusicAsset {
            Name = "Level",
            Bus = "Nowhere",
            Start = "Missing"
        }, out var problems);

        Assert.Equal(0, player.Bus);
        Assert.Null(player.Current);
        Assert.Equal(2, problems.Count);
    }
}
