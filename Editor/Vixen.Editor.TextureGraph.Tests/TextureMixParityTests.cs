// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary><c>Mix.rvn</c> is <c>Blend.rvn</c> plus one line, and this is what holds it to that.</summary>
/// <remarks>
///     <para>
///         <b>The two files carry the same sixteen operators because a texture-graph kernel cannot
///         import one.</b> <c>TexturePlanEvaluator.VariantFor</c> compiles a kernel with no
///         <c>referencePaths</c> — <a href="https://github.com/Rikarin/Vixen/issues/635">#635</a> —
///         so a shared <c>Combine</c> has nowhere to live, which is the same bargain the thirteen
///         functions transcribed out of the shader library already make and which
///         <c>TextureKernelLanguageSeamTests</c> already keeps for those. This is that arrangement
///         for the pair <a href="https://github.com/Rikarin/Vixen/issues/1059">#1059</a> added.
///     </para>
///     <para>
///         ⚠ <b>A text equality and not a shape comparison, because these two are not spelled
///         differently on purpose.</b> The seam tests compare numbers, operators and calls because a
///         kernel's transcription of a library function renames parameters and drops
///         <c>static</c>; <c>Mix.Combine</c> is a copy of <c>Blend.Combine</c> made by a script, so
///         the strongest available assertion is that it still is one. A comparison of extracted
///         shapes would go green on a copy that had lost a variable.
///     </para>
///     <para>
///         ⚠ <b>What this would say if it read nothing at all.</b> An extractor that answered the
///         empty string for both files would make every equality here pass, which is the exact shape
///         of instrument this repository keeps finding. So
///         <see cref="Both_copies_of_the_operator_table_are_whole" /> reads the sixteen selectors out
///         of what was extracted before anything is compared, and it is the assertion the other two
///         rest on.
///     </para>
///     <para>
///         <b>It is not a claim that either is correct.</b> Two identical wrong transcriptions pass
///         here and fail on a device — <c>TextureBlendDeviceTests</c> for one and
///         <c>TextureMixDeviceTests</c> for the other, and the second compares the two kernels'
///         pictures under all sixteen modes rather than their text.
///     </para>
/// </remarks>
public class TextureMixParityTests {
    /// <summary>The sixteenth is the fall-through, so fifteen selectors are written out.</summary>
    /// <remarks>
    ///     ⚠ <b>Read out of the extraction rather than trusted.</b> <c>Copy</c> has no <c>mode ==</c>
    ///     of its own — <c>Combine</c> falls through to the foreground — so a table that had lost its
    ///     last case would still answer for a copy. The count below is what says the body is the
    ///     whole body; the equality is what says the two are the same one.
    /// </remarks>
    static ImmutableArray<int> Selectors { get; } = [.. Enumerable.Range(1, 15)];

    /// <summary>Comment markers, which are prose about the arithmetic rather than the arithmetic.</summary>
    static readonly Regex Comment = new(@"//.*$", RegexOptions.Multiline);

    /// <summary>Neither extraction is empty, and both hold all fifteen written selectors.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the instrument for the other two tests in this file</b>, which are equalities
    ///     and would therefore be satisfied by two empty strings. It is deliberately an assertion
    ///     about <em>content</em> rather than about length: a body truncated after mode 8 is a
    ///     plausible copy-paste accident and has a plausible length.
    /// </remarks>
    [Fact]
    public void Both_copies_of_the_operator_table_are_whole() {
        foreach (var kernel in new[] { "Blend", "Mix" }) {
            var body = Function(TextureKernels.Source(kernel), "Combine");

            Assert.NotEmpty(body);

            foreach (var selector in Selectors) {
                Assert.Contains($"mode == {selector}", body, StringComparison.Ordinal);
            }

            // The fall-through, which is what a mode nobody wrote a case for produces — and what
            // makes a missing case invisible on a device, because it is a `Copy` and a `Copy` is a
            // picture.
            Assert.Contains("return b", body, StringComparison.Ordinal);
        }
    }

    /// <summary>The sixteen operators are the same text in both files, comments aside.</summary>
    [Fact]
    public void The_operator_table_is_the_same_in_both_kernels() =>
        Assert.Equal(
            Lines(Function(TextureKernels.Source("Blend"), "Combine")),
            Lines(Function(TextureKernels.Source("Mix"), "Combine"))
        );

    /// <summary>
    ///     ⚠ The composite differs in exactly one line, and that line is where the opacity is read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Exactly one, counted, rather than "the mask appears somewhere".</b> The whole claim
    ///         <c>Mix</c> makes about itself is that it is <c>Blend</c> with the opacity read per
    ///         texel; a second difference — an alpha rule quietly simplified, the <c>atop</c> branch
    ///         dropped, the <c>max(alpha, 1e-6)</c> floor moved — is the thing this exists to catch,
    ///         and it is invisible in every picture where the backdrop is opaque, which is every
    ///         picture a texture graph makes until a group isolates one (#832, #899).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the line is named on both sides.</b> Asserting only "one line differs" would
    ///         pass if the difference had moved to some other line and the opacity had gone back to
    ///         being a uniform — a <c>Mix</c> that ignores its mask, which is a kernel that composites
    ///         everything everywhere.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_composite_differs_in_exactly_the_line_that_reads_the_mask() {
        var blend = Lines(Function(TextureKernels.Source("Blend"), "Main"));
        var mix = Lines(Function(TextureKernels.Source("Mix"), "Main"));

        Assert.NotEmpty(blend);
        Assert.Equal(blend.Length, mix.Length);

        var differing = Enumerable.Range(0, blend.Length)
            .Where(at => !string.Equals(blend[at], mix[at], StringComparison.Ordinal))
            .ToArray();

        Assert.Single(differing);

        Assert.Equal("val amount = saturate(opacity) * saturate(b.w)", blend[differing[0]]);
        Assert.Equal(
            "val amount = saturate(opacity * Coverage(coord.x, coord.y)) * saturate(b.w)",
            mix[differing[0]]
        );
    }

    /// <summary>One function's body, with its comments and its blank lines removed.</summary>
    /// <param name="source">The kernel's Raven text.</param>
    /// <param name="name">The function.</param>
    /// <returns>The body, braces included.</returns>
    /// <remarks>
    ///     ⚠ <b>Every kernel compared here is a block body, so there is no <c>=&gt;</c> case</b> —
    ///     unlike <c>TextureKernelLanguageSeamTests.Function</c>, which needs one because the
    ///     <c>Random</c> helpers are written both ways. An arrow function reached by this extractor
    ///     would return the <em>next</em> function's block, which is the failure where a comparison
    ///     passes while reading the wrong thing; <c>Assert.True</c> below is what stops it, because
    ///     an arrow before the brace means the name found is not a block.
    /// </remarks>
    static string Function(string source, string name) {
        var text = Comment.Replace(source, string.Empty);
        var at = text.IndexOf($"func {name}(", StringComparison.Ordinal);

        Assert.True(at >= 0, $"no `func {name}(` in the kernel this test was pointed at");

        var open = text.IndexOf('{', at);
        var arrow = text.IndexOf("=>", at, StringComparison.Ordinal);

        Assert.True(open >= 0, $"`func {name}` has no body");
        Assert.True(
            arrow < 0 || arrow > open,
            $"`func {name}` is an expression body, which this extractor cannot read"
        );

        var depth = 0;

        for (var index = open; index < text.Length; index++) {
            if (text[index] == '{') {
                depth++;
            } else if (text[index] == '}') {
                depth--;

                if (depth == 0) {
                    return text[open..(index + 1)];
                }
            }
        }

        Assert.Fail($"`func {name}` has no closing brace");

        return string.Empty;
    }

    /// <summary>A body as its non-empty lines, each trimmed.</summary>
    /// <remarks>
    ///     Indentation is not the arithmetic and neither is a blank line left behind by a stripped
    ///     comment, so both are removed before anything is compared — and a difference in either
    ///     would otherwise read as a difference in the composite.
    /// </remarks>
    static string[] Lines(string body) =>
        [.. body.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];
}
