// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport;

/// <summary>
///     A profile and the seed it is drawn against: everything
///     <see cref="NetworkSimulation" /> needs, in one value a session's options can carry.
/// </summary>
/// <remarks>
///     <para>
///         <b>The two together rather than two properties, because either alone is a trap.</b> A seed
///         with no profile does nothing and says nothing; a profile with a defaulted seed is exactly
///         what <see cref="NetworkSimulation" />'s constructor refuses to accept — <i>"a simulation
///         whose seed was picked for you is a simulation whose failures you cannot reproduce"</i>.
///         Requiring both in one construction is what keeps that rule when the decision moves from a
///         call site onto a record.
///     </para>
///     <para>
///         ⚠ <b>A seed per participant, not a seed per match.</b> Eight clients handed the same seed
///         lose the same packets in the same order, which is a synchronised outage rather than a bad
///         network and will make a bug look like a server fault. <c>Samples/08-Multiplayer</c>
///         derives each participant's from the match's, and that is the shape to copy.
///     </para>
/// </remarks>
/// <param name="Profile">How bad the link pretends to be.</param>
/// <param name="Seed">
///     The seed every random decision is drawn from. Stated rather than defaulted, and worth printing
///     at startup: the five seconds spent typing a number is the price of every future bug report
///     being replayable.
/// </param>
public sealed record NetworkSimulationSettings(NetworkSimulationProfile Profile, ulong Seed) {
    /// <summary>What a development build should run with, given a seed.</summary>
    /// <param name="seed">The seed, which is still the caller's to choose and to print.</param>
    /// <returns>The settings.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="NetworkSimulationProfile.Broadband" /> because that profile already says
    ///     in its own summary that it is the one a development build should run with</b> — 35 ms one
    ///     way, 8 ms of jitter, 0.5 % loss. Naming a sixth profile for this would be surface with no
    ///     caller in a record that already has five. What this adds over writing
    ///     <c>new(NetworkSimulationProfile.Broadband, seed)</c> is only that the choice has one name
    ///     to grep for.
    /// </remarks>
    public static NetworkSimulationSettings Development(ulong seed) => new(NetworkSimulationProfile.Broadband, seed);
}
