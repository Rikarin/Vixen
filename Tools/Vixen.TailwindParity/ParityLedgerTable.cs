// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TailwindParity;

/// <summary>One row of <c>docs/plan/43-web-styling-parity.tsv</c>, read by column name.</summary>
/// <param name="Root">The Tailwind root the row is about, <c>inset-*</c> or <c>sr-only</c>.</param>
/// <param name="Kind"><c>static</c> or <c>functional</c>, as Tailwind's own registry splits them.</param>
/// <param name="Example">One class the row is exercised by.</param>
/// <param name="Classes">The static class names the survey recorded under this root.</param>
sealed record LedgerRow(string Root, string Kind, string Example, IReadOnlyList<string> Classes);

/// <summary>The parity ledger, read for its Tailwind-side columns only.</summary>
/// <remarks>
///     ⚠ <b>Read by column <i>name</i>, where <c>ParityLedger</c> in the test project reads by
///     index.</b> That is not a style preference. This assembly does not build with the ledger's own
///     reader and would otherwise carry a second copy of <c>Cells[1]</c> through <c>Cells[13]</c>,
///     which is the shape that silently measures the wrong column the day somebody inserts one. The
///     header line is in the file; there is no reason to hard-code its order twice.
/// </remarks>
static class ParityLedgerTable {
    /// <summary>Reads the ledger's Tailwind-side columns.</summary>
    /// <param name="path">The <c>.tsv</c>.</param>
    /// <returns>One entry per data row.</returns>
    public static IReadOnlyList<LedgerRow> Read(string path) {
        ArgumentNullException.ThrowIfNull(path);

        var lines = File.ReadAllLines(path);

        if (lines.Length == 0) {
            throw new InvalidOperationException($"{path} is empty");
        }

        var header = lines[0].Split('\t');
        var root = IndexOf(header, "root", path);
        var kind = IndexOf(header, "kind", path);
        var example = IndexOf(header, "example", path);
        var classes = IndexOf(header, "classes", path);
        var rows = new List<LedgerRow>();

        for (var index = 1; index < lines.Length; index++) {
            if (lines[index].Length == 0) {
                continue;
            }

            var cells = lines[index].Split('\t');

            if (cells.Length != header.Length) {
                throw new InvalidOperationException(
                    $"line {index + 1} of {path} has {cells.Length} columns, not {header.Length}"
                );
            }

            rows.Add(
                new LedgerRow(
                    cells[root],
                    cells[kind],
                    cells[example],
                    cells[classes].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                )
            );
        }

        if (rows.Count == 0) {
            throw new InvalidOperationException($"{path} has a header and no rows, so it can prove nothing");
        }

        return rows;
    }

    static int IndexOf(string[] header, string name, string path) {
        var index = Array.IndexOf(header, name);

        if (index < 0) {
            throw new InvalidOperationException($"{path} has no `{name}` column");
        }

        return index;
    }
}
