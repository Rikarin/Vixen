// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;

namespace Vixen.Raven;

/// <summary>
///     Which concrete shader fills each <c>compose</c> slot — one material's worth of
///     feature choices.
/// </summary>
/// <remarks>
///     <para>
///         A <c>compose</c> member is a shader-typed field declared against a
///         <c>protocol</c>: <c>compose val diffuse: IDiffuseModel</c>. The shader is written
///         once against the protocol, and each material says which implementation to use.
///         Resolution happens at compile time, so a call through the slot becomes a direct
///         call to the implementation — there is no dispatch at runtime.
///     </para>
///     <para>
///         Slots are named by field, optionally qualified by shader
///         (<c>Lit.diffuse</c>) when two shaders in one compilation declare a slot of the
///         same name and need different implementations.
///     </para>
/// </remarks>
public sealed class ComposeBindings : IEnumerable<KeyValuePair<string, string>> {
    /// <summary>Nothing bound. A shader with an unfilled slot will not compile.</summary>
    public static ComposeBindings Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    readonly Dictionary<string, string> bindings;

    /// <summary>Number of slots bound.</summary>
    public int Count => bindings.Count;

    ComposeBindings(Dictionary<string, string> bindings) {
        this.bindings = bindings;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => bindings.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Builds a set from slot/shader pairs.</summary>
    public static ComposeBindings Create(IEnumerable<KeyValuePair<string, string>> bindings) {
        ArgumentNullException.ThrowIfNull(bindings);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (slot, shader) in bindings) {
            map[slot] = shader;
        }

        return map.Count == 0 ? Empty : new(map);
    }

    /// <summary>
    ///     Parses <c>slot=Shader</c> strings, as a command line or material definition
    ///     supplies them. The slot may be qualified: <c>Lit.diffuse=Lambert</c>.
    /// </summary>
    /// <exception cref="ArgumentException">An entry is malformed.</exception>
    public static ComposeBindings Parse(IEnumerable<string> entries) =>
        TryParse(entries, out var result, out var error)
            ? result
            : throw new ArgumentException(error, nameof(entries));

    /// <summary>
    ///     Parses <c>slot=Shader</c> strings, reporting a malformed entry rather than
    ///     throwing — for callers that show the problem to a person.
    /// </summary>
    /// <param name="error">Why parsing failed, or null on success.</param>
    public static bool TryParse(IEnumerable<string> entries, out ComposeBindings bindings, out string? error) {
        ArgumentNullException.ThrowIfNull(entries);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries) {
            if (string.IsNullOrWhiteSpace(entry)) {
                continue;
            }

            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0) {
                bindings = Empty;
                error = $"Malformed compose binding '{entry}': expected slot=Shader.";
                return false;
            }

            var slot = entry[..separator].Trim();
            var shader = entry[(separator + 1)..].Trim();

            if (slot.Length == 0 || shader.Length == 0) {
                bindings = Empty;
                error = $"Malformed compose binding '{entry}': both the slot and the shader must be named.";
                return false;
            }

            map[slot] = shader;
        }

        bindings = map.Count == 0 ? Empty : new(map);
        error = null;
        return true;
    }

    /// <summary>
    ///     The shader bound to a slot, or null if none is. A binding qualified by shader
    ///     wins over a bare one, so a compilation can bind most slots once and override
    ///     per shader.
    /// </summary>
    public string? Resolve(string containingShader, string slot) =>
        bindings.GetValueOrDefault($"{containingShader}.{slot}") ?? bindings.GetValueOrDefault(slot);
}
