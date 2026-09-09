// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     The prelude's three lists — the build's, the assembly's and the prose's — are one list.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this is for.</b> <see cref="TextureKernelPrelude.Sources" /> is derived from the
///         manifest, so it cannot drift from what the build embedded. What it can drift from is the
///         <c>.csproj</c> that names the files and the paragraph beside it that explains why exactly
///         those files — and a paragraph is where the reason lives, so a wrong one is worse than no
///         one. ⚠ <b>Two comments counted three while four were embedded, for a batch after the
///         fourth landed</b>: <c>Core/ColorSpaces.rvn</c> joined the set to close
///         <a href="https://github.com/Rikarin/Vixen/issues/1093">#1093</a> and the prose that
///         argued for "these three" stayed. Nothing in the tree could see it, because a count in a
///         doc comment is not a thing anything reads.
///     </para>
///     <para>
///         ⚠ <b>So the count is written as something derived rather than remembered.</b> The prose
///         still spells a number, because "why exactly these four" is a better sentence than "why
///         exactly these", and <see cref="The_prelude_s_prose_counts_what_the_prelude_carries" />
///         is what makes the number a claim the tree checks: a fifth library file cannot land
///         without the paragraph following it, and a paragraph that has stopped matching is red on
///         the batch that broke it rather than on the one that notices.
///     </para>
///     <para>
///         ⚠ <b>Ask what these print on the day the prelude is empty.</b> Both sides would be empty
///         and every set comparison would agree with itself — which is why each case asserts the
///         walk found something before comparing anything, and why the two sides come from genuinely
///         different places: the <c>.csproj</c>'s text on disk against
///         <c>GetManifestResourceNames</c>'s answer about the built assembly. A test that derived
///         both from the manifest would be a claim that the manifest equals itself.
///     </para>
///     <para>
///         ⚠ <b>Anchored at this file's compiled path and never walked up to the repository
///         root.</b> <c>.claude/worktrees</c> holds a whole checkout per agent, so a walk from the
///         root would read some other agent's copy of the project file.
///     </para>
/// </remarks>
public sealed class TexturePreludeManifestTests {
    /// <summary>How the prose spells a small count, which is how English spells it.</summary>
    /// <remarks>
    ///     Only as far as the set could plausibly grow. A prelude of nine library files is a
    ///     different design and should not be waved through by a lookup table that happened to have
    ///     a word for it.
    /// </remarks>
    static readonly string[] Words = [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight"
    ];

    /// <summary>The <c>.csproj</c> names exactly the library files the assembly carries.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What this can and cannot see, stated rather than assumed.</b> The expectation is
    ///         the <c>LogicalName</c> attributes in the project file, read as text; the subject is
    ///         <see cref="TextureKernelPrelude.Sources" />, which walks the built assembly's
    ///         manifest. The csproj is what *fills* that manifest, so **deleting an item moves both
    ///         sides at once and this case stays green** — the shape of a fixture that builds its own
    ///         inputs. What it does catch is the reader's half, which the csproj does not control:
    ///         <c>TextureKernelPrelude</c>'s own <c>Prefix</c> and the filter in its <c>Read</c>. A
    ///         prefix renamed on one side of that seam empties <c>Sources</c> while the project file
    ///         still declares four, and every kernel that <c>import</c>s the library then fails
    ///         <c>RVN2010</c> at run time rather than here.
    ///     </para>
    ///     <para>
    ///         <b>The deletion is caught by the other case, and that is why they are a pair.</b>
    ///         <see cref="The_prelude_s_prose_counts_what_the_prelude_carries" /> reads a number out
    ///         of the prose, which no build action can move — so an item removed from the csproj
    ///         leaves a paragraph arguing for four files beside three.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the text is compared too, not only the names.</b> The <c>EmbeddedResource</c>
    ///         points at <c>..\..\Raven\Library\**</c> so that editing the library edits what a
    ///         kernel compiles against — the prelude's own claim, and a copy taken into this
    ///         assembly would satisfy every name comparison here and drift in silence.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_prelude_is_what_the_project_file_embeds() {
        var project = ProjectFile();
        var declared = Declared(File.ReadAllText(project));

        Assert.True(
            declared.Length > 0,
            $"'{project}' declares no `LogicalName` under '{Prefix}'. Either the prelude stopped being "
            + "embedded — in which case every kernel that imports the library now fails RVN2010 — or the "
            + "regex above no longer matches how the item is written, and this case is comparing nothing "
            + "with nothing."
        );

        var embedded = TextureKernelPrelude.Sources
            .Select(source => source.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, embedded);

        var library = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Here())!)!)!,
            "Raven",
            "Library"
        );

        Assert.All(
            TextureKernelPrelude.Sources,
            source => {
                // ⚠ The name is `Core.ColorSpaces.rvn`, so the last dot is the extension and every
                // dot before it is a folder. Replacing all of them would ask for `Core/ColorSpaces/rvn`.
                var stem = source.Name[..source.Name.LastIndexOf('.')];
                var path = Path.Combine(library, stem.Replace('.', Path.DirectorySeparatorChar) + ".rvn");

                Assert.True(
                    File.Exists(path),
                    $"'{path}' is not on disk, so '{source.Name}' names a library file this checkout has not "
                    + "got and the comparison below cannot run."
                );

                Assert.Equal(
                    File.ReadAllText(path).ReplaceLineEndings("\n"),
                    source.Text.ReplaceLineEndings("\n")
                );
            }
        );
    }

    /// <summary>The paragraph that argues for the set names every file in it, and counts them right.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the half that made the stale "three" possible.</b> The prose is where the
    ///         <em>reason</em> for each entry lives — <c>Core/Math.rvn</c> is there because
    ///         <c>Core/Random.rvn</c> spells <c>Math.SphericalToCartesian</c>, and a set that stops
    ///         short fails <c>RVN2010</c> on every kernel at once — so a file with no sentence beside
    ///         it is a file nobody can argue with later.
    ///     </para>
    ///     <para>
    ///         <b>Both the names and the number.</b> The names catch a file that landed without its
    ///         paragraph; the number catches the subtler one, which is a paragraph edited to mention
    ///         a new file while its opening count stayed where it was. ⚠ The count is the one a
    ///         reader takes away — "why exactly these four" is what tells them the list is closed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_prelude_s_prose_counts_what_the_prelude_carries() {
        var source = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Here())!)!,
            "Vixen.Editor.TextureGraph",
            "TextureKernelPrelude.cs"
        );

        Assert.True(
            File.Exists(source),
            $"'{source}' is not on disk. It is anchored at this file's compiled path; a run whose sources are "
            + "not on the machine cannot take this case, and a silent pass over a missing file is the failure "
            + "this class is about."
        );

        var prose = File.ReadAllText(source);
        var carried = TextureKernelPrelude.Sources;

        Assert.NotEmpty(carried);

        Assert.All(
            carried,
            entry => {
                // The prose spells a library file the way the library does — `Core/ColorSpaces.rvn`
                // — and the manifest name is the same path with dots. One rewrite, and it is the
                // last dot that is the extension.
                var written = entry.Name[..entry.Name.LastIndexOf('.')].Replace('.', '/')
                    + entry.Name[entry.Name.LastIndexOf('.')..];

                Assert.True(
                    prose.Contains(written, StringComparison.Ordinal),
                    $"`TextureKernelPrelude`'s remarks never say '{written}', so a library file is in every "
                    + "kernel's compilation with no sentence saying why. The paragraph is the only place the "
                    + "reason for an entry lives — nothing else in the tree records that `Core/Math.rvn` is "
                    + "there for `Core/Random.rvn`'s sake."
                );
            }
        );

        Assert.True(
            carried.Length < Words.Length,
            $"The prelude now carries {carried.Length} sources and this case only knows the English for "
            + $"{Words.Length - 1}. A set that large is a design change rather than an entry, and it should not "
            + "be waved through by a lookup table."
        );

        var counted = "these " + Words[carried.Length];

        Assert.True(
            prose.Contains(counted, StringComparison.Ordinal),
            $"`TextureKernelPrelude` carries {carried.Length} library sources and its remarks never say "
            + $"'{counted}'. ⚠ This is exactly the drift #1093 left behind: `Core/ColorSpaces.rvn` joined the "
            + "set and two comments went on arguing for three files. Either the paragraph's count is stale, or "
            + "a source landed without the sentence that says why it had to."
        );
    }

    /// <summary>The prefix a prelude source's <c>LogicalName</c> is given.</summary>
    /// <remarks>
    ///     ⚠ Deliberately not <c>Vixen.Editor.TextureGraph.Shaders.</c> — <c>TextureKernels</c> reads
    ///     every <c>.rvn</c> under that one and <em>that set is the kernel list</em>, so a library
    ///     file landing there would register as a kernel with no <c>[ComputeShader]</c>.
    /// </remarks>
    const string Prefix = "Vixen.Editor.TextureGraph.Prelude.";

    /// <summary>Where this file was compiled from.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>This test project's sibling, the assembly under test.</summary>
    static string ProjectFile() => Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(Here())!)!,
        "Vixen.Editor.TextureGraph",
        "Vixen.Editor.TextureGraph.csproj"
    );

    /// <summary>Every prelude resource name the project file declares, ordered as the manifest is.</summary>
    /// <param name="project">The project file's text.</param>
    /// <returns>The names, without the prefix.</returns>
    static string[] Declared(string project) => [
        .. Regex
            .Matches(project, "LogicalName=\"(?<name>[^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["name"].Value)
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(name => name[Prefix.Length..])
            .Order(StringComparer.Ordinal)
    ];
}
