// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Layout.Tests.Taffy;

/// <summary>
///     That the attribute map actually reaches every attribute the corpus uses.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the check that pays for the corpus being data rather than generated code.</b>
///     Yoga's fixtures are C#, so an attribute nobody translated is a method that does not compile.
///     Taffy's are XML read at run time, so the same mistake is a silent no-op — a fixture that sets
///     <c>aspect-ratio</c>, has it ignored, and passes anyway because the expected numbers happen to
///     match. At 5 524 fixtures that would never be noticed by reading.
///
///     So: derive the attribute set from the committed corpus, push every one of them through
///     <see cref="TaffyStyleMap" />, and require an answer. Applied or refused are both answers.
///     Falling through is not, and <see cref="TaffyStyleMap" /> throws
///     <see cref="InvalidOperationException" /> rather than <see cref="TaffyUnsupportedException" />
///     when it happens, which is what this tells apart.
/// </remarks>
public class TaffyCorpusCoverageTests {
    [Fact]
    public void Every_attribute_in_the_corpus_is_either_applied_or_refused_by_name() {
        var seen = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var category in TaffyCorpus.Categories) {
            foreach (var fixture in TaffyCorpus.Load(category)) {
                Collect(fixture.Input, seen);
            }
        }

        using var tree = new LayoutTree();
        var unmapped = new List<string>();

        foreach (var (name, value) in seen) {
            var node = tree.CreateNode();

            try {
                TaffyStyleMap.Apply(tree, node, name, value, default, default);
            } catch (TaffyUnsupportedException) {
                // A deliberate refusal. That is coverage, not a hole.
            } catch (InvalidOperationException) {
                unmapped.Add(name);
            }
        }

        Assert.Empty(unmapped);

        // ⚠ Pinned, so that a corpus refresh which *removes* an attribute is as loud as one that adds
        // it. A shrinking corpus is the quieter of the two failures and the easier one to miss.
        Assert.Equal(56, seen.Count);
    }

    static void Collect(TaffyInput input, SortedDictionary<string, string> seen) {
        foreach (var (name, value) in input.Attributes) {
            seen.TryAdd(name, value);
        }

        foreach (var child in input.Children) {
            Collect(child, seen);
        }
    }
}
