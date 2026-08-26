// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Generators.Tests;

/// <summary>What the analyzer says about a declaration class, and what it deliberately says nothing about.</summary>
/// <remarks>
///     ⚠ <b>The first test here is the one that matters: the shape as it is written today reports
///     nothing.</b> A rule that fired on <c>EditorStrings</c> or <c>ControlStrings</c> as they stand
///     would not be a check, it would be a demand that the declaration shape change — and doc 46 § A3
///     is explicit that the shape is the thing worth having unchanged, because a second generator
///     outside this repository emits it too.
/// </remarks>
public class StringDeclarationTests {
    const string Declared = """
        using System.Collections.Generic;
        using Vixen.Ui;

        public static class ShopStrings {
            public static StringId Buy { get; } = new("shop.action.buy", "Buy");
            public static StringId Cancel { get; } = new("shop.action.cancel", "Cancel");

            public static IReadOnlyList<StringId> All { get; } = [Buy, Cancel];
        }
        """;

    /// <summary>The shape both declaration classes in this repository are written in.</summary>
    [Fact]
    public async Task The_shape_as_written_reports_nothing() {
        var reported = await AnalyzerHarness.RunAsync(Declared);

        Assert.Empty(reported);
    }

    /// <summary>An id declared and left out of the list a translator's template is built from.</summary>
    [Fact]
    public async Task A_declaration_missing_from_All_is_an_error() {
        var reported = await AnalyzerHarness.RunAsync(
            """
            using System.Collections.Generic;
            using Vixen.Ui;

            public static class ShopStrings {
                public static StringId Buy { get; } = new("shop.action.buy", "Buy");
                public static StringId Cancel { get; } = new("shop.action.cancel", "Cancel");

                public static IReadOnlyList<StringId> All { get; } = [Buy];
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(StringDeclarationAnalyzer.MissingFromAllId, diagnostic.Id);
        Assert.Equal("Cancel", AnalyzerHarness.Underlined(diagnostic));
        Assert.Contains("no translator's template contains it", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>Two declarations under one id: a catalogue is a map, so one of them is unreachable.</summary>
    [Fact]
    public async Task Two_declarations_under_one_id_are_an_error() {
        var reported = await AnalyzerHarness.RunAsync(
            """
            using System.Collections.Generic;
            using Vixen.Ui;

            public static class ShopStrings {
                public static StringId Buy { get; } = new("shop.action.buy", "Buy");
                public static StringId Purchase { get; } = new("shop.action.buy", "Purchase");

                public static IReadOnlyList<StringId> All { get; } = [Buy, Purchase];
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(StringDeclarationAnalyzer.DuplicateIdId, diagnostic.Id);

        // Named by the earlier member alphabetically rather than by file order, so the pair does not
        // swap which of the two is reported between builds.
        Assert.Contains("ShopStrings.Buy", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>An id built at a call site in an assembly that has already said where its ids live.</summary>
    [Fact]
    public async Task A_string_built_outside_the_declaration_class_is_an_error() {
        var reported = await AnalyzerHarness.RunAsync(
            Declared
            + """

            public static class Checkout {
                public static string Label() => new StringId("shop.action.pay", "Pay").Source;
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(StringDeclarationAnalyzer.UndeclaredId, diagnostic.Id);
        Assert.Contains("ShopStrings", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ And nothing is reported in an assembly that has no declaration class, which is the
    ///     boundary of what this rule claims.
    /// </summary>
    /// <remarks>
    ///     An assembly with no declaration class has not answered the question of where its ids live,
    ///     and answering it for the author would fail nine editor module assemblies and a project
    ///     template on the day it landed. <c>./build.sh CheckStrings</c> counts that population
    ///     instead — see <c>docs/plan/11</c> § As built.
    /// </remarks>
    [Fact]
    public async Task A_string_built_where_there_is_no_declaration_class_is_not_reported() {
        var reported = await AnalyzerHarness.RunAsync(
            """
            using Vixen.Ui;

            public static class Checkout {
                public static string Label() => new StringId("shop.action.pay", "Pay").Source;
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     A computed id is not compared with anything, because it cannot be.
    /// </summary>
    /// <remarks>
    ///     An editor mode registers a command per tool and builds the id from the tool's name. That is
    ///     a legitimate shape a declaration class has no way to express, and a duplicate-id rule that
    ///     guessed at it would report on a string it cannot read.
    /// </remarks>
    [Fact]
    public async Task A_computed_id_is_not_compared() {
        var reported = await AnalyzerHarness.RunAsync(
            """
            using System.Collections.Generic;
            using Vixen.Ui;

            public static class ShopStrings {
                public const string Prefix = "shop.action.";

                public static StringId Buy { get; } = new(Prefix + "buy", "Buy");
                public static StringId Purchase { get; } = new(Prefix + "buy", "Purchase");

                public static IReadOnlyList<StringId> All { get; } = [Buy, Purchase];
            }
            """
        );

        Assert.Empty(reported);
    }
}
