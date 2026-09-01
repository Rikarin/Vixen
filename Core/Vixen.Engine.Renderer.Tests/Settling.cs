// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Engine.Renderer;
using Xunit;

namespace Tests;

/// <summary>
///     Waiting for asynchronous work in this assembly, without a clock.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every settle in this assembly used to give up after thirty seconds, and the thirty
///         seconds is the CI failure rather than the guard against it.</b> Counted on the runs to
///         2026-08-26: <c>WorldRendererTests</c> seventeen failures across all three operating
///         systems, <c>AssetWaterSourceTests</c> nine on Windows, <c>AssetTerrainSourceTests</c>
///         four, and <c>AssetTextureStreamingTests</c> the one that was fixed first and whose
///         remarks carry the measurement. On the failing run this assembly went from six seconds to
///         two minutes forty-three while ~170 others stayed within 1.2× of their neighbour, so
///         nothing was starved but this.
///     </para>
///     <para>
///         ⚠ <b>The remedy that does not work is raising the number</b> — it was raised once
///         already, from two hundred five-millisecond attempts to thirty seconds, and the remark
///         recording that is what these replace. The reads being waited for are
///         <see cref="Task.Run(Action)" />, and <c>build.sh Test</c> runs every test project at
///         once, so the pool inside one test host is saturated by other collections sitting in
///         settle loops of their own. A work item queued into a saturated pool waits on .NET's
///         thread injection, which adds about two threads a second: the delay is a property of how
///         many workers the whole host has blocked, and is unrelated to the read. Measured on this
///         machine — blocking two hundred pool workers delayed a newly queued item by <b>1 m 45
///         s</b>. Thirty seconds, sixty, two hundred are each a guess about somebody else's
///         scheduler.
///     </para>
///     <para>
///         So the giving-up condition is a fact about the thing being waited for. While it says it
///         has work outstanding the work exists and is worth another attempt, however long the pool
///         takes to run it; when it has said it has none at both ends of eight consecutive attempts,
///         no number of further attempts can change the answer, and that is a real failure reported
///         in milliseconds rather than in thirty seconds.
///     </para>
/// </remarks>
static class Settling {
    /// <summary>How many consecutive idle attempts mean the work is not merely late.</summary>
    /// <remarks>
    ///     ⚠ <b>Consecutive, and read at both ends of an attempt.</b> A read that finishes part way
    ///     through one leaves nothing outstanding by the end of it and has not been taken up yet —
    ///     the take-up is the next attempt's ask — so a single idle observation says nothing. This is
    ///     not a patience: eight idle attempts is eight milliseconds, and a loaded runner does not
    ///     reach it because a loaded runner has work outstanding.
    /// </remarks>
    public const int Quiet = 8;

    /// <summary>How many fruitless attempts mean the work is spinning rather than waiting.</summary>
    /// <remarks>
    ///     <para>
    ///         Larger than <see cref="Quiet" /> and for a different reason. That one counts attempts
    ///         on a source that has said it has nothing outstanding, where eight is already generous;
    ///         this one has to let a full pipeline fill — an attempt may start loads and place
    ///         nothing yet — before calling it a spin.
    ///     </para>
    ///     <para>
    ///         It is still not a patience, and the difference matters because this file exists to
    ///         say that patiences are wrong. Every attempt counted here is one in which the streamer
    ///         placed no page <em>and</em> refused one, which for an in-memory store is anomalous
    ///         however loaded the machine is; starvation refuses nothing, so a starved run never
    ///         reaches this number however long it takes.
    ///     </para>
    /// </remarks>
    public const int Rounds = 64;

    /// <summary>
    ///     What one attempt found: whether anything is outstanding, and how the outstanding work is
    ///     going.
    /// </summary>
    /// <param name="Outstanding">
    ///     Whether anything is on its way that a further attempt could take up. While this holds the
    ///     work exists and is worth waiting for, however long the pool takes to run it.
    /// </param>
    /// <param name="Placed">
    ///     How many pages have reached a slot since the streamer was built —
    ///     <c>TextureStreamer.Loads</c>, or zero for a wait with no streamer under it.
    /// </param>
    /// <param name="Refused">
    ///     How many arrivals have been refused a slot — <c>TextureStreamer.Rejections</c>, or zero
    ///     for a wait with no streamer under it.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three readings rather than one, because "outstanding" cannot tell starvation from
    ///         a spin and those are the two things a settle has to answer differently.</b> Under a
    ///         saturated pool the same pages sit in flight and no counter moves at all: waiting is
    ///         the correct answer and tolerating it however long it lasts is the whole point of this
    ///         file. Under a livelock — a page that loads, is refused a slot, and is put back by
    ///         <c>PageResidency.Renew</c> — something is outstanding on every attempt for ever too,
    ///         and waiting is never going to end.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Placement is the only progress, and the four cheaper signals were each measured
    ///         and rejected.</b> "In flight" is true for ever under the livelock; the queue getting
    ///         shorter is true on about every other attempt, because <c>Renew</c> re-queues what was
    ///         refused; a per-attempt refusal comparison resets on most attempts, because refusals
    ///         climb only every one to three; and <c>Loads</c> holding still cannot distinguish the
    ///         livelock from a starved pool, which is why it is the progress signal here and not the
    ///         give-up. See <c>TextureStreamingTests.Drain</c>, where the same shape is measured.
    ///     </para>
    /// </remarks>
    public readonly record struct Work(bool Outstanding, long Placed, long Refused);

    /// <summary>Attempts until something is true, or until nothing could make it true.</summary>
    /// <param name="attempt">
    ///     One attempt: the ask, and the frame or fold that takes up whatever has landed. Run before
    ///     <paramref name="done" /> is read, because work that finished during an attempt is answered
    ///     by that attempt and not by the next one.
    /// </param>
    /// <param name="done">What is being waited for.</param>
    /// <param name="working">Whether anything is outstanding that a further attempt could take up.</param>
    /// <param name="never">What to say when the work runs out before <paramref name="done" /> holds.</param>
    /// <remarks>
    ///     The form for a wait with no streamer under it, where a livelock has nothing to live in:
    ///     a source that only reads documents either has a read outstanding or does not. It reports
    ///     no placements and no refusals, which leaves <see cref="Rounds" />'s guard inert rather
    ///     than guessing on its behalf.
    /// </remarks>
    public static void Until(Action attempt, Func<bool> done, Func<bool> working, string never) =>
        Until(attempt, done, () => new Work(working(), 0, 0), never);

    /// <summary>Attempts until something is true, or until nothing could make it true.</summary>
    /// <param name="attempt">
    ///     One attempt: the ask, and the frame or fold that takes up whatever has landed. Run before
    ///     <paramref name="done" /> is read, because work that finished during an attempt is answered
    ///     by that attempt and not by the next one.
    /// </param>
    /// <param name="done">What is being waited for.</param>
    /// <param name="working">What is outstanding, and how it is going. See <see cref="Work" />.</param>
    /// <param name="never">What to say when the work runs out before <paramref name="done" /> holds.</param>
    /// <remarks>
    ///     <para>
    ///         <b>Two give-ups, because there are two ways for a wait to be hopeless and neither
    ///         implies the other.</b> Nothing outstanding at both ends of <see cref="Quiet" />
    ///         consecutive attempts is a source that has run out of things to do. Something
    ///         outstanding on every attempt, no page placed for <see cref="Rounds" /> of them, and
    ///         the refusal count climbing across that window is a streamer spinning: it is loading
    ///         pages, being refused a slot, and asking again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Over the window and not per attempt.</b> Measured under a sabotaged
    ///         <c>PageResidency</c> that refuses every arrival: something was outstanding on every
    ///         attempt for ever while refusals climbed only every one to three, so a per-attempt
    ///         comparison resets on most attempts and the loop never ends. The baseline is therefore
    ///         reset when a page is <em>placed</em>, which is the one event a livelock cannot
    ///         produce.
    ///     </para>
    /// </remarks>
    public static void Until(Action attempt, Func<bool> done, Func<Work> working, string never) {
        var quiet = 0;
        var stuck = 0;
        var start = working();
        var placed = start.Placed;
        var refused = start.Refused;

        while (true) {
            var before = working();

            attempt();

            if (done()) {
                return;
            }

            var after = working();

            quiet = before.Outstanding || after.Outstanding ? 0 : quiet + 1;

            Assert.True(
                quiet < Quiet,
                $"{never}, and for {quiet} attempts nothing has been outstanding — so no number of "
                + "further attempts can change it"
            );

            // ⚠ A page reaching a slot is the ONLY thing that counts as progress here, for the
            // reasons Work's remarks give: every cheaper signal is true for ever under the livelock
            // this exists to catch.
            if (after.Placed > placed) {
                stuck = 0;
                placed = after.Placed;
                refused = after.Refused;
            } else {
                stuck++;
            }

            // A long fruitless window is only worth waiting on while the work is WAITING rather than
            // SPINNING. Starvation moves no counter at all and is tolerated for ever; a spin moves
            // the refusal count, and that is the only thing that says so.
            var spinning = after.Refused > refused;

            Assert.True(
                stuck < Rounds || !spinning,
                $"{never}, and nothing has reached a slot for {stuck} attempts while refusals went "
                + $"from {refused} to {after.Refused} — it is spinning rather than waiting, so no "
                + "number of further attempts can change it"
            );

            // A yield rather than a budget: it hands the core to whatever is doing the reading, and
            // nothing above decides anything by how many of these have gone by.
            Thread.Sleep(1);
        }
    }

    /// <summary>Whether a mounted renderer has anything on its way that a further frame could take up.</summary>
    /// <param name="renderer">A renderer over mounted content.</param>
    /// <param name="texture">The streamed texture the fixture is waiting on.</param>
    /// <remarks>
    ///     <para>
    ///         The four asynchronous hops a mounted world makes, in the order it makes them: the mesh
    ///         document, the material document, the texture's KTX2 header, and the pages the streamer
    ///         asks for once the header has told it what the levels are.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>All four, and the ones early in the chain are the ones easy to leave out.</b>
    ///         Before the header read has been taken up the streamer has no texture registered at
    ///         all, so <c>Loading</c> and <c>PendingRequests</c> are both zero — a predicate reading
    ///         only those two would call the renderer idle on its first frame, before anything had
    ///         been asked for, and give up vacuously. That is the failure mode this whole exercise
    ///         is about: a settle that cannot fail is worse than one that fails flakily.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Cast rather than read off a typed property, because there is not one.</b>
    ///         <see cref="WorldRenderer.Source" /> and <see cref="WorldRenderer.Painter" /> are the
    ///         seams a project can replace, so they are typed as the interfaces; what
    ///         <see cref="WorldRenderer.Mount" /> puts in them is always the asset-backed pair, which
    ///         is what a fixture over mounted content is waiting for. A pattern match rather than a
    ///         cast, so a renderer built without mounting reads as idle instead of throwing.
    ///     </para>
    ///     <para>
    ///         <b>Which clauses actually hold a settle open, counted 2026-09-01 under two hundred
    ///         blocked pool workers.</b> <c>WorldRendererTests.AMountedWorldDrawsItsMeshInItsOwnMaterial</c>:
    ///         33 168 of 33 170 calls true, <em>all</em> of them the texture header read, over 1 m 38 s.
    ///         <c>TextureDemandTests.AWidthDriftingAcrossARungDoesNotOscillate</c>: three of five true,
    ///         one header read and two streamer clauses. The mesh and material clauses were true
    ///         <b>zero</b> times in either, because both of those loads complete synchronously over an
    ///         in-memory bundle — see <see cref="AssetMaterialSource.Reading" />, which records the
    ///         measurement. They are kept for the day the content is a real one, and they are honest
    ///         about costing nothing today.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A livelock reads as outstanding for ever, so the outstanding flag is not the
    ///         whole reading.</b> A page that loads, is refused a slot and is put back by
    ///         <c>PageResidency.Renew</c> leaves <c>Loading</c> above zero on every frame for ever.
    ///         Reproduced by sabotaging <c>PageResidency.Place</c> to refuse every arrival: before
    ///         the placement and refusal readings below were added, that hung
    ///         <c>TextureDemandTests</c> past 180 s and <c>AssetTextureStreamingTests</c> past 300 s
    ///         rather than failing either — the gap was inherited from the settle this one is
    ///         modelled on and was shared by both, which is what made it the helper's and not a
    ///         fixture's. It is the same shape <c>TextureStreamingTests.Drain</c> and
    ///         <c>TextureDemandTests.Quiet</c> already carried, and it is here rather than in each
    ///         fixture for the same reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The state guarded is one <c>PageResidency.Service</c> argues is unreachable</b> —
    ///         "Pin's own budget check is what stops this being reachable" — and that is an argument
    ///         about pinned pages rather than a proof about arrivals, which is why it is guarded
    ///         instead of assumed. What the guard costs a healthy run is nothing: it fires only when
    ///         no page has reached a slot for <see cref="Rounds" /> attempts <em>and</em> refusals
    ///         climbed across that window, and a starved pool refuses nothing.
    ///     </para>
    /// </remarks>
    public static Work Working(WorldRenderer renderer, AssetReference texture) =>
        new(
            (renderer.Source is AssetMeshSource meshes && meshes.Requested - meshes.Loaded - meshes.Failed > 0)
            || renderer.Painter is AssetMaterialSource { Reading: > 0 }
            || renderer.Painted?.Reading(texture) is not null
            || renderer.Painted?.Streaming is { Loading: > 0 }
            || renderer.Painted?.Streaming is { PendingRequests: > 0 },
            renderer.Painted?.Streaming?.Loads ?? 0,
            renderer.Painted?.Streaming?.Rejections ?? 0
        );
}
