// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;

namespace Vixen.Shaders;

/// <summary>
///     A shader name plus the permutation values that select one variant of it.
/// </summary>
/// <remarks>
///     <para>
///         The cache key for everything downstream: an in-memory dictionary, an on-disk bytecode
///         cache, and the build-time enumeration that has to produce a superset of what a
///         playthrough asks for. All three need the same value to mean the same variant, which is
///         why this is a value type with a stated normal form rather than whatever a dictionary of
///         objects happened to hash to.
///     </para>
///     <para>
///         <strong>Only keys the shader actually branched on belong here.</strong> Raven reports
///         <c>UsedPermutationKeys</c> for exactly this reason: twenty declared flags where three
///         affect the output is eight distinct shaders, not a million. Filtering is the caller's —
///         <see cref="From" /> takes the keys to include — because only the effect system knows
///         which shader is being keyed, and a key that quietly dropped values would be a cache that
///         returns the wrong shader.
///     </para>
///     <para>
///         Sorted by name so that setting the same values in a different order gives the same key.
///         Without it the cache would hold one entry per insertion order and hit almost never, which
///         is the sort of miss that shows up as a frame-time cliff rather than as a wrong image.
///     </para>
/// </remarks>
public readonly struct EffectKey : IEquatable<EffectKey> {
    readonly ImmutableArray<KeyValuePair<string, string>> values;
    readonly int hash;

    /// <summary>The shader this selects a variant of.</summary>
    public string ShaderName { get; }

    /// <summary>The permutation values, sorted by name.</summary>
    public ImmutableArray<KeyValuePair<string, string>> Values => values.IsDefault ? [] : values;

    EffectKey(string shaderName, ImmutableArray<KeyValuePair<string, string>> sorted) {
        ShaderName = shaderName;
        values = sorted;

        var accumulated = new HashCode();
        accumulated.Add(shaderName, StringComparer.Ordinal);

        foreach (var (name, value) in sorted) {
            accumulated.Add(name, StringComparer.Ordinal);
            accumulated.Add(value, StringComparer.Ordinal);
        }

        hash = accumulated.ToHashCode();
    }

    /// <summary>The key for a shader with no permutations set.</summary>
    public static EffectKey Of(string shaderName) {
        ArgumentException.ThrowIfNullOrEmpty(shaderName);
        return new(shaderName, []);
    }

    /// <summary>
    ///     The key for a shader, taking from <paramref name="parameters" /> only the permutations
    ///     named in <paramref name="used" />.
    /// </summary>
    /// <param name="shaderName">The shader.</param>
    /// <param name="parameters">Where the values come from. A key it does not set takes its default.</param>
    /// <param name="used">
    ///     The permutation keys this shader's output actually depends on — Raven's
    ///     <c>UsedPermutationKeys</c>.
    /// </param>
    public static EffectKey From(string shaderName, ParameterCollection parameters, IEnumerable<ParameterKey> used) {
        ArgumentException.ThrowIfNullOrEmpty(shaderName);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(used);

        var builder = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();

        foreach (var key in used) {
            if (!key.IsPermutation) {
                throw new ArgumentException(
                    $"'{key.Name}' is a value key, so it cannot select a variant. Only permutation "
                    + "keys belong in an effect key.",
                    nameof(used)
                );
            }

            builder.Add(new(key.Name, Text(parameters, key)));
        }

        // Ordinal by name, so the same values set in a different order are the same key. Without
        // this the cache holds one entry per insertion order and hits almost never.
        builder.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return new(shaderName, builder.ToImmutable());
    }

    /// <summary>One permutation's value, as the text the compiler's <c>--define</c> takes.</summary>
    /// <remarks>
    ///     Text rather than the value's own type, because this crosses two boundaries where a type
    ///     cannot follow it: an on-disk cache filename and a compiler command line. Invariant
    ///     culture and lowercase booleans, so a machine with a comma decimal separator produces the
    ///     same key — which is what makes a shared bytecode cache shareable.
    /// </remarks>
    static string Text(ParameterCollection parameters, ParameterKey key) {
        foreach (var (candidate, value) in parameters.Permutations) {
            if (ReferenceEquals(candidate, key)) {
                return Format(value);
            }
        }

        return Format(Default(key));
    }

    static object Default(ParameterKey key) =>
        key switch {
            PermutationKey<bool> flag => flag.DefaultValue,
            PermutationKey<int> number => number.DefaultValue,
            PermutationKey<uint> number => number.DefaultValue,
            _ => string.Empty
        };

    static string Format(object value) =>
        value switch {
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    /// <inheritdoc />
    public bool Equals(EffectKey other) {
        if (hash != other.hash
            || !string.Equals(ShaderName, other.ShaderName, StringComparison.Ordinal)
            || Values.Length != other.Values.Length) {
            return false;
        }

        for (var i = 0; i < Values.Length; i++) {
            if (!string.Equals(Values[i].Key, other.Values[i].Key, StringComparison.Ordinal)
                || !string.Equals(Values[i].Value, other.Values[i].Value, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EffectKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => hash;

    /// <summary>Whether two keys select the same variant of the same shader.</summary>
    public static bool operator ==(EffectKey left, EffectKey right) => left.Equals(right);

    /// <summary>Whether two keys select different variants, or different shaders.</summary>
    public static bool operator !=(EffectKey left, EffectKey right) => !left.Equals(right);

    /// <summary>The key as one stable string — a cache filename, a log line.</summary>
    public override string ToString() =>
        Values.IsEmpty
            ? ShaderName
            : $"{ShaderName}[{string.Join(",", Values.Select(pair => $"{pair.Key}={pair.Value}"))}]";
}
