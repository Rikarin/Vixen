// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vixen.Shaders;

/// <summary>One variant a build is asked to produce.</summary>
/// <remarks>
///     An <see cref="EffectKey" /> in a shape a person can write and a diff can show. The key itself
///     is a struct with a precomputed hash and an immutable array — right for a dictionary on a frame
///     path, wrong for a file somebody edits by hand to add the shader their new material needs.
/// </remarks>
public sealed record EffectRequest {
    /// <summary>The shader.</summary>
    public string Shader { get; init; } = string.Empty;

    /// <summary>Its permutation values, by name. Absent means the declared default.</summary>
    public SortedDictionary<string, string> Permutations { get; init; } = new(StringComparer.Ordinal);

    /// <summary>What fills its <c>compose</c> slots, by slot.</summary>
    public SortedDictionary<string, string> Composition { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The key this names.</summary>
    public EffectKey ToKey() => EffectKey.Of(Shader, Permutations, ShaderComposition.Of(Composition));

    /// <summary>The request for a key.</summary>
    public static EffectRequest From(EffectKey key) {
        var request = new EffectRequest { Shader = key.ShaderName };

        foreach (var (name, value) in key.Values) {
            request.Permutations[name] = value;
        }

        foreach (var (slot, shader) in key.Composition.Slots) {
            request.Composition[slot] = shader;
        }

        return request;
    }
}

/// <summary>
///     The set of variants a build must produce, as a file.
/// </summary>
/// <remarks>
///     <para>
///         The input to build-time pre-generation and the output of a development run:
///         <see cref="EffectSystem.Requests" /> is exactly this list, so the loop is play the game
///         against a compiler, write the manifest, build the bundle, and the next run compiles
///         nothing. A project can also check one in and edit it, which is what you do for the variant
///         that only appears in the ending nobody on the team has reached yet.
///     </para>
///     <para>
///         JSON rather than the engine's binary serializer, which is what
///         <see cref="EffectBundle" /> uses. The bundle is a shipped artefact read only by the
///         runtime; this is a build input read by people, reviewed in a diff, and merged when two
///         branches each add a material. Those want opposite things from a format.
///     </para>
/// </remarks>
public sealed record EffectManifest {
    /// <summary>What to produce.</summary>
    public EffectRequest[] Effects { get; init; } = [];

    /// <summary>The manifest for a set of keys, deduplicated and ordered so two runs match.</summary>
    /// <remarks>
    ///     Ordered by the key's own text, which is its normal form. A manifest whose order followed
    ///     the order a playthrough happened to ask in would produce a different file from every run
    ///     of the same level, and the diff would be unreadable exactly when it mattered.
    /// </remarks>
    public static EffectManifest Of(IEnumerable<EffectKey> keys) {
        ArgumentNullException.ThrowIfNull(keys);

        return new() {
            Effects = [
                .. keys
                    .Distinct()
                    .OrderBy(key => key.ToString(), StringComparer.Ordinal)
                    .Select(EffectRequest.From)
            ]
        };
    }

    /// <summary>The keys it names.</summary>
    public IEnumerable<EffectKey> ToKeys() => Effects.Select(request => request.ToKey());

    /// <summary>Reads a manifest.</summary>
    /// <exception cref="InvalidDataException">The text is not a manifest.</exception>
    public static EffectManifest Parse(string json) =>
        JsonSerializer.Deserialize(json, EffectManifestJson.Default.EffectManifest)
        ?? throw new InvalidDataException("The effect manifest is empty or unreadable.");

    /// <summary>Writes it.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, EffectManifestJson.Default.EffectManifest);
}

/// <summary>The manifest's serialiser, generated rather than reflected over.</summary>
/// <remarks>
///     <c>System.Text.Json</c>'s reflecting entry points are unusable here: this assembly is compiled
///     AOT-clean, and both of them are annotated as needing dynamic code. The generated context costs
///     one partial class and makes the manifest readable on a trimmed, AOT-published build — which is
///     precisely the build a "which variants did this run ask for" capture is most wanted from.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(EffectManifest))]
internal sealed partial class EffectManifestJson : JsonSerializerContext;
