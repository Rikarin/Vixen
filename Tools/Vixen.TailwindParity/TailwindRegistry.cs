// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vixen.TailwindParity;

/// <summary>A committed snapshot of tailwindcss's <c>__unstable__loadDesignSystem()</c> registry.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the half of doc 43's cross product that no test in this repository could
///         see.</b> Part 0 of <c>docs/plan/43-web-styling-parity.md</c> says the Tailwind side —
///         which roots exist, which classes each root covers — was transcribed by hand from
///         <c>tailwindcss@4.3.3</c> on 2026-08-07, and nothing in the tree could check the
///         transcription, because the package is not in the tree. So the ledger's measured columns
///         were re-derived on every run while the columns they are measured <i>against</i> were a
///         year-old reading nobody could re-take.
///     </para>
///     <para>
///         ⚠ <b>The refusals are recorded, and so is what was asked.</b> A snapshot listing only the
///         class names v4 rejects would answer "is this a real class?" with silence for a name it had
///         never been shown — so a row added after the snapshot was taken would pass the check
///         without anything having looked at it. <see cref="Checked" /> is therefore the full set the
///         snapshot was taken over, and a ledger class outside it is a failure that says the snapshot
///         is stale rather than one that says the class is wrong.
///     </para>
/// </remarks>
sealed record TailwindRegistry {
    /// <summary>The npm package the snapshot was taken from.</summary>
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    /// <summary>The exact version npm resolved, not the one the command line asked for.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    /// <summary>The day the snapshot was taken.</summary>
    [JsonPropertyName("taken")]
    public string Taken { get; init; } = "";

    /// <summary>Every static utility root — a class name that carries no value.</summary>
    [JsonPropertyName("staticRoots")]
    public string[] StaticRoots { get; init; } = [];

    /// <summary>Every functional utility root — the name half of a class that takes a value.</summary>
    [JsonPropertyName("functionalRoots")]
    public string[] FunctionalRoots { get; init; } = [];

    /// <summary>Every variant v4 registers, which is the other half of the vocabulary.</summary>
    [JsonPropertyName("variants")]
    public string[] Variants { get; init; } = [];

    /// <summary>Every class name the snapshot asked the v4 compiler about.</summary>
    [JsonPropertyName("checked")]
    public string[] Checked { get; init; } = [];

    /// <summary>The subset of <see cref="Checked" /> that v4 compiles to nothing at all.</summary>
    [JsonPropertyName("refused")]
    public string[] Refused { get; init; } = [];

    /// <summary>Reads a snapshot from its committed JSON.</summary>
    /// <param name="path">The snapshot file.</param>
    /// <returns>The registry.</returns>
    public static TailwindRegistry Read(string path) {
        ArgumentNullException.ThrowIfNull(path);

        var snapshot = JsonSerializer.Deserialize<TailwindRegistry>(File.ReadAllText(path));

        if (snapshot is null || snapshot.Version.Length == 0) {
            throw new InvalidOperationException($"{path} is not a tailwindcss registry snapshot");
        }

        // ⚠ An empty registry agrees with every ledger ever written, so it is refused here rather
        // than downstream. This is the shape doc 43's own exit criterion 3 calls out — an
        // enumeration that returns nothing passes every membership test over it.
        if (snapshot.FunctionalRoots.Length == 0 || snapshot.StaticRoots.Length == 0) {
            throw new InvalidOperationException($"{path} lists no utility roots, so it can prove nothing");
        }

        return snapshot;
    }
}
