// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Rendering.Water;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Water;

/// <summary>Doc 35's six debug verbs, as things a person can actually turn on.</summary>
/// <remarks>
///     <para>
///         <b>The verbs existed and nothing could reach them.</b> <c>WaterDebug</c> declares six
///         <c>[ConsoleCommand]</c>s, and the only thing that finds an attributed command is
///         <c>ConsoleCommands.RegisterFrom(Assembly)</c> — which nothing in this tree calls, because
///         nothing in this tree constructs a <c>ConsoleCommands</c> at all outside its own tests. So
///         <c>water.showFlow</c> was a name in a source file.
///     </para>
///     <para>
///         ⚠ <b>Registered under their console names on purpose.</b> An editor command id and a
///         console verb are two ways of typing the same thing, and giving the palette a different
///         name — <c>water.debug.flow</c> — would mean the sentence in the documentation matched
///         neither. The command palette is the editor's console.
///     </para>
///     <para>
///         ⚠ <b>The flags are static, and that is <c>WaterDebug</c>'s design rather than this file's
///         shortcut.</b> A verb is typed once and read by whatever draws — the viewport presenter
///         here, a compositor node in a game — and neither can be handed to the other. What it costs
///         is that two editors in one process share the flags, which for a debug toggle is what
///         somebody flipping it wants anyway.
///     </para>
///     <para>
///         ⚠ <b>Not gated on the water mode being active</b>, on <c>WaterMode.CreateZoneCommand</c>'s
///         terms: "why does this river run backwards" is a question asked from whatever mode the
///         author happened to be in.
///     </para>
/// </remarks>
public sealed partial class WaterModule {
    /// <summary>The six verbs, in the order the mode's menu lists them.</summary>
    /// <remarks>
    ///     ⚠ <b>Two of them are inert in a pane and are registered anyway.</b>
    ///     <c>water.showTiles</c> and <c>water.showLod</c> describe the patches a device selected and
    ///     <c>water.showRipples</c> a simulation only a game runs — the editor's preview surface is a
    ///     CPU grid and has none of the three. Hiding them would make the set the author sees depend
    ///     on which host they are in, which is worse than a toggle that draws nothing; what they do
    ///     get is a checkbox whose state travels into play mode.
    /// </remarks>
    static readonly (string Id, string Label, Func<bool> Read, Action Toggle)[] DebugVerbs = [
        ("water.showTiles", "Show Water Tiles", () => WaterDebug.ShowTiles, () => WaterDebug.ShowTiles = !WaterDebug.ShowTiles),
        ("water.showLod", "Show Water LOD Bands", () => WaterDebug.ShowLod, () => WaterDebug.ShowLod = !WaterDebug.ShowLod),
        ("water.showInfo", "Show Water Info Channels", () => WaterDebug.ShowInfo, () => WaterDebug.ShowInfo = !WaterDebug.ShowInfo),
        ("water.showFlow", "Show Water Flow", () => WaterDebug.ShowFlow, () => WaterDebug.ShowFlow = !WaterDebug.ShowFlow),
        ("water.showBuoyancy", "Show Buoyancy", () => WaterDebug.ShowBuoyancy, () => WaterDebug.ShowBuoyancy = !WaterDebug.ShowBuoyancy),
        ("water.showRipples", "Show Water Ripples", () => WaterDebug.ShowRipples, () => WaterDebug.ShowRipples = !WaterDebug.ShowRipples)
    ];

    /// <summary>What the palette groups them under. The mode's own, restated here for its scope.</summary>
    static readonly StringId DebugCategory = new("editor.category.water", "Water");

    /// <summary>Puts them in the shell, and takes them back out when the module unloads.</summary>
    void WaterDebugCommands(Vixen.Editor.Plugin.PluginContext context) {
        foreach (var (id, label, read, toggle) in DebugVerbs) {
            var verb = id;

            Shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, label), toggle) {
                    Category = DebugCategory,
                    Checked = read
                }
            );

            context.OnUnload(() => Shell.Commands.Remove(verb));
        }

        // ⚠ Switched off when the module goes, because the flags outlive it. A static left true by a
        // session that ended is a viewport drawing arrows for a toolset that is no longer loaded, and
        // nothing on screen would say where they came from.
        context.OnUnload(WaterDebug.Reset);
    }
}
