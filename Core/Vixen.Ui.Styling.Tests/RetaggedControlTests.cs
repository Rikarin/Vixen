// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     Whether a control put under a tag of its own still gets what its own user-agent rule declares.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A control's own rule is keyed on its tag, so renaming the tag silently deletes the
///         rule.</b> <c>tag="…"</c> on a capitalised markup tag — and <c>Add&lt;T&gt;("some-tag")</c>
///         in C# — is the sanctioned way to put a control under a name a stylesheet already knows,
///         and it is a good mechanism. What neither it nor its documentation says is that the
///         cascade has no other handle on the control: <c>scroll-view { … }</c> matches a
///         <c>ScrollView</c> and nothing else does, so a <c>ScrollView</c> under
///         <c>add-component-list</c> gets whatever that tag's rules say and <b>none</b> of its own.
///         See `Rikarin/Vixen#1327`.
///     </para>
///     <para>
///         ⚠ <b>For <c>ScrollView</c> the lost declarations are the clip and the bars' anchor, and
///         neither failure shows up where the mistake is.</b> Without <c>overflow: hidden</c> the
///         scrolled-off rows draw over whatever is above the view; without
///         <c>position: relative</c> the absolutely-positioned bars anchor to the nearest positioned
///         ancestor instead of to the view, so the scrollbar is somewhere else on screen. It had
///         already shipped twice — <c>choice-scroller</c> (fixed under #1275) and
///         <c>add-component-list</c>, whose bars ran the full height of the Add Component popup
///         because the popup is the <c>position: absolute</c> the bars found.
///     </para>
///     <para>
///         <b>This is the third of the three shapes #1327 ranked, and it is the cheap one.</b> It
///         only ever sees what is committed, which is exactly right for a mistake that is made by
///         copying a tag's previous declarations into a new rule: the copy happens in the tree, and
///         the tree is what this reads. Written like <c>docs/WhitespaceExempt.txt</c> and
///         <c>OverflowLedgerTests</c> — the known losses are named with the property each one costs,
///         a new one fails, and a loss that has been closed and is still listed fails too, so the
///         ledger can only shrink.
///     </para>
///     <para>
///         ⚠ <b>Production sites only.</b> A test that stands a <c>Button</c> up under the tag
///         <c>go</c> to photograph it is not making this mistake, it is naming a fixture — and
///         <c>ControlVisualTests</c> alone does it thirty times. So the sweep skips any path with a
///         <c>*.Tests</c> directory in it, the same way the rest of this repository means
///         "production".
///     </para>
/// </remarks>
public sealed partial class RetaggedControlTests {
    /// <summary>The losses that are known and accepted, as <c>Type under tag: properties</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         Both entries are <c>TextBlock</c>, whose own rule is <c>text { display: inline }</c>
    ///         plus a colour that each of these two restates. They are the viewport's two
    ///         absolutely-positioned readouts, and <b>the <c>display</c> is recorded rather than
    ///         restated because restating it changes what the editor draws</b> — an inline box and a
    ///         flex box do not lay a glyph run out identically, and rule 7 of this repository's
    ///         working agreement says a visual change wants a picture rather than an argument. So
    ///         this is a ledger entry and not a fix, and it says so.
    ///     </para>
    ///     <para>
    ///         ⚠ Keyed by type and tag rather than by <c>file:line</c>, deliberately: a line number
    ///         in a committed ledger is a false failure every time somebody adds a field above the
    ///         call. The site is reported in the failure message, where it is useful and cannot go
    ///         stale.
    ///     </para>
    /// </remarks>
    static readonly string[] Known = [
        "TextBlock under viewport-readout: display",
        "TextBlock under viewport-stats: display"
    ];

    /// <summary>
    ///     Every production site that renames a control is named here with what the rename costs, and
    ///     nowhere else.
    /// </summary>
    [Fact]
    public void Every_retagged_control_keeps_what_its_own_user_agent_rule_declares() {
        var engine = RepositoryScan.Sheets();
        var declared = DeclaredTags();
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (type, tag, site) in Sites()) {
            if (!declared.TryGetValue(type, out var own) || own.Equals(tag, StringComparison.Ordinal)) {
                continue;
            }

            if (RepositoryScan.Missing(engine, tag, own) is { Count: > 0 } lost) {
                found[$"{type} under {tag}: {string.Join(", ", lost)}"] = site;
            }
        }

        var unexpected = found.Keys.Except(Known, StringComparer.Ordinal).ToList();
        var closed = Known.Except(found.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unexpected.Count == 0,
            "These controls are under a tag of their own and so match none of their own user-agent "
            + "rule — every declaration named here is one the control had before it was renamed and "
            + "does not have now. Restate them on the new tag's rule (see Rikarin/Vixen#1327) rather "
            + "than adding to the ledger:\n  "
            + string.Join("\n  ", unexpected.Select(entry => $"{entry}   [{found[entry]}]"))
        );

        Assert.True(
            closed.Count == 0,
            "These ledger entries no longer name a loss — either the rule now restates the "
            + "declaration or the site is gone. Take them out, so the list only shrinks:\n  "
            + string.Join("\n  ", closed)
        );
    }

    /// <summary>The instrument, over a sheet whose answer is known.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The comparison is over <i>resolved</i> properties rather than over declaration
    ///         text</b>, which is what makes a rule that beats another, or one sealed in a
    ///         <c>@media</c>, count for what it actually computes — see
    ///         <see cref="RepositoryScan.Missing" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the resolved set keeps a shorthand as a property of its own</b>, which was
    ///         the opposite of what this instrument was first written to assert. <c>overflow</c>
    ///         survives the cascade under its own name, so a rule writing <c>overflow-x</c> and
    ///         <c>overflow-y</c> in place of it is reported here as losing <c>overflow</c> — even
    ///         though <c>LayoutStyleBuilder</c> reads the shorthand and then lets the longhands
    ///         override it, so the two are the same box. That is a false accusation this census can
    ///         make, it is cheap to answer (write the shorthand), and nothing in the tree writes the
    ///         pair today. It is asserted rather than described so that the day the cascade starts
    ///         expanding shorthands, this line says so.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_comparison_is_over_resolved_properties_and_a_shorthand_is_one_of_them() {
        var engine = new StyleEngine();

        engine.Load(
            """
            own-tag { flex-direction: column; overflow: hidden; position: relative; }
            restated { flex-direction: column; overflow: hidden; position: relative; }
            per-axis { flex-direction: column; overflow-x: hidden; overflow-y: hidden; position: relative; }
            partial-rule { flex-direction: column; }
            unruled-neighbour { color: #fff; }
            """,
            StyleOrigin.Author
        );

        Assert.Empty(RepositoryScan.Missing(engine, "restated", "own-tag"));
        Assert.Equal(["overflow"], RepositoryScan.Missing(engine, "per-axis", "own-tag"));
        Assert.Equal(["overflow", "position"], RepositoryScan.Missing(engine, "partial-rule", "own-tag"));

        // A tag no rule names loses everything the control's own rule declares, which is the whole
        // shape of the defect: `add-component-list` carried two declarations of its own and none of
        // `scroll-view`'s three.
        Assert.Equal(
            RepositoryScan.Missing(engine, "unruled-neighbour", "own-tag").Count,
            RepositoryScan.Missing(engine, "nothing-names-this", "own-tag").Count
        );
    }

    /// <summary>
    ///     The scan found the repository, found the sites in it, and can tell a control's own tag
    ///     from the one it was given.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Without this the census above passes loudest on the day it stops running.</b> Every
    ///     way the sweep can break — a moved root, a regex that stopped matching, a walk that found
    ///     no sources — breaks it by finding no sites, and no sites is a green assertion and an empty
    ///     ledger difference. So the corpus is asserted to be a corpus, and the two names the header
    ///     talks about are asserted to be the two things it says they are.
    /// </remarks>
    [Fact]
    public void The_scan_actually_ran() {
        var declared = DeclaredTags();
        var sites = Sites();

        Assert.True(declared.Count >= 100, $"only {declared.Count} types were found to declare a tag.");
        Assert.True(sites.Count >= 20, $"only {sites.Count} production retag sites were found.");

        // The map reads a C# override and a markup directive, which are the two ways a tag is named.
        Assert.Equal("scroll-view", declared["ScrollView"]);
        Assert.Equal("water-zone-facts", declared["WaterZoneFacts"]);

        // Both spellings of a retag reach the site list: the markup attribute and the C# call.
        Assert.Contains(sites, site => site is { Type: "ScrollView", Tag: "add-component-list" });
        Assert.Contains(sites, site => site is { Type: "ScrollView", Tag: "choice-scroller" });

        // And a test fixture standing a control up under a name of its own is not a site.
        Assert.DoesNotContain(sites, site => site is { Type: "Button", Tag: "go" });
    }

    /// <summary>An element written inside a markup comment is prose, and the line count survives it.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that matters is the second assertion.</b> Dropping a comment outright makes
    ///     the first one pass and quietly moves every site after it up by however many lines the
    ///     comment had, so the census would go on reporting real defects against the wrong
    ///     <c>file:line</c> — a failure that reads as a stale ledger rather than as a broken
    ///     instrument. The sheets this census guards now explain each conversion in a multi-line
    ///     comment directly above the converted element, so that offset would be one comment wide
    ///     at exactly the sites people are editing.
    ///     <para>
    ///         ⚠ <b>The commented element has to be written out in full, angle brackets and all</b>,
    ///         which the first draft of this fixture was not: <c>MarkupElement</c> anchors on
    ///         <c>&lt;</c> followed by a capital, so prose merely <i>naming</i>
    ///         <c>ScrollView tag="…"</c> was never a match and the no-op sabotage passed. A green
    ///         sabotage proves nothing, and what it was hiding here was that the fixture did not
    ///         contain the defect.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_commented_out_element_is_not_a_site_and_does_not_move_the_ones_below_it() {
        var markup = Uncommented(
            """
            <Panel>
                <!-- What this pane used to be, kept because the reason is worth reading:
                     <ScrollView tag="explained-in-prose" /> lost its own rule, so the
                     three declarations are restated on the tag below. -->
                <ScrollView tag="really-here" />
            </Panel>
            """
        );

        var tagged = MarkupElement.Matches(markup)
            .Where(match => MarkupTagAttribute.IsMatch(match.Groups["attributes"].Value))
            .ToList();

        Assert.DoesNotContain(
            tagged,
            match => MarkupTagAttribute.Match(match.Groups["attributes"].Value).Groups["tag"].Value
                == "explained-in-prose"
        );

        var real = Assert.Single(tagged);

        Assert.Equal("really-here", MarkupTagAttribute.Match(real.Groups["attributes"].Value).Groups["tag"].Value);

        // And it is still on line 5, counted exactly as `Sites` counts it.
        Assert.Equal(5, markup.Take(real.Index).Count(character => character == '\n') + 1);
    }

    /// <summary>Every production site that creates a control under a tag, as type, tag and where.</summary>
    static List<(string Type, string Tag, string Site)> Sites() {
        var root = RepositoryScan.Root();
        var found = new List<(string, string, string)>();

        void Note(string type, string tag, string path, int line) =>
            found.Add((type, tag, $"{Path.GetRelativePath(root, path).Replace('\\', '/')}:{line}"));

        foreach (var path in RepositoryScan.Files("*.cs").Concat(RepositoryScan.Files("*.vxml"))) {
            if (!Production(root, path) || Path.GetFileName(path) is "RetaggedControlTests.cs") {
                continue;
            }

            var line = 0;

            foreach (var text in File.ReadLines(path)) {
                line++;
                var trimmed = text.TrimStart();

                // Prose about a call is not a call. A doc comment naming
                // `panel.Add<WaterZoneFacts>("water-facts")` is real and is in `BuildContext`.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith('*')
                    || trimmed.StartsWith("<!--", StringComparison.Ordinal)) {
                    continue;
                }

                foreach (Match match in TagFirstCall.Matches(text)) {
                    Note(match.Groups["type"].Value, match.Groups["tag"].Value, path, line);
                }

                foreach (Match match in ChildCall.Matches(text)) {
                    Note(match.Groups["type"].Value, match.Groups["tag"].Value, path, line);
                }
            }

            if (!path.EndsWith(".vxml", StringComparison.Ordinal)) {
                continue;
            }

            var markup = Uncommented(File.ReadAllText(path));

            foreach (Match match in MarkupElement.Matches(markup)) {
                if (MarkupTagAttribute.Match(match.Groups["attributes"].Value) is { Success: true } attribute) {
                    Note(
                        match.Groups["type"].Value,
                        attribute.Groups["tag"].Value,
                        path,
                        markup.Take(match.Index).Count(character => character == '\n') + 1
                    );
                }
            }
        }

        return found;
    }

    /// <summary>The same markup with every <c>&lt;!-- … --&gt;</c> blanked and every newline kept.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The line loop above skips a comment and the markup sweep did not</b>, so a
    ///         <c>&lt;ScrollView tag="…"&gt;</c> written inside a comment was reported as a
    ///         production retag, with a line number and a property list, and the only answer would
    ///         have been to add a ledger entry for a control that does not exist. Nothing in the
    ///         tree did it — but the sheets this census was written for now carry a
    ///         <c>&lt;!-- … --&gt;</c> block explaining the conversion directly above nearly every
    ///         converted element, so the next person to write out the shape they are explaining
    ///         lands on it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Blanked rather than removed</b>: the line a match reports is counted by
    ///         newlines in the text before it, so deleting a multi-line comment would shift every
    ///         site after it in the file and the census would name the wrong element. Only the
    ///         non-newline characters go.
    ///     </para>
    /// </remarks>
    static string Uncommented(string markup) =>
        MarkupComment.Replace(
            markup,
            match => new string(match.Value.Select(character => character == '\n' ? '\n' : ' ').ToArray())
        );

    /// <summary>What tag each type carries when nobody renames it.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off disk rather than out of an assembly</b>, for this project's usual reason: it
    ///     references <c>Vixen.Ui.Styling</c> and nothing above it, so <c>ScrollView</c> is not a
    ///     type it can name. Both spellings are read — the <c>TagName</c> override a control writes
    ///     in C#, and the <c>@tag</c> directive a component writes in markup, which the emitter turns
    ///     into exactly that override.
    ///     <para>
    ///         ⚠ Keyed by simple name, so two types that share one resolve to whichever file sorts
    ///         first. That is a false negative and not a false accusation: the census then compares a
    ///         retagged control against the wrong control's rule and, finding nothing lost, says
    ///         nothing.
    ///     </para>
    /// </remarks>
    static Dictionary<string, string> DeclaredTags() {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in RepositoryScan.Files("*.cs")) {
            string? type = null;

            foreach (var text in File.ReadLines(path)) {
                if (TypeDeclaration.Match(text) is { Success: true } declaration) {
                    type = declaration.Groups["name"].Value;
                }

                if (type is not null && TagOverride.Match(text) is { Success: true } tag) {
                    map.TryAdd(type, tag.Groups["tag"].Value);
                }
            }
        }

        foreach (var path in RepositoryScan.Files("*.vxml")) {
            foreach (var text in File.ReadLines(path)) {
                if (TagDirective.Match(text) is { Success: true } directive) {
                    map.TryAdd(Path.GetFileNameWithoutExtension(path), directive.Groups["tag"].Value);
                    break;
                }
            }
        }

        return map;
    }

    /// <summary>Whether a path is production rather than a test project's.</summary>
    static bool Production(string root, string path) =>
        !Path.GetRelativePath(root, path)
            .Split('/', '\\')
            .Any(segment => segment.EndsWith(".Tests", StringComparison.Ordinal));

    /// <summary>A creation call whose <b>first</b> argument is the tag the control is to carry.</summary>
    /// <remarks>
    ///     ⚠ <b>The first argument and only the first, which cost this census its first six
    ///     findings.</b> <c>Add&lt;T&gt;(tag, id, params classNames)</c> and
    ///     <c>Part&lt;T&gt;(tag, params classNames)</c> take a tag first and <i>classes</i> after it,
    ///     so <c>Part&lt;ColorStrip&gt;(null, "hue")</c> is a colour strip keeping its own tag and
    ///     gaining a class — not a strip renamed to <c>hue</c>. A pattern that took any string
    ///     literal in the call reported that, and <c>Part&lt;Icon&gt;(null, "submenu")</c>, and
    ///     <c>Part&lt;TextBlock&gt;(null, "menu-detail")</c>, as controls that had lost their rule;
    ///     each of the three is styled by <c>icon.submenu</c> or its like, a class selector, and
    ///     nothing was wrong with any of them.
    /// </remarks>
    [GeneratedRegex("""
        \b(?:Add|Create|Part|Prepend)\s*<\s*(?<type>[A-Za-z_]\w*)\s*>\s*\(\s*"(?<tag>[a-z][a-z0-9-]*)"
        """)]
    private static partial Regex TagFirstCall { get; }

    /// <summary>The one creation call whose tag is second, because its first argument is the parent.</summary>
    /// <remarks>
    ///     <c>BuildContext.Child&lt;T&gt;(parent, tag)</c> is what markup's <c>tag=</c> is emitted
    ///     as, so this arm is the hand-written spelling of the attribute the markup sweep reads.
    /// </remarks>
    [GeneratedRegex("""
        \bChild\s*<\s*(?<type>[A-Za-z_]\w*)\s*>\s*\(\s*[^,()"]+,\s*"(?<tag>[a-z][a-z0-9-]*)"
        """)]
    private static partial Regex ChildCall { get; }

    /// <summary>A capitalised markup element and everything up to its closing angle bracket.</summary>
    /// <remarks>
    ///     ⚠ The attribute arm alternates a quoted run with a bare character, so a <c>&gt;</c> inside
    ///     an attribute value does not end the element — <c>Selected="@(row.Index &gt; 2)"</c> is
    ///     real markup in this tree, and a simpler <c>[^&gt;]*</c> stops in the middle of it.
    /// </remarks>
    [GeneratedRegex("""<(?<type>[A-Z]\w*)(?<attributes>(?:[^<>"]|"[^"]*")*)>""")]
    private static partial Regex MarkupElement { get; }

    /// <summary>A markup comment, including the newlines inside it.</summary>
    /// <remarks>
    ///     Lazy and <c>Singleline</c>, so it ends at the first <c>--&gt;</c> and spans lines — the
    ///     blocks this census has to survive are several lines of prose about the very construct it
    ///     is looking for.
    /// </remarks>
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex MarkupComment { get; }

    /// <summary>The <c>tag="…"</c> attribute, which renames what a capitalised tag creates.</summary>
    /// <remarks>⚠ Preceded by whitespace rather than <c>\b</c>, so <c>data-tag="…"</c> is not one.</remarks>
    [GeneratedRegex(""""
        (?<=\s)tag\s*=\s*"(?<tag>[a-z][a-z0-9-]*)"
        """")]
    private static partial Regex MarkupTagAttribute { get; }

    /// <summary>A class or record declaration, from which the next <c>TagName</c> override hangs.</summary>
    [GeneratedRegex(
        @"^\s*(?:(?:public|internal|private|protected|file|sealed|abstract|static|partial|new|readonly|ref)\s+)*(?:class|record|struct)\s+(?<name>[A-Za-z_]\w*)"
    )]
    private static partial Regex TypeDeclaration { get; }

    /// <summary>The one line a control writes to name its own tag.</summary>
    [GeneratedRegex(""""
        ^\s*protected\s+(?:internal\s+)?override\s+string\s+TagName\s*=>\s*"(?<tag>[a-z][a-z0-9-]*)"
        """")]
    private static partial Regex TagOverride { get; }

    /// <summary>The <c>@tag</c> directive, which is a component's spelling of the same override.</summary>
    [GeneratedRegex(@"^@tag\s+(?<tag>[a-z][a-z0-9-]*)\s*$")]
    private static partial Regex TagDirective { get; }
}
