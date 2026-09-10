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
