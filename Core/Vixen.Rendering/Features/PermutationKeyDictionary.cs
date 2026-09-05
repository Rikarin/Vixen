// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using Vixen.Shaders;

namespace Vixen.Rendering.Features;

/// <summary>
///     Which permutation keys each shader's variants are selected by, and which of them nothing may
///     drop.
/// </summary>
/// <remarks>
///     <para>
///         <strong>A dictionary with one rule, and the rule is the reason this is not a
///         <c>Dictionary</c>.</strong> A host says which keys a pass's effect key is built from by
///         assigning the generated <c>…Keys.UsedPermutationKeys</c> under the shader's name — one line,
///         written once per host, and every shipping host writes it. An engine that needs a key that
///         list does not carry — one contributed by a <em>composed</em> shader, which no pass's
///         reflection can know about — registers it separately. ⚠ <b>Assignment then discarded the
///         registration, and nothing said so.</b>
///     </para>
///     <para>
///         ⚠ <b>What that cost, measured.</b> <c>WorldRenderer</c>'s constructor registered
///         <c>MaterialKeys.LayerCount</c> for <c>ForwardPlus</c> and both shipping samples assigned
///         <c>ForwardPlusKeys.UsedPermutationKeys</c> over it afterwards — as did five golden device
///         suites — so a three-layer material resolved the variant compiled for the shader's declared
///         two in <em>every host that actually drew</em>. The unregistered-permutation trap, arrived at
///         from the far side: the key was registered, and then unregistered by a line whose author had
///         no way to know.
///     </para>
///     <para>
///         So <see cref="Register" /> is not merely additive: what it registers <em>survives a later
///         assignment</em>. That is what makes the defect unfixable by forgetting rather than fixable
///         by remembering — the next host somebody writes assigns the same generated array on the same
///         line, and the key is still there afterwards, because there is no API on this type that can
///         take it away.
///     </para>
///     <para>
///         Nothing here is a claim that a key should be in the effect key at all. A key nothing
///         branches on splits the variant cache for nothing, which is why the assigned list is the
///         shader's own reported set and this holds only what an engine explicitly registered on top.
///     </para>
/// </remarks>
public sealed class PermutationKeyDictionary : IReadOnlyDictionary<string, IReadOnlyList<ParameterKey>> {
    readonly Dictionary<string, List<ParameterKey>> keys = new(StringComparer.Ordinal);

    /// <summary>
    ///     The keys an engine registered, which an assignment cannot drop.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="keys" /> rather than a flag on an entry, because the two answer
    ///     different questions: one is what the effect key is built from <em>now</em>, and this is what
    ///     has to be true of it after any assignment at all.
    /// </remarks>
    readonly Dictionary<string, List<ParameterKey>> registered = new(StringComparer.Ordinal);

    /// <summary>The shader names something has been said about.</summary>
    public IEnumerable<string> Keys => keys.Keys;

    /// <summary>Each shader's key list.</summary>
    public IEnumerable<IReadOnlyList<ParameterKey>> Values => keys.Values;

    /// <summary>How many shaders have an entry.</summary>
    public int Count => keys.Count;

    /// <summary>The keys one shader's variants are selected by.</summary>
    /// <param name="shaderName">The effect key's shader.</param>
    /// <returns>Its keys, in the order they were stated.</returns>
    /// <remarks>
    ///     ⚠ <b>Assigning replaces the list except for what <see cref="Register" /> put in it</b>, which
    ///     is appended back afterwards. A host assigning the generated <c>UsedPermutationKeys</c> for a
    ///     pass therefore ends up with exactly those keys plus whatever the engine registered — never
    ///     fewer — and duplicates in the assigned set are dropped, because a repeated key splits the
    ///     variant cache for nothing.
    /// </remarks>
    public IReadOnlyList<ParameterKey> this[string shaderName] {
        get => keys[shaderName];

        set {
            ArgumentException.ThrowIfNullOrEmpty(shaderName);
            ArgumentNullException.ThrowIfNull(value);

            var stated = new List<ParameterKey>(value.Count);

            foreach (var key in value) {
                if (!stated.Contains(key)) {
                    stated.Add(key);
                }
            }

            if (registered.TryGetValue(shaderName, out var pinned)) {
                foreach (var key in pinned) {
                    if (!stated.Contains(key)) {
                        stated.Add(key);
                    }
                }
            }

            keys[shaderName] = stated;
        }
    }

    /// <summary>Whether anything has been said about a shader.</summary>
    /// <param name="shaderName">The effect key's shader.</param>
    /// <returns>Whether it has an entry.</returns>
    public bool ContainsKey(string shaderName) => keys.ContainsKey(shaderName);

    /// <summary>The keys one shader's variants are selected by, if any were stated.</summary>
    /// <param name="shaderName">The effect key's shader.</param>
    /// <param name="value">Its keys.</param>
    /// <returns>Whether there was an entry.</returns>
    public bool TryGetValue(string shaderName, out IReadOnlyList<ParameterKey> value) {
        if (keys.TryGetValue(shaderName, out var stated)) {
            value = stated;
            return true;
        }

        value = [];
        return false;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, IReadOnlyList<ParameterKey>>> GetEnumerator() {
        foreach (var (shaderName, stated) in keys) {
            yield return new(shaderName, stated);
        }
    }

    /// <summary>
    ///     Adds a key to a shader's list and states that no later assignment may remove it.
    /// </summary>
    /// <param name="shaderName">The effect key's shader.</param>
    /// <param name="key">The shader's own key, not a renderer flag.</param>
    /// <remarks>
    ///     <para>
    ///         For a key <em>no reflection reports</em>, which is the only case this exists for: a
    ///         permutation declared by a composed surface belongs to the material's composition rather
    ///         than to the pass, so the pass's generated <c>UsedPermutationKeys</c> cannot carry it and
    ///         a host assigning that array is not making a mistake by leaving it out.
    ///     </para>
    ///     <para>
    ///         Idempotent. Registering twice — two renderers over one feature — leaves one entry,
    ///         because a repeated key is a variant-cache split for nothing.
    ///     </para>
    /// </remarks>
    public void Register(string shaderName, ParameterKey key) {
        ArgumentException.ThrowIfNullOrEmpty(shaderName);
        ArgumentNullException.ThrowIfNull(key);

        if (!registered.TryGetValue(shaderName, out var pinned)) {
            pinned = [];
            registered[shaderName] = pinned;
        }

        if (!pinned.Contains(key)) {
            pinned.Add(key);
        }

        if (!keys.TryGetValue(shaderName, out var stated)) {
            keys[shaderName] = [key];
            return;
        }

        if (!stated.Contains(key)) {
            stated.Add(key);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
