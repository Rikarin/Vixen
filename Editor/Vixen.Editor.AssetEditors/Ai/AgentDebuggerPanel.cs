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
///         in the editor owns one. So the panel looks for one in <see cref="PluginServices" /> and
///         shows an empty model when there is none, which is the honest reading of doc 20's first
///         bar: a verb that is not implemented is <i>visibly</i> not implemented rather than absent.
///         A host that does step agents — a game embedding the editor — publishes its system with
///         <c>Services.Add(system)</c> and the panel goes live with no further wiring.
///     </para>
///     <para>
///         ⚠ <b>This used to add "there is no play mode that steps agents", and that is false.</b>
///         <c>PlayModeController</c> steps a real <c>EngineLoop</c>, and <c>IPlaySystems</c> is the
///         declared seam for adding a system that needs a service the loop cannot invent —
///         <c>PlayPhysics</c> is the worked example. What stops a <c>PlayAi</c> contribution is
///         lifetime rather than scheduling: an <c>AiSystem</c> created on Play dies on Stop, and
///         <see cref="PluginServices" /> has no removal, which is precisely why <c>PlayPhysics</c>
///         calls <c>session.Provide</c> instead. Nothing here can see a play session.
///         <see href="https://github.com/Rikarin/Vixen/issues/470">#470</see> carries that decision.
///     </para>
/// </remarks>
public sealed partial class AssetEditorsModule {
    /// <summary>What the agent debugger is registered as.</summary>
    public const string AgentDebuggerPanelId = "ai-debugger";

    /// <summary>
    ///     The one model, kept across every open and close of the panel — see this class's remarks.
    /// </summary>
    readonly AgentDebugModel agentModel = new();

    /// <summary>The asset editors following that model, so the tinting keeps up with the agent.</summary>
    /// <remarks>
    ///     ⚠ <b>A list rather than one view, because the same model tints three different editors and
    ///     a project may have two of them open at once.</b> Entries are dropped when the element is
    ///     removed from its document — <see cref="UiElement.IsRemoved" /> is the only honest test,
    ///     because a docked panel is torn down by the workspace and tells nobody here.
    /// </remarks>
    readonly List<UiElement> agentFollowers = [];

    AgentDebuggerView? agentDebugger;

    PluginContext? agentContext;

    /// <summary>What the followed GOAP graph was last projected for, so it is rebuilt only on a change.</summary>
    int agentPlanState;

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

    /// <summary>Points a freshly-opened AI asset editor at the debugger's model.</summary>
    /// <param name="view">Whatever an asset editor's factory built.</param>
    /// <returns>Whether it was one of the three that can follow an agent.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The three <c>Follow</c> methods had no non-test caller.</b> Each one was built,
    ///         tested and finished, and the model they take is held by this module — so the canvas
    ///         tinting doc 37 § Part 5 asks for existed on both sides and was joined by nothing. The
    ///         seam is <c>EditorApplication.Joined</c>, which is the one place that sees every
    ///         newly-built asset-editor view: this assembly has the views and the model and no
    ///         panels, and that assembly has the panels and no idea what an agent is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The model rather than a copy of it, and it is the same instance the panel shows.</b>
    ///         A breakpoint set on the canvas has to be the one the debugger installs into the running
    ///         system, and a second model would be a second breakpoint set that nothing reads.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No planner is passed to <see cref="GoapDomainView.Follow" />, because none can
    ///         be.</b> The running planners live inside <c>GoapPlanQueue</c>'s private array and
    ///         nothing exposes one, so the rejection trace that parameter installs is unreachable from
    ///         the editor — the plan, the goal and the world keys all still arrive, because those come
    ///         off the model.
    ///     </para>
    /// </remarks>
    public bool Follow(UiElement view) {
        ArgumentNullException.ThrowIfNull(view);

        switch (view) {
            case BehaviorTreeView tree:
                tree.Follow(agentModel);

                break;

            case UtilitySetView utility:
                utility.Follow(agentModel);

                break;

            case GoapDomainView goap:
                goap.Follow(agentModel);

                break;

            default:
                return false;
        }

        agentFollowers.Add(view);

        return true;
    }

    /// <summary>Re-photographs the running system, if this editor has one.</summary>
    /// <remarks>
    ///     ⚠ <b>A followed canvas counts as somebody looking.</b> The refresh is gated on the panel
    ///     being open because installing the breakpoint set and turning a tree's trace on are costs
    ///     that belong to a person watching — and an open behaviour-tree editor tinted by a running
    ///     agent is exactly such a person, so it keeps the model warm on its own.
    /// </remarks>
    void Live() {
        Sweep();

        if (agentDebugger is null && agentFollowers.Count == 0) {
            return;
        }

        if (TryLive(out var system, out var world)) {
            agentModel.Refresh(system, world);
        }

        agentDebugger?.Refresh();

        Retint();
    }

    /// <summary>Drops the followers whose panel has been closed.</summary>
    void Sweep() {
        for (var index = agentFollowers.Count - 1; index >= 0; index--) {
            if (agentFollowers[index].IsRemoved) {
                agentFollowers.RemoveAt(index);
            }
        }
    }

    /// <summary>Re-tints every editor following the model.</summary>
    /// <remarks>
    ///     ⚠ <b>The GOAP graph is rebuilt only when what it draws has changed, and the other two are
    ///     refreshed every frame.</b> <see cref="BehaviorTreeView.RefreshLive" /> writes an accent per
    ///     box and <see cref="UtilitySetView.Refresh" /> writes signals whose values compare equal
    ///     when nothing moved; <see cref="GoapDomainView.Compile" /> compiles the domain and
    ///     <i>replaces</i> the canvas's graph, which drops the selection and reallocates every node.
    ///     Doing that per frame would make a domain impossible to click on.
    /// </remarks>
    void Retint() {
        var plan = PlanState();
        var replanned = plan != agentPlanState;

        agentPlanState = plan;

        foreach (var view in agentFollowers) {
            switch (view) {
                case BehaviorTreeView tree:
                    tree.RefreshLive();

                    break;

                case UtilitySetView utility:
                    utility.Refresh();

                    break;

                case GoapDomainView goap when replanned:
                    goap.Compile();

                    break;
            }
        }
    }

    /// <summary>Everything the GOAP projection reads off the model, as one number.</summary>
    /// <remarks>
    ///     ⚠ <b>The world keys are in it as well as the plan.</b> A node's condition state is drawn
    ///     from the projected world, so an agent whose plan has not changed can still be drawing a
    ///     condition that has — and a fingerprint over the plan alone would freeze the picture at the
    ///     moment the plan was made, which is the half of it that looks most convincingly live.
    /// </remarks>
    int PlanState() {
        var state = new HashCode();
        var plan = agentModel.Plan;

        state.Add(plan?.Goal ?? default);
        state.Add(plan?.GoalIndex ?? -1);
        state.Add(plan?.Failure ?? default);
        state.Add(plan?.Count ?? 0);
        state.Add(plan?.Head ?? -1);

        foreach (var key in agentModel.WorldKeys) {
            state.Add(key);
        }

        return state.ToHashCode();
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
