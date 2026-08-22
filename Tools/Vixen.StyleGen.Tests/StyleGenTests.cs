// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling.Utilities;
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
    /// <summary>A project theme: two colours and a spacing base, over the engine's shipped default.</summary>
    /// <remarks>
    ///     ⚠ <b>A base of 2, deliberately unlike the engine's 4.</b> Every assertion below that names
    ///     a pixel — <c>gap-3</c> is 6px, <c>@apply gap-2</c> is 4px — is only about this file if the
    ///     project's <c>@theme</c> really did win. Left at the default the same numbers would come
    ///     out with the theme file deleted, and the layering would be untested by every test here.
    /// </remarks>
    const string Tokens = """
        @theme {
            --spacing: 2px;
            --color-surface: var(--surface);
            --color-surface-raised: var(--surface-raised);
        }
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
            Themes = [Write("vixen.ui.vcss", Tokens)],
            Scan = [Write("Panel.cs", """element.AddClass("gap-3");""")]
        });

        Assert.Contains(".gap-3 { gap: 6px; }", result.Css, StringComparison.Ordinal);
    }

    /// <summary>And markup, which is the other half of the same scan.</summary>
    [Fact]
    public void A_class_name_written_in_markup_reaches_the_sheet() {
        var result = Run(new StyleGenRequest {
            Themes = [Write("vixen.ui.vcss", Tokens)],
            Scan = [Write("Panel.vxml", """<row class="flex bg-surface-raised">""")]
        });

        Assert.Contains(".flex { display: flex; }", result.Css, StringComparison.Ordinal);
        Assert.Contains(".bg-surface-raised { background-color: var(--surface-raised); }", result.Css, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The base sheet is first, and the order is what this pins — not who wins.</b> The
    ///     sheet here names no layer, so it is unlayered and beats the generated utilities whatever
    ///     the specificity; a sheet that opened <c>@layer components</c> would lose to them instead.
    ///     Either way the tool has to emit it <em>first</em>, because a base rule written second would
    ///     win on source order too and a regression in the layering would then pass every test. It is
    ///     also first because a base sheet is where <c>@layer base, components, utilities;</c> belongs
    ///     when the project writes one: the ladder's order is fixed by whoever names a layer first.
    /// </summary>
    [Fact]
    public void The_base_sheet_comes_first_and_its_apply_is_expanded() {
        var result = Run(new StyleGenRequest {
            Themes = [Write("vixen.ui.vcss", Tokens)],
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
            Themes = [Write("vixen.ui.vcss", Tokens)],
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
            Themes = [Write("vixen.ui.vcss", Tokens)],
            Scan = [Write("Panel.vxml", """<row class="flexx">""")]
        });

        Assert.Contains("flexx", result.Unrecognised);
        Assert.DoesNotContain(".flexx", result.Css, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The report separates the class that half-exists from the prose, because a file with
    ///     both in one list is a file nobody can read.</b> <c>bg-clip-text</c> is a real Tailwind
    ///     class against a root this engine registers and <c>however</c> is a word out of a comment;
    ///     they used to arrive as two adjacent lines saying nothing about which was which, in a list
    ///     that for any real project is several hundred long. Both sections are headed even when
    ///     empty, so an untouched report and a report of a run that found nothing are different
    ///     files.
    /// </summary>
    [Fact]
    public void The_report_puts_a_half_registered_root_above_the_prose() {
        var result = Run(new StyleGenRequest {
            Themes = [Write("vixen.ui.vcss", Tokens)],
            Scan = [Write("Panel.vxml", """<row class="bg-clip-text however p-4">""")]
        });

        var report = StyleGenRunner.Report(result);

        Assert.Contains(
            "bg-clip-text\tthe family 'bg' has no value 'clip-text'",
            report,
            StringComparison.Ordinal
        );

        Assert.Contains("# 1 candidates named a registered family", report, StringComparison.Ordinal);
        Assert.Contains("however", report, StringComparison.Ordinal);

        // The news is above the prose, which is the whole of why there are two sections.
        Assert.True(
            report.IndexOf("bg-clip-text", StringComparison.Ordinal)
            < report.IndexOf("however", StringComparison.Ordinal)
        );

        Assert.Contains(".p-4 {", result.Css, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A <c>.vcss</c> in the scan set is read as a stylesheet, and the seven rules that fixes
    ///     were never class names.</b> The step globs <c>**/*.vcss</c> alongside the C# and the markup,
    ///     and the scanner parses nothing — so <c>position: absolute</c> in a hand-written sheet was
    ///     indistinguishable from <c>class="absolute"</c> and put <c>.absolute</c>, <c>.block</c>,
    ///     <c>.grid</c>, <c>.hidden</c>, <c>.inline</c>, <c>.relative</c> and <c>.static</c> into
    ///     <c>Vixen.Editor.Ui</c>'s generated sheet. The scope is decided here, by extension, because
    ///     this is the only place that knows what kind of file it just read.
    /// </summary>
    /// <remarks>
    ///     The same text under a <c>.cs</c> name still yields <c>flex</c>, which is the half that must
    ///     not change: a colon in C# says nothing about what is on the other side of it.
    /// </remarks>
    [Fact]
    public void A_stylesheets_declaration_values_are_not_scanned_as_class_names() {
        const string css = "panel-row { display: flex; position: absolute; }";

        var asSheet = Run(new StyleGenRequest {
            Themes = [Write("vixen.ui.vcss", Tokens)],
            Scan = [Write("Panel.vcss", css)]
        });

        Assert.DoesNotContain(".flex ", asSheet.Css, StringComparison.Ordinal);
        Assert.DoesNotContain(".absolute ", asSheet.Css, StringComparison.Ordinal);

        var asCode = Run(new StyleGenRequest {
            Themes = [Write("vixen.ui.vcss", Tokens)],
            Scan = [Write("Panel.cs", css)]
        });

        Assert.Contains(".flex { display: flex; }", asCode.Css, StringComparison.Ordinal);
        Assert.Contains(".absolute { position: absolute; }", asCode.Css, StringComparison.Ordinal);
    }

    /// <summary>A token that will not read is an error, not a stylesheet with a hole in it.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure shape changed with the format and got narrower, which is worth saying.</b>
    ///     Under YAML a whole file could fail to parse — a malformed tag, an unclosed flow sequence —
    ///     and the reader's own remarks record that being the likeliest failure of all. A stylesheet
    ///     has no such cliff: an unreadable declaration is one declaration, the block around it still
    ///     reads, and what is left is a <i>value</i> that is not the kind its namespace takes. So the
    ///     error below is a radius that is not a length rather than a file that is not YAML.
    /// </remarks>
    [Fact]
    public void A_broken_token_fails_the_build() {
        var result = Run(new StyleGenRequest {
            Themes = [Write("vixen.ui.vcss", "@theme { --spacing: wide; }")],
            Scan = []
        });

        Assert.NotEmpty(result.Errors);
    }

    /// <summary>A theme file that is not there at all is an error too, and says which path it looked at.</summary>
    [Fact]
    public void A_missing_theme_file_names_the_path() {
        var missing = Path.Combine(root, "nowhere.vcss");
        var result = Run(new StyleGenRequest { Themes = [missing] });

        Assert.Contains(result.Errors, error => error.Contains(missing, StringComparison.Ordinal));
    }

    /// <summary>The shipped palette is there with no theme file at all, in the oklch it was written in.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole point of the default, and the assertion is on the <i>colour</i> rather than
    ///     on the rule existing.</b> A palette transcribed to hex would satisfy "bg-blue-500 emits
    ///     something" and would have thrown away the thing that makes it worth shipping: two of every
    ///     three v4 colours are outside sRGB, and only an unclamped oklch triple still holds the
    ///     chroma the gamut mapper reduces at presentation. See docs/plan/43 § D4.
    /// </remarks>
    [Fact]
    public void A_project_with_no_theme_file_still_has_the_shipped_palette() {
        var result = Run(new StyleGenRequest {
            Scan = [Write("Panel.vxml", """<row class="bg-blue-500 rounded-lg p-4">""")]
        });

        Assert.Empty(result.Errors);
        Assert.Contains(".bg-blue-500 { background-color: oklch(62.3% 0.214 259.815); }", result.Css, StringComparison.Ordinal);
        Assert.Contains(".rounded-lg { border-radius: 8px; }", result.Css, StringComparison.Ordinal);
        Assert.Contains(".p-4 { padding: 16px; }", result.Css, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The override path, sabotaged.</b> A project token has to beat the shipped one, and a
    ///     test that only ever asserts the project's value would pass just as well if the default had
    ///     never been loaded. So this asserts both directions in one run: <c>blue-500</c> is the
    ///     project's, its neighbour <c>blue-600</c> is still v4's, and the namespace as a whole is
    ///     still there. Layering that silently replaced the default set instead of merging with it
    ///     would fail the second assertion; layering the wrong way round would fail the first.
    /// </summary>
    [Fact]
    public void A_projects_token_beats_the_shipped_one_and_leaves_the_rest() {
        var result = Run(new StyleGenRequest {
            Themes = [Write("vixen.ui.vcss", "@theme { --color-blue-500: #ff0000; }")],
            Scan = [Write("Panel.vxml", """<row class="bg-blue-500 bg-blue-600">""")]
        });

        Assert.Contains(".bg-blue-500 { background-color: #ff0000; }", result.Css, StringComparison.Ordinal);
        Assert.Contains(".bg-blue-600 { background-color: oklch(54.6% 0.245 262.881); }", result.Css, StringComparison.Ordinal);
    }

    /// <summary>Clearing a namespace empties it, which is how a project opts out of the default.</summary>
    /// <remarks>
    ///     v4's own mechanism, and the editor's: <c>--color-*: initial;</c> then the project's own
    ///     colours. The second half is what makes this different from "the default was never loaded"
    ///     — <c>brand</c> has to survive the clear that removed <c>blue-500</c>.
    /// </remarks>
    [Fact]
    public void Clearing_a_namespace_removes_the_shipped_tokens_and_keeps_the_projects() {
        var result = Run(new StyleGenRequest {
            Themes = [Write("vixen.ui.vcss", "@theme { --color-*: initial; --color-brand: #123456; }")],
            Scan = [Write("Panel.vxml", """<row class="bg-blue-500 bg-brand">""")]
        });

        // ⚠ A refusal and not an unrecognised candidate, and this is the case that makes the split
        // worth having: `bg` is registered, so somebody who cleared the colour namespace and forgot
        // to re-add `blue-500` wrote a class whose root exists. Reporting that as "not a utility"
        // alongside the project's prose is how a cleared token goes unnoticed.
        Assert.Equal(
            [new UtilityRefusal("bg-blue-500", "bg", "blue-500", UtilityRefusalKind.Value)],
            result.Unresolved
        );

        Assert.DoesNotContain("bg-blue-500", result.Unrecognised);
        Assert.Contains(".bg-brand { background-color: #123456; }", result.Css, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A base sheet's <c>@theme</c> is read and then taken out, and what it references comes back
    ///     as a <c>root</c> rule at the top.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Referenced, not all of it.</b> The shipped default alone is three hundred and
    ///     forty-seven declarations; emitting them whole would put every one on the root of every
    ///     document to serve the one a sheet says <c>var()</c> against. So the negative assertion
    ///     here is the load-bearing one.
    /// </remarks>
    [Fact]
    public void A_base_sheets_theme_becomes_a_root_rule_holding_only_what_is_referenced() {
        var result = Run(new StyleGenRequest {
            Base = [Write("hud.vcss", "@theme { --color-brand: #123456; }\n.card { color: var(--color-brand); }")],
            Scan = [Write("Panel.vxml", "<row>")]
        });

        Assert.DoesNotContain("@theme", result.Css, StringComparison.Ordinal);
        Assert.Contains("--color-brand: #123456;", result.Css, StringComparison.Ordinal);
        Assert.DoesNotContain("--color-blue-500", result.Css, StringComparison.Ordinal);
        Assert.True(
            result.Css.IndexOf("--color-brand", StringComparison.Ordinal) < result.Css.IndexOf(".card", StringComparison.Ordinal),
            "the root rule has to precede the sheet, so a hand-written root declaration still wins on source order"
        );
    }

    /// <summary>
    ///     ⚠ <b>A token that names itself is an alias, and writing it back out would erase what it
    ///     aliases.</b> <c>--radius-row: var(--radius-row)</c> is the editor's whole idiom — the
    ///     theme points at a property the hand-written sheet declares, so there is one palette and
    ///     not two agreeing copies. Emitted into <c>root</c> it becomes a self-reference that lands
    ///     after the real declaration, wins on source order and resolves to nothing.
    /// </summary>
    [Fact]
    public void A_token_that_references_its_own_name_is_never_emitted() {
        var result = Run(new StyleGenRequest {
            Themes = [Write("vixen.ui.vcss", "@theme { --radius-row: var(--radius-row); }")],
            Base = [Write("hud.vcss", "root { --radius-row: 4px; }")],
            Scan = [Write("Panel.vxml", """<row class="rounded-row">""")]
        });

        Assert.Contains(".rounded-row { border-radius: var(--radius-row); }", result.Css, StringComparison.Ordinal);
        Assert.DoesNotContain("--radius-row: var(--radius-row)", result.Css, StringComparison.Ordinal);
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
            Themes = [Write("vixen.ui.vcss", Tokens)],
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
            Themes = [Write("vixen.ui.vcss", Tokens)],
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
        // ⚠ The generated file carries the host's line endings, and every landmark below is a
        // newline. Searching for ";\n" on Windows found nothing, `end` came back -1, and the slice
        // threw with a negative length rather than saying which assumption was wrong.
        accessor = accessor.Replace("\r\n", "\n", StringComparison.Ordinal);

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
