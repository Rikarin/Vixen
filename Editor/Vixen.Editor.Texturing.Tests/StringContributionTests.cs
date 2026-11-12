// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/1202">#1202</a>: the toolset's words reach a
///     translator's template, which they did not on the day the declaration class was written.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the assertion that would have been false.</b> <c>TexturingStrings.All</c>
///         existed, held nine command labels, and was read by nothing at all — so
///         <c>Strings.Template</c> did not export one of them and no translator's template contained
///         a single word this toolset says. The <c>All</c> list was the answer to
///         <c>StringFamily</c>'s argument and nothing walked it.
///     </para>
///     <para>
///         ⚠ <b>Only the positive half is asserted, and that is deliberate.</b>
///         <see cref="StringContributions" /> is process-wide, like <c>Strings</c> itself, so
///         "the template does not contain it yet" is a claim about which test ran first — the same
///         race that made <c>Vixen.Editor.App.Tests</c> turn collection parallelism off. What is
///         asserted is monotone and order-independent: after this module activates, the word is
///         there. <c>StringContributionTests</c> in <c>Vixen.Editor.Ui.Tests</c> holds the
///         registration's own before-and-after, where nothing is shared.
///     </para>
/// </remarks>
public class StringContributionTests {
    /// <summary>Activating the toolset puts its command labels in the editor's template.</summary>
    [Fact]
    public void Activating_the_toolset_puts_its_words_in_the_editors_template() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        var template = EditorStrings.Template("cs");
        var declared = TexturingStrings.All;

        Assert.NotEmpty(declared);

        foreach (var id in declared) {
            Assert.Equal(id.Source, template.Find(id.Id));
        }
    }
}
