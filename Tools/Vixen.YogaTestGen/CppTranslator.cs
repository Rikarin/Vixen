// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace Vixen.YogaTestGen;

/// <summary>Why a fixture could not be translated.</summary>
/// <param name="Test">The fixture's name.</param>
/// <param name="Reason">The line that stopped it.</param>
record SkippedTest(string Test, string Reason);

/// <summary>One translated fixture.</summary>
/// <param name="Name">The fixture's name, as Yoga generated it.</param>
/// <param name="Body">The C# statements, one per line, already indented.</param>
record TranslatedTest(string Name, IReadOnlyList<string> Body);

/// <summary>
///     Turns one of Yoga's generated C++ fixtures into the equivalent C# against
///     <c>Vixen.Ui.Layout</c>.
/// </summary>
/// <remarks>
///     <para>
///         This is a line translator and not a C++ parser, which is defensible only because the
///         input is machine-generated: every statement is one of about forty shapes, one per line,
///         with no control flow and no expressions worth the name. A line it does not recognise is
///         never guessed at — the whole fixture is dropped and the reason is reported, because a
///         conformance suite that silently omits the cases it found hard is worse than no suite.
///     </para>
///     <para>
///         The numbers are what actually matter here. Every expected value in the output came from
///         a real browser laying out a real HTML fixture, which is the entire reason ADR-006 calls
///         this the highest-leverage thing in the UI plan: it turns "re-implement a subtle CSS
///         algorithm" into a red/green loop against Chrome.
///     </para>
/// </remarks>
static partial class CppTranslator {
    /// <summary>Translates every fixture in one file.</summary>
    /// <param name="source">The C++ source.</param>
    /// <param name="skipped">Collects the fixtures that could not be translated.</param>
    /// <returns>The fixtures that could.</returns>
    public static List<TranslatedTest> Translate(string source, List<SkippedTest> skipped) {
        var translated = new List<TranslatedTest>();

        foreach (Match test in TestBlock().Matches(source)) {
            var name = test.Groups["name"].Value;
            var body = new List<string>();
            string? failure = null;

            foreach (var raw in test.Groups["body"].Value.Split('\n')) {
                var line = raw.Trim();
                if (line.Length == 0) {
                    if (body.Count > 0 && body[^1].Length > 0) {
                        body.Add(string.Empty);
                    }

                    continue;
                }

                var statement = TranslateLine(line);
                if (statement is null) {
                    failure = line;
                    break;
                }

                if (statement.Length > 0) {
                    body.Add(statement);
                }
            }

            if (failure is not null) {
                skipped.Add(new SkippedTest(name, failure));
                continue;
            }

            while (body.Count > 0 && body[^1].Length == 0) {
                body.RemoveAt(body.Count - 1);
            }

            translated.Add(new TranslatedTest(name, body));
        }

        return translated;
    }

    /// <summary>Translates one statement, or null if it is not one of the known shapes.</summary>
    /// <param name="line">The trimmed C++ line.</param>
    /// <returns>The C# statement, an empty string for a line with no equivalent, or null.</returns>
    static string? TranslateLine(string line) {
        // The config object carries per-tree settings Vixen keeps on the tree itself, and the
        // manual frees are what `using var tree` does.
        if (line.StartsWith("YGConfigRef ", StringComparison.Ordinal)
            || line.StartsWith("YGConfigFree(", StringComparison.Ordinal)
            || line.StartsWith("YGNodeFreeRecursive(", StringComparison.Ordinal)) {
            return string.Empty;
        }

        var create = NodeCreation().Match(line);
        if (create.Success) {
            return $"var {Identifier(create.Groups["name"].Value)} = tree.CreateNode();";
        }

        var insert = InsertChild().Match(line);
        if (insert.Success) {
            return $"tree.InsertChild({Identifier(insert.Groups["parent"].Value)}, "
                + $"{Identifier(insert.Groups["child"].Value)}, {insert.Groups["index"].Value});";
        }

        var calculate = CalculateLayout().Match(line);
        if (calculate.Success) {
            return $"tree.CalculateLayout({Identifier(calculate.Groups["node"].Value)}, "
                + $"{Number(calculate.Groups["width"].Value)}, {Number(calculate.Groups["height"].Value)}, "
                + $"{MapEnum(calculate.Groups["direction"].Value)});";
        }

        var assert = Assertion().Match(line);
        if (assert.Success) {
            var getter = assert.Groups["getter"].Value switch {
                "Left" => "GetLeft",
                "Top" => "GetTop",
                "Right" => "GetRight",
                "Bottom" => "GetBottom",
                "Width" => "GetWidth",
                "Height" => "GetHeight",
                _ => null
            };

            return getter is null
                ? null
                : $"Assert.Equal({Number(assert.Groups["expected"].Value)}, "
                + $"tree.{getter}({Identifier(assert.Groups["node"].Value)}), Tolerance);";
        }

        var context = SetContext().Match(line);
        if (context.Success) {
            return $"tree.SetContext({Identifier(context.Groups["node"].Value)}, "
                + $"\"{context.Groups["text"].Value}\");";
        }

        var measure = SetMeasureFunc().Match(line);
        if (measure.Success) {
            return measure.Groups["function"].Value == "IntrinsicSizeMeasure"
                ? $"tree.SetMeasureFunction({Identifier(measure.Groups["node"].Value)}, "
                + "ConformanceMeasure.IntrinsicSize);"
                : null;
        }

        var style = StyleSetter().Match(line);
        return style.Success
            ? StyleSetters.Translate(style.Groups["property"].Value, style.Groups["arguments"].Value)
            : null;
    }

    /// <summary>Turns <c>root_child0</c> into <c>rootChild0</c>.</summary>
    /// <param name="name">The C++ identifier.</param>
    /// <returns>The C# one.</returns>
    internal static string Identifier(string name) {
        var parts = name.Split('_');
        return string.Concat(parts.Select((part, index) =>
                index == 0 || part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]
            )
        );
    }

    /// <summary>Formats a C++ numeric literal as a C# float literal.</summary>
    /// <param name="value">The literal, which may be <c>YGUndefined</c>.</param>
    /// <returns>The C# expression.</returns>
    internal static string Number(string value) {
        value = value.Trim();
        if (value == "YGUndefined") {
            return "float.NaN";
        }

        value = value.TrimEnd('f');
        return double.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString("0.0###########", CultureInfo.InvariantCulture) + "f"
            : value + "f";
    }

    /// <summary>Maps a <c>YG…</c> enum constant onto Vixen's.</summary>
    /// <param name="constant">The C++ constant.</param>
    /// <returns>The C# expression, or null if Vixen has no equivalent.</returns>
    internal static string? MapEnum(string constant) => constant.Trim() switch {
        "YGDirectionLTR" => "Direction.Ltr",
        "YGDirectionRTL" => "Direction.Rtl",
        "YGDirectionInherit" => "Direction.Inherit",
        "YGFlexDirectionColumn" => "FlexDirection.Column",
        "YGFlexDirectionColumnReverse" => "FlexDirection.ColumnReverse",
        "YGFlexDirectionRow" => "FlexDirection.Row",
        "YGFlexDirectionRowReverse" => "FlexDirection.RowReverse",
        "YGJustifyFlexStart" => "Justify.FlexStart",
        "YGJustifyCenter" => "Justify.Center",
        "YGJustifyFlexEnd" => "Justify.FlexEnd",
        "YGJustifySpaceBetween" => "Justify.SpaceBetween",
        "YGJustifySpaceAround" => "Justify.SpaceAround",
        "YGJustifySpaceEvenly" => "Justify.SpaceEvenly",
        "YGAlignAuto" => "Align.Auto",
        "YGAlignFlexStart" => "Align.FlexStart",
        "YGAlignCenter" => "Align.Center",
        "YGAlignFlexEnd" => "Align.FlexEnd",
        "YGAlignStretch" => "Align.Stretch",
        "YGAlignBaseline" => "Align.Baseline",
        "YGAlignSpaceBetween" => "Align.SpaceBetween",
        "YGAlignSpaceAround" => "Align.SpaceAround",
        "YGAlignSpaceEvenly" => "Align.SpaceEvenly",
        "YGPositionTypeStatic" => "PositionType.Static",
        "YGPositionTypeRelative" => "PositionType.Relative",
        "YGPositionTypeAbsolute" => "PositionType.Absolute",
        "YGWrapNoWrap" => "Wrap.NoWrap",
        "YGWrapWrap" => "Wrap.Wrap",
        "YGWrapWrapReverse" => "Wrap.WrapReverse",
        "YGOverflowVisible" => "Overflow.Visible",
        "YGOverflowHidden" => "Overflow.Hidden",
        "YGOverflowScroll" => "Overflow.Scroll",
        "YGDisplayFlex" => "Display.Flex",
        "YGDisplayNone" => "Display.None",
        "YGBoxSizingBorderBox" => "BoxSizing.BorderBox",
        "YGBoxSizingContentBox" => "BoxSizing.ContentBox",
        "YGEdgeLeft" => "Edge.Left",
        "YGEdgeTop" => "Edge.Top",
        "YGEdgeRight" => "Edge.Right",
        "YGEdgeBottom" => "Edge.Bottom",
        "YGEdgeStart" => "Edge.Start",
        "YGEdgeEnd" => "Edge.End",
        "YGEdgeHorizontal" => "Edge.Horizontal",
        "YGEdgeVertical" => "Edge.Vertical",
        "YGEdgeAll" => "Edge.All",
        "YGGutterColumn" => "Gutter.Column",
        "YGGutterRow" => "Gutter.Row",
        "YGGutterAll" => "Gutter.All",

        // Deliberately absent: YGDisplayContents. `display: contents` is outside the algorithm
        // scope doc 09 states, and a fixture using it is dropped rather than approximated.
        _ => null
    };

    [GeneratedRegex(@"TEST\(YogaTest, (?<name>\w+)\) \{(?<body>.*?)\n\}", RegexOptions.Singleline)]
    private static partial Regex TestBlock();

    [GeneratedRegex(@"^YGNodeRef (?<name>\w+) = YGNodeNewWithConfig\(config\);$")]
    private static partial Regex NodeCreation();

    [GeneratedRegex(@"^YGNodeInsertChild\((?<parent>\w+), (?<child>\w+), (?<index>\d+)\);$")]
    private static partial Regex InsertChild();

    [GeneratedRegex(
        @"^YGNodeCalculateLayout\((?<node>\w+), (?<width>[\w.\-]+), (?<height>[\w.\-]+), (?<direction>\w+)\);$"
    )]
    private static partial Regex CalculateLayout();

    [GeneratedRegex(
        @"^ASSERT_FLOAT_EQ\((?<expected>[\w.\-]+), YGNodeLayoutGet(?<getter>\w+)\((?<node>\w+)\)\);$"
    )]
    private static partial Regex Assertion();

    [GeneratedRegex(@"^YGNodeSetContext\((?<node>\w+), \(void\*\)""(?<text>[^""]*)""\);$")]
    private static partial Regex SetContext();

    [GeneratedRegex(@"^YGNodeSetMeasureFunc\((?<node>\w+), &facebook::yoga::test::(?<function>\w+)\);$")]
    private static partial Regex SetMeasureFunc();

    [GeneratedRegex(@"^YGNodeStyleSet(?<property>\w+)\((?<arguments>.*)\);$")]
    private static partial Regex StyleSetter();
}
