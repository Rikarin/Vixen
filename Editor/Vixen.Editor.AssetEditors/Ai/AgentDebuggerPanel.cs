// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Vixen.Ecs;
using Vixen.Editor.Ai;
using Vixen.Editor.AssetEditors.Ai;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Ui;

namespace Vixen.Editor.AssetEditors;

/// <summary>Doc 37 § P7's panel, registered — the half of that phase that was built and unreachable.</summary>
/// <remarks>
///     <para>
///         <b>P7 shipped a view, a model, six tests and no way to open any of it.</b>
///         <see cref="AgentDebuggerView" /> was never handed to <c>Shell.RegisterPanel</c>,
///         <see cref="AgentDebugModel" /> was constructed only by this assembly's own tests, and
///         <c>docs/overview.md</c> recorded the whole row as <i>"none of it is reachable"</i>. Every
///         exit criterion of that phase was met as a test, which is exactly the shape of defect a
///         test suite cannot see: nothing was broken, and nobody could use it.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in the editor's application, because the view is in this assembly
///         and this module already exists to be its last mile.</b> The four AI asset editors are
///         registered by <see cref="StandardEditors" /> and reached from the application; the
///         debugger has no document and no extension, so a factory registry has no seat for it. A
///         module that already owns a <see cref="PluginContext" /> does — and
///         <see cref="PluginContext.AddPanel(string, StringId, Action{Vixen.Editor.Ui.Docking.DockPanel})" />
///         unregisters it again on unload, which <c>Shell.RegisterPanel</c> would not.
///     </para>
///     <para>
///         ⚠ <b>The model is a field and the view is not.</b> A panel's factory runs again every time
///         it is reopened, so a model built inside the lambda would throw away the breakpoints and
///         the selected agent every time somebody closed the tab. The view is the opposite: it
///         belongs to the panel that built it, and <see cref="PanelDescriptor.Closed" /> drops it so
///         a stale control is never refreshed.
///     </para>
///     <para>
///         ⚠ <b>Live needs an <see cref="AiSystem" /> and the editor does not have one.</b>
///         <see cref="AgentDebugModel.Refresh" /> photographs a system in this process, and nothing
///         in the editor owns one — there is no play mode that steps agents. So the panel looks for
///         one in <see cref="PluginServices" /> and shows an empty model when there is none, which is
///         the honest reading of doc 20's first bar: a verb that is not implemented is
///         <i>visibly</i> not implemented rather than absent. A host that does step agents — a game
///         embedding the editor, or a play mode when there is one — publishes its system with
///         <c>Services.Add(system)</c> and the panel goes live with no further wiring.
///     </para>
/// </remarks>
public sealed partial class AssetEditorsModule {
    /// <summary>What the agent debugger is registered as.</summary>
    public const string AgentDebuggerPanelId = "ai-debugger";

    /// <summary>
    ///     The one model, kept across every open and close of the panel — see this class's remarks.
    /// </summary>
    readonly AgentDebugModel agentModel = new();

    AgentDebuggerView? agentDebugger;

    PluginContext? agentContext;

    /// <summary>The model the panel shows, so a test can assert what it was fed.</summary>
    internal AgentDebugModel AgentModel => agentModel;

    /// <summary>Registers the panel and keeps it fed.</summary>
    /// <param name="context">The module's context.</param>
    void AgentDebuggerPanel(PluginContext context) {
        // ⚠ Before AddPanel and not after: the factory calls Live(), and a panel restored from a
        // saved layout is built during registration rather than on a later click. Assigning this
        // afterwards would make the panel dead exactly in the case where somebody had left it open.
        agentContext = context;

        context.AddPanel(
            new PanelDescriptor(
                AgentDebuggerPanelId,
                new StringId("editor.panel.ai-debugger", "Agent Debugger"),
                panel => {
                    agentDebugger = panel.Add<AgentDebuggerView>();
                    agentDebugger.Show(agentModel);

                    // ⚠ Both buttons were built by OnCreated and wired to nothing, which is the
                    // second half of "unreachable": a panel nobody registered is also a panel whose
                    // controls nobody ever connected. Continue lets a halted agent go, and Open
                    // reports which asset to show — the view raises Opening rather than opening
                    // anything, because it knows an agent's Symbol and not where a project keeps
                    // its files.
                    agentDebugger.Continue.Clicked += _ => Resume();
                    agentDebugger.Open.Clicked += _ => agentDebugger?.OpenAsset();

                    Live();
                }
            ) {
                Closed = () => agentDebugger = null
            }
        );

        // Only while the panel is open: Refresh installs the breakpoint set and turns on a tree's
        // trace, and both are costs that belong to somebody looking rather than to every editor
        // session.
        context.OnUpdate(_ => Live());
    }

    /// <summary>Re-photographs the running system, if this editor has one.</summary>
    void Live() {
        if (agentDebugger is null) {
            return;
        }

        if (TryLive(out var system, out var world)) {
            agentModel.Refresh(system, world);
        }

        agentDebugger.Refresh();
    }

    /// <summary>Lets the selected agent go, when something is stepping it.</summary>
    void Resume() {
        if (TryLive(out var system, out var world)) {
            agentModel.Resume(system, world);
        }

        agentDebugger?.Refresh();
    }

    /// <summary>The running system and the world it steps, when both are there.</summary>
    /// <remarks>
    ///     ⚠ <b>The system by its own type rather than behind an interface invented for it.</b>
    ///     <see cref="PluginServices" /> is keyed on the type a host published, and
    ///     <see cref="AiSystem" /> is already the public thing a game holds — a one-implementation
    ///     interface here would be a seam nobody has checked is a seam, which is the failure doc 37 §
    ///     Part 4 exists to refuse.
    /// </remarks>
    bool TryLive(out AiSystem system, out World world) {
        system = null!;
        world = null!;

        if (agentContext is null || !agentContext.Services.TryGet<AiSystem>(out var found)) {
            return false;
        }

        if (!agentContext.Services.TryGet<IActiveScene>(out var scene)) {
            return false;
        }

        system = found;
        world = scene.Current.World;

        return true;
    }
}
