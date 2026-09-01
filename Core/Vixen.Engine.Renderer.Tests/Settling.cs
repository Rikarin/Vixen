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

    /// <summary>Attempts until something is true, or until nothing could make it true.</summary>
    /// <param name="attempt">
    ///     One attempt: the ask, and the frame or fold that takes up whatever has landed. Run before
    ///     <paramref name="done" /> is read, because work that finished during an attempt is answered
    ///     by that attempt and not by the next one.
    /// </param>
    /// <param name="done">What is being waited for.</param>
    /// <param name="working">Whether anything is outstanding that a further attempt could take up.</param>
    /// <param name="never">What to say when the work runs out before <paramref name="done" /> holds.</param>
    public static void Until(Action attempt, Func<bool> done, Func<bool> working, string never) {
        var quiet = 0;

        while (true) {
            var before = working();

            attempt();

            if (done()) {
                return;
            }

            quiet = before || working() ? 0 : quiet + 1;

            Assert.True(
                quiet < Quiet,
                $"{never}, and for {quiet} attempts nothing has been outstanding — so no number of "
                + "further attempts can change it"
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
    ///         ⚠ <b>What this cannot see is a livelock, and that is a known gap rather than an
    ///         oversight.</b> A page that loads, is refused a slot and is put back by
    ///         <c>PageResidency.Renew</c> leaves <c>Loading</c> above zero on every frame for ever,
    ///         so a settle waiting on it waits for ever rather than failing. Reproduced by sabotaging
    ///         <c>PageResidency.Place</c> to refuse every arrival, which hangs
    ///         <c>AssetTextureStreamingTests</c> as well — the gap is inherited from the settle this
    ///         one is modelled on, not introduced here. The two waits that could be given a
    ///         progress-based give-up have one — <c>TextureStreamingTests.Drain</c> and
    ///         <c>TextureDemandTests.Quiet</c>, both of which detect it — and doing the same for a
    ///         whole renderer needs a signal this predicate does not have. The state it guards is one
    ///         <c>PageResidency.Service</c> explicitly argues is unreachable ("Pin's own budget check
    ///         is what stops this being reachable"), so it is written down here rather than defended
    ///         against with a guard invented in a hurry.
    ///     </para>
    /// </remarks>
    public static bool Working(WorldRenderer renderer, AssetReference texture) =>
        (renderer.Source is AssetMeshSource meshes && meshes.Requested - meshes.Loaded - meshes.Failed > 0)
        || renderer.Painter is AssetMaterialSource { Reading: > 0 }
        || renderer.Painted?.Reading(texture) is not null
        || renderer.Painted?.Streaming is { Loading: > 0 }
        || renderer.Painted?.Streaming is { PendingRequests: > 0 };
}
