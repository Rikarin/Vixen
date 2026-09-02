// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Rules;

namespace Vixen.Net.Engine;

/// <summary>Names the policy file a node of a prefab or a scene is governed by.</summary>
/// <remarks>
///     <para>
///         <b>The authored half of <see cref="NetworkRulesRegistry.Set" />.</b> The registry holds a
///         policy per <c>NetworkId</c>, which is a number the server allocated and therefore
///         something no content can carry; this is a designer's claim about content — <i>this thing
///         plays by these rules</i> — made before any peer has connected and true in every process
///         that loads the asset. <see cref="NetworkObject" /> makes exactly the same split for
///         exactly the same reason, one question over.
///     </para>
///     <para>
///         ⚠ <b>A name and not a handle, on <c>WaterZoneComponent.WaveAsset</c>'s terms.</b> A prefab
///         is content, and content cannot hold a reference to a thing the content build has not
///         loaded yet — so what survives the build is the name the policy calls itself, and
///         <c>NetworkSpawner</c> resolves it against
///         <see cref="NetworkRulesRegistry.TryGetNamed" /> at the moment an instance gets its id.
///     </para>
///     <para>
///         ⚠ <b>A name that resolves to nothing is not an error and is not silent either.</b> The
///         instance falls back to the registry's default — which is server-authoritative, the safe
///         answer — and <c>NetworkSpawner.UnresolvedRules</c> counts it. A policy that quietly did
///         not apply is how a co-operative game ships with a rule nobody notices is missing until a
///         player cannot pick anything up.
///     </para>
/// </remarks>
/// <example>
///     Authored, in a <c>.vxprefab</c>:
///     <code>
///     - name: Sword
///       components:
///         - !NetworkObject {}
///         - !NetworkRulesReference { asset: Pickup }
///     </code>
/// </example>
[Component]
[DataContract]
public struct NetworkRulesReference {
    /// <summary>The <see cref="NetworkRulesAsset.Name" /> of the policy that governs this node.</summary>
    public string Asset;
}
