// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using Vixen.DocGen.Guide;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     No example in <em>this repository's own</em> guide looks a culture up by name, because under
///     the invariant globalization this tree and the web head build with the lookup throws
///     (<a href="https://github.com/Rikarin/Vixen/issues/1426">#1426</a>).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A runtime failure that no compile gate can see.</b> <c>text-input.md</c> showed
///         <c>field.Culture = CultureInfo.GetCultureInfo("de-DE")</c> in a <c>no-compile</c> fence;
///         compiled, it would still have built. It throws <c>CultureNotFoundException</c> in every
///         sample and test here (<c>Directory.Build.props</c> sets <c>InvariantGlobalization</c>) and in
///         any application on the web head, whose props turn it on by default — while
///         <c>date-picker.md</c>, <c>CalendarTests</c> and <c>TextFieldTests.Continental</c> each already
///         built their locale by cloning the invariant culture. The guide was the one place that did
///         not know.
///     </para>
///     <para>
///         Every fence is read, compiled or not and in any language: a markup page's <c>&lt;code&gt;</c>
///         block is C# too. The empty name is the invariant culture and is allowed.
///     </para>
/// </remarks>
public class RealGuideCultureTests {
    /// <summary>A named culture looked up or constructed: the calls that throw under invariant globalization.</summary>
    static readonly Regex NamedCulture = new(
        @"(?:GetCultureInfo(?:ByIetfLanguageTag)?|CreateSpecificCulture|new\s+CultureInfo|new\s+RegionInfo)\s*\(\s*""(?<name>[^""]+)""",
        RegexOptions.Compiled
    );

    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null) {
                if (Directory.Exists(Path.Combine(directory.FullName, "docs", "guide"))) {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("No docs/guide above " + AppContext.BaseDirectory + ".");
        }
    }

    /// <summary>
    ///     The premise, asserted about this test host: a named culture is not there to look up. If
    ///     invariant globalization is ever turned off for the tree this goes red, and the rule below
    ///     should be re-argued rather than kept — though the web head would still need it.
    /// </summary>
    [Fact]
    public void A_named_culture_throws_in_this_tree() {
        Assert.Throws<CultureNotFoundException>(() => CultureInfo.GetCultureInfo("de-DE"));
    }

    /// <summary>⚠ No guide fence looks a culture up by name.</summary>
    [Fact]
    public void No_guide_example_looks_up_a_named_culture() {
        var (pages, _) = GuideReader.Read(Root, new SourceLinks(Root, "https://github.com/Rikarin/Vixen", commit: null));
        var fences = pages.SelectMany(page => page.Examples).ToList();

        // The instrument first: a walk that found nothing reads exactly like a guide with nothing wrong.
        Assert.True(fences.Count >= 200, $"the guide walk found only {fences.Count} fences, so this asserts about almost nothing");

        var found = fences
            .SelectMany(fence => NamedCulture.Matches(fence.Code).Select(match => $"{fence.Page}:{fence.Line}: {match.Value}"))
            .ToList();

        Assert.True(
            found.Count == 0,
            "guide examples look a culture up by name, which throws CultureNotFoundException under the "
            + "InvariantGlobalization this repository and the web head build with (#1426). Clone "
            + "CultureInfo.InvariantCulture and write the separators out, as TextFieldTests.Continental does:\n  "
            + string.Join("\n  ", found)
        );
    }

    /// <summary>The detector against the shape it exists for: the example as the guide used to carry it.</summary>
    [Fact]
    public void The_rule_sees_the_example_the_guide_carried() {
        Assert.Matches(NamedCulture, """field.Culture = CultureInfo.GetCultureInfo("de-DE");""");
        Assert.Matches(NamedCulture, """var czech = new CultureInfo("cs-CZ");""");
        Assert.DoesNotMatch(NamedCulture, """var culture = (CultureInfo) CultureInfo.InvariantCulture.Clone();""");
        Assert.DoesNotMatch(NamedCulture, """var invariant = CultureInfo.GetCultureInfo("");""");
    }
}
