// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Texturing;

/// <summary>Doc 48's texturing verbs, as a service: what the Tools menu runs and a plugin can call.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D2 one level down (#1306).</b> The verbs were private methods of
///         <see cref="TexturingModule" /> that only its commands could reach — so a plugin that wanted
///         "bake the open stack the way the menu does" could not have it, and a test had to run a
///         command by id. They are the module's public methods now, each command is a name for one of
///         them, and the module publishes itself under this contract with <c>PluginContext.Publish</c>,
///         so <c>context.Services.Require&lt;ITexturing&gt;()</c> reaches the same behaviour and is
///         withdrawn when the module unloads.
///     </para>
///     <para>
///         ⚠ <b>Each verb acts on what the module has open and answers in a notification.</b> The
///         graph is the one <see cref="OpenGraph" /> opened and the stack the one
///         <see cref="OpenStack" /> opened; with nothing open the verb says so on the shell's
///         notification list rather than throwing, which is what a command run from a palette with
///         nothing open has always done.
///     </para>
///     <para>
///         ⚠ <b>The service is the module itself rather than a class beside it.</b> Blockout's verbs
///         read dials a mode owns and moved out into <c>BlockoutVerbs</c>; these read the open
///         documents, the baker, the brush and the armed force-bake, which are the module's state —
///         doc 50 § D2's "the state moves with the verb" — so the verbs stayed where that state is and
///         moved into a file of their own (<c>TexturingVerbs.cs</c>) rather than into a second object
///         that would reach back for every field.
///     </para>
/// </remarks>
public interface ITexturing {
    /// <summary>Opens the <c>.vxtexgraph</c> selected in the Project panel on the Texture Graph canvas.</summary>
    void OpenGraph();

    /// <summary>Opens the <c>.vxlayers</c> or <c>.vxsmartmat</c> selected in the Project panel in the layers panel.</summary>
    void OpenStack();

    /// <summary>Swaps the pointer between selecting and painting, and opens the paint pane when painting.</summary>
    void TogglePaint();

    /// <summary>Turns the open graph into a material in the project.</summary>
    void BakeMaterial();

    /// <summary>Turns the open graph into a material that marches its own height map.</summary>
    void BakeParallaxMaterial();

    /// <summary>Turns the open layer stack into one material per texture set.</summary>
    void BakeStackMaterial();

    /// <summary>Packs the open stack's layer weights into a splat map and binds it onto the material.</summary>
    void BakeSplatMap();

    /// <summary>Re-runs the last bake a painted-over map refused, and does nothing when none was.</summary>
    void BakeForced();

    /// <summary>Saves the open stack's chosen texture set onto the shelf as a <c>.vxsmartmat</c>.</summary>
    void SaveSmartMaterial();

    /// <summary>Puts the <c>.vxsmartmat</c> selected in the Project panel on top of the open stack's chosen set.</summary>
    void ApplySmartMaterial();
}
