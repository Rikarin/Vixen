// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Terrain;

/// <summary>Every word the terrain and foliage toolsets show, declared once.</summary>
/// <remarks>
///     <para>
///         <b>Two families and one lone id, because most of these ids are computed.</b> Eight
///         commands exist once per digit in each of the two modes, one per sculpt tool, one per
///         paint tool and one per category — a <see cref="StringId" /> property can only declare an
///         id that is written out, so until <see cref="StringFamily" /> existed both modes built
///         their labels at the call site.
///     </para>
///     <para>
///         ⚠ <b>The computed halves are built from the same lists the registration walks</b> —
///         <c>TerrainMode.Tools</c>, <c>PaintTools</c>, <c>Categories</c>, <c>SlotCount</c> and
///         <c>FoliageMode.Tools</c> — so a tool added to one of those lists gets a declared label
///         rather than an undeclared one, and the family throws while the mode registers if it does
///         not.
///     </para>
///     <para>
///         ⚠ <b>One id here was in neither half of <c>CheckStrings</c>' census.</b>
///         <see cref="FoliageTypeRemoveUnavailable" /> was written as
///         <c>Unavailable = new("…", "…")</c> in an object initialiser, which carries neither the
///         type name the loose-id patterns anchor on nor a <c>new StringId</c> the construction
///         patterns match — so it was neither declared nor counted. The analyzer sees it, which is
///         how migrating this assembly found it.
///     </para>
/// </remarks>
public static class TerrainStrings {
    /// <summary>Every command the terrain mode registers, by its command id.</summary>
    public static StringFamily TerrainCommands { get; } = new(
        "editor.command.",
        [
            // The digit row, bound to slots rather than to named tools because two categories share
            // them — which is why the label is the slot's number and not a tool's name.
            .. Enumerable.Range(0, TerrainMode.SlotCount)
                .Select(
                    slot => new KeyValuePair<string, string>(
                        TerrainMode.SlotCommand(slot),
                        "Tool " + (slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    )
                ),
            .. TerrainMode.Tools.Select(
                tool => new KeyValuePair<string, string>(TerrainMode.ToolCommand(tool), tool + " Tool")
            ),
            .. TerrainMode.PaintTools.Select(
                tool => new KeyValuePair<string, string>(TerrainMode.PaintToolCommand(tool), "Paint " + tool)
            ),
            .. TerrainMode.Categories.Select(
                category => new KeyValuePair<string, string>(
                    TerrainMode.CategoryCommand(category),
                    category.ToString()
                )
            ),
            new(TerrainMode.GrowBrushCommand, "Grow Brush"),
            new(TerrainMode.ShrinkBrushCommand, "Shrink Brush"),
            new(TerrainMode.HarderCommand, "Press Harder"),
            new(TerrainMode.SofterCommand, "Press Softer"),
            new(TerrainMode.CreateCommand, "Create Terrain"),
            new(TerrainMode.AddTargetCommand, "Add Target Layer"),
            new(TerrainMode.RemoveTargetCommand, "Remove Target Layer"),
            new(TerrainMode.AddLayerCommand, "Add Terrain Layer"),
            new(TerrainMode.RemoveLayerCommand, "Remove Terrain Layer"),
            new(TerrainMode.DuplicateLayerCommand, "Duplicate Terrain Layer"),
            new(TerrainMode.ClearLayerCommand, "Clear Terrain Layer"),
            new(TerrainMode.CollapseLayerCommand, "Collapse Terrain Layer"),
            new(TerrainMode.RaiseLayerCommand, "Raise Terrain Layer"),
            new(TerrainMode.LowerLayerCommand, "Lower Terrain Layer"),
            new(TerrainMode.ToggleLayerCommand, "Show / Hide Terrain Layer")
        ]
    );

    /// <summary>Every command the foliage mode registers, by its command id.</summary>
    public static StringFamily FoliageCommands { get; } = new(
        "editor.command.",
        [
            .. Enumerable.Range(0, FoliageMode.SlotCount)
                .Select(
                    slot => new KeyValuePair<string, string>(
                        FoliageMode.SlotCommand(slot),
                        "Tool " + (slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    )
                ),
            .. FoliageMode.Tools.Select(
                tool => new KeyValuePair<string, string>(FoliageMode.ToolCommand(tool), tool + " Foliage")
            ),
            new(FoliageMode.GrowBrushCommand, "Grow Brush"),
            new(FoliageMode.ShrinkBrushCommand, "Shrink Brush"),
            new(FoliageMode.AddTypeCommand, "Add Foliage Type"),
            new(FoliageMode.RemoveTypeCommand, "Remove Foliage Type"),
            new(FoliageMode.DeselectCommand, "Deselect Foliage"),
            new(FoliageMode.DeleteSelectionCommand, "Delete Selected Foliage")
        ]
    );

    /// <summary>Why removing a foliage type is greyed out.</summary>
    /// <remarks>
    ///     A single declaration rather than a family member, because it is not a command's label: it
    ///     is the sentence <c>EditorCommand.Unavailable</c> shows instead of one.
    /// </remarks>
    public static StringId FoliageTypeRemoveUnavailable { get; } = new(
        "editor.command.foliage.type-remove.unavailable",
        "Removing a type renumbers every instance above it; not yet built."
    );

    /// <summary>What a translator's template for these two toolsets holds.</summary>
    /// <remarks>
    ///     Spread from both families, which is what puts every member of them in the template. A
    ///     family left out of this list would hide a whole toolset rather than one word, which is why
    ///     <c>VXS0310</c> counts a <see cref="StringFamily" /> property as a declaration.
    /// </remarks>
    public static IReadOnlyList<StringId> All { get; } = [
        .. TerrainCommands.All,
        .. FoliageCommands.All,
        FoliageTypeRemoveUnavailable
    ];
}
