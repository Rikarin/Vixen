// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Raven.IR;
using Vixen.Shaders;

namespace Vixen.ShaderCompiler;

/// <summary>What enumerating a shader's variants produced.</summary>
/// <param name="Effects">The distinct variants, in key order.</param>
/// <param name="Compilations">How many compilations it took, including the ones that turned out to be duplicates.</param>
/// <param name="Used">The permutation keys that turned out to matter, by their declared names.</param>
/// <param name="Dependent">
///     Whether which keys matter depends on what the other keys are set to.
/// </param>
/// <remarks>
///     <para>
///         <strong><paramref name="Dependent" /> is a warning, and worth reading.</strong> A key is
///         in an <see cref="EffectKey" /> only if the shader read it, and the engine decides which
///         keys those are from the reflection checked in beside the shader — one reflection, from one
///         variant. That works when reading a key does not depend on the others, which is the
///         ordinary case: a flag tested at the top of the function is read whatever the rest are.
///     </para>
///     <para>
///         When it is not the ordinary case — <c>if (Outer) { if (Inner) … }</c> — this finds more
///         variants than a draw can ask for, because the generated key list was built from a
///         compilation that never reached <c>Inner</c>. Baking them is not wrong, but the extra ones
///         will not be resolved by anything, and the shader wants restructuring so that the inner
///         flag is read unconditionally.
///     </para>
/// </remarks>
public sealed record PermutationClosureResult(
    ImmutableArray<EffectData> Effects,
    int Compilations,
    ImmutableArray<string> Used,
    bool Dependent = false
);

/// <summary>
///     Every variant a shader actually has, found by compiling until the answer stops changing.
/// </summary>
/// <remarks>
///     <para>
///         Doc 06's economy, made mechanical: twenty declared flags describe a million combinations
///         and a handful of them are distinct shaders, because Raven reports which keys a compilation
///         <em>read</em> rather than which were declared. The trouble is that the answer depends on
///         the values — a flag guarded by another flag is unread until the outer one is on — so a
///         single compilation with the defaults undercounts, and the cross product of everything
///         declared overcounts by orders of magnitude.
///     </para>
///     <para>
///         <strong>So it is a fixed point.</strong> Compile the defaults, see which keys were read,
///         enumerate over those, and if any of those compilations read a key that was not in the set,
///         put it in and start again. The set only grows and is bounded by what the shader declares,
///         so it terminates; and it terminates having compiled exactly the variants that exist.
///     </para>
///     <para>
///         <strong>Numbers need help and booleans do not.</strong> A <c>bool</c> has two values and
///         enumerating them is complete. An <c>int</c> does not, and the interesting values are
///         project knowledge — a light-count bucket is 4, 16 and 64 because of what the project's
///         scenes look like, which is not in the shader. Unless a caller supplies a domain a numeric
///         key contributes its declared default alone, which is honest: it produces a bundle that is
///         missing the variants nobody said they wanted, and a run against it reports them as misses
///         by name.
///     </para>
/// </remarks>
public static class PermutationClosure {
    /// <summary>How many compilations one shader may take before this gives up.</summary>
    /// <remarks>
    ///     A backstop, not a budget. Ten independent booleans that all matter is a thousand and
    ///     twenty-four genuine variants of one shader, which is a shader that wants splitting rather
    ///     than a build that wants waiting — and finding that out after an hour of compiling is the
    ///     worst way to find it out.
    /// </remarks>
    public const int DefaultLimit = 1024;

    /// <summary>Enumerates a shader's variants.</summary>
    /// <param name="compiler">What compiles them.</param>
    /// <param name="shaderName">The shader.</param>
    /// <param name="composition">What fills its <c>compose</c> slots, if any.</param>
    /// <param name="domains">
    ///     Values to try for a permutation key, by its declared name. A key not named here gets both
    ///     values if it is a <c>bool</c> and its declared default alone otherwise.
    /// </param>
    /// <param name="limit">How many compilations to allow.</param>
    /// <exception cref="InvalidOperationException">The shader has more variants than <paramref name="limit" />.</exception>
    public static PermutationClosureResult Expand(
        RavenEffectCompiler compiler,
        string shaderName,
        ShaderComposition composition = default,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? domains = null,
        int limit = DefaultLimit
    ) {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentException.ThrowIfNullOrEmpty(shaderName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var declared = compiler.Declared(shaderName, composition);
        var prefix = shaderName + ".";
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var compilations = 0;
        var passes = 0;

        while (true) {
            passes++;
            var enumerated = declared.Where(permutation => used.Contains(permutation.Name)).ToArray();
            var found = new Dictionary<EffectKey, EffectData>();
            var grew = false;

            foreach (var assignment in Assignments(enumerated, domains)) {
                if (++compilations > limit) {
                    throw new InvalidOperationException(
                        $"'{shaderName}' has more than {limit} variants. Either narrow a permutation's domain or "
                        + "split the shader — a build that compiles this many of one shader is not going to finish."
                    );
                }

                var requested = EffectKey.Of(
                    shaderName,
                    assignment.Select(pair => new KeyValuePair<string, string>(prefix + pair.Key, pair.Value)),
                    composition
                );

                if (compiler.TryGet(requested) is not { } produced) {
                    return new([], compilations, [], false);
                }

                // Two assignments that differ only in a key this variant did not read are one
                // variant, and the key it comes back under is the one that says so.
                found[produced.ToKey()] = produced;

                foreach (var permutation in produced.Permutations) {
                    var bare = permutation.Name.StartsWith(prefix, StringComparison.Ordinal)
                        ? permutation.Name[prefix.Length..]
                        : permutation.Name;

                    grew |= used.Add(bare);
                }
            }

            // A key only read when another key is on — `if (UseShadows) { if (SoftShadows) … }` —
            // appears the first time the outer one is enumerated, and everything found before that
            // was found without it. Starting over is cheap next to shipping a bundle missing half a
            // shader's variants.
            if (!grew) {
                // Two passes is the ordinary shape — one to learn which keys are read, one to
                // enumerate them and confirm nothing new appeared. A third means a key only became
                // readable once another was set, which is what `Dependent` is reporting.
                return new(
                    [.. found.OrderBy(entry => entry.Key.ToString(), StringComparer.Ordinal).Select(entry => entry.Value)],
                    compilations,
                    [.. used],
                    passes > 2
                );
            }
        }
    }

    /// <summary>Every combination of the values each enumerated key may take.</summary>
    static IEnumerable<IReadOnlyList<KeyValuePair<string, string>>> Assignments(
        IReadOnlyList<Vixen.Raven.Reflection.PermutationInfo> keys,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? domains
    ) {
        List<KeyValuePair<string, string>> current = [];
        return Walk(0);

        IEnumerable<IReadOnlyList<KeyValuePair<string, string>>> Walk(int index) {
            if (index == keys.Count) {
                yield return current.ToArray();
                yield break;
            }

            var key = keys[index];

            foreach (var value in Domain(key, domains)) {
                current.Add(new(key.Name, value));

                foreach (var assignment in Walk(index + 1)) {
                    yield return assignment;
                }

                current.RemoveAt(current.Count - 1);
            }
        }
    }

    /// <summary>The values one key is tried at.</summary>
    static IReadOnlyList<string> Domain(
        Vixen.Raven.Reflection.PermutationInfo key,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? domains
    ) {
        if (domains?.TryGetValue(key.Name, out var supplied) == true && supplied.Count > 0) {
            return supplied;
        }

        return key.Type.Scalar == IrTypeKind.Bool ? ["false", "true"] : [key.DefaultValue];
    }
}
