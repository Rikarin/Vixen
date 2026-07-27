// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.YogaTestGen;

/// <summary>Maps Yoga's <c>YGNodeStyleSet…</c> family onto <c>LayoutTree</c>'s setters.</summary>
/// <remarks>
///     Yoga spells the unit into the function name — <c>SetWidth</c>, <c>SetWidthPercent</c>,
///     <c>SetWidthAuto</c> — where Vixen carries it in the value. That is the bulk of what this
///     file does, and it is why a fixture reads as one call per CSS declaration on both sides.
/// </remarks>
static class StyleSetters {
    /// <summary>Translates one style call.</summary>
    /// <param name="property">The part of the name after <c>YGNodeStyleSet</c>.</param>
    /// <param name="arguments">The comma-separated arguments, untouched.</param>
    /// <returns>The C# statement, or null if Vixen has no equivalent.</returns>
    public static string? Translate(string property, string arguments) {
        var parts = SplitArguments(arguments);
        if (parts.Count == 0) {
            return null;
        }

        var node = CppTranslator.Identifier(parts[0]);

        return property switch {
            "Width" => Dimension(node, "Width", "Points", parts, 1),
            "WidthPercent" => Dimension(node, "Width", "Percent", parts, 1),
            "WidthAuto" => $"tree.SetDimension({node}, Dimension.Width, StyleLength.Auto);",
            "WidthMaxContent" => Keyword(node, "SetDimension", "Dimension.Width", "MaxContent"),
            "WidthFitContent" => Keyword(node, "SetDimension", "Dimension.Width", "FitContent"),
            "WidthStretch" => Keyword(node, "SetDimension", "Dimension.Width", "Stretch"),

            "Height" => Dimension(node, "Height", "Points", parts, 1),
            "HeightPercent" => Dimension(node, "Height", "Percent", parts, 1),
            "HeightAuto" => $"tree.SetDimension({node}, Dimension.Height, StyleLength.Auto);",
            "HeightMaxContent" => Keyword(node, "SetDimension", "Dimension.Height", "MaxContent"),
            "HeightFitContent" => Keyword(node, "SetDimension", "Dimension.Height", "FitContent"),
            "HeightStretch" => Keyword(node, "SetDimension", "Dimension.Height", "Stretch"),

            "MinWidth" => Bound(node, "Min", "Width", "Points", parts),
            "MinWidthPercent" => Bound(node, "Min", "Width", "Percent", parts),
            "MinHeight" => Bound(node, "Min", "Height", "Points", parts),
            "MinHeightPercent" => Bound(node, "Min", "Height", "Percent", parts),
            "MaxWidth" => Bound(node, "Max", "Width", "Points", parts),
            "MaxWidthPercent" => Bound(node, "Max", "Width", "Percent", parts),
            "MaxHeight" => Bound(node, "Max", "Height", "Points", parts),
            "MaxHeightPercent" => Bound(node, "Max", "Height", "Percent", parts),

            "Flex" => $"tree.SetFlex({node}, {CppTranslator.Number(parts[1])});",
            "FlexGrow" => $"tree.SetFlexGrow({node}, {CppTranslator.Number(parts[1])});",
            "FlexShrink" => $"tree.SetFlexShrink({node}, {CppTranslator.Number(parts[1])});",
            "FlexBasis" => $"tree.SetFlexBasis({node}, StyleLength.Points({CppTranslator.Number(parts[1])}));",
            "FlexBasisPercent" => $"tree.SetFlexBasis({node}, StyleLength.Percent({CppTranslator.Number(parts[1])}));",
            "FlexBasisAuto" => $"tree.SetFlexBasis({node}, StyleLength.Auto);",
            "FlexBasisMaxContent" => $"tree.SetFlexBasis({node}, StyleLength.Keyword(LayoutUnit.MaxContent));",
            "FlexBasisFitContent" => $"tree.SetFlexBasis({node}, StyleLength.Keyword(LayoutUnit.FitContent));",
            "FlexBasisStretch" => $"tree.SetFlexBasis({node}, StyleLength.Keyword(LayoutUnit.Stretch));",

            "Margin" => EdgeLength(node, "SetMargin", "Points", parts),
            "MarginPercent" => EdgeLength(node, "SetMargin", "Percent", parts),
            "MarginAuto" => EdgeAuto(node, "SetMargin", parts),
            "Padding" => EdgeLength(node, "SetPadding", "Points", parts),
            "PaddingPercent" => EdgeLength(node, "SetPadding", "Percent", parts),
            "Border" => EdgeLength(node, "SetBorder", "Points", parts),
            "Position" => EdgeLength(node, "SetPosition", "Points", parts),
            "PositionPercent" => EdgeLength(node, "SetPosition", "Percent", parts),
            "PositionAuto" => EdgeAuto(node, "SetPosition", parts),

            "Gap" => Gap(node, "Points", parts),
            "GapPercent" => Gap(node, "Percent", parts),

            "AspectRatio" => $"tree.SetAspectRatio({node}, {CppTranslator.Number(parts[1])});",

            "Direction" => Enumeration(node, "SetDirection", parts),
            "FlexDirection" => Enumeration(node, "SetFlexDirection", parts),
            "JustifyContent" => Enumeration(node, "SetJustifyContent", parts),
            "AlignContent" => Enumeration(node, "SetAlignContent", parts),
            "AlignItems" => Enumeration(node, "SetAlignItems", parts),
            "AlignSelf" => Enumeration(node, "SetAlignSelf", parts),
            "PositionType" => Enumeration(node, "SetPositionType", parts),
            "FlexWrap" => Enumeration(node, "SetFlexWrap", parts),
            "Overflow" => Enumeration(node, "SetOverflow", parts),
            "Display" => Enumeration(node, "SetDisplay", parts),
            "BoxSizing" => Enumeration(node, "SetBoxSizing", parts),

            _ => null
        };
    }

    static string Dimension(string node, string axis, string unit, List<string> parts, int index) =>
        $"tree.SetDimension({node}, Dimension.{axis}, StyleLength.{unit}({CppTranslator.Number(parts[index])}));";

    static string Bound(string node, string bound, string axis, string unit, List<string> parts) =>
        $"tree.Set{bound}Dimension({node}, Dimension.{axis}, StyleLength.{unit}({CppTranslator.Number(parts[1])}));";

    static string Keyword(string node, string setter, string axis, string unit) =>
        $"tree.{setter}({node}, {axis}, StyleLength.Keyword(LayoutUnit.{unit}));";

    static string? EdgeLength(string node, string setter, string unit, List<string> parts) {
        var edge = CppTranslator.MapEnum(parts[1]);
        return edge is null
            ? null
            : $"tree.{setter}({node}, {edge}, StyleLength.{unit}({CppTranslator.Number(parts[2])}));";
    }

    static string? EdgeAuto(string node, string setter, List<string> parts) {
        var edge = CppTranslator.MapEnum(parts[1]);
        return edge is null ? null : $"tree.{setter}({node}, {edge}, StyleLength.Auto);";
    }

    static string? Gap(string node, string unit, List<string> parts) {
        var gutter = CppTranslator.MapEnum(parts[1]);
        return gutter is null
            ? null
            : $"tree.SetGap({node}, {gutter}, StyleLength.{unit}({CppTranslator.Number(parts[2])}));";
    }

    static string? Enumeration(string node, string setter, List<string> parts) {
        var value = CppTranslator.MapEnum(parts[1]);
        return value is null ? null : $"tree.{setter}({node}, {value});";
    }

    static List<string> SplitArguments(string arguments) =>
        [.. arguments.Split(',').Select(argument => argument.Trim())];
}
