// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>What a game that never calls <c>Update</c> sounds like, and what its panel would say.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Written because <c>Samples/13</c> was that game.</b> It constructed an
///         <see cref="AudioEngine" />, fired one-shots at it for the whole run, and never called
///         <see cref="AudioEngine.Update()" /> or registered an <c>AudioSystem</c> — so the arena went
///         silent after its sixty-fourth sound and nothing said why. <c>Update</c> is the only thing
///         that returns a finished voice to the pool: <c>TryClaim</c> takes only free slots and
///         <c>TrySteal</c> considers only playing or paused ones, so a <c>Finished</c> slot is
///         unreachable to both.
///     </para>
///     <para>
///         ⚠ <b>And <c>AudioStatistics</c> is assembled inside the same call</b>, which is what makes
///         this the audio overlay's business rather than a footnote. A panel wired to an engine in
///         this state draws <c>load 0 %</c>, <c>voices 0/0</c> and <c>0/0/0</c> faults — a full set of
///         plausible idle numbers over a subsystem that has stopped working, which is worse than no
///         panel at all.
///     </para>
/// </remarks>
public sealed class UncollectedVoiceTests {
    /// <summary>A pool that is never collected fills up permanently.</summary>
    [Fact]
    public void One_shots_exhaust_the_pool_when_nothing_collects_them() {
        var (engine, device) = AudioTestData.Engine(voices: 4);

        using var owned = engine;

        var clip = AudioTestData.Impulse(frames: 16);

        // Four short sounds, each rendered to its end. Every one of them is Finished by the time the
        // next starts, and every one of them is still holding a slot.
        for (var index = 0; index < 4; index++) {
            engine.Play(clip);
            AudioTestData.Render(device, 256);
        }

        var handle = engine.Play(clip);

        Assert.False(handle.IsValid, "a fifth sound found a slot in a pool of four finished ones");

        // The counter that says so, and which nothing in the tree was reading: the request was
        // dropped rather than stealing, because a finished voice cannot be stolen either.
        engine.Update(0f);

        Assert.True(engine.Statistics.DroppedRequests > 0);
    }

    /// <summary>And one that is collected keeps working for as long as the game runs.</summary>
    /// <remarks>
    ///     The same loop with the one call the sample was missing. Sabotage the fix by deleting the
    ///     <c>Update</c> line and this fails at the fifth sound, which is exactly the defect.
    /// </remarks>
    [Fact]
    public void Updating_the_engine_returns_finished_voices_to_the_pool() {
        var (engine, device) = AudioTestData.Engine(voices: 4);

        using var owned = engine;

        var clip = AudioTestData.Impulse(frames: 16);

        for (var index = 0; index < 32; index++) {
            var handle = engine.Play(clip);

            Assert.True(handle.IsValid, $"sound {index} found no voice in a pool that is being collected");

            AudioTestData.Render(device, 256);
            engine.Update(0f);
        }

        Assert.Equal(0, engine.Statistics.DroppedRequests);

        // And the numbers the panel draws are real numbers now rather than the zeroed struct an
        // engine that is never updated keeps for ever.
        Assert.Equal(4, engine.Statistics.VoiceCapacity);
    }
}
