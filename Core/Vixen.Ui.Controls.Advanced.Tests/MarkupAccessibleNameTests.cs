// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using Vixen.Ui.Markup.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     Every English word a <c>.vxml</c> hands a control to <i>say</i>, held against a committed
///     census.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>Rikarin/Vixen#1338</c>: the accessibility gate is green on exactly the population it
///         cannot judge.</b> <see cref="Vixen.Ui.Testing.AccessibilitySnapshot.Untranslated" /> asks
///         whether an announced word is still the source text of a <i>declared</i> string — it builds
///         its comparison out of the declaration table, so a control announcing a literal that no
///         declaration class carries has nothing to compare against and passes. Silently, in every
///         assembly, and widening the window of controls it walks does not reach it: the blindness is
///         in what the comparison can express.
///     </para>
///     <para>
///         ⚠ <b>Undecidable at run time and decidable in the source, which is why this is a scan and
///         not another sweep.</b> A running control announcing "Directional Light" may be reading an
///         entity's name, a file's name or a hex colour — none of which a catalogue could ever carry
///         — so no walk of a live tree can tell an untranslatable literal from data. In markup the
///         two are different syntax: <c>Label="@EditorStrings.PluginsReload.Text"</c> goes through the
///         catalogue and <c>Label="Add Component"</c> is a word a translator can never reach.
///     </para>
///     <para>
///         ⚠ <b>The attributes are the ones that become a <i>spoken</i> name, and each is proved to be
///         one rather than believed to be.</b> <c>ButtonBase.NativeAccessibleName</c> is
///         <c>Label</c>, <c>Alert</c>'s and <c>Dialog</c>'s is <c>Title</c>, <c>Toast</c>'s is
///         <c>Message</c>, <c>Image</c>'s is <c>Description</c> and <c>Avatar</c>'s is <c>Name</c>.
///         <see cref="Every_watched_attribute_reaches_a_spoken_name" /> builds one control per
///         attribute and shows the word coming back out of a walk of the accessibility tree, so the
///         day an override moves, this census is reported as watching a dead attribute instead of
///         going quietly vacuous.
///     </para>
///     <para>
///         ⚠ <b>A census rather than a refusal, and the difference is that there are 171 of them.</b>
///         "Fail on a literal name" is a gate with 171 failures on its first run, which is the wall
///         nobody keeps green. What is committed is the <i>set</i>, compared exactly in both
///         directions: a new literal cannot arrive without a line, and a row that has been localised
///         must leave. The loud direction is arrival — the editor's words are being moved into
///         <c>EditorStrings</c> a panel at a time, so this file can only shrink.
///     </para>
///     <para>
///         ⚠ <b>The scan is over-inclusive and says so.</b> <c>Name</c> is a generic attribute and
///         only <c>Avatar</c> turns it into a name, so a <c>Name="dark"</c> on something else is a
///         row that is not a defect. That is the safe direction for a census whose loud event is a
///         row arriving: a name that is not really spoken costs a line, and a spoken name the scan
///         declined to see costs a screen-reader user a word no translator can reach.
///     </para>
///     <para>
///         ⚠ <b>It is not over-inclusive about which <i>lines</i> it reads, and that is a different
///         direction with a different cost.</b> A <c>&lt;!-- … --&gt;</c> comment quoting an
///         attribute as an example, and a <c>Label = "Add"</c> in a <c>@code</c> body, are prose and
///         C# about markup rather than markup — the two sources <c>Rikarin/Vixen#1317</c> found in
///         the tag census, in the same batch as this file, which shipped with no filter at all.
///         <see cref="VxmlLines" /> takes both out before the pattern runs and is the same reader
///         that census uses. A row sourced from a comment is permanent, and fails the day somebody
///         rewords the comment, in a file whose whole argument is that it can only shrink.
///         ⚠ <b>Believed latent and measured live.</b> The first census committed 172 rows and one
///         of them — <c>Editor/Vixen.Editor.Ui/Parts/FactRow.vxml Name …</c> — came from
///         <c>&lt;FactRow Name="…" Value="…" /&gt;</c> written in that file's header comment as an
///         <i>example of how to use the part</i>. It is 171 now, and the word it recorded was never
///         said by anything.
///     </para>
///     <para>
///         ⚠ <b>Markup only, and the C# half is <see cref="CodeAccessibleNameTests" />.</b>
///         <c>button.Label = "Add"</c> written in C# is the same defect, and it needs a different
///         instrument: in C# the property name alone is a census of log messages and window titles,
///         so that file resolves each receiver's type and asks a probe whether it speaks. It reads
///         the <c>@code</c> bodies this file skips, and the <c>.cs</c> files with them, so neither
///         census has a hole the other assumes is covered.
///     </para>
/// </remarks>
public partial class MarkupAccessibleNameTests {
    /// <summary>Where the census lives, relative to the repository root.</summary>
    const string CensusFile = "Core/Vixen.Ui.Controls.Advanced.Tests/LiteralAccessibleNames.txt";

    /// <summary>Set <c>VIXEN_REGENERATE=1</c> to write the census back instead of asserting it.</summary>
    static bool Regenerating => Environment.GetEnvironmentVariable("VIXEN_REGENERATE") is "1";

    /// <summary>
    ///     The markup attributes whose value a control answers <c>NativeAccessibleName</c> with.
    /// </summary>
    /// <remarks>
    ///     <c>AccessibleName</c> and <c>AccessibleDescription</c> are the direct spellings and have
    ///     no literal instance today; they are watched so that the first one is a row rather than a
    ///     silence.
    /// </remarks>
    internal static readonly string[] Watched = [
        "AccessibleDescription",
        "AccessibleName",
        "Description",
        "Label",
        "Message",
        "Name",
        "Title"
    ];

    /// <summary>An attribute on a markup element, with its value.</summary>
    /// <remarks>
    ///     ⚠ Preceded by whitespace rather than <c>\b</c>, so <c>LeadingIcon.Label="…"</c> and
    ///     <c>DisplayName="…"</c> are not this attribute on this element.
    /// </remarks>
    [GeneratedRegex("""""
        (?<=\s)(?<attribute>[A-Z][A-Za-z0-9]*)\s*=\s*"(?<value>[^"]*)"
        """"")]
    private static partial Regex Attribute { get; }

    /// <summary>What the markup writes, sorted into the two kinds this file tells apart.</summary>
    /// <param name="Literal">A row per literal spoken name, as <c>path TAB attribute TAB value</c>.</param>
    /// <param name="Bound">How many watched attributes took an <c>@</c> expression instead.</param>
    /// <param name="Files">How many <c>.vxml</c> files the walk read.</param>
    public sealed record Markup(IReadOnlyList<string> Literal, int Bound, int Files);

    /// <summary>The scan, done once.</summary>
    public static Markup Scanned => scanned ??= Scan();

    static Markup? scanned;

    static Markup Scan() {
        var root = Root();
        var rows = new SortedSet<string>(StringComparer.Ordinal);
        var bound = 0;
        var files = 0;

        foreach (var path in Sources()) {
            files++;

            var name = Path.GetRelativePath(root, path).Replace('\\', '/');
            var found = ScanFile(name, File.ReadLines(path));

            rows.UnionWith(found.Literal);
            bound += found.Bound;
        }

        return new Markup([.. rows], bound, files);
    }

    /// <summary>What one file says, with its prose and its C# left out.</summary>
    /// <param name="name">How a row spells the file.</param>
    /// <param name="lines">The file, in order.</param>
    /// <returns>Its literal rows, and how many of its watched attributes were bound.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A markup line is what <see cref="VxmlLines" /> says it is, rather than what this
    ///         file assumes.</b> The first spelling of this ran the pattern over every line, which is
    ///         exactly the defect <c>Rikarin/Vixen#1317</c> fixed in the tag sweep one assembly away
    ///         in this same batch: a header comment writing <c>Label="Add Component"</c> as an
    ///         <i>example</i>, and a <c>Label = "Add"</c> in a <c>@code</c> body, both match the
    ///         pattern and both become permanent committed rows. A census that is exact in both
    ///         directions then turns such a row into a failure the day somebody rewords the comment,
    ///         which is how a file whose whole argument is that it can only shrink acquires a reason
    ///         to be deleted. No committed <c>.vxml</c> writes one today — measured — so this moves
    ///         no row; it is the guard for the day one does, and the reason the reader is shared
    ///         rather than written a second time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The C# is skipped here and scanned in <see cref="CodeAccessibleNameTests" />.</b>
    ///         <c>button.Label = "Add"</c> is the same defect whether it is in a <c>@code</c> body or
    ///         a <c>.cs</c> file. Reading it here would cover the <c>@code</c> bodies and none of the
    ///         <c>.cs</c> files — a census whose domain is an accident of where somebody happened to
    ///         put a line — so the C# census reads both.
    ///     </para>
    /// </remarks>
    internal static Markup ScanFile(string name, IEnumerable<string> lines) {
        var watched = Watched.ToHashSet(StringComparer.Ordinal);
        var rows = new SortedSet<string>(StringComparer.Ordinal);
        var bound = 0;

        foreach (var line in VxmlLines.Read(lines)) {
            if (line.Region == VxmlRegion.Code) {
                continue;
            }

            foreach (Match match in Attribute.Matches(line.Text)) {
                var attribute = match.Groups["attribute"].Value;

                if (!watched.Contains(attribute)) {
                    continue;
                }

                var value = match.Groups["value"].Value;

                // ⚠ An `@` expression is the localised spelling and the whole discriminator:
                // `Label="@EditorStrings.PluginsReload.Text"` reads the catalogue, and so does
                // `Label="@row.Label"` one hop further on. Neither is a word written here.
                if (value.StartsWith('@')) {
                    bound++;
                    continue;
                }

                // A value with nothing in it is not a word anybody says.
                if (value.Trim().Length == 0) {
                    continue;
                }

                Assert.DoesNotContain('\t', value);
                rows.Add($"{name}\t{attribute}\t{value}");
            }
        }

        return new Markup([.. rows], bound, 1);
    }

    /// <summary>
    ///     Every attribute the census watches is one a control really turns into a spoken name.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The instrument check, and the one this census is worthless without.</b> The watched
    ///     list is a claim about seven <c>NativeAccessibleName</c> overrides in
    ///     <c>Vixen.Ui.Controls</c>, and a claim in a list is a claim nothing tests. If
    ///     <c>ButtonBase</c> stopped answering with its <c>Label</c>, this census would go on
    ///     committing 148 rows about an attribute nobody speaks, and every one of them would read as
    ///     evidence. So each attribute is set on the control that maps it and the word is read back
    ///     out of the accessibility tree by <see cref="Announced" /> — which is what a screen reader
    ///     is handed, and is not the same claim as reading the property back.
    /// </remarks>
    [Fact]
    public void Every_watched_attribute_reaches_a_spoken_name() {
        using var fixture = new AdvancedFixture();

        var spoken = new List<string>();

        void Says(string attribute, string word) {
            fixture.Update();

            Assert.Contains(word, Announced(fixture.Document.Root), StringComparer.Ordinal);
            spoken.Add(attribute);
        }

        var button = fixture.Document.Root.Add<Button>();
        button.Label = "the button";
        Says("Label", "the button");

        var alert = fixture.Document.Root.Add<Alert>();
        alert.Title = "the alert";
        Says("Title", "the alert");

        var toast = fixture.Document.Root.Add<Toast>();
        toast.Message = "the toast";
        Says("Message", "the toast");

        var image = fixture.Document.Root.Add<Image>();
        image.Description = "the image";
        Says("Description", "the image");

        var avatar = fixture.Document.Root.Add<Avatar>();
        avatar.Name = "the avatar";
        Says("Name", "the avatar");

        // The two direct spellings, which any element answers with and which markup may write on
        // anything at all.
        // ⚠ The role is set because a `Panel` has none, and an element with no role is not in the
        // accessibility tree at all — so the pair of legs below used to be the only two that could
        // not have reached a screen reader, and were the only two written as a property round-trip.
        // Markup sets `Role` the same way, and `Containers.cs:31` says so in as many words.
        var panel = fixture.Document.Root.Add<Panel>();
        panel.Role = AccessibleRole.Region;

        panel.AccessibleName = "the panel";
        Says("AccessibleName", "the panel");

        panel.AccessibleDescription = "the description";
        Says("AccessibleDescription", "the description");

        Assert.Equal(
            Watched.Order(StringComparer.Ordinal),
            spoken.Order(StringComparer.Ordinal)
        );
    }

    /// <summary>
    ///     Every word a rendered accessibility tree carries, which is what a screen reader is handed.
    /// </summary>
    /// <param name="root">Where to start.</param>
    /// <returns>Each announced name and description under <paramref name="root" />.</returns>
    /// <remarks>
    ///     ⚠ <b>The walk <c>AccessibilitySnapshot.Untranslated</c> makes, rather than a read of the
    ///     property that was just written.</b> Asserting <c>element.AccessibleName == word</c> after
    ///     setting <c>AccessibleName</c> is a settable property returning what it was given: it stays
    ///     green if <c>UiElement</c> stops consulting the value for the tree entirely, which is
    ///     exactly the failure this instrument check exists to catch for the other five attributes.
    ///     Requiring the word to come back out of a tree walk asks the question the census needs
    ///     answered — is this attribute a word somebody hears — for all seven on the same footing.
    /// </remarks>
    internal static List<string> Announced(UiElement root) {
        var words = new List<string>();
        Hear(root, words);

        return words;
    }

    static void Hear(UiElement element, List<string> into) {
        if (element.IsInAccessibilityTree) {
            if (element.AccessibleName is { Length: > 0 } name) {
                into.Add(name);
            }

            if (element.AccessibleDescription is { Length: > 0 } description) {
                into.Add(description);
            }
        }

        foreach (var child in element.Children) {
            Hear(child, into);
        }
    }

    /// <summary>A comment about markup, and the C# below it, are not words anybody says.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Synthetic lines, because no committed <c>.vxml</c> writes one of these — and that
    ///         is why a test reading the tree could not show it.</b> Every one of the census's rows
    ///         comes from real markup today, so the scan with no filter at all and the scan with this
    ///         one produce the identical file. What differs is what happens the day somebody writes a
    ///         header comment showing how to label a button: with no filter that example becomes a
    ///         committed row, and the census — exact in both directions — then fails whenever the
    ///         comment is reworded, in a file whose whole argument is that it can only shrink.
    ///     </para>
    ///     <para>
    ///         The same two sources are what <c>Rikarin/Vixen#1317</c> fixed in the tag sweep, and
    ///         this is the same reader answering for both.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Prose_and_code_are_not_a_spoken_name() {
        var found = ScanFile(
            "Editor/Vixen.Editor.App/Synthetic.vxml",
            [
                "<!--",
                "    The panel's buttons are labelled the translatable way. Do not write",
                "    Label=\"From A Comment\" here — it is a word no translator can reach.",
                "-->",
                "<Panel>",
                "    <Button Label=\"From Markup\" />",
                "    <Button Label=\"@EditorStrings.PluginsReload.Text\" />",
                "</Panel>",
                "@code {",
                "    /// <summary>The <c>Title=\"From A Doc Comment\"</c> spelling.</summary>",
                "    void Build() {",
                "        Root.Add<Button>(new() { Label = \"From Code\" });",
                "    }",
                "}"
            ]
        );

        Assert.Equal(
            ["Editor/Vixen.Editor.App/Synthetic.vxml\tLabel\tFrom Markup"],
            found.Literal
        );

        // The bound value is counted, and only it — a scan that saw the comment's `Label` would have
        // found a second literal, and one that read the `@code` body a third.
        Assert.Equal(1, found.Bound);
    }

    /// <summary>The walk found the repository, and can tell its two kinds of value apart.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the census below passes loudest on the day it stops running.</b> Every
    ///     way the scan can break — a moved root, a regex that stopped matching, a walk that found
    ///     no <c>.vxml</c> — breaks it by producing no rows, and no rows against a census somebody
    ///     truncated is a pass. So the corpus is asserted to be a corpus, and one literal and one
    ///     bound value are named: a scan that classified every value as bound would report no
    ///     literals and agree with an emptied census perfectly.
    /// </remarks>
    [Fact]
    public void The_markup_scan_actually_ran() {
        var markup = Scanned;

        Assert.True(markup.Files >= 60, $"only {markup.Files} `.vxml` files were found, against 95 measured.");

        Assert.True(
            markup.Bound >= 20,
            $"only {markup.Bound} watched attributes took an `@` expression, against 50 measured — so "
            + "the scan is calling every value a literal and the census is a record of its own bug."
        );

        // `ComponentsView.vxml:170` writes `Label="Add Component"`, which is the issue's own example.
        Assert.Contains(
            "Editor/Vixen.Editor.App/ComponentsView.vxml\tLabel\tAdd Component",
            markup.Literal,
            StringComparer.Ordinal
        );

        // And `PluginManagerView.vxml:56` writes `Label="@EditorStrings.PluginsReload.Text"`, which
        // is the same attribute on the same kind of control, done the way that can be translated.
        Assert.DoesNotContain(
            markup.Literal,
            row => row.StartsWith("Editor/Vixen.Editor.App/PluginManagerView.vxml\tLabel\tReload", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     The literal spoken names markup writes are exactly the committed census, in both
    ///     directions.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Exact rather than a floor, which is the guard this repository has twice had eaten
    ///         by success.</b> A ceiling — "no more than 171" — cannot say that a word came back, so
    ///         a row would outlive the literal it records and the next untranslatable name would take
    ///         the seat a localised one vacated.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A row leaving is the cheerful direction and still fails.</b> That is deliberate:
    ///         it means a panel's words went into a declaration class, which is exactly the change
    ///         this file exists to make visible, and the diff is the record of it.
    ///     </para>
    ///     <para>
    ///         The census is read off disk and its absence throws rather than emptying the expected
    ///         set — the answer to "what does this print on the day it does not run" is a failure.
    ///         Re-run with <c>VIXEN_REGENERATE=1</c> to write it back, and read the diff.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_literal_spoken_name_in_markup_is_in_the_committed_census() {
        var path = Path.Combine(Root(), CensusFile);
        var literal = Scanned.Literal;

        if (Regenerating) {
            Write(path, literal);
        }

        var census = Census(path);

        var arrived = literal.Where(row => !census.Contains(row)).ToList();
        var departed = census.Where(row => !literal.Contains(row)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0,
            $"""
             The census of literal accessible names in markup is out of date.

             Written in markup and not in {CensusFile}:
             {Lines(arrived)}

             In {CensusFile} and no longer written — delete the row:
             {Lines(departed)}

             A `Label`, `Title`, `Message`, `Description` or `Name` attribute is what the control
             answers `AccessibleName` with, so a literal here is a word a screen reader says and no
             translator can reach. Nothing else in this repository can see one: the localisation
             gate compares against the declaration table, and a word no class declares is not in it.
             Move it into a `*Strings` class and write `@Whatever.Text`, or add the row.
             """
        );
    }

    static HashSet<string> Census(string path) {
        var lines = File.ReadAllLines(path);

        // "It has rows" cannot stand in for "it was read": an answered census and a truncated one
        // are both zero rows, and only one of them still has its header.
        Assert.True(
            lines.Count(static line => line.StartsWith('#')) >= 5,
            $"{CensusFile} has lost its header, so it was emptied rather than answered."
        );

        var rows = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines) {
            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }

            rows.Add(line.TrimEnd());
        }

        return rows;
    }

    static void Write(string path, IEnumerable<string> rows) {
        var text = new StringBuilder();

        foreach (var line in File.ReadLines(path)) {
            if (!line.StartsWith('#') && line.Trim().Length != 0) {
                break;
            }

            // '\n', not AppendLine: the census is committed under `eol=lf`, and Environment.NewLine
            // made every regeneration on Windows a whole-file CRLF diff.
            text.Append(line).Append('\n');
        }

        foreach (var row in rows) {
            text.Append(row).Append('\n');
        }

        File.WriteAllText(path, text.ToString());
    }

    static string Lines(IEnumerable<string> rows) {
        var joined = new StringBuilder();

        foreach (var row in rows) {
            joined.Append("  ").AppendLine(row.Replace('\t', ' '));
        }

        return joined.Length == 0 ? "  (none)" : joined.ToString().TrimEnd('\n');
    }

    /// <summary>Every <c>.vxml</c> in the working tree, in a stable order.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude</c> is pruned, and that is the difference between a test about this
    ///     repository and a test about whatever else is on the disk: an agent worktree under
    ///     <c>.claude/worktrees/</c> is a full checkout of arbitrary other work, and a census
    ///     comparing against it asserts on somebody else's uncommitted markup.
    /// </remarks>
    static List<string> Sources() {
        var found = new List<string>();
        Walk(Root(), found);
        found.Sort(StringComparer.Ordinal);

        return found;
    }

    static readonly string[] Unwalked = [".git", ".claude", "bin", "obj", "artifacts", "node_modules"];

    static void Walk(string directory, List<string> into) {
        into.AddRange(Directory.EnumerateFiles(directory, "*.vxml"));

        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (!Unwalked.Contains(Path.GetFileName(child), StringComparer.Ordinal)) {
                Walk(child, into);
            }
        }
    }

    static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
