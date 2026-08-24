// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>The two escape hatches beside <c>w-[37px]</c>: <c>[mask-type:luminance]</c> and <c>bg-(--brand)</c>.</summary>
/// <remarks>
///     <para>
///         <b><c>docs/plan/43</c> F7.</b> Arbitrary <i>values</i> worked and were well tested;
///         arbitrary <i>properties</i> parsed to an empty utility name and were rejected, and v4's
///         CSS-variable shorthand reached the colour lookup as the literal text <c>(--brand)</c>.
///         Both were silent — an unknown class emits no rule and says nothing, which is
///         indistinguishable from a typo.
///     </para>
///     <para>
///         ⚠ <b>The interesting half of this file is <see cref="An_arbitrary_property_is_outside_the_consumption_gates_domain_by_construction" />
///         rather than the parsing.</b> An arbitrary property bypasses the family table on purpose,
///         so nothing validates what it emits — which collides with the rule that a change may not
///         emit a property nothing acts on. It does not need an exemption, and the test says why.
///     </para>
/// </remarks>
public class ArbitraryPropertyTests {
    static readonly ThemeTokens Tokens = ThemeTokens.Parse(UtilityFixture.Theme);

    static List<UtilityDeclaration> Resolve(string candidate) {
        Assert.True(UtilityParser.TryParse(candidate, out var parsed), $"'{candidate}' did not parse");

        var declarations = new List<UtilityDeclaration>();
        Assert.True(UtilityFamilies.TryResolve(parsed, Tokens, declarations), $"'{candidate}' did not resolve");

        return declarations;
    }

    static void Refused(string candidate) {
        var declarations = new List<UtilityDeclaration>();

        if (UtilityParser.TryParse(candidate, out var parsed)) {
            Assert.False(
                UtilityFamilies.TryResolve(parsed, Tokens, declarations),
                $"'{candidate}' resolved to {string.Join("; ", declarations.Select(d => $"{d.Property}: {d.Value}"))}"
            );
        }

        // And whichever half refused it, the generator emits no rule for it at all — and says so in
        // exactly one of its two channels.
        //
        // ⚠ Which channel is not this helper's claim, and asserting `Unrecognised` was how it came to
        // be one. `bg-(brand)` and `text-indent-4` name the registered families `bg` and `text`, so
        // they are refusals rather than prose (see `ShadowedFamilyTests`); `Foo(bar)` is prose. All
        // three emit nothing, which is the whole of what "refused" means here.
        var generator = new UtilityGenerator(Tokens);
        Assert.DoesNotContain(candidate, generator.Generate([candidate]), StringComparison.Ordinal);
        Assert.Equal(0, generator.RuleCount);

        Assert.True(
            generator.Unrecognised.Contains(candidate, StringComparer.Ordinal)
            ^ generator.Unresolved.Any(r => string.Equals(r.Candidate, candidate, StringComparison.Ordinal)),
            $"'{candidate}' was reported in neither channel, or in both"
        );
    }

    /// <summary>The hatch itself: a property with no family emits exactly what it says.</summary>
    /// <remarks>
    ///     ⚠ <b>No family is consulted and none exists</b> — <c>mask-type</c> is not registered, which
    ///     is what makes it the right example. The declaration goes through verbatim and the cascade
    ///     is left to refuse it downstream if nothing interns it.
    /// </remarks>
    [Theory]
    [InlineData("[mask-type:luminance]", "mask-type", "luminance")]
    [InlineData("[color:red]", "color", "red")]
    [InlineData("[--my-gap:4px]", "--my-gap", "4px")]
    [InlineData("[font-variation-settings:'wght'_700]", "font-variation-settings", "'wght' 700")]
    public void An_arbitrary_property_emits_the_declaration_it_names(string candidate, string property, string value) {
        var declaration = Assert.Single(Resolve(candidate));

        Assert.Equal(property, declaration.Property, StringComparer.Ordinal);
        Assert.Equal(value, declaration.Value, StringComparer.Ordinal);
    }

    /// <summary>The property name is not underscore-converted and the value is.</summary>
    /// <remarks>
    ///     A space is never part of a property name, so a <c>_</c> in one is a <c>_</c> somebody meant
    ///     — <c>--my_var</c> is a custom property — where a <c>_</c> in the value is the space a class
    ///     attribute cannot hold. Converting both would make the first unwritable.
    /// </remarks>
    [Fact]
    public void The_underscore_convention_applies_to_the_value_and_not_to_the_property_name() {
        var declaration = Assert.Single(Resolve("[--my_var:1px_solid_red]"));

        Assert.Equal("--my_var", declaration.Property, StringComparer.Ordinal);
        Assert.Equal("1px solid red", declaration.Value, StringComparer.Ordinal);
    }

    /// <summary>A malformed arbitrary property produces no rule, on either half of the colon.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>text[1..]</c> defect has two more ways in now and this is both of them closed.</b>
    ///     <c>IsPlausibleValue</c> was added after a C# range expression reached a generated sheet as
    ///     <c>font-size: 1..</c> — emitted, parsed by ExCSS, dropped without a word. An arbitrary
    ///     property needs the same treatment on the name as well, because there is no family behind it
    ///     to notice that <c>1..</c> is not a property.
    /// </remarks>
    [Theory]
    // The property half is not an identifier.
    [InlineData("[1..:red]")]
    [InlineData("[mask type:red]")]
    [InlineData("[9lives:red]")]
    [InlineData("[-x:red]")]
    [InlineData("[:red]")]
    [InlineData("[--:red]")]
    [InlineData("[ma(sk:red]")]
    // The value half is not CSS.
    [InlineData("[font-size:1..]")]
    [InlineData("[color:rgb(1,2]")]
    [InlineData("[content:'unclosed]")]
    [InlineData("[color:]")]
    // No colon at all is an arbitrary value with no utility to hang it on, not a property.
    [InlineData("[red]")]
    [InlineData("[37px]")]
    public void A_malformed_arbitrary_property_produces_no_rule_at_all(string candidate) => Refused(candidate);

    /// <summary>A sign and a slash are refused rather than silently dropped.</summary>
    /// <remarks>
    ///     Negation is arithmetic on a resolved number and there is no number here; a slash is a
    ///     <i>family's</i> reading of an opacity and this candidate has no family. Either one honoured
    ///     as far as emitting the declaration and then ignored would be half a class quietly discarded,
    ///     which is the failure mode this whole file is about.
    /// </remarks>
    [Theory]
    [InlineData("-[color:red]")]
    [InlineData("[color:red]/50")]
    public void A_negated_or_opacity_suffixed_arbitrary_property_is_refused(string candidate) => Refused(candidate);

    /// <summary>v4's <c>bg-(--brand)</c> is exactly <c>bg-[var(--brand)]</c>.</summary>
    /// <remarks>
    ///     Rewritten in the parser rather than given a path of its own, so the two spellings cannot
    ///     drift: everything downstream — <c>IsPlausibleValue</c>, the border-edge colour test, the
    ///     gradient stops — sees one shape.
    /// </remarks>
    [Theory]
    [InlineData("bg-(--brand)", "bg-[var(--brand)]")]
    [InlineData("text-(--ink)", "text-[var(--ink)]")]
    [InlineData("w-(--sidebar)", "w-[var(--sidebar)]")]
    [InlineData("border-(--edge)", "border-[var(--edge)]")]
    public void The_variable_shorthand_resolves_to_what_the_bracket_form_resolves_to(string shorthand, string bracket) {
        Assert.Equal(Resolve(bracket), Resolve(shorthand));

        // And it really did become a `var()` rather than reaching a token table as literal text.
        Assert.All(Resolve(shorthand), d => Assert.Contains("var(--", d.Value, StringComparison.Ordinal));
    }

    /// <summary>The shorthand takes a custom property and leaves every other parenthesis alone.</summary>
    /// <remarks>
    ///     ⚠ <b>The scanner is over-inclusive and hands the parser every <c>f(x)</c> in every C# file.</b>
    ///     A rule that claimed any parenthesised tail would turn <c>Foo(bar)</c> into a utility named
    ///     <c>Foo</c> with a nonsense value; <c>--</c> is what v4 requires and it is also what keeps
    ///     this from being a new way to invent utilities out of source code.
    /// </remarks>
    [Theory]
    [InlineData("bg-(brand)")]
    [InlineData("Foo(bar)")]
    [InlineData("(--brand)")]
    [InlineData("bg-(--)")]
    [InlineData("bg-(--brand")]
    public void The_variable_shorthand_is_not_claimed_by_anything_that_merely_has_parentheses(string candidate) =>
        Refused(candidate);

    /// <summary>An arbitrary value containing parentheses is still an arbitrary value.</summary>
    /// <remarks>
    ///     The bracket is looked for first, so <c>grid-cols-[repeat(3,1fr)]</c> never reaches the
    ///     shorthand at all — which is the ordering that keeps the older hatch working unchanged.
    /// </remarks>
    [Theory]
    [InlineData("grid-cols-[repeat(3,1fr)]", "repeat(3,1fr)")]
    [InlineData("w-[calc(100%-2rem)]", "calc(100%-2rem)")]
    [InlineData("bg-[var(--brand)]", "var(--brand)")]
    public void An_arbitrary_value_with_parentheses_is_unaffected(string candidate, string arbitrary) {
        Assert.True(UtilityParser.TryParse(candidate, out var parsed));

        Assert.Equal(arbitrary, parsed.Arbitrary, StringComparer.Ordinal);
        Assert.Null(parsed.Property);
    }

    /// <summary>An arbitrary property is not a family with a strange value, and <c>SplitName</c> never sees it.</summary>
    /// <remarks>
    ///     ⚠ <b><c>SplitName</c> takes the longest registered prefix and does not retry a shorter one</b>,
    ///     so a candidate that reached it with a property-shaped name could be claimed by a family and
    ///     resolved as something else entirely. It cannot happen, and the reason is structural rather
    ///     than lucky: the parser sets <c>Arbitrary</c> before the split and <c>SplitName</c> is only
    ///     called when <c>Arbitrary</c> is null. This pins both ends of that — the candidate carries no
    ///     family name, and the family that shares its prefix resolves differently.
    /// </remarks>
    [Fact]
    public void An_arbitrary_property_is_never_mistaken_for_a_family() {
        Assert.True(UtilityParser.TryParse("[text-align:center]", out var parsed));

        // `text` is a registered family and `text-align` starts with it. The candidate is neither.
        Assert.Empty(parsed.Name);
        Assert.Equal("text-align", parsed.Property, StringComparer.Ordinal);
        Assert.Equal(string.Empty, parsed.Value, StringComparer.Ordinal);

        var declaration = Assert.Single(Resolve("[text-align:center]"));
        Assert.Equal("text-align", declaration.Property, StringComparer.Ordinal);

        // ⚠ The two routes happen to agree here — `text-center` emits `text-align: center` too — and
        // agreeing is not the claim. The claim is that they are different routes, which is visible on
        // the candidate and not on the declaration: the family form carries a registered name and no
        // property, the hatch carries a property and no name.
        Assert.True(UtilityParser.TryParse("text-center", out var family));
        Assert.Equal("text", family.Name, StringComparer.Ordinal);
        Assert.Null(family.Property);

        // And the hatch reaches a property under a spelling the family table refuses, which is the
        // case that could not work if `SplitName` had claimed the `text` prefix. `text-indent` is a
        // real property with a real reader and the `indent-*` family emits it — but under *that*
        // name: `text-indent-4` splits to the registered family `text` with the value `indent-4`,
        // which is not a utility, and the hatch is how the property is reached by its CSS name.
        Assert.Equal("text-indent", Assert.Single(Resolve("[text-indent:4px]")).Property, StringComparer.Ordinal);
        Refused("text-indent-4");
    }

    /// <summary>Both hatches survive the scan of a <c>.vcss</c>, where a colon means a declaration.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same shape of reasoning as <c>Apply_is_not_a_declaration_and_its_utilities_survive_the_scan</c>,
    ///         and it had to be checked because an arbitrary property is <i>made of</i> the thing the
    ///         narrowing skips.</b> <c>ScanStyleSheet</c> skips from an identifier-then-colon to the
    ///         statement's terminator, and <c>[mask-type:luminance]</c> contains exactly that colon.
    ///     </para>
    ///     <para>
    ///         It survives, and structurally rather than by luck: <c>IsDeclaration</c> requires the
    ///         colon to follow a run of identifier characters back to the first non-blank character of
    ///         the statement, and an arbitrary property always begins with <c>[</c>, which is not one.
    ///         In an <c>@apply</c> the statement begins with <c>@</c>, which is not one either — so
    ///         even <c>hover:[mask-type:luminance]</c>, whose <i>first</i> colon does follow a bare
    ///         identifier, is safe wherever it can legitimately appear.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Both_hatches_survive_the_stylesheet_scan() {
        const string sheet = """
            .card { @apply [mask-type:luminance] bg-(--brand) hover:[color:red] flex; color: red; }
            """;

        var found = new HashSet<string>(StringComparer.Ordinal);
        CandidateScanner.ScanStyleSheet(sheet, found);

        Assert.Contains("[mask-type:luminance]", found);
        Assert.Contains("bg-(--brand)", found);
        Assert.Contains("hover:[color:red]", found);
        Assert.Contains("flex", found);

        // The rule the exception is an exception to still holds.
        Assert.DoesNotContain("red", found);
    }

    /// <summary>And through the ordinary scan, which is what C# and <c>.vxml</c> input gets.</summary>
    [Fact]
    public void Both_hatches_survive_the_ordinary_scan() {
        var found = new HashSet<string>(StringComparer.Ordinal);
        CandidateScanner.Scan("""<div class="[mask-type:luminance] bg-(--brand) w-[37px]" />""", found);

        Assert.Contains("[mask-type:luminance]", found);
        Assert.Contains("bg-(--brand)", found);
        Assert.Contains("w-[37px]", found);
    }

    /// <summary>End to end: both hatches reach a generated sheet as rules with escaped selectors.</summary>
    [Fact]
    public void Both_hatches_reach_the_generated_sheet() {
        var generator = new UtilityGenerator(Tokens);
        var css = generator.Generate(["[mask-type:luminance]", "bg-(--brand)", "hover:[color:red]"]);

        Assert.Empty(generator.Unrecognised);
        Assert.Contains(@".\[mask-type\:luminance\] { mask-type: luminance; }", css, StringComparison.Ordinal);
        Assert.Contains("var(--brand)", css, StringComparison.Ordinal);

        // A variant composes with an arbitrary property exactly as with anything else.
        Assert.Contains(@".hover\:\[color\:red\]:hover { color: red; }", css, StringComparison.Ordinal);
    }

    /// <summary><c>@apply</c> takes an arbitrary property, because it is declarations and nothing else.</summary>
    /// <remarks>
    ///     Worth its own case because <c>ApplyExpander</c> refuses a scoped family by asking
    ///     <c>UtilityFamilies.ScopeOf(parsed.Name)</c>, and an arbitrary property's name is empty. An
    ///     unregistered name has no scope, which is the right answer here for the right reason: an
    ///     arbitrary property is one declaration about the element itself and can never be a rule over
    ///     somebody's children.
    /// </remarks>
    [Fact]
    public void Apply_can_expand_an_arbitrary_property() {
        var expander = new ApplyExpander(Tokens);
        var expanded = expander.Expand(".card { @apply [mask-type:luminance] bg-(--brand); }");

        Assert.Empty(expander.Diagnostics);
        Assert.Contains("mask-type: luminance;", expanded, StringComparison.Ordinal);
        Assert.Contains("var(--brand)", expanded, StringComparison.Ordinal);
    }

    /// <summary>The consumption gate needs no exemption for this, and that is why it is not weakened.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The claim this file exists to defend.</b> An arbitrary property bypasses the family
    ///         table entirely — that is the point of it — so nothing says what
    ///         <c>[mask-type:luminance]</c> means and nothing validates that anything reads it. That
    ///         collides head-on with <c>UtilityConsumptionGateTests</c>, whose one sentence is that a
    ///         change may not emit a property nothing in the engine acts on.
    ///     </para>
    ///     <para>
    ///         <b>The resolution is that the gate's domain is the registry, not the emitted CSS.</b>
    ///         <c>UtilityConsumptionProbe.Emissions</c> enumerates <see cref="UtilityFamilies.Surface" />,
    ///         which is computed from the family table. An arbitrary property is never registered, so
    ///         it is not on the surface, contributes nothing to <c>Emitted</c>, and can appear in
    ///         neither <c>Inert</c> nor <c>InertProperties.txt</c>. <b>No code anywhere says "skip the
    ///         gate for this"</b> — and a branch that did would be the actual hole, because a domain
    ///         defined negatively by a list of escapes is one pull request away from a longer list.
    ///     </para>
    ///     <para>
    ///         <b>Nor can the hatch launder a family's debt, which is the real test.</b> Registering a
    ///         <c>--tw-*</c> fragment <i>was</i> a way to move a property out of <c>Inert</c>, which is
    ///         why that mechanism needed an explicit guard holding the assembler accountable. There is
    ///         no matching move here: writing an arbitrary property in a <c>.vxml</c> changes
    ///         <c>Surface</c> by nothing, because <c>Surface</c> never reads a source file, and the
    ///         only way to take a family off the surface is to delete its registration — which stops
    ///         every use of it generating anywhere in the tree, loudly.
    ///     </para>
    ///     <para>
    ///         <b>What the gate protects is a promise, and an arbitrary property makes none.</b> The
    ///         registry saying <c>p-4</c> exists is a promise that <c>p-4</c> does something, so a
    ///         <c>p-4</c> that does nothing is a lie only a hand survey would catch. Nothing told the
    ///         author that <c>[mask-type:luminance]</c> would work; they typed the property name
    ///         themselves, and "emitted, and dropped by the cascade if no consumer interns it" is the
    ///         documented behaviour of the hatch rather than a defect in it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is all reasoning about how <c>Emissions</c> happens to be written, so this
    ///         test pins it.</b> Rewriting the probe to scan a project's generated <c>.g.vcss</c>
    ///         instead of enumerating the registry is a plausible "make it more end-to-end" refactor,
    ///         and it would silently drag every arbitrary property in the tree into the gate's domain,
    ///         where each would measure inert and either fail the build or earn an allow-list line that
    ///         could never expire. This fails first, and says why.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_arbitrary_property_is_outside_the_consumption_gates_domain_by_construction() {
        var surface = UtilityFamilies.Surface(ThemeTokens.Parse(UtilityConsumptionProbe.ProbeTheme));

        // The surface is family names and their values. Nothing on it is an arbitrary property or the
        // variable shorthand, because neither is registered and the surface is the registry.
        Assert.NotEmpty(surface);
        Assert.DoesNotContain(surface, candidate => candidate.StartsWith('['));
        Assert.DoesNotContain(surface, candidate => candidate.Contains("(--", StringComparison.Ordinal));

        // A property only an arbitrary property could emit is on nobody's books: not emitted, not
        // inert, not composed, and — the clause that matters — not allow-listed either. If the gate's
        // domain ever grew to include the hatch, `mask-type` is what would appear here first.
        var measured = UtilityConsumptionProbe.Take();

        Assert.DoesNotContain("mask-type", measured.Emitted);
        Assert.DoesNotContain("mask-type", measured.Inert.Keys);
        Assert.DoesNotContain("mask-type", measured.Composed.Keys);
        Assert.DoesNotContain("mask-type", InertAllowList.Load().Keys);

        // And it really is a property the engine does not act on — so if it were ever judged, it would
        // fail. That is what makes the exemption load-bearing rather than academic.
        Assert.Empty(UtilityConsumptionProbe.Channels("mask-type", "luminance"));

        // Meanwhile the hatch does emit it, which is the behaviour the gate must not be asked about.
        Assert.Equal("mask-type", Assert.Single(Resolve("[mask-type:luminance]")).Property, StringComparer.Ordinal);
    }
}
