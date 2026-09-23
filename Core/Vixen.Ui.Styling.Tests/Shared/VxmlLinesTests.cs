// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Markup.Testing;

/// <summary>What <see cref="VxmlLines" /> lets a sweep believe a line says.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Linked into every suite beside the reader itself, so each assembly proves its own
///         copy.</b> The reader exists because two sweeps in two assemblies had drifted about what a
///         markup line is; a single copy of its tests, compiled into one of them, would leave the
///         other trusting a file nothing there had run.
///     </para>
///     <para>
///         ⚠ <b>Synthetic lines rather than the repository, and that is the point rather than a
///         shortcut.</b> Every case here is one no committed <c>.vxml</c> writes today — that was
///         measured, both for the comment spans and for the <c>@code</c> body — so a test reading the
///         tree would be green against a reader that handled none of them. These are the lines the
///         guard exists for, and there is nowhere else they can be shown.
///     </para>
/// </remarks>
public class VxmlLinesTests {
    /// <summary>A comment's span is cut out; the markup sharing its line is not.</summary>
    /// <remarks>
    ///     ⚠ <b>The false-accusation direction.</b> Dropping the whole line loses the <c>foo</c> and
    ///     the <c>bar</c> below, and a reach census that cannot see a tag reports it as a name the
    ///     repository never writes — a rule deleted for being dead while the element it styles is on
    ///     screen. That is the one direction a reach census must not err in.
    /// </remarks>
    [Fact]
    public void Markup_beside_a_comment_survives_it() {
        var read = VxmlLines.Read([
            "<foo /> <!-- a note about <ghost> -->",
            "<!-- an opening, mentioning <phantom>",
            "     and its second line, also <spectre> --> <bar>",
            "<baz />"
        ]);

        var markup = string.Join("\n", read.Select(static line => line.Text));

        Assert.Contains("<foo", markup, StringComparison.Ordinal);
        Assert.Contains("<bar", markup, StringComparison.Ordinal);
        Assert.Contains("<baz", markup, StringComparison.Ordinal);

        Assert.DoesNotContain("ghost", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("phantom", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("spectre", markup, StringComparison.Ordinal);
    }

    /// <summary>The two halves of a comment are not spliced into a name neither wrote.</summary>
    [Fact]
    public void A_comment_leaves_a_gap_rather_than_a_join() {
        var read = VxmlLines.Read(["<Panel Foo=\"a<!-- … -->b\" />"]);

        Assert.DoesNotContain("ab", read[0].Text, StringComparison.Ordinal);
    }

    /// <summary>Everything from the <c>@code</c> line on is C#, and the directive line with it.</summary>
    [Fact]
    public void A_code_body_is_labelled_as_code() {
        var read = VxmlLines.Read([
            "<Panel />",
            "@code {",
            "    /// <summary>A <paramref name=\"row\" /> and a <b>word</b>.</summary>",
            "    void Build(Row row) {",
            "        Fields.Add(\"input-title\");",
            "    }",
            "}"
        ]);

        Assert.Equal(VxmlRegion.Markup, read[0].Region);
        Assert.All(read.Skip(1), static line => Assert.Equal(VxmlRegion.Code, line.Region));

        // The `///` block is dropped outright, which is where `<b>`, `<para>` and `<paramref>` come
        // from and the reason `strong` was reported while `b`, `i` and `em` were not.
        Assert.DoesNotContain(read, static line => line.Text.Contains("paramref", StringComparison.Ordinal));
    }

    /// <summary>The line numbers are the file's, not the kept lines'.</summary>
    /// <remarks>
    ///     A census reports <c>path:line</c> and a reader that renumbered as it filtered would send
    ///     every reader of a failure to the wrong line — quietly, since the number would still look
    ///     like one.
    /// </remarks>
    [Fact]
    public void The_numbers_are_the_files_own() {
        var read = VxmlLines.Read([
            "<!-- a header -->",
            "",
            "<Panel />",
            "<Other />"
        ]);

        Assert.Equal(2, read.Count);
        Assert.Equal(3, read[0].Number);
        Assert.Equal(4, read[1].Number);
    }

    /// <summary>A block comment opening a line of a <c>@code</c> body is prose, as it is in a <c>.cs</c>.</summary>
    [Fact]
    public void A_block_comment_in_a_code_body_is_not_code() {
        var read = VxmlLines.Read([
            "@code {",
            "    /* Fields.Add(\"from-a-comment\"); */",
            "    void Build() => Fields.Add(\"from-code\");",
            "}"
        ]);

        Assert.DoesNotContain(read, static line => line.Text.Contains("from-a-comment", StringComparison.Ordinal));
        Assert.Contains(read, static line => line.Text.Contains("from-code", StringComparison.Ordinal));
    }

    /// <summary>The masked file is the file, line for line, with only the prose gone.</summary>
    /// <remarks>
    ///     A sweep that counts newlines to report a line — <c>SchedulerReachTests</c> does — needs a
    ///     blank where a comment was rather than no line at all, or every line after the header is
    ///     reported one too high per line it had.
    /// </remarks>
    [Fact]
    public void The_masked_file_keeps_every_line_in_its_place() {
        string[] file = [
            "<!-- <Demo Name=\"prose\" />",
            "     still prose -->",
            "<Panel Name=\"markup\" /> <!-- note -->",
            "@code {",
            "    // Name = \"a comment\"",
            "    void Build() => Name = \"code\";",
            "}"
        ];

        var masked = VxmlLines.Masked(file);

        Assert.Equal(file.Length, masked.Length);
        Assert.Equal(string.Empty, masked[0]);
        Assert.Equal(string.Empty, masked[1]);
        Assert.Contains("\"markup\"", masked[2], StringComparison.Ordinal);
        Assert.DoesNotContain("note", masked[2], StringComparison.Ordinal);
        Assert.Equal(string.Empty, masked[4]);
        Assert.Contains("\"code\"", masked[5], StringComparison.Ordinal);
    }

    /// <summary>Only a <c>.vxml</c> is masked; a <c>.cs</c> comes back exactly as it was handed in.</summary>
    [Fact]
    public void Only_markup_is_masked() {
        string[] file = ["<!-- <Demo Name=\"prose\" /> -->", "// a comment"];

        Assert.Same(file, VxmlLines.Source("A.cs", file));
        Assert.Equal([string.Empty, "// a comment"], VxmlLines.Source("A.vxml", file));
        Assert.Equal([string.Empty, "// a comment"], VxmlLines.Source("A.VXML", file));
    }

    /// <summary>The <c>@code</c> directive is recognised, and a longer word starting the same is not.</summary>
    [Theory]
    [InlineData("@code {", true)]
    [InlineData("    @code", true)]
    [InlineData("@code{", true)]
    [InlineData("@codegen {", false)]
    [InlineData("@code_name", false)]
    [InlineData("<!-- @code -->", false)]
    [InlineData("@using Vixen.Ui", false)]
    public void The_code_directive_is_a_whole_word(string text, bool code) => Assert.Equal(code, VxmlLines.IsCode(text));
}
