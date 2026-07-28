// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Vixen.Shaders;

/// <summary>Where the bytes of a variant come from, before any device has seen them.</summary>
/// <remarks>
///     <para>
///         <see cref="IEffectProvider" /> answers with an <see cref="Effect" />, which is a thing on
///         a device; this answers with an <see cref="EffectData" />, which is a thing on a disk or a
///         wire. The distinction is what lets the tiers compose: a disk cache that missed can ask the
///         dev machine and <em>write down what came back</em>, which it could not do if the answer
///         were already a set of device handles.
///     </para>
///     <para>
///         So the shape of the chain is: sources stack, and one
///         <see cref="EffectSourceProvider" /> at the top turns whatever the stack produced into an
///         effect. A shipping build's stack is one deep.
///     </para>
/// </remarks>
public interface IEffectSource {
    /// <summary>The variant for a key, or null when this source does not have it.</summary>
    EffectData? TryGet(EffectKey key);
}

/// <summary>
///     Adapts a source into a tier of the <see cref="EffectSystem" />.
/// </summary>
/// <remarks>
///     Separate from the sources themselves so that the device appears exactly once in the chain.
///     Every source would otherwise need one, which would mean a bundle could not be opened, checked
///     or diffed by a tool that has no graphics device — and inspecting what a build baked is
///     precisely the thing you want to do when a shipping build reports a miss.
/// </remarks>
public sealed class EffectSourceProvider(IEffectSource source, EffectLoader loader) : IEffectProvider {
    /// <summary>Where the bytes come from.</summary>
    public IEffectSource Source { get; } = source;

    /// <summary>What turns them into an effect.</summary>
    public EffectLoader Loader { get; } = loader;

    /// <inheritdoc />
    public Effect? TryGet(EffectKey key) => Source.TryGet(key) is { } data ? Loader.Load(data) : null;
}

/// <summary>
///     A set of pre-compiled variants held in memory, indexed by key.
/// </summary>
/// <remarks>
///     <para>
///         What a shipping build has instead of a compiler. An <see cref="EffectBundle" /> comes out
///         of content as a flat list; this indexes it once and answers in a dictionary lookup
///         thereafter.
///     </para>
///     <para>
///         A key baked twice is refused rather than resolved by last-writer-wins. Two records under
///         one key means the build enumerated the same variant along two paths and compiled it twice
///         — which is either wasted build time or, if the two differ, a bundle whose behaviour
///         depends on file order.
///     </para>
/// </remarks>
public sealed class EffectStore : IEffectSource {
    readonly ConcurrentDictionary<EffectKey, EffectData> effects = new();

    /// <summary>An empty store, for a host that adds variants as it goes.</summary>
    public EffectStore() { }

    /// <summary>A store over everything a bundle holds.</summary>
    /// <exception cref="ArgumentException">The bundle holds two records under one key.</exception>
    public EffectStore(EffectBundle bundle) {
        ArgumentNullException.ThrowIfNull(bundle);

        foreach (var effect in bundle.Effects) {
            Add(effect);
        }
    }

    /// <summary>How many variants it holds.</summary>
    public int Count => effects.Count;

    /// <summary>Every key it can answer, in no particular order.</summary>
    public IEnumerable<EffectKey> Keys => effects.Keys;

    /// <summary>Adds a variant.</summary>
    /// <exception cref="ArgumentException">Its key is already present.</exception>
    public void Add(EffectData effect) {
        ArgumentNullException.ThrowIfNull(effect);

        var key = effect.ToKey();

        if (!effects.TryAdd(key, effect)) {
            throw new ArgumentException($"'{key}' is in this store twice.", nameof(effect));
        }
    }

    /// <summary>The bundle this store would bake, ordered so two builds of the same set match.</summary>
    public EffectBundle ToBundle() =>
        new() {
            Effects = [.. effects.Values.OrderBy(effect => effect.ToKey().ToString(), StringComparer.Ordinal)]
        };

    /// <inheritdoc />
    public EffectData? TryGet(EffectKey key) => effects.GetValueOrDefault(key);
}
