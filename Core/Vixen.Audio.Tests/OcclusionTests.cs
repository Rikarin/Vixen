// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Mixing;
using Vixen.Audio.Parameters;
using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>Occlusion, without anything that can cast a ray.</summary>
public sealed class OcclusionTests {
    /// <summary>A provider that says whatever a test wants it to.</summary>
    sealed class Fixed : IAudioOcclusionProvider {
        public float Value { get; set; }

        public int Asked { get; private set; }

        public float Occlusion(in Vector3 source, in Vector3 listener) {
            Asked++;
            return Value;
        }
    }

    /// <summary>A provider that answers by how far along the X axis the source is.</summary>
    sealed class ByPosition : IAudioOcclusionProvider {
        public float Occlusion(in Vector3 source, in Vector3 listener) => Math.Clamp(source.X, 0f, 1f);
    }

    static PlaybackSettings At(Vector3 position) => new() {
        IsSpatial = true,
        Spatial = new SpatialSettings { Position = position, MaxDistance = 1_000f }
    };

    /// <summary>Runs the engine for a while, so the seek has time to arrive.</summary>
    static void Settle(AudioEngine engine, float seconds = 1f) {
        for (var i = 0; i < 60; i++) {
            engine.Update(seconds / 60f);
        }
    }

    [Fact]
    public void WithNoProviderNothingIsOccluded() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            var handle = engine.Play(AudioTestData.Constant(48_000, 1f), At(new Vector3(10f, 0f, 0f)));
            Settle(engine);

            Assert.True(handle.IsValid);
            Assert.Equal(0f, engine.OcclusionOf(handle));
            Assert.Equal(0, engine.Occlusion.Queries);
        }
    }

    [Fact]
    public void AProviderThatSaysBlockedBlocks() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            engine.Occlusion.Provider = new Fixed { Value = 1f };

            var handle = engine.Play(AudioTestData.Constant(48_000, 1f), At(new Vector3(10f, 0f, 0f)));
            Settle(engine);

            Assert.Equal(1f, engine.OcclusionOf(handle), 1e-3f);
            Assert.True(engine.Occlusion.Queries > 0);
        }
    }

    /// <summary>
    ///     The flicker guard. A source at a doorway swings between blocked and clear as either end
    ///     moves; taking that at face value is a stutter, so it has to arrive over time.
    /// </summary>
    [Fact]
    public void TheAnswerArrivesOverTimeRatherThanAtOnce() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            engine.Occlusion.Provider = new Fixed { Value = 1f };
            engine.Occlusion.SeekSeconds = 1f;

            var handle = engine.Play(AudioTestData.Constant(48_000, 1f), At(new Vector3(10f, 0f, 0f)));

            // A tenth of the way across the range, so about a tenth of the way there.
            engine.Update(0.1f);

            var partial = engine.OcclusionOf(handle);

            Assert.True(partial is > 0f and < 0.5f, $"one tenth of a second in it was already at {partial:F3}");

            Settle(engine);
            Assert.Equal(1f, engine.OcclusionOf(handle), 1e-3f);
        }
    }

    [Fact]
    public void ASeekOfZeroArrivesImmediately() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            engine.Occlusion.Provider = new Fixed { Value = 1f };
            engine.Occlusion.SeekSeconds = 0f;

            var handle = engine.Play(AudioTestData.Constant(48_000, 1f), At(new Vector3(10f, 0f, 0f)));
            engine.Update(1f / 60f);

            Assert.Equal(1f, engine.OcclusionOf(handle));
        }
    }

    /// <summary>A 2D sound has no position for a ray to start at, so nothing is asked about it.</summary>
    [Fact]
    public void ASoundThatIsNotInTheWorldIsNotAskedAbout() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            var provider = new Fixed { Value = 1f };
            engine.Occlusion.Provider = provider;

            var handle = engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings());
            Settle(engine);

            Assert.Equal(0, provider.Asked);
            Assert.Equal(0f, engine.OcclusionOf(handle));
        }
    }

    /// <summary>The cost claim: fixed per frame, whatever the pool is doing.</summary>
    [Fact]
    public void NoMoreThanTheBudgetIsAskedPerFrame() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            var provider = new Fixed { Value = 0.5f };
            engine.Occlusion.Provider = provider;
            engine.Occlusion.Budget = 2;

            for (var i = 0; i < 8; i++) {
                engine.Play(AudioTestData.Constant(48_000, 1f), At(new Vector3(i + 1f, 0f, 0f)));
            }

            engine.Update(1f / 60f);
            Assert.Equal(2, provider.Asked);

            engine.Update(1f / 60f);
            Assert.Equal(4, provider.Asked);
        }
    }

    /// <summary>And that the round robin gets all the way round rather than asking the same two.</summary>
    [Fact]
    public void EveryVoiceGetsItsTurn() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            engine.Occlusion.Provider = new ByPosition();
            engine.Occlusion.Budget = 1;
            engine.Occlusion.SeekSeconds = 0f;

            var clear = engine.Play(AudioTestData.Constant(48_000, 1f), At(new Vector3(0f, 0f, 0f)));
            var blocked = engine.Play(AudioTestData.Constant(48_000, 1f), At(new Vector3(1f, 0f, 0f)));

            // One cast a frame over a whole pool: enough frames for the cursor to come round twice.
            for (var i = 0; i < engine.VoiceCapacity * 2; i++) {
                engine.Update(1f / 60f);
            }

            Assert.Equal(0f, engine.OcclusionOf(clear));
            Assert.Equal(1f, engine.OcclusionOf(blocked));
        }
    }

    /// <summary>
    ///     The bug this shape exists to prevent, and the same one the parameter automation had: a
    ///     footstep that takes an occluded voice's slot must not be muffled by a wall it is not
    ///     behind.
    /// </summary>
    [Fact]
    public void ASoundThatTakesAnOccludedVoicesSlotIsNotOccluded() {
        var (engine, _) = AudioTestData.Engine(voices: 1);

        using (engine) {
            engine.Occlusion.Provider = new ByPosition();
            engine.Occlusion.SeekSeconds = 0f;

            var muffled = engine.Play(
                AudioTestData.Constant(48_000, 1f),
                At(new Vector3(1f, 0f, 0f)) with { Priority = 0 }
            );

            Settle(engine);
            Assert.Equal(1f, engine.OcclusionOf(muffled));

            // The only slot there is, taken by something with a clear path.
            var footstep = engine.Play(
                AudioTestData.Constant(48_000, 1f),
                At(new Vector3(0f, 0f, 0f)) with { Priority = 10 }
            );

            Assert.True(footstep.IsValid);
            Assert.False(engine.IsPlaying(muffled));

            // Before any update has had a chance to re-query: the value the new sound starts at is
            // what matters, because that is the block it is first heard in.
            Assert.Equal(0f, engine.OcclusionOf(footstep));
        }
    }

    /// <summary>A sound two players can hear is as occluded as the one with the better view of it.</summary>
    [Fact]
    public void WithTwoListenersTheClearerPathWins() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            var listeners = new AudioListenerSet();
            listeners.TryAdd(new AudioListener { Position = new Vector3(-50f, 0f, 0f) });
            listeners.TryAdd(new AudioListener { Position = new Vector3(50f, 0f, 0f) });
            engine.SetListeners(listeners);

            // Blocked from one ear, clear from the other.
            engine.Occlusion.Provider = new Blocking(new Vector3(-50f, 0f, 0f));
            engine.Occlusion.SeekSeconds = 0f;

            var handle = engine.Play(AudioTestData.Constant(48_000, 1f), At(new Vector3(0f, 0f, 0f)));
            Settle(engine);

            Assert.Equal(0f, engine.OcclusionOf(handle));
        }
    }

    sealed class Blocking(Vector3 blockedFrom) : IAudioOcclusionProvider {
        public float Occlusion(in Vector3 source, in Vector3 listener) =>
            Vector3.Distance(listener, blockedFrom) < 1e-3f ? 1f : 0f;
    }

    // ── Onto a curve ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The point of routing it through a parameter: what a wall sounds like is drawn, not
    ///     hard-coded. And what it should mostly do is dull the sound rather than quieten it.
    /// </summary>
    [Fact]
    public void ACurveAgainstOcclusionClosesTheFilter() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            engine.Occlusion.Provider = new Fixed { Value = 1f };
            engine.Occlusion.SeekSeconds = 0f;

            var sheet = new AudioParameterSheet([
                new AudioParameterDefinition {
                    Name = "occlusion",
                    Builtin = AudioBuiltinParameter.Occlusion,
                    Maximum = 1f,
                    Automation = [new(AudioParameterTarget.LowPassHz, AudioCurve.Ramp(24_000f, 400f))]
                }
            ]);

            var handle = engine.Play(AudioTestData.Constant(48_000, 1f), At(new Vector3(10f, 0f, 0f)));
            engine.AttachParameters(handle, sheet);

            engine.Update(1f / 60f);

            Assert.Equal(1f, engine.ParameterOf(handle, 0), 1e-3f);
            Assert.Equal(400f, engine.LowPassOf(handle), 1f);
        }
    }

    [Fact]
    public void GameplayCannotSetABuiltInOcclusionParameterByHand() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            var sheet = new AudioParameterSheet([
                new AudioParameterDefinition { Name = "occlusion", Builtin = AudioBuiltinParameter.Occlusion }
            ]);

            var handle = engine.Play(AudioTestData.Constant(48_000, 1f), At(Vector3.Zero));
            engine.AttachParameters(handle, sheet);

            Assert.False(engine.SetParameter(handle, "occlusion", 1f));
        }
    }
}
