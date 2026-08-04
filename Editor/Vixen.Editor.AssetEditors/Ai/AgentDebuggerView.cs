// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai.Diagnostics;
using Vixen.Core;
using Vixen.Editor.Ai;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>
///     The agent debugger: which agents there are, what the selected one is doing, and why.
/// </summary>
/// <remarks>
///     <para>
///         doc 37 § Part 5 § Shared's agent inspector, and § P7's editor panels. Five lists over one
///         <see cref="AgentDebugModel" />: the agents, the four sections of its snapshot, the recorded
///         log, and whatever <see cref="AiDiagnosis" /> made of it.
///     </para>
///     <para>
///         ⚠ <b>It has no document and no command stack, because there is nothing here to edit.</b>
///         The one thing it changes is the breakpoint set, which is not part of any asset — a
///         breakpoint is a fact about a debugging session and putting it in the <c>.vxbt</c> would
///         commit it.
///     </para>
///     <para>
///         ⚠ <b>The findings are drawn above the log rather than beside it.</b> "This agent changed
///         action nine times in forty ticks" is the sentence somebody needs before they start reading
///         forty records, and a panel that made them find it themselves would be a log viewer with a
///         diagnosis feature nobody notices.
///     </para>
/// </remarks>
public sealed class AgentDebuggerView : Control {
    AgentDebugModel? model;

    /// <inheritdoc />
    protected override string TagName => "agent-debugger";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Every agent in the world, one row each.</summary>
    public UiElement Agents { get; private set; } = null!;

    /// <summary>What the selected agent is, in one line.</summary>
    public UiElement Header { get; private set; } = null!;

    /// <summary>What it is doing: the active path, the scored candidates, the plan.</summary>
    public UiElement Doing { get; private set; } = null!;

    /// <summary>Why.</summary>
    public UiElement Why { get; private set; } = null!;

    /// <summary>Its data: the blackboard, live.</summary>
    public UiElement Data { get; private set; } = null!;

    /// <summary>Its senses.</summary>
    public UiElement Senses { get; private set; } = null!;

    /// <summary>What is visibly wrong with it.</summary>
    public UiElement Findings { get; private set; } = null!;

    /// <summary>What it has decided lately, oldest first.</summary>
    public UiElement Log { get; private set; } = null!;

    /// <summary>Lets a stopped agent go.</summary>
    public Button Continue { get; private set; } = null!;

    /// <summary>Opens the asset the selected agent is running, scrolled to what it is doing.</summary>
    /// <remarks>
    ///     doc 37 § Part 5 § Shared. ⚠ <b>It reports what to open rather than opening it</b>, through
    ///     <see cref="Opening" /> — this panel knows an agent's <c>Symbol</c> and its active node, and
    ///     it does not know where the project keeps its files or which host owns the tab strip. A view
    ///     that reached for a document service would be a view that cannot be tested without one.
    /// </remarks>
    public Button Open { get; private set; } = null!;

    /// <summary>What to open, and where in it: the asset's name and the live node's index.</summary>
    public event Action<Symbol, int>? Opening;

    /// <summary>The model it is showing, or null.</summary>
    public AgentDebugModel? Model => model;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var body = Add("agent-debugger-body");
        var left = body.Add("agent-debugger-agents");

        left.Add("panel-title").Text = "Agents";
        Agents = left.Add("agent-list");

        var right = body.Add("agent-debugger-detail");

        Header = right.Add("agent-header");

        var toolbar = right.Add("agent-toolbar");

        Continue = toolbar.Add<Button>();
        Continue.Label = "Continue";

        Open = toolbar.Add<Button>();
        Open.Label = "Open asset";

        right.Add("panel-title").Text = "Doing";
        Doing = right.Add("agent-rows");

        right.Add("panel-title").Text = "Why";
        Why = right.Add("agent-rows");

        right.Add("panel-title").Text = "Data";
        Data = right.Add("agent-rows");

        right.Add("panel-title").Text = "Senses";
        Senses = right.Add("agent-rows");

        right.Add("panel-title").Text = "Findings";
        Findings = right.Add("agent-findings");

        right.Add("panel-title").Text = "Log";
        Log = right.Add("agent-log");
    }

    /// <summary>Shows a model, and follows it from then on.</summary>
    /// <param name="value">The model.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    public void Show(AgentDebugModel value) {
        ArgumentNullException.ThrowIfNull(value);

        model = value;

        Refresh();
    }

    /// <summary>
    ///     Says which asset the selected agent is running and where in it, for whoever owns the tabs.
    /// </summary>
    /// <returns>Whether there was anything to open.</returns>
    public bool OpenAsset() {
        if (model is null || !model.Snapshot.Asset.IsSome) {
            return false;
        }

        // ⚠ The *live* node, not the selected one: an author pressing this wants the tree scrolled to
        // what the agent is doing, which is the whole reason the button is on this panel rather than
        // in the asset browser.
        Opening?.Invoke(model.Snapshot.Asset, model.ActivePath.Count > 0 ? model.ActivePath[^1] : 0);

        return true;
    }

    /// <summary>Rebuilds every list from the model.</summary>
    public void Refresh() {
        if (model is null) {
            return;
        }

        Empty(Agents);
        Empty(Header);
        Empty(Doing);
        Empty(Why);
        Empty(Data);
        Empty(Senses);
        Empty(Findings);
        Empty(Log);

        foreach (var entity in model.Agents) {
            var row = Agents.Add("agent-row");

            row.Add("agent-name").Text = entity.ToString();

            if (entity == model.Selected) {
                row.Add("agent-mark").Text = "•";
            }
        }

        var snapshot = model.Snapshot;

        Header.Add("agent-planner").Text = snapshot.Planner.ToString();
        Header.Add("agent-asset").Text = snapshot.Asset.ToString();
        Header.Add("agent-action").Text = snapshot.Action.ToString();
        Header.Add("agent-status").Text = model.Halted ? "halted" : snapshot.Status.ToString();
        Header.Add("agent-origin").Text = model.Origin.ToString();

        if (snapshot.Reason.Length > 0) {
            Header.Add("agent-reason").Text = snapshot.Reason;
        }

        Rows(Doing, AiDebugSection.Doing);
        Rows(Why, AiDebugSection.Why);
        Rows(Data, AiDebugSection.Data);
        Rows(Senses, AiDebugSection.Senses);

        foreach (var finding in model.SelectedFindings()) {
            var row = Findings.Add("agent-finding");

            row.Add("agent-symptom").Text = finding.Symptom.ToString();
            row.Add("agent-detail").Text = finding.ToString();
        }

        foreach (var record in model.Log) {
            var row = Log.Add("agent-log-row");

            row.Add("agent-tick").Text = record.Tick.ToString(CultureInfo.InvariantCulture);
            row.Add("agent-name").Text = record.Action.ToString();
            row.Add("agent-status").Text = record.Status.ToString();
            row.Add("agent-detail").Text = record.Reason.IsSome ? record.Reason.ToString() : string.Empty;
        }
    }

    void Rows(UiElement into, AiDebugSection section) {
        if (model is null) {
            return;
        }

        foreach (var row in model.Section(section)) {
            var element = into.Add(row.Active ? "agent-row-live" : "agent-row");

            element.Add("agent-name").Text = row.Name;
            element.Add("agent-value").Text = row.Value;
        }
    }

    static void Empty(UiElement element) {
        while (element.Children.Count > 0) {
            element.Children[^1].Remove();
        }
    }
}
