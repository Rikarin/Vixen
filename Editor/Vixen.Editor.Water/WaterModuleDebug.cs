// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Rendering.Water;
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
///         ⚠ <b>The flags are this module's, one set per editor, and they used to be the process's.</b>
///         They were <c>WaterDebug</c>'s six statics, on the argument that two editors in one process
///         sharing a debug toggle is what somebody flipping it wants anyway — and the harness opens
///         several editors in one process, so a checkbox ticked in one session was ticked in every
///         other one, and a test that ticked it decided what an unrelated test's pane drew. The set
///         is published as a <see cref="WaterDebugFlags" /> contribution, which is how the viewport's
///         presenter finds it without naming this module, and it goes when the module does.
///         <c>WaterDebug.Process</c> stays what a game's console flips.
///     </para>
///     <para>
///         ⚠ <b>Not gated on the water mode being active</b>, on <c>WaterMode.CreateZoneCommand</c>'s
///         terms: "why does this river run backwards" is a question asked from whatever mode the
///         author happened to be in.
///     </para>
/// </remarks>
public sealed partial class WaterModule {
    /// <summary>Draws the patches a device selected.</summary>
    public const string ShowTilesCommand = "water.showTiles";

    /// <summary>Draws which LOD band each patch is in.</summary>
    public const string ShowLodCommand = "water.showLod";

    /// <summary>Draws the info channels.</summary>
    public const string ShowInfoCommand = "water.showInfo";

    /// <summary>Draws the flow field.</summary>
    public const string ShowFlowCommand = "water.showFlow";

    /// <summary>Draws what buoyancy reads.</summary>
    public const string ShowBuoyancyCommand = "water.showBuoyancy";

    /// <summary>Draws the ripple simulation only a game runs.</summary>
    public const string ShowRipplesCommand = "water.showRipples";

    /// <summary>The six verbs, in the order the mode's menu lists them.</summary>
    /// <remarks>
    ///     ⚠ <b>Two of them are inert in a pane and are registered anyway.</b>
    ///     <c>water.showTiles</c> and <c>water.showLod</c> describe the patches a device selected and
    ///     <c>water.showRipples</c> a simulation only a game runs — the editor's preview surface is a
    ///     CPU grid and has none of the three. Hiding them would make the set the author sees depend
    ///     on which host they are in, which is worse than a toggle that draws nothing.
    ///     ⚠ <b>Nor does the checkbox travel into play mode, as this used to say it did</b>: nothing in
    ///     the editor's play session reads a water flag, the process's or a module's.
    /// </remarks>
    (string Id, Func<bool> Read, Action Toggle)[] DebugVerbs() => [
        (ShowTilesCommand, () => debug.ShowTiles, () => debug.ShowTiles = !debug.ShowTiles),
        (ShowLodCommand, () => debug.ShowLod, () => debug.ShowLod = !debug.ShowLod),
        (ShowInfoCommand, () => debug.ShowInfo, () => debug.ShowInfo = !debug.ShowInfo),
        (ShowFlowCommand, () => debug.ShowFlow, () => debug.ShowFlow = !debug.ShowFlow),
        (ShowBuoyancyCommand, () => debug.ShowBuoyancy, () => debug.ShowBuoyancy = !debug.ShowBuoyancy),
        (ShowRipplesCommand, () => debug.ShowRipples, () => debug.ShowRipples = !debug.ShowRipples)
    ];

    /// <summary>This editor's switches — what the six verbs flip and the viewport draws from.</summary>
    readonly WaterDebugFlags debug = new();

    /// <summary>This editor's switches, for a host or a test reading what the verbs flipped.</summary>
    public WaterDebugFlags Debug => debug;

    /// <summary>Puts the verbs in the shell and the switches in the registry, and takes all of it back out.</summary>
    void WaterDebugCommands(Vixen.Editor.Plugin.PluginContext context, IEditorRegistry extensions) {
        foreach (var (id, read, toggle) in DebugVerbs()) {
            context.AddCommand(
                new EditorCommand(id, WaterStrings.Commands[id], toggle) {
                    Category = WaterStrings.CategoryWater,
                    Checked = read
                }
            );
        }

        context.Owns(extensions.Add(debug));

        // ⚠ Switched off when the module goes, so a reload starts from nothing drawn — the state a
        // freshly opened editor is in, rather than arrows somebody turned on before they unloaded it.
        context.OnUnload(debug.Reset);
    }
}
