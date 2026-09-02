// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Net.Rules;

namespace Vixen.Net.Engine;

/// <summary>A network policy as a file: what a <c>.vxnetrules</c> holds.</summary>
/// <remarks>
///     <para>
///         <b>[16 § Rules](../../../docs/plan/16-networking.md) makes the policy a declaration rather
///         than a switch, and this is the declaration on disk.</b> A co-operative game and a
///         competitive shooter want different answers to every question in
///         <see cref="NetworkRules" />; with a file, relaxing server authority is an explicit,
///         reviewable decision somebody wrote down and a reviewer can read in a diff.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Vixen.Net</c>, beside <see cref="NetworkObject" /> and for
///         exactly its reason.</b> The reference to this asset is a component a prefab carries, and
///         what a compiled scene may name is a component with <c>[Component]</c> <i>and</i>
///         <c>[DataContract]</c> — which <c>Vixen.Net</c> cannot produce, because it may not
///         reference <c>Vixen.Engine</c> and so runs neither generator. The asset lives beside the
///         reference so that one assembly answers for both halves.
///     </para>
///     <para>
///         ⚠ <b>A wrapper around <see cref="NetworkRules" /> rather than the rules themselves, and
///         the wrapper is what carries the name.</b> A prefab names this asset by name — see
///         <see cref="NetworkRulesReference" /> for why a name and not a handle — so the file has to
///         hold one, and a bare policy has nowhere to put it. The same shape, and the same argument,
///         as <c>WaterWavesAsset</c> around a spectrum.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     name: Pickup
///     rules:
///       changeOwner: Everyone
///       claim: WhenUnowned
///       onOwnerDisconnect: TransferToServer
///     </code>
///     A dropped weapon anybody may take and nobody may steal — the rule neither
///     <see cref="NetworkRules.ChangeOwner" /> nor <see cref="NetworkRules.Claim" /> can spell alone.
/// </example>
[DataContract]
public sealed class NetworkRulesAsset {
    /// <summary>What a policy file is called on disk.</summary>
    public const string Extension = ".vxnetrules";

    /// <summary>What it is called, which is what a prefab names it by.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The policy itself.</summary>
    public NetworkRules Rules { get; set; } = NetworkRules.ServerAuthoritative;

    /// <summary>Why this policy cannot mean what it says, or <see langword="null" /> if it can.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One rule, and it is the one combination that reads as a decision and is not
    ///         one.</b> <see cref="OwnershipClaim.WhenUnowned" /> constrains <i>clients</i> taking
    ///         things from each other, so it says nothing at all when no client may ask — and a file
    ///         whose author wrote both lines meant the first to do something. Every other pairing has
    ///         a defensible reading.
    ///     </para>
    ///     <para>
    ///         Refused at import rather than at run time, because at run time the symptom is a
    ///         pick-up that never happens and a policy file that looks exactly right.
    ///     </para>
    /// </remarks>
    public string? Validate() =>
        Rules.Claim == OwnershipClaim.WhenUnowned && Rules.ChangeOwner == RuleAudience.ServerOnly
            ? "claim: WhenUnowned says when a client may take ownership, and changeOwner: ServerOnly "
            + "says no client ever may — so the claim decides nothing. Widen changeOwner to Owner or "
            + "Everyone, or drop the claim."
            : null;
}
