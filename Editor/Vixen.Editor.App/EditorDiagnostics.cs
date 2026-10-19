// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Diagnostics;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>What the host hands the editor about its device, and the scopes the editor's own loop is in.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The seven panels and the report are not here any more — see <c>DiagnosticsModule</c>.</b>
///         Pointing the profiler, the GPU timeline and the statistics readout at a project, a scene and
///         a device is a joining job, and doc 36 § P3 moved it into an assembly of its own. The report
///         followed once the log ring and the data directory were published (#1298), so what stays is
///         genuinely the <i>application</i>'s: the device and the frame the host sets, and the
///         profiling keys the editor's own frame loop is measured in.
///     </para>
///     <para>
///         ⚠ <b>Doc 20's "the profiler must be able to profile the editor" is the reason
///         <see cref="EditorKeys" /> exists at all.</b> A source selector is worth nothing if the
///         editor's own frame has no scopes in it, so the four phases of the loop are instrumented
///         here — and they are the four <c>EditorHost</c>'s own remarks name, so the chart matches
///         the sentence a reader has already been told.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>The registered module of a kind, or null when the host did not name one.</summary>
    /// <remarks>
    ///     ⚠ <b>Null rather than a fresh one.</b> A second module that owns a panel's state is one the
    ///     host never activated, so every hook it hands out is joined to nothing and looks joined —
    ///     which is how the GPU panel once read "No graphics device" beside a window Vulkan was drawing:
    ///     the host wrote into a <c>new DiagnosticsModule()</c> nothing had registered. ⚠ The asset
    ///     editors' is the one typed module field left (#1297); the diagnostics one went with #1298.
    /// </remarks>
    static T? FindModule<T>(IReadOnlyList<(string Id, string Name, IEditorPlugin Module)> modules)
        where T : class, IEditorPlugin {
        foreach (var (_, _, module) in modules) {
            if (module is T found) {
                return found;
            }
        }

        return null;
    }

    /// <summary>The graphics device, when the host has one.</summary>
    IGraphicsDevice? graphicsDevice;

    /// <summary>The device the editor draws with, when the host has one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The only announcement the application gets that there is a device at all.</b>
    ///         The host builds one when the window can present and releases it when the window goes, so
    ///         this is where everything device-shaped in the editor has to be built and torn down —
    ///         see <see cref="AttachRenderer" />, which is the whole of the viewport's renderer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Held here and no longer forwarded into the diagnostics module</b> (#1298). The
    ///         GPU timeline reads it through <see cref="IEditorGraphics" />, which every plugin is
    ///         handed, so the application holds no field to the module and names its assembly nowhere.
    ///     </para>
    /// </remarks>
    public IGraphicsDevice? GraphicsDevice {
        get => graphicsDevice;
        set {
            // ⚠ First, and while the old device is still what everything in this application answers
            // with — that window is the whole of #968. `IEditorGraphics` invites a plugin to *hold* a
            // device and published no way to hear that the loan had ended, so a plugin's pipelines,
            // shader modules and `EffectLoader` outlived `vkDestroyDevice`, which the specification
            // makes undefined and MoltenVK does not complain about. A notification raised after the
            // line below would be one a plugin could do nothing with, which is the state the
            // texturing module was already in: it noticed by comparing devices and could only *drop*
            // what it was holding, because by the time it noticed the device had gone.
            if (graphicsDevice is { } going && !ReferenceEquals(going, value)) {
                plugins.DeviceLost(going);
            }

            graphicsDevice = value;
            AttachRenderer(value);
        }
    }

    /// <summary>The frame's GPU regions, as the host resolved them.</summary>
    /// <remarks>
    ///     A property rather than a <see cref="GpuProfiler" /> this class owns, because the object
    ///     that records the timestamps has to be the one recording the frame — which is the host, not
    ///     the application. Published as <see cref="IEditorGraphics.Frame" />.
    /// </remarks>
    public GpuFrame GpuFrame { get; set; } = GpuFrame.Empty;

    /// <summary>Registers a command that brings a panel up.</summary>
    /// <remarks>
    ///     ⚠ <b>Each one opens its panel rather than doing the thing.</b> "Profiler" is not a verb
    ///     that profiles — it is the verb that shows you the profiler, and the Record button inside it
    ///     is the one that samples. Making the menu line sample directly would be a menu item with no
    ///     visible effect and no way to stop it.
    /// </remarks>
    void Panel(string id, StringId title, string panel, StringId? category = null) =>
        Verb(id, title, category ?? EditorStrings.CategoryTools, () => Shell.Workspace.Toggle(panel));

    /// <summary>The modules this editor was built with, for a test that has to reach one.</summary>
    internal IReadOnlyList<(string Id, string Name, IEditorPlugin Module)> Modules => modules;

    /// <summary>The scopes the editor's own loop is measured in.</summary>
    /// <remarks>
    ///     ⚠ <b>Four, and they are <c>EditorHost</c>'s own four.</b> Its remarks name the loop as
    ///     "pump the platform's events into the document, run the layout and draw passes, turn the
    ///     draw lists into geometry, and record that geometry into a frame" — so a chart whose bars
    ///     are called something else would be a chart a reader has to translate. The fifth is this
    ///     application's own update, which is the one that is neither chrome nor GPU.
    /// </remarks>
    internal static class EditorKeys {
        /// <summary>The whole frame.</summary>
        public static readonly ProfilingKey Frame = ProfilingKey.Register("Editor.Frame");

        /// <summary>Pumping the platform's events into the document.</summary>
        public static readonly ProfilingKey Pump = ProfilingKey.Register("Editor.Pump");

        /// <summary>This application's own per-frame work.</summary>
        public static readonly ProfilingKey Update = ProfilingKey.Register("Editor.Update");

        /// <summary>Layout and draw over the element tree.</summary>
        public static readonly ProfilingKey Document = ProfilingKey.Register("Editor.Document");

        /// <summary>Turning draw lists into vertices.</summary>
        public static readonly ProfilingKey Geometry = ProfilingKey.Register("Editor.Geometry");

        /// <summary>Recording and submitting.</summary>
        public static readonly ProfilingKey Present = ProfilingKey.Register("Editor.Present");
    }
}

/// <summary>The scene the editor is showing, which is the prefab when one is being inspected.</summary>
/// <remarks>
///     ⚠ <b>A small adapter rather than <c>EditorApplication</c> implementing the interface.</b> The
///     application is several partials wide and implements nothing; a member called <c>Current</c> on
///     it would be one more name in a class that already has too many.
/// </remarks>
sealed class ShownScene(EditorApplication editor) : IActiveScene {
    /// <inheritdoc />
    public SceneDocument Current => editor.Shown;
}

/// <summary>The view the focused pane draws through.</summary>
/// <remarks>
///     ⚠ <b><see cref="ShownScene" />'s twin, and asked every time for the same reason.</b> The
///     focused pane changes as somebody clicks between four of them, so a panel handed a view at
///     activation would keep answering for whichever pane happened to have focus when it loaded.
/// </remarks>
sealed class ShownView(EditorApplication editor) : IActiveView {
    /// <inheritdoc />
    public RenderView? Current => editor.Viewport?.View;
}

