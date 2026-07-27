// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport;

/// <summary>What a transport will carry, stated by the transport rather than assumed by its callers.</summary>
/// <remarks>
///     Three facts, each of which changes what the layer above does. The size cap decides where
///     fragmentation happens; <paramref name="IsLossy" /> decides whether the reliability layer has
///     anything to do; <paramref name="IsInProcess" /> is what lets a listen server skip serialising
///     a payload it is about to hand to itself.
/// </remarks>
/// <param name="MaxPayloadBytes">
///     The largest single payload. Not a suggestion — a longer one throws rather than being split,
///     because a transport that silently fragments is a transport whose bandwidth numbers lie.
/// </param>
/// <param name="IsInProcess">
///     Whether both ends are in this process. True for the local transport, false for anything with
///     a socket in it.
/// </param>
/// <param name="IsLossy">
///     Whether an unreliable payload may be lost, duplicated or reordered on the way. False only for
///     a transport that physically cannot lose one — which is the local transport, until
///     <see cref="NetworkSimulation" /> is wrapped around it.
/// </param>
public readonly record struct TransportCapabilities(int MaxPayloadBytes, bool IsInProcess, bool IsLossy);
