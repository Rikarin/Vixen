// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vixen.Shaders;

/// <summary>
///     The values set against a set of parameter keys: a material's parameters, a view's, a draw's.
/// </summary>
/// <remarks>
///     <para>
///         One byte buffer with an offset per key, rather than a dictionary of boxed values. The
///         thing this is asked to do thousands of times a frame is "give me the bytes for these
///         parameters so I can fill a constant buffer", and a <c>Dictionary&lt;key, object&gt;</c>
///         answers that with a boxing allocation and a pointer chase per parameter.
///     </para>
///     <para>
///         <strong>Values and permutations are kept apart, because they are consumed at different
///         times.</strong> A value is written into a buffer every frame it changes; a permutation
///         decides <em>which shader exists</em> and changing one is a recompile. Stride keeps them in
///         one collection and distinguishes by key type at every use; keeping them in two makes
///         "what is this frame's effect key" a field rather than a filter over everything set.
///     </para>
///     <para>
///         Not thread-safe, deliberately. A collection belongs to one material or one view, and the
///         frame phases that read it — preparation, recording — do so after the phase that writes it
///         has finished. Guarding it would put a lock on the hottest path in the renderer to protect
///         against a pattern the frame structure already rules out.
///     </para>
/// </remarks>
public sealed class ParameterCollection {
    readonly Dictionary<ParameterKey, Slot> slots = [];
    readonly Dictionary<ParameterKey, object> permutations = [];
    byte[] storage = [];
    int used;

    /// <summary>Bumped whenever a value changes, so a consumer can skip work it already did.</summary>
    /// <remarks>
    ///     A counter rather than a dirty flag, because there is more than one consumer: the constant
    ///     buffer writer and the descriptor set builder both want "has this changed since I last
    ///     looked", and a flag one of them clears is a change the other never sees.
    /// </remarks>
    public int Version { get; private set; }

    /// <summary>Bumped whenever a permutation changes, which is when the effect must be re-resolved.</summary>
    public int PermutationVersion { get; private set; }

    /// <summary>How many value keys have been set.</summary>
    public int Count => slots.Count;

    /// <summary>The keys that have a value, in no particular order.</summary>
    public IEnumerable<ParameterKey> Keys => slots.Keys;

    // --- Values -------------------------------------------------------------

    /// <summary>Sets a value.</summary>
    /// <remarks>
    ///     Setting a key to what it already holds does not bump <see cref="Version" />, which is what
    ///     makes the version mean "something changed" rather than "something was assigned". A caller
    ///     that rebuilds its parameters every frame from values that rarely move — a post-process node
    ///     reconfiguring its chain — would otherwise re-upload a constant buffer every frame that
    ///     nothing in it had changed. <see cref="Set{T}(PermutationKey{T}, T)" /> has always worked
    ///     this way; this is the same rule for values.
    /// </remarks>
    public void Set<T>(ParameterKey<T> key, T value) where T : unmanaged {
        ArgumentNullException.ThrowIfNull(key);

        var size = Unsafe.SizeOf<T>();

        // Whether the key was already here, asked before Reserve creates it. A slot this call just
        // allocated holds whatever a previous Clear left behind, and comparing against that would
        // occasionally decide a brand-new key had not changed.
        var existing = slots.TryGetValue(key, out var previous) && previous.Size == size;
        var slot = Reserve(key, size);
        var destination = storage.AsSpan(slot.Offset, size);

        if (existing && destination.SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)))) {
            return;
        }

        MemoryMarshal.Write(destination, in value);
        Version++;
    }

    /// <summary>The value set for a key, or the key's own default when nothing set one.</summary>
    /// <remarks>
    ///     The default rather than <c>default(T)</c>: a shader declares <c>var exposure: float = 1f</c>
    ///     and the generated key carries that 1, so a material that never mentions exposure gets the
    ///     value the shader author intended rather than black.
    /// </remarks>
    public T Get<T>(ParameterKey<T> key) where T : unmanaged {
        ArgumentNullException.ThrowIfNull(key);

        if (!slots.TryGetValue(key, out var slot)) {
            return key.DefaultValue;
        }

        return MemoryMarshal.Read<T>(storage.AsSpan(slot.Offset, slot.Size));
    }

    /// <summary>Whether a key has a value of its own.</summary>
    public bool Has(ParameterKey key) {
        ArgumentNullException.ThrowIfNull(key);
        return slots.ContainsKey(key) || permutations.ContainsKey(key);
    }

    /// <summary>The raw bytes set for a key, empty when nothing set one.</summary>
    /// <remarks>
    ///     For the constant-buffer writer, which copies rather than interprets. Read-only because the
    ///     buffer is compacted on <see cref="Clear" /> and moved when it grows.
    /// </remarks>
    public ReadOnlySpan<byte> Bytes(ParameterKey key) {
        ArgumentNullException.ThrowIfNull(key);
        return slots.TryGetValue(key, out var slot) ? storage.AsSpan(slot.Offset, slot.Size) : default;
    }

    // --- Permutations -------------------------------------------------------

    /// <summary>Sets a permutation, which changes which shader this collection selects.</summary>
    public void Set<T>(PermutationKey<T> key, T value) where T : notnull {
        ArgumentNullException.ThrowIfNull(key);

        if (permutations.TryGetValue(key, out var existing) && existing.Equals(value)) {
            return;
        }

        permutations[key] = value;
        PermutationVersion++;
    }

    /// <summary>The permutation set for a key, or the shader's declared default.</summary>
    public T Get<T>(PermutationKey<T> key) where T : notnull {
        ArgumentNullException.ThrowIfNull(key);
        return permutations.TryGetValue(key, out var value) ? (T)value : key.DefaultValue;
    }

    /// <summary>Every permutation set here, for building an effect key.</summary>
    public IEnumerable<KeyValuePair<ParameterKey, object>> Permutations => permutations;

    // --- Composition --------------------------------------------------------

    /// <summary>
    ///     Copies everything from another collection over this one.
    /// </summary>
    /// <remarks>
    ///     What layering a material over its base, or a draw over its material, is made of. The
    ///     source wins on every key it sets and is silent on the rest, which is the only rule that
    ///     lets an override be partial — the common case being a material instance that changes one
    ///     colour.
    /// </remarks>
    public void Apply(ParameterCollection source) {
        ArgumentNullException.ThrowIfNull(source);

        if (ReferenceEquals(source, this)) {
            return;
        }

        foreach (var (key, slot) in source.slots) {
            var destination = Reserve(key, slot.Size);
            source.storage.AsSpan(slot.Offset, slot.Size).CopyTo(storage.AsSpan(destination.Offset, slot.Size));
        }

        foreach (var (key, value) in source.permutations) {
            permutations[key] = value;
        }

        Version++;

        if (source.permutations.Count > 0) {
            PermutationVersion++;
        }
    }

    /// <summary>Forgets everything, keeping the buffer for the next fill.</summary>
    public void Clear() {
        slots.Clear();
        permutations.Clear();
        used = 0;
        Version++;
        PermutationVersion++;
    }

    Slot Reserve(ParameterKey key, int size) {
        if (slots.TryGetValue(key, out var existing)) {
            // Reused only when the size matches. It always does — a key's type is fixed at
            // interning, and ParameterKeys refuses a second registration under another type — so a
            // mismatch means the key was constructed by hand, and appending is safer than writing
            // past a slot.
            if (existing.Size == size) {
                return existing;
            }
        }

        EnsureCapacity(used + size);

        var slot = new Slot(used, size);
        used += size;
        slots[key] = slot;
        return slot;
    }

    void EnsureCapacity(int required) {
        if (required <= storage.Length) {
            return;
        }

        Array.Resize(ref storage, Math.Max(required, Math.Max(storage.Length * 2, 256)));
    }

    readonly record struct Slot(int Offset, int Size);
}
