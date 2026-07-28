// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Serialization;
using Vixen.Shaders;

namespace Vixen.ShaderCompiler;

/// <summary>
///     Build-time pre-generation: the third tier, produced.
/// </summary>
/// <remarks>
///     <para>
///         Two ways in, because the question "which variants does this project need" has two honest
///         answers and neither one alone is enough. <see cref="Add(EffectManifest)" /> takes the set
///         a run actually asked for — the exact answer, available only after somebody plays the game.
///         <see cref="AddClosure" /> takes a shader and produces every variant it has — the complete
///         answer, available before anybody plays it, at the cost of compiling variants nothing will
///         ever draw.
///     </para>
///     <para>
///         A project uses both: closures for the handful of shaders every frame goes through, a
///         manifest for the long tail. What comes out is one <see cref="EffectBundle" /> either way,
///         and a shipping build that loads it and nothing else cannot compile a shader.
///     </para>
///     <para>
///         <strong>A key that will not compile fails the build here.</strong> The alternative is a
///         bundle missing an entry, which is a miss at run time — on a device, in a level, weeks
///         later, presenting as an object that does not draw.
///     </para>
/// </remarks>
public sealed class EffectBundleBuilder(RavenEffectCompiler compiler) {
    readonly EffectStore store = new();
    readonly List<EffectKey> missing = [];

    /// <summary>What compiles the variants.</summary>
    public RavenEffectCompiler Compiler { get; } = compiler;

    /// <summary>How many distinct variants have been compiled.</summary>
    public int Count => store.Count;

    /// <summary>
    ///     Keys that were asked for and that no shader answered.
    /// </summary>
    /// <remarks>
    ///     Collected rather than thrown, because a stale manifest naming a material somebody deleted
    ///     is an ordinary thing to find and the build should report all of them at once rather than
    ///     the first. A compilation <em>error</em> is different and does throw: that is a broken
    ///     shader, not a stale request.
    /// </remarks>
    public ImmutableArray<EffectKey> Missing => [.. missing];

    /// <summary>Compiles everything a manifest names.</summary>
    /// <exception cref="ShaderCompilationException">One of them did not compile.</exception>
    public void Add(EffectManifest manifest) {
        ArgumentNullException.ThrowIfNull(manifest);
        Add(manifest.ToKeys());
    }

    /// <summary>Compiles a set of keys.</summary>
    /// <exception cref="ShaderCompilationException">One of them did not compile.</exception>
    public void Add(IEnumerable<EffectKey> keys) {
        ArgumentNullException.ThrowIfNull(keys);

        foreach (var key in keys) {
            if (Compiler.TryGet(key) is not { } effect) {
                missing.Add(key);
                continue;
            }

            Include(effect);
        }
    }

    /// <summary>Compiles every variant one shader has.</summary>
    /// <param name="shaderName">The shader.</param>
    /// <param name="composition">What fills its <c>compose</c> slots.</param>
    /// <param name="domains">Which values to try for a numeric permutation key. See <see cref="PermutationClosure" />.</param>
    /// <returns>What the enumeration found, for a build that wants to report it.</returns>
    public PermutationClosureResult AddClosure(
        string shaderName,
        ShaderComposition composition = default,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? domains = null
    ) {
        var closure = PermutationClosure.Expand(Compiler, shaderName, composition, domains);

        if (closure.Effects.Length == 0) {
            missing.Add(EffectKey.Of(shaderName).With(composition));
            return closure;
        }

        foreach (var effect in closure.Effects) {
            Include(effect);
        }

        return closure;
    }

    /// <summary>The bundle, ordered so two builds of one set produce one file.</summary>
    public EffectBundle Build() => store.ToBundle();

    /// <summary>Writes the bundle where a content build will pick it up.</summary>
    public void Write(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) {
            System.IO.Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, Serializer.ToBytes(Build()));
    }

    /// <summary>
    ///     Adds a variant, ignoring one already present.
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="EffectStore.Add" />, which refuses a duplicate. Reaching the same
    ///     variant twice is expected here and means nothing is wrong: a manifest captured from a run
    ///     names variants a closure also produces, and two materials with different features often
    ///     resolve to one compiled shader. The store's refusal is about a <em>baked bundle</em>
    ///     holding two records under one key, which is still impossible.
    /// </remarks>
    void Include(EffectData effect) {
        if (store.TryGet(effect.ToKey()) is null) {
            store.Add(effect);
        }
    }
}
