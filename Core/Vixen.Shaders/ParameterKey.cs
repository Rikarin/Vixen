// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Vixen.Shaders;

/// <summary>
///     A named, typed handle for one shader parameter, interned so that equal names are the
///     same object.
/// </summary>
/// <remarks>
///     <para>
///         Stride's <c>ParameterKey</c> idea, with the reflection cost moved to compile time:
///         <c>Vixen.Shaders.Generators</c> reads Raven's reflection and emits a
///         <c>static readonly</c> key per parameter, so a render feature refers to
///         <c>LightingKeys.LightCount</c> and never to the string <c>"Lighting.LightCount"</c>.
///     </para>
///     <para>
///         <strong>Interning is what makes a key cheap to use.</strong> A key is compared and
///         hashed by reference, so a dictionary keyed by one costs a pointer compare rather than a
///         string compare — and two assemblies that generated bindings from the same shader get the
///         same object rather than two that merely look alike. It also makes a whole class of bug
///         impossible: a key that reached the wrong shader is caught at creation as a type conflict
///         rather than by writing four bytes into the wrong place.
///     </para>
/// </remarks>
public abstract class ParameterKey : IEquatable<ParameterKey> {
    private protected ParameterKey(string name, Type valueType) {
        Name = name;
        ValueType = valueType;

        // Precomputed because a key's whole job is to be a dictionary key, and the name never
        // changes. Ordinal: these are identifiers, not text.
        HashCode = StringComparer.Ordinal.GetHashCode(name);
    }

    /// <summary>The dotted name the shader's reflection gave this parameter.</summary>
    public string Name { get; }

    /// <summary>The CLR type a value for this key has.</summary>
    /// <remarks>
    ///     A <see cref="Type" /> object, which is AOT-safe: it comes from a <c>typeof</c> at the
    ///     generated call site and is only ever compared, never reflected over.
    /// </remarks>
    public Type ValueType { get; }

    /// <summary>The name's hash, computed once.</summary>
    public int HashCode { get; }

    /// <summary>Whether this key selects a shader variant rather than carrying a value into one.</summary>
    public abstract bool IsPermutation { get; }

    /// <inheritdoc />
    public bool Equals(ParameterKey? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode;

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A parameter whose value is written into a constant buffer or bound as a resource.</summary>
/// <typeparam name="T">The value's CLR type.</typeparam>
public sealed class ParameterKey<T> : ParameterKey {
    internal ParameterKey(string name, T defaultValue) : base(name, typeof(T)) => DefaultValue = defaultValue;

    /// <summary>The value used when nothing has set one.</summary>
    public T DefaultValue { get; }

    /// <inheritdoc />
    public override bool IsPermutation => false;
}

/// <summary>
///     A parameter that selects which variant of a shader gets compiled.
/// </summary>
/// <remarks>
///     A different type from <see cref="ParameterKey{T}" /> rather than a flag on it, because the two
///     are consumed by different machinery and confusing them is expensive in one direction: setting
///     a permutation key at draw time does nothing until something recompiles, and setting a value
///     key per variant multiplies the cache for no reason. Raven reports which is which — a
///     <c>[Permutation]</c> field is not a uniform — so the generator never has to guess.
/// </remarks>
/// <typeparam name="T">The key's type. Raven admits <c>bool</c>, <c>int</c> and <c>uint</c> only.</typeparam>
public sealed class PermutationKey<T> : ParameterKey {
    internal PermutationKey(string name, T defaultValue) : base(name, typeof(T)) => DefaultValue = defaultValue;

    /// <summary>The value the shader declared, used when a material does not override it.</summary>
    public T DefaultValue { get; }

    /// <inheritdoc />
    public override bool IsPermutation => true;
}

/// <summary>Creates and interns parameter keys.</summary>
public static class ParameterKeys {
    static readonly ConcurrentDictionary<string, ParameterKey> Interned = new(StringComparer.Ordinal);

    /// <summary>The key for <paramref name="name" />, creating it if this is its first mention.</summary>
    /// <exception cref="InvalidOperationException">
    ///     The name is already interned with a different type or a different kind.
    /// </exception>
    public static ParameterKey<T> New<T>(string name, T defaultValue = default!) =>
        (ParameterKey<T>)Resolve(name, static (n, d) => new ParameterKey<T>(n, d), defaultValue, permutation: false);

    /// <summary>The permutation key for <paramref name="name" />, creating it if new.</summary>
    /// <remarks>
    ///     The default comes first, matching how a shader declares one: <c>[Permutation] val
    ///     UseShadows: bool = false</c> puts the value before the name reaches anything.
    /// </remarks>
    public static PermutationKey<T> NewPermutation<T>(T defaultValue, string name) =>
        (PermutationKey<T>)Resolve(name, static (n, d) => new PermutationKey<T>(n, d), defaultValue, permutation: true);

    /// <summary>The key already interned under <paramref name="name" />, if there is one.</summary>
    /// <remarks>
    ///     For the paths that genuinely have only a string — a material asset naming a parameter, an
    ///     editor inspector — and deliberately not for anything on a frame path. It cannot create,
    ///     because the type would have to be guessed.
    /// </remarks>
    public static bool TryGet(string name, [NotNullWhen(true)] out ParameterKey? key) =>
        Interned.TryGetValue(name, out key);

    /// <summary>Every key interned so far, in no particular order.</summary>
    public static IReadOnlyCollection<ParameterKey> All => (IReadOnlyCollection<ParameterKey>)Interned.Values;

    static ParameterKey Resolve<T>(
        string name,
        Func<string, T, ParameterKey> create,
        T defaultValue,
        bool permutation
    ) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var key = Interned.GetOrAdd(name, static (n, state) => state.Create(n, state.Default), (Create: create, Default: defaultValue));

        // A name that means two things is a build-time mistake in generated code or a collision
        // between two shaders — either way, failing here names both the key and the conflict, where
        // letting it through would write a value of one type through a layout computed for another.
        if (key.ValueType != typeof(T) || key.IsPermutation != permutation) {
            throw new InvalidOperationException(
                $"Parameter '{name}' is already registered as {Describe(key)}, and cannot also be "
                + $"{Describe(typeof(T), permutation)}."
            );
        }

        return key;
    }

    static string Describe(ParameterKey key) => Describe(key.ValueType, key.IsPermutation);

    static string Describe(Type type, bool permutation) =>
        permutation ? $"a permutation key of {type.Name}" : $"a value key of {type.Name}";
}
