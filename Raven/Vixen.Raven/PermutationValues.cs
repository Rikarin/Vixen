// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Globalization;

namespace Vixen.Raven;

/// <summary>
///     The values supplied for a compilation's <c>[Permutation]</c> keys — one effect
///     variant's worth of settings.
/// </summary>
/// <remarks>
///     <para>
///         A permutation key is a shader field whose value is fixed at compile time but
///         comes from outside the source: <c>[Permutation] val UseSkinning: bool = false</c>
///         reads as <c>false</c> unless a value is supplied here. Branches on it fold and
///         the dead side is dropped, so a variant compiles to only the code it uses.
///     </para>
///     <para>
///         Keys not present here take the initializer in the source as their value, which is
///         why one is mandatory. Keys present here that no shader declares are ignored — the
///         engine passes a whole effect's settings to each module it compiles.
///     </para>
/// </remarks>
public sealed class PermutationValues : IEnumerable<KeyValuePair<string, object>> {
    /// <summary>No values supplied; every key takes its declared default.</summary>
    public static PermutationValues Empty { get; } = new(new Dictionary<string, object>(StringComparer.Ordinal));

    readonly Dictionary<string, object> values;

    /// <summary>Number of keys supplied.</summary>
    public int Count => values.Count;

    PermutationValues(Dictionary<string, object> values) {
        this.values = values;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    ///     Builds a set from name/value pairs. Values must be <see cref="bool" />,
    ///     <see cref="int" /> or <see cref="uint" />; anything else is rejected here rather
    ///     than surfacing as a confusing type error deep in binding.
    /// </summary>
    /// <exception cref="ArgumentException">A value is not a supported permutation type.</exception>
    public static PermutationValues Create(IEnumerable<KeyValuePair<string, object>> values) {
        ArgumentNullException.ThrowIfNull(values);

        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in values) {
            if (value is not (bool or int or uint)) {
                throw new ArgumentException(
                    $"Permutation key '{key}' was given a {value?.GetType().Name ?? "null"}; "
                    + "permutation values must be bool, int or uint.",
                    nameof(values)
                );
            }

            map[key] = value;
        }

        return map.Count == 0 ? Empty : new(map);
    }

    /// <summary>
    ///     Parses <c>Name=Value</c> strings, as a command line or build script supplies them.
    ///     <c>Name</c> alone means <c>Name=true</c>, matching how a define reads.
    /// </summary>
    /// <exception cref="ArgumentException">An entry is malformed or its value does not parse.</exception>
    public static PermutationValues Parse(IEnumerable<string> defines) =>
        TryParse(defines, out var values, out var error) ? values : throw new ArgumentException(error, nameof(defines));

    /// <summary>
    ///     Parses <c>Name=Value</c> strings, reporting a malformed entry rather than throwing.
    /// </summary>
    /// <remarks>
    ///     For callers that show the problem to a person. A command line reporting
    ///     <c>ArgumentException.Message</c> would append "(Parameter 'defines')", which means
    ///     nothing to whoever typed the define.
    /// </remarks>
    /// <param name="error">Why parsing failed, or null on success.</param>
    public static bool TryParse(
        IEnumerable<string> defines,
        out PermutationValues values,
        out string? error
    ) {
        ArgumentNullException.ThrowIfNull(defines);

        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var define in defines) {
            if (string.IsNullOrWhiteSpace(define)) {
                continue;
            }

            var separator = define.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0) {
                map[define.Trim()] = true;
                continue;
            }

            var name = define[..separator].Trim();
            var text = define[(separator + 1)..].Trim();

            if (name.Length == 0) {
                values = Empty;
                error = $"Malformed permutation define '{define}': the name is empty.";
                return false;
            }

            if (!TryParseValue(text, out var value)) {
                values = Empty;
                error = $"Malformed permutation define '{define}': '{text}' is not a bool, int or uint.";
                return false;
            }

            map[name] = value;
        }

        values = map.Count == 0 ? Empty : new(map);
        error = null;
        return true;
    }

    /// <summary>The value supplied for <paramref name="key" />, or null if none was.</summary>
    public object? GetValueOrDefault(string key) => values.GetValueOrDefault(key);

    /// <summary>Whether a value was supplied for <paramref name="key" />.</summary>
    public bool Contains(string key) => values.ContainsKey(key);

    static bool TryParseValue(string text, out object value) {
        if (bool.TryParse(text, out var flag)) {
            value = flag;
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed)) {
            value = signed;
            return true;
        }

        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned)) {
            value = unsigned;
            return true;
        }

        value = false;
        return false;
    }
}
