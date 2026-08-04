// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live;

/// <summary>What a shard was built from: an assembly version and a content hash.</summary>
/// <remarks>
///     <para>
///         ADR-022. Both halves, and both of them filter placement rather than only rejecting a
///         handshake. Doc 16's session already refuses a peer whose content hash differs; making the
///         same number a <em>placement</em> term is the whole of the incremental-upgrade story,
///         because a client that has not fetched the catalog update yet is then routed to a shard
///         that still matches instead of being told no.
///     </para>
///     <para>
///         ⚠ <b><see cref="Content" /> is the catalog's <c>BuildHash</c>, not a second number.</b>
///         One value, three uses: the handshake compares it, placement filters on it, and
///         <c>vixen content build</c> produces it. A registry mapping "content version" to "catalog
///         hash" would be a fourth place for them to disagree.
///     </para>
/// </remarks>
/// <param name="Build">The assembly version, as the build stamped it. Free-form; compared verbatim.</param>
/// <param name="Content">The catalog's <c>BuildHash</c>.</param>
public readonly record struct RealmVersion(string Build, ulong Content) {
    /// <summary>Nothing in particular — what a spec that named no version decodes to.</summary>
    public static RealmVersion None => default;

    /// <summary>The assembly version. Null only on <c>default</c>; see <see cref="RealmInstanceId" />.</summary>
    public string Build { get; } = Build ?? "";

    /// <summary>Whether this names a version at all.</summary>
    public bool IsValid => !string.IsNullOrEmpty(Build);

    /// <summary>Whether a client carrying <paramref name="other" /> may be placed on this shard.</summary>
    /// <param name="other">What the client says it is.</param>
    /// <returns>Whether both halves agree.</returns>
    /// <remarks>
    ///     Equality, deliberately, with no ordering and no "compatible enough". A version comparison
    ///     with a policy in it is one somebody eventually widens to unblock a release, and the
    ///     failure it lets through is two peers that disagree about a replicated component's layout —
    ///     which is not a rejection, it is a corrupted world.
    /// </remarks>
    public bool Admits(RealmVersion other) => this == other;

    /// <summary>Reads one back.</summary>
    /// <param name="text">What <see cref="ToString" /> wrote.</param>
    /// <param name="version">The version, on success.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? text, out RealmVersion version) {
        version = None;

        if (text is null) {
            return false;
        }

        var separator = text.LastIndexOf('+');

        if (separator <= 0
            || !ulong.TryParse(
                text.AsSpan(separator + 1),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var content
            )) {
            return false;
        }

        version = new(text[..separator], content);

        return true;
    }

    /// <summary>Whether two versions are the same version.</summary>
    /// <param name="other">The other version.</param>
    /// <returns>Whether they are equal.</returns>
    /// <remarks>
    ///     Hand-written so that <c>default</c> equals a constructed empty one — see
    ///     <c>RealmEndpoint.Equals</c> for what the synthesized version costs.
    /// </remarks>
    public bool Equals(RealmVersion other) =>
        Content == other.Content && string.Equals(Build ?? "", other.Build ?? "", StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Build ?? "", Content);

    /// <inheritdoc />
    public override string ToString() =>
        IsValid ? string.Create(CultureInfo.InvariantCulture, $"{Build}+{Content:x16}") : "no version";
}
