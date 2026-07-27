// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Vixen.Shaders;

/// <summary>Where an effect that is not in memory comes from.</summary>
/// <remarks>
///     <para>
///         <strong>The seam that makes "zero runtime shader compilation" structural rather than
///         aspirational.</strong> A shipping build supplies a provider backed by the baked bundle;
///         the editor and a development build supply one backed by Raven. The runtime never
///         references the compiler, so a shipped game <em>cannot</em> compile a shader — not because
///         a flag says so, but because the code to do it was never linked in.
///     </para>
///     <para>
///         It is also what makes the remote compiler ([docs/plan/06 § Effect permutations]) a
///         provider rather than a special case: a device asking a dev machine over TCP satisfies
///         exactly this interface.
///     </para>
/// </remarks>
public interface IEffectProvider {
    /// <summary>The effect for a key, or null when this provider does not have it.</summary>
    /// <remarks>
    ///     Null rather than throwing, because "I do not have it" is the ordinary answer for every
    ///     provider but the last: a bundle provider misses on a key the build did not enumerate, and
    ///     the compiler behind it answers. Throwing would make the common path an exception.
    /// </remarks>
    Effect? TryGet(EffectKey key);
}

/// <summary>
///     Resolves an <see cref="EffectKey" /> to a compiled <see cref="Effect" />, remembering what it
///     already resolved.
/// </summary>
/// <remarks>
///     <para>
///         The in-memory tier of the three [docs/plan/06 § Effect permutations] describes. The other
///         two are providers: an on-disk bytecode cache and, before it, the bundle the content build
///         baked. This class does not know which it has — it asks each in turn and remembers the
///         answer.
///     </para>
///     <para>
///         <strong>A miss is reported, not hidden.</strong> <see cref="MissCount" /> and
///         <see cref="Misses" /> exist so that "no runtime shader compilation in a shipping build"
///         can be a <em>test</em> rather than a hope, which is what doc 06's testing table asks for:
///         run a playthrough, assert the miss list is empty.
///     </para>
/// </remarks>
public sealed class EffectSystem {
    readonly ConcurrentDictionary<EffectKey, Effect> resolved = new();
    readonly List<IEffectProvider> providers = [];
    readonly ConcurrentDictionary<EffectKey, byte> misses = new();

    /// <summary>How many distinct effects are in memory.</summary>
    public int Count => resolved.Count;

    /// <summary>How many distinct keys no provider could satisfy.</summary>
    public int MissCount => misses.Count;

    /// <summary>The keys no provider could satisfy, for a shipping-build assertion.</summary>
    public IEnumerable<EffectKey> Misses => misses.Keys;

    /// <summary>Adds a provider. Providers are asked in the order they were added.</summary>
    /// <remarks>
    ///     Order is the tiering: the fastest source first, the one that can produce anything last.
    ///     A shipping build adds only the bundle, which is what makes a miss there a missing
    ///     permutation rather than a stall.
    /// </remarks>
    public void AddProvider(IEffectProvider provider) {
        ArgumentNullException.ThrowIfNull(provider);
        providers.Add(provider);
    }

    /// <summary>Puts an effect in memory directly, bypassing the providers.</summary>
    /// <remarks>For a bundle being loaded wholesale, and for tests.</remarks>
    public void Add(Effect effect) {
        ArgumentNullException.ThrowIfNull(effect);
        resolved[effect.Key] = effect;
        misses.TryRemove(effect.Key, out _);
    }

    /// <summary>The effect for a key, asking each provider in turn if it is not already in memory.</summary>
    /// <returns>The effect, or null when nothing could supply it.</returns>
    public Effect? Resolve(EffectKey key) {
        if (resolved.TryGetValue(key, out var cached)) {
            return cached;
        }

        foreach (var provider in providers) {
            if (provider.TryGet(key) is not { } produced) {
                continue;
            }

            // GetOrAdd rather than an assignment: two threads resolving the same key at once both
            // get an effect, and this makes them get the *same* one — which matters because the
            // pipeline cache downstream is keyed by effect identity.
            var winner = resolved.GetOrAdd(key, produced);
            misses.TryRemove(key, out _);
            return winner;
        }

        misses.TryAdd(key, 0);
        return null;
    }

    /// <summary>The effect for a key, or false when nothing could supply it.</summary>
    public bool TryResolve(EffectKey key, [NotNullWhen(true)] out Effect? effect) {
        effect = Resolve(key);
        return effect is not null;
    }

    /// <summary>Forgets every resolved effect and every recorded miss.</summary>
    /// <remarks>
    ///     What a hot reload does: the source changed, so everything compiled from it is stale. The
    ///     providers stay, because where effects come from has not changed — only what they contain.
    /// </remarks>
    public void Invalidate() {
        resolved.Clear();
        misses.Clear();
    }

    /// <summary>Forgets one effect, for a reload that knows what changed.</summary>
    public bool Invalidate(EffectKey key) => resolved.TryRemove(key, out _);
}
