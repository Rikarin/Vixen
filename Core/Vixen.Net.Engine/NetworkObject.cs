// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Replication;

namespace Vixen.Net.Engine;

/// <summary>Marks a node of a prefab or a scene as one the network should give an id to.</summary>
/// <remarks>
///     <para>
///         <b>The authored half of <see cref="NetworkId" />, and the two are deliberately different
///         components.</b> A <see cref="NetworkId" /> is a number the server allocated — a handle,
///         which only exists once a session does. This is a designer's claim about content: <i>this
///         entity is a thing the network addresses</i>, made before any peer has connected and true
///         in every process that loads the asset. One is runtime state and one is authored fact, and
///         a component that tried to be both would have to be written into a scene file to be
///         authorable — which would let a designer, or a play-mode save, put a number in content that
///         no server ever handed out.
///     </para>
///     <para>
///         ⚠ <b><c>[Component]</c> and <c>[DataContract]</c>, which is the whole point of it.</b>
///         What a compiled scene may name is a component carrying both
///         (<c>SceneComponentRegistry</c>), and <see cref="NetworkId" /> carries only the first —
///         which it cannot fix, because <c>Vixen.Net</c> may not reference <c>Vixen.Engine</c> and so
///         runs neither generator. So the marker lives in the assembly that already sees both, beside
///         <see cref="Players.PlayerPawn" />, which is an authored fact about a prefab for the same
///         reason.
///     </para>
///     <para>
///         <b>A tag, so it costs a bit in an archetype mask and not a byte of chunk memory.</b> There
///         is nothing to say beyond the marking: which id the node ends up with is decided by the
///         thing that instantiates it — <c>NetworkIdAllocator</c> for a spawn,
///         <see cref="NetworkSceneId.BakedId" /> for an object a scene placed.
///     </para>
/// </remarks>
/// <example>
///     Authored, in a <c>.vxprefab</c>. The empty flow mapping is not decoration — a tag has no
///     members, and a node that is only a type tag is a scalar rather than a mapping:
///     <code>
///     - name: Barrel
///       components:
///         - !NetworkObject {}
///     </code>
/// </example>
[Component]
[DataContract]
public struct NetworkObject : ITagComponent;
