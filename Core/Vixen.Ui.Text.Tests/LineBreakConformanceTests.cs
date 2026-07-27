// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Text;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>The UAX#14 line breaking conformance suite.</summary>
/// <remarks>
///     Every case is the Unicode Consortium's, and its own description of the case is the failure
///     message — so a failure names the rule it broke rather than leaving it to be worked out from
///     two lists of numbers.
/// </remarks>
public class LineBreakConformanceTests {
    [Fact]
    public void The_conformance_suite_is_green() {
        var failures = new List<string>();
        var total = 0;

        foreach (var (codePoints, expected, description) in Cases()) {
            total++;

            var (text, offsets) = Encode(codePoints);
            var opportunities = new List<int>();

            LineBreaker.Collect(text, opportunities);

            var actual = opportunities.Select(offset => offsets[offset]).ToArray();
            if (actual.SequenceEqual(expected)) {
                continue;
            }

            if (failures.Count < 6) {
                failures.Add(
                    $"  expected [{string.Join(", ", expected)}] got [{string.Join(", ", actual)}]  {description}"
                );
            }
        }

        Assert.True(total > 19_000, $"only {total} cases were read — the data file is not being embedded");
        Assert.True(
            failures.Count == 0,
            $"{failures.Count}+ of {total} UAX#14 cases failed:\n{string.Join("\n", failures)}"
        );
    }

    static IEnumerable<(int[] CodePoints, int[] Expected, string Description)> Cases() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Text.Tests.Generated.LineBreakConformance.data")
            ?? throw new InvalidOperationException("the conformance data is not embedded");

        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line) {
            if (line.Length == 0 || line[0] == '#') {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 2) {
                continue;
            }

            yield return (
                [.. fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(hex => int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture))],
                [.. fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)],
                fields.Length > 2 ? fields[2] : string.Empty
            );
        }
    }

    static (string Text, Dictionary<int, int> Offsets) Encode(int[] codePoints) {
        var builder = new StringBuilder();
        var offsets = new Dictionary<int, int>();

        for (var i = 0; i < codePoints.Length; i++) {
            offsets[builder.Length] = i;
            builder.Append(char.ConvertFromUtf32(codePoints[i]));
        }

        offsets[builder.Length] = codePoints.Length;
        return (builder.ToString(), offsets);
    }
}
