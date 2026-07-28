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
    readonly ConcurrentDictionary<EffectKey, byte> requests = new();
    readonly ConcurrentDictionary<EffectKey, byte> queued = new();
    readonly ConcurrentQueue<EffectKey> pending = new();

    /// <summary>How many distinct effects are in memory.</summary>
    public int Count => resolved.Count;

    /// <summary>How many distinct keys no provider could satisfy.</summary>
    public int MissCount => misses.Count;

    /// <summary>The keys no provider could satisfy, for a shipping-build assertion.</summary>
    public IEnumerable<EffectKey> Misses => misses.Keys;

    /// <summary>
    ///     Every distinct key anything has asked for, hit or miss.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The other half of the pre-generation problem. Doc 06 asks the content build to
    ///         enumerate "permutations reachable from the project's materials and compositors", and
    ///         the honest answer is that no static analysis of a scene knows which shading model a
    ///         script will switch to on level three. What does know is a playthrough: run the game
    ///         against a compiler, write this list out as an <see cref="EffectManifest" />, and the
    ///         build has the exact set rather than a conservative superset.
    ///     </para>
    ///     <para>
    ///         Distinct from <see cref="Misses" /> because the two answer different questions. A miss
    ///         says the bundle is wrong; a request says what the bundle should contain — and after a
    ///         good build every request is a hit, which is exactly when the miss list stops being
    ///         able to tell you anything.
    ///     </para>
    /// </remarks>
    public IEnumerable<EffectKey> Requests => requests.Keys;

    /// <summary>How many distinct keys have been asked for.</summary>
    public int RequestCount => requests.Count;

    /// <summary>
    ///     What to draw with while a variant is being produced, or null to wait for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Setting this is what turns resolution asynchronous, and doc 06 asks for it in one
    ///         sentence: "development builds compile on demand, asynchronously, rendering with a
    ///         placeholder material for the frames until ready — never a hitch, never a stall." A
    ///         compile is hundreds of milliseconds and it happens the first time a material is seen,
    ///         which is exactly when a player is walking into a new room.
    ///     </para>
    ///     <para>
    ///         <strong>It is never put in the cache.</strong> The dictionary holds what a key
    ///         resolved to, and a placeholder is what it resolved to <em>for now</em> — caching it
    ///         would make the temporary answer permanent, which is a magenta object that never
    ///         becomes anything and no error anywhere.
    ///     </para>
    ///     <para>
    ///         Null is the shipping arrangement, and leaves <see cref="Resolve" /> exactly as it was:
    ///         ask each provider, cache the answer, record a miss. A shipping build has nothing that
    ///         could compile later, so there is nothing for a placeholder to be a placeholder for.
    ///     </para>
    /// </remarks>
    public Effect? Placeholder { get; set; }

    /// <summary>How many keys are waiting to be produced.</summary>
    public int PendingCount => pending.Count;

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
        // Recorded before the in-memory tier, not after it. What the build has to contain is what a
        // run asks for, and a key asked for a thousand times and cached after the first would
        // otherwise be recorded only if the frame it first appeared on happened to be sampled.
        requests.TryAdd(key, 0);

        if (resolved.TryGetValue(key, out var cached)) {
            return cached;
        }

        // Asked for once and then waited for, rather than asked for again every frame. The queue is
        // what makes the answer arrive later; asking the providers here as well would be the stall
        // the placeholder exists to avoid.
        if (Placeholder is { } waiting) {
            if (queued.TryAdd(key, 0)) {
                pending.Enqueue(key);
            }

            return waiting;
        }

        return Produce(key);
    }

    /// <summary>
    ///     Produces some of what is waiting, and answers how many arrived.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Called by the host, off the render thread — a job, a worker, whatever the game already
    ///         has. This class owns no thread of its own on purpose: a compile is somebody's CPU time
    ///         and how much of it to spend, on which thread, against what else is running, is a
    ///         scheduling decision the engine's job system is for and an effect cache is not.
    ///     </para>
    ///     <para>
    ///         It also makes the whole arrangement testable without a clock. A test pumps and asserts;
    ///         nothing is waited on, nothing is flaky.
    ///     </para>
    /// </remarks>
    /// <param name="limit">At most how many to produce, so a caller can bound its frame.</param>
    /// <returns>How many effects were produced and put in memory.</returns>
    public int Pump(int limit = int.MaxValue) {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var produced = 0;

        while (produced < limit && pending.TryDequeue(out var key)) {
            // Left in `queued` whatever happens. A key nothing can supply is a miss, and asking again
            // next frame would be a compilation per frame for as long as the object is on screen —
            // which is the stall, arriving by a different route.
            if (Produce(key) is not null) {
                produced++;
            }
        }

        return produced;
    }

    /// <summary>Asks each provider in turn, and remembers what came back.</summary>
    Effect? Produce(EffectKey key) {
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

        // And what was waiting, because it was waiting for the old source. Whatever still needs a
        // variant asks again on its next frame and gets back in the queue — which is also what makes
        // a reload draw placeholders rather than stale shaders while it recompiles.
        queued.Clear();
        pending.Clear();
    }

    /// <summary>Forgets what has been asked for, without forgetting anything resolved.</summary>
    /// <remarks>
    ///     For a capture that should cover one level rather than the whole session — reset at the
    ///     load screen, dump at the end. Separate from <see cref="Invalidate()" /> because throwing
    ///     away compiled effects to start a new capture would make the capture itself the thing that
    ///     caused the stalls.
    /// </remarks>
    public void ClearRequests() => requests.Clear();

    /// <summary>Forgets one effect, for a reload that knows what changed.</summary>
    public bool Invalidate(EffectKey key) {
        queued.TryRemove(key, out _);
        return resolved.TryRemove(key, out _);
    }
}
