// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Net.Engine.Players;

/// <summary>Marks a body a player may be given. Placed on the prefab, never on the wire.</summary>
/// <remarks>
///     <para>
///         <b>It costs nothing to replicate because it is not replicated.</b> A networked prefab is
///         instantiated from the same content on both ends, so a tag authored on it is present on the
///         client the moment <c>NetworkSpawnSystem</c> builds the instance — which is what lets a
///         client work out which of the things it owns is the one it drives without being told, and
///         without a byte of wire spent saying so.
///     </para>
///     <para>
///         ⚠ <b><c>[Component]</c> and <c>[DataContract]</c>, deliberately both.</b> Unlike the
///         possession edge, this carries no handle and no derived state: it is an authored fact about
///         a prefab, so a prefab and a scene must both be able to say it.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct PlayerPawn : ITagComponent;
