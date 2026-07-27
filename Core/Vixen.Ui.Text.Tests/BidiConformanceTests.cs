// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>The UAX#9 conformance suite, in its code-point form.</summary>
/// <remarks>
///     <para>
///         <c>BidiCharacterTest.txt</c> rather than <c>BidiTest.txt</c>: the first is written in real
///         code points and so exercises the property table as well as the algorithm, where the second
///         is written in class names and tests the algorithm alone. The first subsumes the second's
///         coverage in the way that matters, and committing both would put fifteen megabytes of test
///         data in the repository to say one thing twice.
///     </para>
///     <para>
///         Each case gives the paragraph, the requested direction, the resolved paragraph level, the
///         level of every character, and the visual order. All four are checked — a level array that
///         is right and a reordering that is wrong is a real and common failure.
///     </para>
/// </remarks>
public class BidiConformanceTests {
    [Fact]
    public void The_conformance_suite_is_green() {
        var failures = new List<string>();
        var total = 0;

        foreach (var line in Lines()) {
            var fields = line.Split(';');
            if (fields.Length < 5) {
                continue;
            }

            total++;

            var codePoints = fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(hex => int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToArray();

            var direction = fields[1] switch {
                "0" => ParagraphDirection.LeftToRight,
                "1" => ParagraphDirection.RightToLeft,
                _ => ParagraphDirection.Auto
            };

            var expectedLevel = int.Parse(fields[2], CultureInfo.InvariantCulture);
            var result = BidiAlgorithm.Resolve(codePoints, direction);

            // A level of `x` means the character was removed, and its level is not checked — X9
            // says so, and asserting one would be asserting an implementation detail.
            var expectedLevels = fields[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var expectedOrder = fields[4].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                .ToArray();

            var wrong = result.ParagraphLevel != expectedLevel || !result.VisualOrder.SequenceEqual(expectedOrder);

            for (var i = 0; !wrong && i < expectedLevels.Length; i++) {
                if (expectedLevels[i] == "x") {
                    continue;
                }

                wrong = result.Levels[i] != int.Parse(expectedLevels[i], CultureInfo.InvariantCulture);
            }

            if (!wrong || failures.Count >= 6) {
                continue;
            }

            failures.Add(
                $"  {fields[0]} dir={fields[1]}\n"
                + $"    paragraph: expected {expectedLevel} got {result.ParagraphLevel}\n"
                + $"    levels:    expected [{fields[3]}] got [{string.Join(" ", result.Levels)}]\n"
                + $"    order:     expected [{fields[4]}] got [{string.Join(" ", result.VisualOrder)}]"
            );
        }

        Assert.True(total > 90_000, $"only {total} cases were read — the data file is not being embedded");
        Assert.True(
            failures.Count == 0,
            $"{failures.Count}+ of {total} UAX#9 cases failed:\n{string.Join("\n", failures)}"
        );
    }

    static IEnumerable<string> Lines() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Text.Tests.Generated.BidiCharacterConformance.data")
            ?? throw new InvalidOperationException("the conformance data is not embedded");

        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line) {
            if (line.Length > 0 && line[0] != '#') {
                yield return line;
            }
        }
    }
}
