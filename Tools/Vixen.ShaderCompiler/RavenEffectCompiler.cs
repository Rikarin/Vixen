// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Artefacts;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Syntax;
using Vixen.Shaders;

namespace Vixen.ShaderCompiler;

/// <summary>A compilation that produced errors rather than a shader.</summary>
public sealed class ShaderCompilationException(string message, ImmutableArray<string> diagnostics)
    : Exception(message) {
    /// <summary>Everything the compiler said, formatted as it would print them.</summary>
    public ImmutableArray<string> Diagnostics { get; } = diagnostics;
}

/// <summary>
///     Compiles a variant on demand, in process, and hands back the record the engine loads.
/// </summary>
/// <remarks>
///     <para>
///         An <see cref="IEffectSource" /> like the disk cache and the bundle, which is the whole
///         reason the tiers compose: the editor stacks a disk cache over one of these and gets
///         "compile once, then read" without either of them knowing about the other.
///     </para>
///     <para>
///         <strong>The sources are parsed once and the compilation is redone per variant.</strong>
///         Parsing is the same work for every variant — a permutation changes what binding and
///         lowering do with the tree, not what the tree is — while everything after it genuinely
///         differs, which is the point of a permutation. Sharing the trees is what makes enumerating
///         forty variants of one shader cost forty lowerings rather than forty parses as well.
///     </para>
///     <para>
///         It goes through <see cref="Compilation" /> rather than shelling out to
///         <c>vixen-raven</c>. A process per variant would be measured in tens of milliseconds of
///         startup each, and the artefact would have to make a round trip through the file system to
///         come back — which for a service answering a device over TCP is the whole latency budget
///         spent on nothing.
///     </para>
/// </remarks>
public sealed class RavenEffectCompiler : IEffectSource {
    readonly ImmutableArray<SyntaxTree> trees;
    readonly ImmutableArray<string> sources;
    readonly ImmutableArray<RavenReference> references;
    readonly ITargetBackend backend;

    /// <summary>The backend name, as <c>TargetBackends</c> knows it.</summary>
    public string Target { get; }

    /// <summary>
    ///     A hash of the sources, which every artefact this produces carries.
    /// </summary>
    /// <remarks>
    ///     What a disk cache compares against to notice that a shader was edited. Computed once from
    ///     the same texts <see cref="CompiledEffect.Create" /> hashes, so an artefact made here and
    ///     one made by the command line for the same tree agree.
    /// </remarks>
    public string SourceHash { get; }

    /// <summary>How many compilations have been run.</summary>
    public int Compilations { get; private set; }

    /// <summary>Reads and parses a set of shader sources.</summary>
    /// <param name="paths">The <c>.rvn</c> files. They become one compilation, so they see each other.</param>
    /// <param name="target">Which backend to generate for.</param>
    /// <param name="referencePaths">Compiled <c>.rvnlib</c> libraries to bind against.</param>
    /// <exception cref="ArgumentException">The target is not a backend, or a source will not parse.</exception>
    public RavenEffectCompiler(IEnumerable<string> paths, string target = "spirv", IEnumerable<string>? referencePaths = null) {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrEmpty(target);

        backend = TargetBackends.Create(target)
                  ?? throw new ArgumentException(
                      $"'{target}' is not a target. Available: {string.Join(", ", TargetBackends.Names)}.",
                      nameof(target)
                  );

        Target = target;

        var parsed = ImmutableArray.CreateBuilder<SyntaxTree>();
        var texts = ImmutableArray.CreateBuilder<string>();

        foreach (var path in paths) {
            var text = File.ReadAllText(path);
            texts.Add(text);
            parsed.Add(SyntaxTree.ParseText(text, path: path));
        }

        trees = parsed.ToImmutable();
        sources = texts.ToImmutable();

        var failures = trees.SelectMany(tree => tree.Diagnostics).Where(diagnostic => diagnostic.IsError).ToArray();

        if (failures.Length > 0) {
            throw new ArgumentException(
                $"The shader sources do not parse: {string.Join("; ", failures.Select(diagnostic => diagnostic.ToString()))}",
                nameof(paths)
            );
        }

        references = [.. (referencePaths ?? []).Select(RavenReference.FromFile)];

        // The same hash CompiledEffect computes over the same texts in the same order, so an
        // artefact made here and one made by the command line for one tree agree about whether a
        // cached entry is stale.
        SourceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", sources))));
    }

    /// <summary>
    ///     What fills a slot no key names.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A compilation binds every tree it was given, not only the shader that was
    ///         asked for.</b> So asking this for <c>Tonemap</c> — a post-process shader with no slots
    ///         of its own — still has to satisfy <c>ForwardPlus.shading</c>,
    ///         <c>GBufferPass.surface</c> and the eight slots of <c>CompositeSurface</c>, because
    ///         those files are in the same compilation. Without a default, every key that does not
    ///         happen to name all of them fails with errors about shaders it has nothing to do with.
    ///     </para>
    ///     <para>
    ///         <b>The key wins where the two overlap</b>, which is what makes this a <em>default</em>
    ///         rather than a policy: a material that chose a shading model gets that model, and the
    ///         slots it said nothing about get filled so the compilation binds.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty is the historical behaviour and is only right for a caller whose every key
    ///         is complete.</b> That is what a content build produces, because <c>MaterialCompiler</c>
    ///         fills the whole set; it is not what a host asking for one post-process variant
    ///         produces, and the difference is why this exists.
    ///     </para>
    /// </remarks>
    public ShaderComposition Composition { get; init; }

    /// <summary>The key's slots over the defaults.</summary>
    IEnumerable<KeyValuePair<string, string>> Composed(EffectKey key) {
        if (Composition.Count == 0) {
            return key.Composition.Slots;
        }

        var slots = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (slot, filler) in Composition.Slots) {
            slots[slot] = filler;
        }

        foreach (var (slot, filler) in key.Composition.Slots) {
            slots[slot] = filler;
        }

        return slots;
    }

    /// <summary>
    ///     Compiles the variant a key names.
    /// </summary>
    /// <returns>The variant, or null when this compilation has no shader by that name.</returns>
    /// <exception cref="ShaderCompilationException">The compilation reported errors.</exception>
    /// <remarks>
    ///     Null for an unknown shader and an exception for a broken one, because they are different
    ///     answers to the caller above. "I do not have it" is the ordinary miss every source gives
    ///     and the next tier answers it; "it does not compile" is a thing somebody has to read.
    /// </remarks>
    public EffectData? TryGet(EffectKey key) {
        var permutations = PermutationValues.Parse(Defines(key));
        var composes = ComposeBindings.Create(Composed(key));
        var compilation = Compilation.Create(key.ShaderName, permutations, composes, references, trees);

        Compilations++;
        Check(compilation.GetDiagnostics(), key);

        var bag = new DiagnosticBag();
        var module = Lowerer.LowerWithLinks(compilation, bag).Module;
        IrVerifier.Verify(module, bag);
        Check(bag, key);

        var shader = module.Shaders.FirstOrDefault(candidate => candidate.Name == key.ShaderName);

        if (shader is null) {
            return null;
        }

        var generated = backend.Generate(module, bag);
        Check(bag, key);

        // The backend names each unit "<shader>.<stage>", which is how a unit is attributed back to
        // the shader that produced it — a compilation of eight shaders generates units for all of
        // them and only this one's belong in this artefact.
        var units = generated
            .Where(unit => unit.Name.StartsWith(shader.Name + ".", StringComparison.Ordinal))
            .ToArray();

        var effect = CompiledEffect.Create(
            shader.Name,
            Target,
            units,
            ReflectionBuilder.Describe(shader, compilation.UsedPermutationKeys),
            permutations,
            sources
        );

        return EffectTranslator.Translate(effect, key.Composition);
    }

    /// <summary>
    ///     Every permutation key the shader declares, for enumerating its variants.
    /// </summary>
    /// <remarks>
    ///     Read off a compilation with nothing supplied, because what a shader <em>declares</em> is
    ///     the same for every variant. What it <em>reads</em> is not, which is what
    ///     <see cref="PermutationClosure" /> is for.
    /// </remarks>
    /// <returns>The declared keys, or an empty list when there is no such shader.</returns>
    public ImmutableArray<PermutationInfo> Declared(string shaderName, ShaderComposition composition = default) {
        ArgumentException.ThrowIfNullOrEmpty(shaderName);

        var compilation = Compilation.Create(
            shaderName,
            PermutationValues.Empty,
            ComposeBindings.Create(composition.Slots),
            references,
            trees
        );

        Check(compilation.GetDiagnostics(), EffectKey.Of(shaderName));

        var bag = new DiagnosticBag();
        var module = Lowerer.LowerWithLinks(compilation, bag).Module;
        Check(bag, EffectKey.Of(shaderName));

        var shader = module.Shaders.FirstOrDefault(candidate => candidate.Name == shaderName);

        return shader is null ? [] : ReflectionBuilder.Describe(shader, compilation.UsedPermutationKeys).Permutations;
    }

    /// <summary>The defines a key names, with the engine's qualification stripped off.</summary>
    /// <remarks>
    ///     The engine qualifies every key by its shader — <c>ForwardPlus.UseShadows</c> — because an
    ///     interning table is global and two shaders both declaring <c>UseShadows</c> is the ordinary
    ///     case. The compiler is inside one compilation and knows the bare name. Failing to strip it
    ///     is silent in the worst way: an unrecognised define is not an error, so the shader compiles
    ///     with its declared default and a bundle fills up with variants that are all the same
    ///     shader.
    /// </remarks>
    static IEnumerable<string> Defines(EffectKey key) {
        var prefix = key.ShaderName + ".";

        foreach (var (name, value) in key.Values) {
            var bare = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
            yield return $"{bare}={value}";
        }
    }

    static void Check(IEnumerable<Diagnostic> diagnostics, EffectKey key) {
        var errors = diagnostics.Where(diagnostic => diagnostic.IsError).Select(diagnostic => diagnostic.ToString()).ToImmutableArray();

        if (errors.Length > 0) {
            // The diagnostics go in the message as well as on the exception. Whoever sees this first
            // is reading a build log or a test failure, and an exception that says only "it did not
            // compile" makes them go and find the property.
            throw new ShaderCompilationException(
                $"'{key}' did not compile:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}",
                errors
            );
        }
    }
}
