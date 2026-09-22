// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/1202">#1202</a>: the registration a toolset
///     makes when it activates, and the before-and-after that could not be asserted anywhere shared.
/// </summary>
/// <remarks>
///     ⚠ <b>The subject is process-wide, so the fixtures are lists this assembly owns.</b> A test
///     that registered <c>WaterStrings.All</c> would be asserting something another suite may have
///     asserted first, and the editor's toolsets are five assemblies this one deliberately does not
///     reference. A list declared here is registered by nobody else, which is what makes "not in the
///     template" a fact rather than a race.
/// </remarks>
public class StringContributionTests {
    static readonly IReadOnlyList<StringId> Contributed = [
        new("test.contribution.first", "First"),
        new("test.contribution.second", "Second")
    ];

    static readonly IReadOnlyList<StringId> NeverRegistered = [new("test.contribution.absent", "Absent")];

    /// <summary>A list nobody registered is in no template; the one that is registered is in every one.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion that would have been false on the day <c>WaterStrings</c> was
    ///     written</b>, with the toolset's five <c>All</c> lists standing where
    ///     <see cref="NeverRegistered" /> stands: declared, in an <c>All</c> list, and read by
    ///     nothing, so <c>Strings.Template</c> exported none of them.
    /// </remarks>
    [Fact]
    public void Only_a_registered_declaration_reaches_the_template() {
        Assert.DoesNotContain(NeverRegistered[0], StringContributions.Declared);
        Assert.Null(EditorStrings.Template("cs").Find(NeverRegistered[0].Id));

        StringContributions.Declare(Contributed);

        var template = EditorStrings.Template("cs");

        foreach (var id in Contributed) {
            Assert.Equal(id.Source, template.Find(id.Id));
        }

        Assert.Null(template.Find(NeverRegistered[0].Id));
    }

    /// <summary>A list two activations declared stays in the template until both have withdrawn it.</summary>
    /// <remarks>
    ///     <para>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1316">#1316</a>. Every module that
    ///         activates declares its class's <c>All</c> — one static array — and records a
    ///         withdrawal for its own unload. Two modules up at once are two claims on one array,
    ///         and a registry that removed it on the first withdrawal silently emptied the other's
    ///         words out of the template. The Texturing suite met that as an order-dependent
    ///         failure: parallel fixtures, the first to dispose taking the words the second was
    ///         about to read.
    ///     </para>
    ///     <para>
    ///         Its own list rather than <see cref="Contributed" />, because the other tests here
    ///         leave that one declared and this one needs to see it go.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_list_two_activations_declared_stays_until_both_withdraw() {
        IReadOnlyList<StringId> shared = [new("test.contribution.shared", "Shared")];

        StringContributions.Declare(shared);
        StringContributions.Declare(shared);

        Assert.True(StringContributions.Withdraw(shared));
        Assert.Contains(shared[0], StringContributions.Declared);
        Assert.Equal("Shared", EditorStrings.Template("cs").Find(shared[0].Id));

        Assert.True(StringContributions.Withdraw(shared));
        Assert.DoesNotContain(shared[0], StringContributions.Declared);
        Assert.Null(EditorStrings.Template("cs").Find(shared[0].Id));

        // And nothing is left standing on it: a third withdrawal has nothing to remove.
        Assert.False(StringContributions.Withdraw(shared));
    }

    /// <summary>Registering the same list twice is one registration.</summary>
    /// <remarks>
    ///     The plugins panel's Disable/Enable pair deactivates and reactivates a module, so a module
    ///     that contributed on every activation would export its strings once per click. The catalog
    ///     survives that — it is a map — and the count a translator is quoted does not.
    /// </remarks>
    [Fact]
    public void Registering_one_list_twice_contributes_it_once() {
        StringContributions.Declare(Contributed);

        var before = StringContributions.Declared.Count;

        StringContributions.Declare(Contributed);

        Assert.Equal(before, StringContributions.Declared.Count);
    }
}
