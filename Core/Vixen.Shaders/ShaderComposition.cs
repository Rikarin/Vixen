// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Shaders;

/// <summary>
///     Which shader fills each of a shader's <c>compose</c> slots — one material's feature choices.
/// </summary>
/// <remarks>
///     <para>
///         A pass is written once against a protocol — <c>compose val surface: IMaterialSurface</c> —
///         and each material says which implementation to use. Resolution happens when the shader is
///         compiled, so a call through a slot becomes a direct call and only the chosen
///         implementation is emitted. This is the value that says which.
///     </para>
///     <para>
///         <strong>Part of the cache key, not a note beside it.</strong> Two materials that differ
///         only in their features produce different code from the same shader name and the same
///         permutations, so a key without this is a cache that hands a metal-roughness material the
///         specular-glossiness shader — a wrong image with no error anywhere.
///     </para>
///     <para>
///         Sorted by slot for the reason <see cref="EffectKey" /> sorts its values: the same choices
///         made in a different order have to be the same key, or the cache holds one entry per
///         insertion order and hits almost never.
///     </para>
///     <para>
///         A plain pair of strings rather than the compiler's own <c>ComposeBindings</c>: this
///         travels into a runtime that must not link the compiler, and out the other side onto a
///         command line or a cache filename. The provider that does have a compiler is where the two
///         meet.
///     </para>
/// </remarks>
public readonly struct ShaderComposition : IEquatable<ShaderComposition> {
    readonly ImmutableArray<KeyValuePair<string, string>> slots;
    readonly int hash;

    /// <summary>Nothing composed, for a shader with no slots.</summary>
    public static ShaderComposition Empty { get; }

    /// <summary>The slots and what fills them, sorted by slot.</summary>
    public ImmutableArray<KeyValuePair<string, string>> Slots => slots.IsDefault ? [] : slots;

    /// <summary>How many slots are filled.</summary>
    public int Count => Slots.Length;

    ShaderComposition(ImmutableArray<KeyValuePair<string, string>> sorted) {
        slots = sorted;

        var accumulated = new HashCode();

        foreach (var (slot, shader) in sorted) {
            accumulated.Add(slot, StringComparer.Ordinal);
            accumulated.Add(shader, StringComparer.Ordinal);
        }

        hash = accumulated.ToHashCode();
    }

    /// <summary>The composition binding <paramref name="slots" />, in its normal form.</summary>
    /// <remarks>
    ///     A slot bound twice takes the last binding, which is what lets a caller lay a material's
    ///     own choices over a set of defaults without having to remove the default first.
    /// </remarks>
    public static ShaderComposition Of(IEnumerable<KeyValuePair<string, string>> slots) {
        ArgumentNullException.ThrowIfNull(slots);

        Dictionary<string, string> resolved = new(StringComparer.Ordinal);

        foreach (var (slot, shader) in slots) {
            ArgumentException.ThrowIfNullOrEmpty(slot);
            ArgumentException.ThrowIfNullOrEmpty(shader);
            resolved[slot] = shader;
        }

        if (resolved.Count == 0) {
            return Empty;
        }

        var builder = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>(resolved.Count);
        builder.AddRange(resolved);
        builder.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return new(builder.ToImmutable());
    }

    /// <summary>What fills <paramref name="slot" />, or null when nothing does.</summary>
    public string? Resolve(string slot) {
        foreach (var (candidate, shader) in Slots) {
            if (string.Equals(candidate, slot, StringComparison.Ordinal)) {
                return shader;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public bool Equals(ShaderComposition other) {
        if (hash != other.hash || Slots.Length != other.Slots.Length) {
            return false;
        }

        for (var i = 0; i < Slots.Length; i++) {
            if (!string.Equals(Slots[i].Key, other.Slots[i].Key, StringComparison.Ordinal)
                || !string.Equals(Slots[i].Value, other.Slots[i].Value, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ShaderComposition other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => hash;

    /// <summary>Whether two compositions choose the same implementations.</summary>
    public static bool operator ==(ShaderComposition left, ShaderComposition right) => left.Equals(right);

    /// <summary>Whether they differ.</summary>
    public static bool operator !=(ShaderComposition left, ShaderComposition right) => !left.Equals(right);

    /// <summary>The composition as the <c>slot=Shader</c> list a compiler takes.</summary>
    public override string ToString() => string.Join(",", Slots.Select(pair => $"{pair.Key}={pair.Value}"));
}
