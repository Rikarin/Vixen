// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Samples.Mmo.Rules;
using Xunit;

namespace Vixen.Samples.Mmo.Realms.Tests;

/// <summary>That twenty gameplay libraries actually compose, which nothing before this proved.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28 ships twenty-odd packages so that a game can decline most of them, and every
///         milestone up to this one composed a handful.</b> <c>GameplayConfig.Build</c> is where two
///         modules declaring the same stat, two claiming one definition alias, and a module whose
///         dependency nobody used are each caught — and none of those is a mistake a single library's
///         own tests can make.
///     </para>
///     <para>
///         ⚠ <b>These assertions are about the shape and not the count.</b> The one count here is the
///         number of modules, because that number <em>is</em> the claim: twenty libraries plus the
///         kernel. Everything else is asserted as a property, so that adding content does not break a
///         test and adding a conflict does.
///     </para>
/// </remarks>
public sealed class CompositionTests {
    readonly GameplayComposition composition = MmoModules.Compose();

    [Fact]
    public void TwentyLibrariesAndTheKernelCompose() {
        // Build() is where the checking happens, so reaching this line is most of the assertion.
        Assert.Equal(21, composition.Modules.Count);
    }

    [Fact]
    public void NoTwoModulesClaimOneDefinitionAlias() {
        // ⚠ Refused by Build() rather than asserted here — this checks the *consequence*, which is
        // that every alias in the composition is unique and therefore that a `!Tag` in a .vxdef
        // names exactly one type. Two types on one alias is a file that decodes as the wrong thing.
        var aliases = composition.Definitions.Select(definition => definition.Tag).ToArray();

        Assert.Equal(aliases.Length, aliases.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NoTwoModulesClaimOneStat() {
        // AttributeLayout is already a *deduplicated* table — Build() is what refuses a second
        // claim on one stat — so what this can check is that the layout is dense and consistent:
        // every schema is at the slot the layout says its id is at.
        for (var slot = 0; slot < composition.Attributes.Count; slot++) {
            Assert.Equal(slot, composition.Attributes.SlotOf(composition.Attributes[slot].Attribute));
        }

        Assert.True(composition.Attributes.Count > 0, "Twenty libraries and no stats between them.");
    }

    [Fact]
    public void TheTagTableBakesAcrossEveryLibrary() {
        // ⚠ The tags a *module* declares are the ones no definition mentions, and they are the ones
        // that go missing. Event.Kill is QuestModule's, it is the verb every Kill objective counts,
        // and nothing in the content names it — so if the composition did not carry it, every
        // objective in the game would compile into one nothing can ever advance.
        Assert.Contains("Event.Kill", composition.Tags, StringComparer.Ordinal);
        Assert.Contains("State.Evading", composition.Tags, StringComparer.Ordinal);
    }

    [Fact]
    public void ComposingTwiceGivesTheSameTagsInTheSameOrder() {
        // A tag's index is its position in a pre-order walk of the table, and the walk starts from
        // this list. Two builds that ordered it differently would number every tag differently, and
        // a component holding a tag index would mean something else on the other one.
        Assert.Equal(composition.Tags, MmoModules.Compose().Tags);
    }
}
