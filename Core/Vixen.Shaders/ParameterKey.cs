// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    /// <summary>
    ///     Whether a shader's declared default has reached this key.
    /// </summary>
    /// <remarks>
    ///     <strong>"I have no default" and "my default is zero" are different claims</strong>, and a
    ///     table interned by name has to be able to tell them apart. <see cref="EffectLoader" /> interns
    ///     every parameter a compiled effect reflects and has no initialiser to read — reflection
    ///     carries offsets and types, not source — so it registers keys with this false, and a later
    ///     generated binding that does know still fills the default in. Were the two the same, whichever
    ///     ran first would decide whether <c>var exposure: float = 1f</c> survived.
    /// </remarks>
    public abstract bool HasDeclaredDefault { get; }

    /// <summary>
    ///     The key's default, as the bytes a constant buffer wants.
    /// </summary>
    /// <remarks>
    ///     <see cref="ParameterCollection.Get{T}(ParameterKey{T})" /> already promises that one which never
    ///     mentions a key yields the value the shader author declared. This is the same promise for
    ///     the code that copies rather than reads: a buffer writer filling only the keys somebody set
    ///     would give <c>var exposure: float = 1f</c> the value zero, which is a black frame produced
    ///     by a parameter nobody touched. Empty for a key whose type is not blittable, since there is
    ///     nothing a buffer could be filled with, and empty while
    ///     <see cref="HasDeclaredDefault" /> is false, since a writer filling a block it already
    ///     cleared and a writer copying zeros over it come to the same thing.
    /// </remarks>
    public abstract ReadOnlySpan<byte> DefaultBytes { get; }

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
    /// <summary>
    ///     What the shader declared, or null while nobody has said.
    /// </summary>
    /// <remarks>
    ///     One immutable object behind one reference rather than a value, a byte array and a flag
    ///     beside each other. A key is interned, so it is shared across every thread that ever names
    ///     it, and a default can arrive after another thread already holds the key — see
    ///     <see cref="Declare" />. Swapping a single reference is what makes a reader see either the
    ///     whole declaration or none of it, rather than new bytes beside an old value, or half of a
    ///     <c>Matrix4x4</c> written in two words.
    /// </remarks>
    Declaration? declaration;

    internal ParameterKey(string name) : base(name, typeof(T)) { }

    internal ParameterKey(string name, T defaultValue) : base(name, typeof(T)) =>
        declaration = new(defaultValue);

    /// <summary>The value used when nothing has set one.</summary>
    /// <remarks>
    ///     <c>default(T)</c> while <see cref="HasDeclaredDefault" /> is false — which is the honest
    ///     answer to "what did the shader declare" when the answer is nothing, and not a claim that
    ///     zero is what it declared.
    /// </remarks>
    public T DefaultValue => Volatile.Read(ref declaration) is { } declared ? declared.Value : default!;

    /// <inheritdoc />
    public override bool HasDeclaredDefault => Volatile.Read(ref declaration) is not null;

    /// <inheritdoc />
    public override bool IsPermutation => false;

    /// <inheritdoc />
    public override ReadOnlySpan<byte> DefaultBytes =>
        Volatile.Read(ref declaration) is { } declared ? declared.Bytes : default;

    /// <summary>
    ///     Records the default the shader declared, unless one already reached this key.
    /// </summary>
    /// <remarks>
    ///     The one thing about an interned key that is not fixed at creation, because the caller that
    ///     creates it is decided by load order rather than by who knows the most. Filling in an
    ///     absence is safe in a way that replacing a declaration is not: it happens at most once, it
    ///     can only move a key from <c>default(T)</c> to what the shader author wrote, and every
    ///     holder of the key sees the same object gain the same value. A second declared default is
    ///     dropped — see <see cref="ParameterKeys.New{T}(string, T)" />.
    /// </remarks>
    internal void Declare(T defaultValue) {
        // Checked before allocating, because this runs on every repeat registration of a name and
        // almost all of them already have their answer.
        if (Volatile.Read(ref declaration) is not null) {
            return;
        }

        Interlocked.CompareExchange(ref declaration, new Declaration(defaultValue), null);
    }

    /// <summary>A declared default, and its bytes, computed once and never changed.</summary>
    sealed class Declaration {
        internal Declaration(T value) {
            Value = value;
            Bytes = Blit(value);
        }

        internal T Value { get; }

        internal byte[] Bytes { get; }

        /// <summary>The default's bytes, once, or nothing for a type a buffer cannot hold.</summary>
        /// <remarks>
        ///     <typeparamref name="T" /> is unconstrained here — a key may name a texture or a sampler,
        ///     which have no bytes — so the check is at runtime.
        ///     <see cref="Unsafe.As{TFrom,TTo}(ref TFrom)" /> rather than <c>MemoryMarshal.AsBytes</c>
        ///     because the latter wants the constraint the type parameter cannot carry, and the guard
        ///     above it is what makes the reinterpretation sound.
        /// </remarks>
        static byte[] Blit(T value) {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
                return [];
            }

            return MemoryMarshal
                .CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref value), Unsafe.SizeOf<T>())
                .ToArray();
        }
    }
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
    /// <remarks>
    ///     Always true, and the asymmetry with <see cref="ParameterKey{T}" /> is the API's:
    ///     <see cref="ParameterKeys.NewPermutation{T}" /> makes the default a required argument, so
    ///     there is no caller that could register one of these without knowing. Nothing reflects a
    ///     permutation without also reading the value it defaults to — a baked artefact carries it as
    ///     text beside the name.
    /// </remarks>
    public override bool HasDeclaredDefault => true;

    /// <inheritdoc />
    public override bool IsPermutation => true;

    /// <inheritdoc />
    /// <remarks>Always empty: a permutation decides which shader exists, and no buffer holds one.</remarks>
    public override ReadOnlySpan<byte> DefaultBytes => default;
}

/// <summary>Creates and interns parameter keys.</summary>
public static class ParameterKeys {
    static readonly ConcurrentDictionary<string, ParameterKey> Interned = new(StringComparer.Ordinal);

    /// <summary>
    ///     The key for <paramref name="name" />, joining an existing one or creating it, without
    ///     saying anything about its default.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>This overload asks; the one beside it tells.</strong> Its callers are the ones
    ///         that have a name and a type and genuinely no default:
    ///         <see cref="EffectLoader" /> interns every parameter a compiled effect reflects, and
    ///         reflection is offsets and types — the initialiser <c>= 1f</c> never survives lowering
    ///         into it. A material naming a parameter from an asset is in the same position.
    ///     </para>
    ///     <para>
    ///         So it leaves <see cref="ParameterKey{T}.HasDeclaredDefault" /> false and a later
    ///         <see cref="New{T}(string, T)" /> — the generated binding, which read the initialiser
    ///         out of Raven — still fills the default in, on the same object every existing holder of
    ///         the key already has. If instead this claimed <c>default(T)</c>, the two would race:
    ///         effect loading is data-driven and a generated bindings class is not initialised until
    ///         something first touches it, so an effect that loaded first would leave every declared
    ///         default in that shader as zero. Silently, and reported by nothing downstream.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The name is already interned with a different type or a different kind.
    /// </exception>
    public static ParameterKey<T> New<T>(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return Checked<ParameterKey<T>>(
            Interned.GetOrAdd(name, static n => new ParameterKey<T>(n)),
            typeof(T),
            permutation: false
        );
    }

    /// <summary>
    ///     The key for <paramref name="name" />, declaring the default the shader gave it.
    /// </summary>
    /// <remarks>
    ///     What the generated bindings call. The default reaches the key whether this call created it
    ///     or found one a <see cref="New{T}(string)" /> caller had already interned — but it does not
    ///     displace a default that is already there. Two shaders declaring one name with different
    ///     values is a question with no good answer, so the answer is "the one that got there first",
    ///     documented rather than surprising, and rare by construction because generated names are
    ///     shader-qualified.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The name is already interned with a different type or a different kind.
    /// </exception>
    public static ParameterKey<T> New<T>(string name, T defaultValue) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var key = Checked<ParameterKey<T>>(
            Interned.GetOrAdd(name, static (n, value) => new ParameterKey<T>(n, value), defaultValue),
            typeof(T),
            permutation: false
        );

        // Created carrying its default above — nothing can observe the key without it — so this has
        // work to do only when the name was already interned by a caller that had none.
        key.Declare(defaultValue);
        return key;
    }

    /// <summary>The permutation key for <paramref name="name" />, creating it if new.</summary>
    /// <remarks>
    ///     The default comes first, matching how a shader declares one: <c>[Permutation] val
    ///     UseShadows: bool = false</c> puts the value before the name reaches anything. Required
    ///     rather than optional, which is why a permutation key has no equivalent of the join above:
    ///     there is no caller that knows one of these by name and not by default.
    /// </remarks>
    public static PermutationKey<T> NewPermutation<T>(T defaultValue, string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return Checked<PermutationKey<T>>(
            Interned.GetOrAdd(name, static (n, value) => new PermutationKey<T>(n, value), defaultValue),
            typeof(T),
            permutation: true
        );
    }

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

    /// <summary>The interned key, once it is agreed that it is the key the caller asked for.</summary>
    /// <remarks>
    ///     <para>
    ///         A name that means two <em>types</em>, or one that means both a value and a permutation,
    ///         is a build-time mistake in generated code or a collision between two shaders — either
    ///         way, failing here names both the key and the conflict, where letting it through would
    ///         write a value of one type through a layout computed for another.
    ///     </para>
    ///     <para>
    ///         <strong>A default is not on that list, and the omission is deliberate.</strong> A
    ///         second caller with no default to offer is not disagreeing with the first, it is silent
    ///         — <see cref="New{T}(string)" /> exists precisely because reflection has a name and a
    ///         type and no initialiser — so the two are told apart by
    ///         <see cref="ParameterKey.HasDeclaredDefault" /> rather than by throwing at a caller
    ///         doing nothing wrong. What remains is two <em>declared</em> defaults for one name, a
    ///         real disagreement, and that is resolved first-wins rather than thrown: unlike a type
    ///         conflict it cannot corrupt a write, it is unreachable for shader-qualified generated
    ///         names, and throwing would turn a static field initialiser into a
    ///         <c>TypeInitializationException</c> at whatever point in the frame first touched the
    ///         bindings class.
    ///     </para>
    /// </remarks>
    static TKey Checked<TKey>(ParameterKey key, Type valueType, bool permutation) where TKey : ParameterKey {
        if (key.ValueType != valueType || key.IsPermutation != permutation) {
            throw new InvalidOperationException(
                $"Parameter '{key.Name}' is already registered as {Describe(key)}, and cannot also be "
                + $"{Describe(valueType, permutation)}."
            );
        }

        return (TKey)key;
    }

    static string Describe(ParameterKey key) => Describe(key.ValueType, key.IsPermutation);

    static string Describe(Type type, bool permutation) =>
        permutation ? $"a permutation key of {type.Name}" : $"a value key of {type.Name}";
}
