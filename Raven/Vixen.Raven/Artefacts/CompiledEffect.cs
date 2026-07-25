// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Artefacts;

/// <summary>One pipeline stage inside a compiled effect.</summary>
/// <param name="Stage">The stage this module drives.</param>
/// <param name="Name">The unit's name, as the backend produced it.</param>
/// <param name="Bytes">
///     SPIR-V words for a binary target, or the UTF-8 source for a text target. Which it is
///     follows from <paramref name="IsBinary" />, not from guessing at the content.
/// </param>
/// <param name="IsBinary">Whether <paramref name="Bytes" /> is a binary module or source text.</param>
public sealed record EffectModule(ShaderStage Stage, string Name, ImmutableArray<byte> Bytes, bool IsBinary) {
    /// <summary>The module as text. Only meaningful for a source target.</summary>
    public string AsText() => Encoding.UTF8.GetString(Bytes.AsSpan());
}

/// <summary>
///     A compiled effect: every stage's module, the interface to bind it against, and enough
///     provenance to know whether it is still valid.
/// </summary>
/// <remarks>
///     <para>
///         The unit the runtime loads instead of compiling. It is written as <c>.rvnfx</c> by
///         <see cref="CompiledEffectWriter" /> and read back by <see cref="CompiledEffectReader" />.
///     </para>
///     <para>
///         <see cref="PermutationKey" /> and <see cref="SourceHash" /> together answer "can I use
///         this?". The key is built from the permutation values that <em>mattered</em>, so two
///         variants differing only in an unread flag share one artefact rather than filling the
///         cache with duplicates.
///     </para>
/// </remarks>
public sealed record CompiledEffect {
    /// <summary>Name of the shader this was compiled from.</summary>
    public required string Name { get; init; }

    /// <summary>Backend that produced the modules, as <see cref="TargetBackends" /> names it.</summary>
    public required string Target { get; init; }

    /// <summary>One module per stage.</summary>
    public ImmutableArray<EffectModule> Modules { get; init; } = [];

    /// <summary>The interface to bind against.</summary>
    public required RavenReflection Reflection { get; init; }

    /// <summary>
    ///     The permutation values that produced this, keyed by name — only the keys that were
    ///     actually read. This is the cache key.
    /// </summary>
    public ImmutableSortedDictionary<string, string> PermutationKey { get; init; } =
        ImmutableSortedDictionary<string, string>.Empty;

    /// <summary>
    ///     Hash of the source this was compiled from, so a stale artefact can be detected without
    ///     recompiling to compare.
    /// </summary>
    public string SourceHash { get; init; } = string.Empty;

    /// <summary>The module for a stage, or null if this effect has none.</summary>
    public EffectModule? ModuleFor(ShaderStage stage) => Modules.FirstOrDefault(m => m.Stage == stage);

    /// <summary>
    ///     Builds the artefact for one shader out of a finished compilation.
    /// </summary>
    /// <param name="name">The shader's name.</param>
    /// <param name="target">Backend name.</param>
    /// <param name="generated">The units the backend produced for this shader.</param>
    /// <param name="reflection">The shader's reflection.</param>
    /// <param name="permutationValues">Everything that was supplied.</param>
    /// <param name="sources">Source texts, hashed in the order given.</param>
    public static CompiledEffect Create(
        string name,
        string target,
        IEnumerable<GeneratedSource> generated,
        RavenReflection reflection,
        PermutationValues? permutationValues = null,
        IEnumerable<string>? sources = null
    ) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(reflection);

        var modules = generated
            .Select(unit => new EffectModule(
                unit.Stage,
                unit.Name,
                [.. unit.Binary ?? Encoding.UTF8.GetBytes(unit.Code)],
                unit.IsBinary
            ))
            .ToImmutableArray();

        return new() {
            Name = name,
            Target = target,
            Modules = modules,
            Reflection = reflection,
            PermutationKey = BuildKey(reflection.UsedPermutationKeys, permutationValues),
            SourceHash = HashSources(sources)
        };
    }

    /// <summary>
    ///     The cache key: only the keys the compilation read, with the values it read them as.
    /// </summary>
    /// <remarks>
    ///     A key that was declared but never read is deliberately absent. That is the whole
    ///     economy of the permutation system — twenty independent flags describe a million
    ///     combinations, and keying on the ones that mattered collapses them to the handful that
    ///     differ.
    /// </remarks>
    static ImmutableSortedDictionary<string, string> BuildKey(
        ImmutableArray<string> usedKeys,
        PermutationValues? values
    ) {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var key in usedKeys) {
            // Absent means the declared default was used, which is itself part of the identity.
            builder[key] = Format(values?.GetValueOrDefault(key));
        }

        return builder.ToImmutable();
    }

    static string Format(object? value) =>
        value switch {
            null => "default",
            bool flag => flag ? "true" : "false",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "default"
        };

    /// <summary>
    ///     SHA-256 over the sources, newline-separated. Content-addressed, so the same input
    ///     always produces the same hash on any machine.
    /// </summary>
    static string HashSources(IEnumerable<string>? sources) {
        if (sources is null) {
            return string.Empty;
        }

        var joined = string.Join("\n", sources);
        if (joined.Length == 0) {
            return string.Empty;
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }
}
