// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Gameplay;

/// <summary>What a piece of authored content is called on the wire and in a saved row.</summary>
/// <remarks>
///     <para>
///         <b>The stable hash of an address, which means no registry and no exchange.</b> Doc 28
///         § Definitions: <c>DefId.From("items/flamebrand")</c> is the same number in the editor, in
///         the realm, in the grain and in the client, computed independently by each of them from
///         content they already agreed on. The alternative — the numbered definition list every game
///         without a content pipeline grows — is an ordered array whose indices are the wire format,
///         and it desynchronises the first time two people add an item on two branches.
///     </para>
///     <para>
///         Deliberately the same construction as <c>NetworkPrefabId</c> and <c>NetworkSceneId</c>
///         ([16](../../../docs/plan/16-networking.md)): 32-bit FNV-1a over the address, zero reserved
///         for "nothing". Deliberately the <em>address</em> and not the content hash — the content
///         hash changes with every edit to the item, so every balance patch would renumber the wire.
///     </para>
///     <para>
///         ⚠ <b>Thirty-two bits collide, and the collision is refused rather than tolerated.</b> Two
///         addresses hashing alike would be two definitions nothing can tell apart, so
///         <see cref="DefinitionCatalogBuilder" /> fails the content build and names both. At ten
///         thousand definitions the odds are about one in a hundred, which is a number a project
///         should be told rather than left to discover in a support ticket.
///     </para>
/// </remarks>
/// <param name="Value">The hash. Zero is <see cref="None" />.</param>
public readonly record struct DefId(uint Value) {
    /// <summary>Not a definition.</summary>
    public static DefId None => default;

    /// <summary>Whether this names one.</summary>
    public bool IsSome => Value != 0;

    /// <summary>The id an address hashes to.</summary>
    /// <param name="address">The addressable's address — <c>items/flamebrand</c>.</param>
    /// <returns>Its id.</returns>
    /// <remarks>
    ///     FNV-1a, because it has to be reproducible from a string in every process that ever runs,
    ///     in whatever language a pipeline is written in — which rules out anything a runtime seeds
    ///     per process.
    /// </remarks>
    public static DefId From(string? address) {
        if (string.IsNullOrEmpty(address)) {
            return None;
        }

        var hash = 2166136261u;

        foreach (var character in address) {
            hash ^= character;
            hash *= 16777619u;
        }

        // Zero is "nothing", so an address that hashed to it would be indistinguishable from absence.
        return new(hash == 0 ? 1u : hash);
    }

    /// <inheritdoc />
    public override string ToString() =>
        Value == 0 ? "no definition" : string.Create(CultureInfo.InvariantCulture, $"def {Value:x8}");
}
