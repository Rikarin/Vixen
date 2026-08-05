// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Samples.Mmo.Authoring;

/// <summary>Turns the tables into a world.</summary>
/// <remarks>
///     ⚠ <b>Every method here writes one kind of thing and returns the addresses it wrote</b>, so the
///     next one can point at them. That is the whole reason the order in <see cref="Program" /> is
///     fixed: a loot table cannot name an item before the item exists, and a creature cannot name a
///     loot table before the table does. The content test checks the result anyway — this only makes
///     the generator's own mistakes cheap to find.
/// </remarks>
sealed partial class World(string root) {
    readonly string root = root;

    readonly List<string> written = [];

    /// <summary>How many files were emitted.</summary>
    public int Count => written.Count;

    /// <summary>Every address written, for a summary.</summary>
    public IReadOnlyList<string> Written => written;

    /// <summary>How many of a prefix.</summary>
    /// <param name="prefix">The address prefix.</param>
    /// <returns>The count.</returns>
    public int CountOf(string prefix) =>
        written.Count(address => address.StartsWith(prefix, StringComparison.Ordinal));

    void Write(string address, string extension, Yaml yaml) {
        var path = Path.Combine(root, address.Replace('/', Path.DirectorySeparatorChar) + extension);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml.ToString());
        written.Add(address);
    }

    /// <summary>Rounds a number the way a designer would: to something that looks chosen.</summary>
    /// <param name="value">The raw number.</param>
    /// <returns>It, rounded to a readable step.</returns>
    /// <remarks>
    ///     ⚠ <b>Not cosmetic.</b> Generated content reads as generated when every number is
    ///     <c>1 447</c>, and content nobody believes is content nobody checks. Stepping to fives and
    ///     tens is what a spreadsheet's author does by hand and it costs nothing.
    /// </remarks>
    internal static int Nice(float value) =>
        value switch {
            < 20 => (int)MathF.Round(value),
            < 200 => (int)(MathF.Round(value / 5) * 5),
            < 2_000 => (int)(MathF.Round(value / 10) * 10),
            _ => (int)(MathF.Round(value / 50) * 50)
        };

    internal static string Text(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
