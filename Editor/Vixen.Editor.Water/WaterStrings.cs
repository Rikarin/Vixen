// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Water;

/// <summary>Every word the water toolset shows, declared once.</summary>
/// <remarks>
///     <para>
///         <b>One family rather than nineteen properties, because the ids are computed.</b> Three of
///         the mode's commands exist once per digit, three once per tool and six once per debug
///         flag; a <see cref="StringId" /> property can only declare an id that is written out, so
///         until <see cref="StringFamily" /> existed this toolset's labels were built at the call
///         site — <c>new StringId("editor.command." + id, "Finish Water Body")</c>.
///     </para>
///     <para>
///         ⚠ <b>That was not a style question: those nineteen words could not be translated at
///         all.</b> <c>Strings.Template</c> exports <c>All</c> lists, and an id that exists only at
///         run time is in no <c>All</c> list, so no translator's template ever contained one of
///         them. ⚠ And the gate written to find exactly that could not see them either — both of
///         <c>CheckStrings</c>' patterns need a string literal where the id goes, so a concatenation
///         was never in the census whose ceiling reads zero.
///     </para>
///     <para>
///         The keys are the command ids themselves, which is what the call sites already had in
///         hand, so a registration reads <c>WaterStrings.Commands[id]</c> where it used to build one.
///     </para>
/// </remarks>
public static class WaterStrings {
    /// <summary>Every command the toolset registers, by its command id.</summary>
    /// <remarks>
    ///     The prefix is the convention the call sites were spelling out: a command's label is
    ///     <c>editor.command.</c> followed by the command's own id.
    /// </remarks>
    public static StringFamily Commands { get; } = new(
        "editor.command.",
        [
            // The digit row. `Tool 1` rather than the tool's name, because what the digit means is
            // "the first tool" and the named commands beside it keep the words the palette is
            // searched with.
            .. Enumerable.Range(0, WaterMode.SlotCount)
                .Select(
                    slot => new KeyValuePair<string, string>(
                        WaterMode.SlotCommand(slot),
                        "Tool " + (slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    )
                ),
            .. WaterMode.Tools.Select(
                tool => new KeyValuePair<string, string>(WaterMode.ToolCommand(tool), tool + " Water")
            ),
            new(WaterMode.FinishCommand, "Finish Water Body"),
            new(WaterMode.UndoPointCommand, "Undo Water Point"),
            new(WaterMode.CancelCommand, "Cancel Water Draw"),
            new(WaterMode.CreateZoneCommand, "Create Water Zone"),
            new(WaterMode.PreviewCarveCommand, "Preview Water Carve"),
            new(WaterModule.CarveTerrainCommand, "Carve Terrain From Water"),
            new("water.showTiles", "Show Water Tiles"),
            new("water.showLod", "Show Water LOD Bands"),
            new("water.showInfo", "Show Water Info Channels"),
            new("water.showFlow", "Show Water Flow"),
            new("water.showBuoyancy", "Show Buoyancy"),
            new("water.showRipples", "Show Water Ripples")
        ]
    );

    /// <summary>What a translator's template for this toolset holds.</summary>
    /// <remarks>
    ///     Spread from the family, which is what puts every member of it in the template. A family
    ///     left out of this list would hide nineteen strings rather than one, which is why
    ///     <c>VXS0310</c> counts a <see cref="StringFamily" /> property as a declaration.
    /// </remarks>
    public static IReadOnlyList<StringId> All { get; } = [.. Commands.All];
}
