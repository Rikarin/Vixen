// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.StyleGen.Tests;

/// <summary>The build step, over a project made of temporary files.</summary>
/// <remarks>
///     ⚠ <b>Real files rather than an abstraction over them.</b> The whole of what this step does is
///     read files MSBuild named and write files MSBuild will compile, so a file-system seam would
///     leave every interesting failure — a path that does not exist, an output rewritten when it did
///     not change — on the side of the seam the tests do not cross.
/// </remarks>
public sealed class StyleGenTests : IDisposable {
    const string Tokens = """
        theme:
            spacing: 2
            colors:
                surface:
                    DEFAULT: 'var(--surface)'
                    raised: 'var(--surface-raised)'
        """;

    readonly string root = Directory.CreateTempSubdirectory("vixen-stylegen").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>
    ///     ⚠ <b>C# is scanned, and this is the case the whole mechanism was worth building for.</b>
    ///     Most of the editor's chrome is built in code with <c>AddClass("…")</c>, and the startup
    ///     bootstrap this step replaces could only ever see embedded markup — so every utility a
    ///     C#-built panel asked for was silently missing. The scanner does not parse anything, which
    ///     is exactly what lets it be pointed at a <c>.cs</c> file.
    /// </summary>
    [Fact]
    public void A_class_name_written_only_in_csharp_reaches_the_sheet() {
        var result = Run(new StyleGenRequest {
            Tokens = Write("vixen.ui.yaml", Tokens),
            Scan = [Write("Panel.cs", """element.AddClass("gap-3");""")]
        });

        Assert.Contains(".gap-3 { gap: 6px; }", result.Css, StringComparison.Ordinal);
    }

    /// <summary>And markup, which is the other half of the same scan.</summary>
    [Fact]
    public void A_class_name_written_in_markup_reaches_the_sheet() {
        var result = Run(new StyleGenRequest {
            Tokens = Write("vixen.ui.yaml", Tokens),
            Scan = [Write("Panel.vxml", """<row class="flex bg-surface-raised">""")]
        });

        Assert.Contains(".flex { display: flex; }", result.Css, StringComparison.Ordinal);
        Assert.Contains(".bg-surface-raised { background-color: var(--surface-raised); }", result.Css, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The base sheet is first, and unlayered, and that is what decides the cascade.</b> A
    ///     generated utility is in <c>@layer utilities</c> and a layer loses to an unlayered rule
    ///     whatever the source order — so putting the hand-written sheet second would make it win for
    ///     the second reason as well, and a regression in the layering would then pass every test.
    ///     This is also the shape the step will be used in once the editor's three themes stop being
    ///     C# string constants: they arrive as <c>--base</c> files and nothing else changes.
    /// </summary>
    [Fact]
    public void The_base_sheet_comes_first_and_its_apply_is_expanded() {
        var result = Run(new StyleGenRequest {
            Tokens = Write("vixen.ui.yaml", Tokens),
            Base = [Write("theme.vcss", ".card { @apply gap-2; color: red; }")],
            Scan = [Write("Panel.vxml", """<row class="flex">""")]
        });

        Assert.StartsWith(".card { gap: 4px;  color: red; }", result.Css, StringComparison.Ordinal);
        Assert.True(
            result.Css.IndexOf(".card", StringComparison.Ordinal) < result.Css.IndexOf("@layer utilities", StringComparison.Ordinal),
            "the utilities were emitted before the hand-written sheet"
        );
    }

    /// <summary>A safelisted name is emitted whether or not anything was seen to write it.</summary>
    /// <remarks>
    ///     The escape hatch for a class name assembled at run time: <c>$"text-{rarity}"</c> is
    ///     <c>text-</c> and a variable, and no scanner can see the whole of it.
    /// </remarks>
    [Fact]
    public void A_safelisted_name_is_emitted_with_nothing_using_it() {
        var result = Run(new StyleGenRequest {
            Tokens = Write("vixen.ui.yaml", Tokens),
            Scan = [Write("Panel.vxml", "<row>")],
            Safelist = ["bg-surface"]
        });

        Assert.Contains(".bg-surface { background-color: var(--surface); }", result.Css, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A misspelt utility is reported, because nothing else in the tool chain can see one.</b>
    ///     Reported into a file rather than as a warning: the scanner is over-inclusive on purpose,
    ///     so most of what lands here is ordinary English out of a comment, and a build that warned
    ///     about each would be one nobody reads the output of. A project's own suite asks the narrow
    ///     question — is every name written in a <c>class</c> attribute a real utility — and this
    ///     list is what it has to ask it of.
    /// </summary>
    [Fact]
    public void A_misspelt_utility_is_reported_and_the_prose_with_it() {
        var result = Run(new StyleGenRequest {
            Tokens = Write("vixen.ui.yaml", Tokens),
            Scan = [Write("Panel.vxml", """<row class="flexx">""")]
        });

        Assert.Contains("flexx", result.Unrecognised);
        Assert.DoesNotContain(".flexx", result.Css, StringComparison.Ordinal);
    }

    /// <summary>A theme file that will not read is an error, not a stylesheet with no colours in it.</summary>
    [Fact]
    public void A_broken_theme_file_fails_the_build() {
        var result = Run(new StyleGenRequest {
            Tokens = Write("vixen.ui.yaml", "theme:\n  colors:\n    accent: [not, a, colour]\n"),
            Scan = []
        });

        Assert.NotEmpty(result.Errors);
    }

    /// <summary>A theme file that is not there at all is an error too, and says which path it looked at.</summary>
    [Fact]
    public void A_missing_theme_file_names_the_path() {
        var missing = Path.Combine(root, "nowhere.yaml");
        var result = Run(new StyleGenRequest { Tokens = missing });

        Assert.Contains(result.Errors, error => error.Contains(missing, StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ <b>An output rewritten with identical bytes is a rebuild of everything downstream.</b>
    ///     Every incremental step in the tool chain reads timestamps, so an unconditional write makes
    ///     the compiler rerun on every build — and makes a hot-reload watcher fire on every build,
    ///     which is the one that would be blamed on the watcher.
    /// </summary>
    [Fact]
    public void An_unchanged_output_is_not_rewritten() {
        var request = new StyleGenRequest {
            Tokens = Write("vixen.ui.yaml", Tokens),
            Scan = [Write("Panel.vxml", """<row class="flex">""")],
            Output = Path.Combine(root, "out", "sheet.g.vcss"),
            Accessor = Path.Combine(root, "out", "Styles.g.cs")
        };

        StyleGenRunner.Write(request, StyleGenRunner.Run(request));

        var stamp = File.GetLastWriteTimeUtc(request.Output);
        File.SetLastWriteTimeUtc(request.Output, stamp.AddDays(-1));
        var moved = File.GetLastWriteTimeUtc(request.Output);

        StyleGenRunner.Write(request, StyleGenRunner.Run(request));

        Assert.Equal(moved, File.GetLastWriteTimeUtc(request.Output));
    }

    /// <summary>
    ///     The accessor is C# a compiler will take, and the constant in it is the sheet byte for
    ///     byte — which is the assertion that would fail if the escaping were wrong.
    /// </summary>
    /// <remarks>
    ///     ⚠ Checked by decoding the literal rather than by comparing the generated text to an
    ///     expectation of it. A test written the other way passes whenever the generator and the
    ///     expectation are wrong in the same way, and the thing at risk here is precisely an escape
    ///     rule — a stylesheet is full of quotes, backslashes and newlines.
    /// </remarks>
    [Fact]
    public void The_accessor_carries_the_sheet_unchanged() {
        var request = new StyleGenRequest {
            Tokens = Write("vixen.ui.yaml", Tokens),
            Scan = [Write("Panel.vxml", """<row class="flex content-[&quot;a\b&quot;]">""")],
            Namespace = "Some.Where",
            Class = "Styles"
        };

        var result = StyleGenRunner.Run(request);
        var accessor = StyleGenRunner.Accessor(request, result);

        Assert.Contains("namespace Some.Where;", accessor, StringComparison.Ordinal);
        Assert.Contains("internal static class Styles", accessor, StringComparison.Ordinal);
        Assert.Equal(result.Css, Decode(accessor, "Css"));
        Assert.Equal(result.Utilities, Decode(accessor, "Utilities"));
    }

    /// <summary>Public when asked, because a project's own accessor sometimes is the public one.</summary>
    [Fact]
    public void The_accessor_is_public_when_asked() {
        var request = new StyleGenRequest { Public = true, Class = "Styles" };

        Assert.Contains(
            "public static class Styles",
            StyleGenRunner.Accessor(request, StyleGenRunner.Run(request)),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     ⚠ <b>The response file, which is the real argument surface.</b> A project with four hundred
    ///     sources gives four hundred paths, past the command-line limit on Windows and near it
    ///     elsewhere — so the <c>.targets</c> always writes one, and a bug here is a build that works
    ///     on the machine it was written on.
    /// </summary>
    [Fact]
    public void A_response_file_is_the_command_line() {
        var response = Write("stylegen.rsp", "--class\nStyles\n--scan\na path with spaces.vxml\n--public\n");
        var request = Arguments.Parse([$"@{response}"], out var error);

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("Styles", request.Class);
        Assert.True(request.Public);
        Assert.Equal(["a path with spaces.vxml"], request.Scan);
    }

    /// <summary>
    ///     ⚠ <b>An unknown option fails rather than being ignored.</b> Nobody reads this command line
    ///     while it is working, so an option renamed on one side and not the other has to be loud —
    ///     the quiet version is a build that stops scanning and a stylesheet that loses half its
    ///     rules.
    /// </summary>
    [Theory]
    [InlineData("--scann")]
    [InlineData("--Scan")]
    public void An_unknown_option_is_refused(string option) {
        Assert.Null(Arguments.Parse([option, "x"], out var error));
        Assert.Contains(option, error!, StringComparison.Ordinal);
    }

    /// <summary>An option given nothing to be is refused too, rather than reading the next option as its value.</summary>
    [Fact]
    public void An_option_with_no_value_is_refused() {
        Assert.Null(Arguments.Parse(["--scan"], out var error));
        Assert.NotNull(error);
    }

    static StyleGenResult Run(StyleGenRequest request) => StyleGenRunner.Run(request);

    string Write(string name, string content) {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Reads a generated <c>const string</c> back out of the C#, undoing the escaping.</summary>
    static string Decode(string accessor, string name) {
        var start = accessor.IndexOf($"public const string {name} =", StringComparison.Ordinal);
        Assert.True(start >= 0, $"the accessor has no '{name}'");

        var end = accessor.IndexOf(";\n", start, StringComparison.Ordinal);
        var body = accessor[(start + $"public const string {name} =".Length)..end];
        var text = new System.Text.StringBuilder();

        foreach (var piece in body.Split('+')) {
            var trimmed = piece.Trim();

            if (trimmed.Length < 2) {
                continue;
            }

            var inner = trimmed[1..^1];

            for (var index = 0; index < inner.Length; index++) {
                if (inner[index] != '\\') {
                    text.Append(inner[index]);
                    continue;
                }

                text.Append(inner[++index] switch {
                    'n' => '\n',
                    'r' => '\r',
                    '"' => '"',
                    _ => '\\'
                });
            }
        }

        return text.ToString();
    }
}
