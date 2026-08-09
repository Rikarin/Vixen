// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors;
using Vixen.Editor.Debugger;
using Vixen.Editor.Profiler;
using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>One theme across many assemblies: every participant compiled against the same tokens.</summary>
/// <remarks>
///     <para>
///         <b>This suite exists because no suite inside a single project can hold the claim it
///         makes.</b> The utility build step runs per project — it globs that project's sources and
///         finds that project's token file — so "the editor's chrome is one design" is a statement
///         about a dozen assemblies at once and every existing test is scoped to one of them.
///         <c>UtilityConsumptionGateTests</c> in particular is a strong gate on what a family may
///         emit and structurally blind to which assemblies can emit anything at all: its scenes live
///         in one probe project, and a project that silently generates nothing looks, from inside
///         that project, exactly like a project with nothing to generate.
///     </para>
///     <para>
///         ⚠ <b>What the failure looked like before shape C, measured rather than assumed.</b> The
///         generation target is conditioned on there being a token source, so an assembly without
///         one did not produce a wrong sheet — it produced none, and the class names in its markup
///         resolved against nothing with no diagnostic anywhere. The visible consequence was not an
///         unstyled panel but a hand-written substitute: <c>class="hidden"</c> in this repository's
///         import views renders only because <c>AdvancedTheme</c> declares
///         <c>.hidden { display: none; }</c> in <c>Vixen.Ui.Controls.Advanced</c>. A utility paid for
///         by hand in a component sheet is what an assembly boundary in a token system costs.
///     </para>
///     <para>
///         ⚠ <b>Two canaries rather than one, because "the sheet is not empty" cannot tell the two
///         failures apart.</b> Most of what the scanner finds are bare English words out of prose and
///         CSS declarations — <c>flex</c>, <c>hidden</c>, <c>block</c>, <c>relative</c> — so an
///         assembly wired to no tokens at all still emits a dozen plausible rules. The pair below is
///         chosen so that each side names a different wrong answer: <c>bg-surface</c> absent means
///         the shared tokens never arrived, and <c>bg-blue-500</c> present means Tailwind's shipped
///         palette arrived instead of the editor's. Both are safelisted in
///         <c>Vixen.Editor.Ui/build/Vixen.Editor.Ui.Styling.targets</c>, so neither depends on a
///         panel continuing to use a particular class.
///     </para>
///     <para>
///         To sabotage-test it: delete the <c>VixenStyleTokens</c> line from that <c>.targets</c> and
///         <see cref="Every_participating_assembly_resolves_a_token_only_the_editor_declares" /> fails
///         for both non-editor assemblies. Point it at a file that does not exist and the build fails
///         instead, which is the other half of the same guarantee.
///     </para>
/// </remarks>
public class SharedThemeTests {
    /// <summary>Every assembly that opts in, and the sheet the build produced for it.</summary>
    /// <remarks>
    ///     ⚠ <b>Read through each assembly's own accessor rather than off disk.</b> What ships is the
    ///     constant compiled into the binary; a test that read <c>obj/</c> would pass on a tree whose
    ///     generated file was never handed to the compiler, which is one of the two ways this step
    ///     has silently done nothing.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>This list is also the ledger of which assemblies have opted in, and it is deliberately
    ///     hand-written.</b> Nine or so editor assemblies still do not take part; joining is one
    ///     <c>Import</c> in the <c>.csproj</c> and one line here, and the reason there is no
    ///     reflection over "every assembly with a theme" is that opting in is a decision — a project
    ///     with a design of its own should declare its own tokens and must not be swept into the
    ///     editor's.
    /// </remarks>
    public static TheoryData<string, string> Participants =>
        new() {
            { "Vixen.Editor.Ui", EditorStyles.Utilities },
            { "Vixen.Editor.Profiler", ProfilerTheme.Utilities },
            { "Vixen.Editor.AssetEditors", AssetEditorTheme.Utilities },
            { "Vixen.Editor.Debugger", DebuggerTheme.Utilities }
        };

    /// <summary>The shared tokens reached the assembly: a token only the editor declares resolves.</summary>
    /// <remarks>
    ///     <c>surface</c> is the editor's own colour token. It is declared in
    ///     <c>Vixen.Editor.Ui/Theming/vixen.ui.vcss</c> and nowhere else, and it is not one of
    ///     Tailwind's, so a sheet containing <c>.bg-surface</c> is a sheet that read the editor's
    ///     <c>@theme</c> — which for two of the three assemblies below is only possible across a
    ///     project boundary.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Participants))]
    public void Every_participating_assembly_resolves_a_token_only_the_editor_declares(
        string assembly,
        string utilities
    ) {
        Assert.False(
            string.IsNullOrWhiteSpace(utilities),
            $"{assembly} produced no utility sheet at all. The build step is conditioned on a token "
            + "source, so a project that names none does not run it and says nothing — import "
            + "Editor/Vixen.Editor.Ui/build/Vixen.Editor.Ui.Styling.targets.");

        Assert.Contains(".bg-surface", utilities, StringComparison.Ordinal);

        // The value and not only the rule: `surface` resolving to something other than the editor's
        // custom property would mean a second palette agreeing by name, which is the failure the
        // token model exists to prevent and the one a rule-name check cannot see.
        Assert.Contains("background-color: var(--surface)", utilities, StringComparison.Ordinal);
    }

    /// <summary>And they are the editor's tokens rather than the shipped defaults.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion that would have caught the wrong-palette hazard, and it is
    ///     worth stating why it is a separate test.</b> A project with no token source falls back to
    ///     <c>ThemeTokens.CreateDefault()</c>, which is Tailwind's own palette — so the two ways to be
    ///     misconfigured are "no tokens" and "somebody else's tokens", and only the first is loud.
    ///     The editor's theme empties the colour namespace before declaring its own, so
    ///     <c>bg-blue-500</c> is precisely the class that resolves under the defaults and under
    ///     nothing else. Its absence is the proof.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Participants))]
    public void No_participating_assembly_falls_back_to_the_shipped_palette(
        string assembly,
        string utilities
    ) =>
        Assert.False(
            utilities.Contains(".bg-blue-500", StringComparison.Ordinal),
            $"{assembly} emitted .bg-blue-500. The editor's theme clears the colour namespace before "
            + "declaring its own, so that class can only resolve against ThemeTokens.CreateDefault() "
            + "— this assembly is compiled against Tailwind's palette rather than the editor's.");

    /// <summary>The sheets compose: every one of them is layered, so their union is order-free.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Shape C's whole claim rests on this and nothing else.</b> Per-assembly sheets are
    ///         only safe because each lands in <c>@layer utilities</c>, where document order decides
    ///         nothing — a dozen of them loaded in whatever sequence the modules happen to install
    ///         behaves as one sheet. An unlayered utility sheet would make the answer depend on which
    ///         panel's assembly was touched first, which is not a bug anyone would find twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two <c>@layer</c>s now, and the first one is doing a job the second cannot.</b>
    ///         A block layer only says "these rules are in <c>utilities</c>"; where <c>utilities</c>
    ///         <i>sorts</i> is fixed by whoever names it first, so a sheet that opened its block and
    ///         said nothing else would be a sheet whose position depends on the install order it is
    ///         supposed to be immune to. The <c>@layer base, components, utilities;</c> statement is
    ///         the ordering, every participant emits the same one, and a re-declaration never moves a
    ///         layer — which is what makes the union order-free in the second sense as well as the
    ///         first. What must still be true is that no participant opens a block layer other than
    ///         <c>utilities</c>.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Participants))]
    public void Every_participating_sheet_is_entirely_inside_the_utilities_layer(
        string assembly,
        string utilities
    ) {
        var text = utilities.TrimStart();

        Assert.True(
            text.StartsWith("@layer base, components, utilities;", StringComparison.Ordinal),
            $"{assembly}'s sheet does not open with the ladder, so where its utilities sort depends "
            + "on which assembly's sheet a document happened to load first.");

        var blocks = new List<string>();

        for (var i = text.IndexOf("@layer", StringComparison.Ordinal); i >= 0;
             i = text.IndexOf("@layer", i + 1, StringComparison.Ordinal)) {
            var terminator = text.IndexOfAny(['{', ';'], i);

            if (terminator >= 0 && text[terminator] == '{') {
                blocks.Add(text[i..terminator].Trim());
            }
        }

        Assert.True(
            blocks is ["@layer utilities"],
            $"{assembly}'s sheet opens {string.Join(", ", blocks)} rather than @layer utilities alone; "
            + "the union of these is only order-free while every rule in all of them is in one layer.");
    }
}
