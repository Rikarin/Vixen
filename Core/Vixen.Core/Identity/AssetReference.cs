// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core;

/// <summary>One asset pointing at another, as it appears in a <c>.vxscene</c>, <c>.vxmat</c> or prefab.</summary>
/// <remarks>
///     <para>
///         <b>A single scalar, and that is not cosmetics.</b>
///         <code>
///     albedo:   vx:9e8a44c9930c64e388ca034c5fe4c426
///     mesh:     vx:1a2b3c4d5e6f70819a2b3c4d5e6f7081#2b9e5f13
///     material: null
///     </code>
///         Unity writes the same thing as a three-key flow mapping. One scalar diffs on one line,
///         merges cleanly, and greps trivially — <c>rg 'vx:9e8a44c9'</c> finds every referrer, which
///         is the question anyone asks before deleting an asset. Unity's <c>type:</c> field is
///         dropped, because nothing reads it.
///     </para>
///     <para>
///         The <c>vx:</c> prefix marks it as a reference to both the parser and a human reading a
///         diff. Without it a GUID is indistinguishable from any other hex string in the file.
///     </para>
/// </remarks>
/// <param name="Asset">Which asset.</param>
/// <param name="SubAsset">Which object inside it, or <see cref="SubAssetId.Main" /> for the main one.</param>
[DataContract]
public readonly record struct AssetReference(AssetId Asset, SubAssetId SubAsset)
    : ISpanFormattable, ISpanParsable<AssetReference> {
    /// <summary>The prefix that marks a scalar as a reference.</summary>
    public const string Prefix = "vx:";

    /// <summary>How long the text form can be: the prefix, a GUID, a hash and a sub-asset id.</summary>
    public const int MaxTextLength = 3 + AssetId.TextLength + 1 + SubAssetId.TextLength;

    /// <summary>A reference to nothing.</summary>
    public static AssetReference Null => default;

    /// <summary>Whether it points at anything.</summary>
    public bool IsNull => Asset.IsEmpty;

    /// <summary>Points at an asset's main object.</summary>
    /// <param name="asset">The asset.</param>
    public AssetReference(AssetId asset) : this(asset, SubAssetId.Main) { }

    /// <summary>Renders the reference, or <c>null</c> if it points at nothing.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        IsNull
            ? "null"
            : SubAsset.IsMain
                ? Prefix + Asset
                : $"{Prefix}{Asset}#{SubAsset}";

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        destination.TryWrite(provider, $"{ToString()}", out charsWritten);

    /// <inheritdoc />
    public static AssetReference Parse(string s, IFormatProvider? provider = null) =>
        Parse((s ?? throw new ArgumentNullException(nameof(s))).AsSpan(), provider);

    /// <inheritdoc />
    public static AssetReference Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var result)
            ? result
            : throw new FormatException($"'{s}' is not an asset reference. Expected 'vx:<guid>' or 'vx:<guid>#<sub>'.");

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out AssetReference result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc />
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out AssetReference result) {
        result = default;

        if (s.IsEmpty || s.SequenceEqual("null") || s.SequenceEqual("~")) {
            return true;
        }

        if (!s.StartsWith(Prefix, StringComparison.Ordinal)) {
            return false;
        }

        var body = s[Prefix.Length..];
        var hash = body.IndexOf('#');

        if (hash < 0) {
            if (!AssetId.TryParse(body, provider, out var asset)) {
                return false;
            }

            result = new(asset);
            return true;
        }

        if (!AssetId.TryParse(body[..hash], provider, out var owner)
            || !SubAssetId.TryParse(body[(hash + 1)..], provider, out var sub)) {
            return false;
        }

        result = new(owner, sub);
        return true;
    }

    /// <summary>Parses a reference, or <c>null</c>.</summary>
    /// <param name="s">The text.</param>
    /// <param name="result">The reference.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out AssetReference result) =>
        TryParse(s, null, out result);

    /// <summary>Parses a reference, or <c>null</c>.</summary>
    /// <param name="s">The text.</param>
    /// <param name="result">The reference.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, out AssetReference result) => TryParse(s, null, out result);
}
