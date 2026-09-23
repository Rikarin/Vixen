// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     The same question the two reference windows ask, over a domain that is derived from the
///     assemblies rather than typed into a test.
/// </summary>
/// <remarks>
///     <para>
///         <b>The class half of <c>Rikarin/Vixen#1321</c>.</b>
///         <c>AccessibleNameLocalisationTests</c> asks the right question —
///         <see cref="AccessibilitySnapshot.Untranslated" />, is any announced word still the source
///         text of a string that has a translation loaded — and asks it of a window somebody typed.
///         Its domain is therefore a hand-written list nobody is told to update, and both windows had
///         drifted within two weeks of being written: <c>GradientEditor</c>, <c>NodeCanvas</c> and
///         <c>Stepper</c> all took catalogue-fed accessible names afterwards and were in neither.
///         Widening a window closes the instances. This closes the class.
///     </para>
///     <para>
///         ⚠ <b>The window is the assembly.</b> Every public, concrete <see cref="UiElement" /> in
///         the two control assemblies with a parameterless constructor is built, under a
///         pseudo-locale, and the whole tree it grows is put through <c>Untranslated</c>. A control
///         populated tomorrow is inside this the day it compiles, with nothing edited here — which
///         is the property the two windows cannot have and the reason the issue asks for a derived
///         set rather than a longer list.
///     </para>
///     <para>
///         ⚠ <b>It does not replace the two windows and must not be read as replacing them.</b> A
///         bare control is not a populated one: <c>Untranslated</c> over a control with no items
///         cannot see a word that only an item's part says, and <c>Unnamed</c> — the "every widget
///         has a name at all" half — is not asked here, because a bare <c>SearchBox</c> with no
///         caption legitimately has nothing to be named by. The windows carry the seeded cases and
///         the naming claim; this carries the domain.
///     </para>
///     <para>
///         ⚠ <b>The derived set is checked against the sweep, and the gap is a committed file.</b>
///         A type that overrides <c>NativeAccessibleName</c> — the one declaration that means "this
///         control has words of its own" — and that the sweep cannot construct is not covered by
///         anything, and saying so is the whole value of deriving the domain in the first place.
///         <see cref="Every_type_that_names_itself_is_swept_or_excused" /> holds that list to
///         <see cref="CensusFile" />, exactly and in both directions, so a type leaving the file is
///         as loud as one arriving.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class AccessibleNameDomainTests {
    /// <summary>The types that name themselves and that the sweep cannot reach.</summary>
    const string CensusFile = "Core/Vixen.Ui.Controls.Advanced.Tests/UnsweptAccessibleNames.txt";

    /// <summary>How many element types the sweep is expected to build, at least.</summary>
    /// <remarks>
    ///     Under the 111 <c>LiveCombinatorPairTests</c> measures, so that adding a control is not a
    ///     failing test — and its job is the day the filter stops matching, because a sweep that
    ///     builds nothing announces nothing and agrees with "no offenders" perfectly.
    /// </remarks>
    const int Elements = 100;

    /// <summary>Every string the control set declares, in a language that is not the source one.</summary>
    /// <remarks>
    ///     The same pseudo-locale the two windows use, and for the same reason: marking each source
    ///     text is enough to make a word that did not go through the catalogue visible, and a
    ///     hand-written table would have to be edited every time a string is declared.
    /// </remarks>
    static StringCatalog Pseudo() {
        var catalog = new StringCatalog("qps");

        foreach (var id in ControlStrings.All) {
            catalog.Set(id.Id, Translated(id));
        }

        return catalog;
    }

    /// <summary>How <see cref="Pseudo" /> spells a declaration, readable with no catalogue installed.</summary>
    static string Translated(StringId id) => "«" + id.Source + "»";

    /// <summary>Every public, concrete element type in the two control assemblies the sweep builds.</summary>
    static IEnumerable<Type> Buildable() =>
        Elementary().Where(static type => type.GetConstructor(Type.EmptyTypes) is not null);

    /// <summary>Every public, concrete element type in the two control assemblies.</summary>
    static IEnumerable<Type> Elementary() =>
        new[] { typeof(Button).Assembly, typeof(DataGrid).Assembly }
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => type.IsPublic && !type.IsAbstract && typeof(UiElement).IsAssignableFrom(type))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal);

    /// <summary>
    ///     The derived domain: every element type that declares words of its own.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A declared override rather than a source scan, because the declaration is the thing
    ///     that means it.</b> <c>UiElement.NativeAccessibleName</c> is <c>Text</c>, so a control that
    ///     announces what it already displays is localised by the same catalogue read that put the
    ///     word on screen and is not interesting. A type that <i>overrides</i> it has taken the
    ///     answer into its own hands — <c>ButtonBase</c> to its <c>Label</c>, <c>ColorPicker</c>'s
    ///     field to a <c>ControlStrings</c> id, <c>TextField</c> to <c>null</c> on purpose — and that
    ///     is exactly the population where a literal can hide.
    /// </remarks>
    static IEnumerable<Type> Naming() =>
        Elementary().Where(
            static type => type.GetProperty(
                "NativeAccessibleName",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly
            ) is not null
        );

    /// <summary>What the sweep found: the offenders, and how much it heard while finding none.</summary>
    /// <param name="Offenders">Each stale announcement, prefixed with the type that grew it.</param>
    /// <param name="Spoken">Every distinct word the swept trees announced.</param>
    /// <param name="Built">How many types were constructed.</param>
    public sealed record Result(IReadOnlyList<string> Offenders, IReadOnlySet<string> Spoken, int Built);

    /// <summary>The sweep, done once.</summary>
    public static Result Swept => swept ??= Sweep();

    static Result? swept;

    static Result Sweep() {
        var make = typeof(AccessibleNameDomainTests)
            .GetMethod(nameof(Make), BindingFlags.NonPublic | BindingFlags.Static)!;

        var offenders = new List<string>();
        var spoken = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;

        try {
            // ⚠ Before anything is built, and this is the whole reason the sweep owns the catalogue
            // rather than a fixture: a control reads its strings in `OnCreated`, so an element
            // constructed before `Strings.Use` carries the English one and would be reported as an
            // offender by the very rule it obeys.
            Strings.Use(Pseudo());

            foreach (var type in Buildable()) {
                using var ui = new AdvancedFixture();

                var element = (UiElement)make.MakeGenericMethod(type).Invoke(null, [ui.Document.Root])!;

                ui.Update();
                count++;

                foreach (var offender in AccessibilitySnapshot.Untranslated(element, ControlStrings.All)) {
                    offenders.Add($"{type.Name}: {offender}");
                }

                Collect(element, spoken);
            }
        } finally {
            Strings.Use(null);
        }

        return new Result(offenders, spoken, count);
    }

    static UiElement Make<T>(UiElement parent) where T : UiElement, new() => parent.Add<T>();

    static void Collect(UiElement element, HashSet<string> into) {
        if (element.IsInAccessibilityTree) {
            if (element.AccessibleName is { Length: > 0 } name) {
                into.Add(name);
            }

            if (element.AccessibleDescription is { Length: > 0 } description) {
                into.Add(description);
            }
        }

        foreach (var child in element.Children) {
            Collect(child, into);
        }
    }

    /// <summary>The premise every assertion below rests on: the sweep built controls and heard them.</summary>
    /// <remarks>
    ///     ⚠ <b>Three claims rather than one floor.</b> The types were built; the trees they grew
    ///     said enough distinct words to be a real walk; and three words a person has traced to the
    ///     control that says them are present <i>in the pseudo-locale's spelling</i>. The last is
    ///     what a count cannot give: a sweep that forgot to install the catalogue would announce the
    ///     same number of English words and every assertion below would pass, because
    ///     <c>Untranslated</c> reports nothing when no declaration has a translation.
    /// </remarks>
    [Fact]
    public void The_naming_sweep_actually_ran() {
        var result = Swept;

        Assert.True(
            result.Built >= Elements,
            $"the sweep built only {result.Built} element types, which is not these two assemblies"
        );

        Assert.True(
            result.Spoken.Count >= 20,
            $"the sweep heard only {result.Spoken.Count} distinct announced words across {result.Built} "
            + "controls — so it built the elements and did not read their names"
        );

        // `ScrollView.cs:51` names its two bars, and `ColorPicker.cs:271` names its field. Each is a
        // `ControlStrings` id read at construction, so the pseudo-locale's guillemets are proof the
        // catalogue was installed before the control was made rather than after.
        // ⚠ Spelled out rather than read off `StringId.Text`: the sweep puts the catalogue back the
        // way it found it, so by the time this runs `.Text` is the source text again and comparing
        // against it would assert the opposite of the intent — which it did, and said so.
        Assert.Contains(Translated(ControlStrings.ScrollBarVertical), result.Spoken, StringComparer.Ordinal);
        Assert.Contains(Translated(ControlStrings.ScrollBarHorizontal), result.Spoken, StringComparer.Ordinal);
        Assert.Contains(Translated(ControlStrings.ColorPickerField), result.Spoken, StringComparer.Ordinal);

        Assert.DoesNotContain(ControlStrings.ScrollBarVertical.Source, result.Spoken, StringComparer.Ordinal);
    }

    /// <summary>
    ///     No control in either assembly announces a word that is still the source text of a string
    ///     with somewhere else to go.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the two windows' third assertion, over a domain nobody has to remember to
    ///     widen.</b> Measured on the day it was written: swapping <c>GradientRail</c>'s name to the
    ///     literal <c>"Colour stops"</c> — the exact drift #1321 reports, and the one the old windows
    ///     were green on — fails here naming the type.
    /// </remarks>
    [Fact]
    public void No_control_in_either_assembly_announces_the_source_language() {
        var result = Swept;

        Assert.True(
            result.Offenders.Count == 0,
            $"""
             {result.Offenders.Count} control(s) announce a word that has a translation loaded and
             did not use it — the element displays the translation and says the English:

             {string.Join("\n", result.Offenders.Select(static offender => $"  {offender}"))}

             An accessible name answered by a literal of the control's own compiles, passes every
             accessibility test (the name exists) and every localisation test (the label translates),
             and is wrong only in another language. Read the word out of `ControlStrings` the way the
             label does, or relate the element to the caption that already carries it.
             """
        );
    }

    /// <summary>
    ///     Every type that overrides <c>NativeAccessibleName</c> is one the sweep builds, or is a row
    ///     in the committed census with a reason.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half that makes the domain derived rather than merely large.</b> The sweep
    ///         above can only judge a type it can construct, so a control that names itself and has
    ///         no parameterless constructor is outside it — silently, the way the two windows were
    ///         silent about <c>GradientEditor</c>. This is where that is written down.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exact in both directions, not a floor.</b> A type that becomes constructible must
    ///         leave the file, because a row excusing a type the sweep now covers would outlive its
    ///         reason and the next unreachable control would take the seat it vacated. Its absence
    ///         throws rather than yielding an empty census — the answer to "what does this print on
    ///         the day it does not run" has to be a failure.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_type_that_names_itself_is_swept_or_excused() {
        var naming = Naming().ToList();

        Assert.True(
            naming.Count >= 15,
            $"only {naming.Count} types were found to override `NativeAccessibleName`, against 21 "
            + "measured — the reflection filter has stopped matching, so the domain is empty and "
            + "this census compares nothing against nothing."
        );

        var unswept = naming
            .Where(static type => type.GetConstructor(Type.EmptyTypes) is null)
            .Select(static type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        var census = Census();

        var arrived = unswept.Where(name => !census.ContainsKey(name)).ToList();
        var departed = census.Keys.Where(name => !unswept.Contains(name)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0,
            $"""
             The census of self-naming controls the sweep cannot reach is out of date.

             Overrides `NativeAccessibleName`, cannot be constructed, and not in {CensusFile}:
             {Lines(arrived)}

             In {CensusFile}, and constructible after all — delete the row:
             {Lines(departed)}

             A control the sweep cannot build is a control no assertion in this file covers, which
             is precisely the blindness deriving the domain exists to end. Give it a parameterless
             constructor, or add a row saying who does cover it and why it cannot be built bare.
             """
        );
    }

    /// <summary>The committed census: a type, the issue that will close it, and the reason.</summary>
    static Dictionary<string, string> Census() {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = File.ReadAllLines(Path.Combine(Root(), CensusFile));

        // "It has rows" cannot stand in for "it was read": an answered census and a truncated one
        // are both zero rows, and only one of them still has the header saying what the file is for.
        Assert.True(
            lines.Count(static line => line.StartsWith('#')) >= 5,
            $"{CensusFile} has lost its header, so it was emptied rather than answered."
        );

        foreach (var line in lines) {
            var text = line.Trim();

            if (text.Length == 0 || text.StartsWith('#')) {
                continue;
            }

            var parts = text.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Assert.True(
                parts.Length >= 3 && parts[1].StartsWith('#'),
                $"{CensusFile} is malformed at '{text}'. Each row is `type<TAB>#issue<TAB>reason`."
            );

            rows[parts[0]] = parts[2];
        }

        return rows;
    }

    static string Lines(IEnumerable<string> names) {
        var joined = new StringBuilder();

        foreach (var name in names) {
            joined.Append("  ").AppendLine(name);
        }

        return joined.Length == 0 ? "  (none)" : joined.ToString().TrimEnd('\n');
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
