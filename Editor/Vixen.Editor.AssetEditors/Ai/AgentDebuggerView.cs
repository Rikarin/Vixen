// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>
///     The agent debugger: which agents there are, what the selected one is doing, and why.
/// </summary>
/// <remarks>
///     <para>
///         doc 37 § Part 5 § Shared's agent inspector, and § P7's editor panels. Five lists over one
///         <see cref="AgentDebugModel" />: the agents, the four sections of its snapshot, the recorded
///         log, and whatever <see cref="Vixen.Ai.Diagnostics.AiDiagnosis" /> made of it.
///     </para>
///     <para>
///         The panel is <c>AgentDebuggerView.vxml</c>; this file is the accessibility modifier, the
///         five records its lists key on, and the cells that exist only so that markup can write an
///         intrinsic tag's own <c>Text</c>.
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
public sealed partial class AgentDebuggerView;

/// <summary>One agent's row in the left-hand list.</summary>
/// <param name="Slot">Where it is in the world's order.</param>
/// <param name="Name">The entity, as it prints itself.</param>
/// <param name="Selected">Whether this is the one the detail pane is about.</param>
/// <remarks>
///     ⚠ <b><see cref="Selected" /> is in the key because it decides whether the row has a second
///     child.</b> The bullet is an element that is there or is not, so a surviving key would keep the
///     bullet on whichever agent was selected when the row first appeared.
/// </remarks>
internal readonly record struct AgentListRow(int Slot, string Name, bool Selected);

/// <summary>The one-line header, as one value so that the whole of it changes together.</summary>
/// <param name="Planner">Utility set, behaviour tree or GOAP.</param>
/// <param name="Asset">Which asset it is running.</param>
/// <param name="Action">What it is doing right now.</param>
/// <param name="Status">Running, succeeded, failed — or that a breakpoint has halted it.</param>
/// <param name="Origin">Where the decision came from.</param>
/// <param name="HasReason">Whether the snapshot carries a reason at all.</param>
/// <param name="Reason">And what it says.</param>
/// <remarks>
///     ⚠ <b>One record rather than six signals, because the six are one photograph.</b>
///     <c>AgentDebugModel.Refresh</c> re-photographs the running system in one go, and six signals
///     would have been six flushes of a header that is only ever written together.
/// </remarks>
internal readonly record struct AgentHeadRow(
    string Planner,
    string Asset,
    string Action,
    string Status,
    string Origin,
    bool HasReason,
    string Reason
);

/// <summary>One row of one of the four snapshot sections.</summary>
/// <param name="Slot">Where it is in that section.</param>
/// <param name="Active">Whether it is the live one, which is a different tag.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Value">And what it reads.</param>
/// <remarks>
///     ⚠ <b><see cref="Active" /> is in the key and that is the whole of this panel's <c>@for</c>
///     risk.</b> <c>agent-row-live</c> is a *tag*, not a class, so the choice is an <c>@if</c> inside
///     the loop body and a surviving key would never re-run it. On a flapping agent — the case the
///     debugger exists for — the highlight would sit on whichever candidate was live on the frame the
///     panel opened, while every value beside it updated correctly.
/// </remarks>
internal readonly record struct AgentSectionRow(int Slot, bool Active, string Name, string Value);

/// <summary>One thing <c>AiDiagnosis</c> found.</summary>
/// <param name="Slot">Where it is in the diagnosis's order.</param>
/// <param name="Symptom">Which symptom it is.</param>
/// <param name="Detail">The finding, as it prints itself.</param>
internal readonly record struct AgentFindingRow(int Slot, string Symptom, string Detail);

/// <summary>One recorded decision.</summary>
/// <param name="Slot">Where it is in the log, oldest first.</param>
/// <param name="Tick">Which tick it was made on.</param>
/// <param name="Action">What was chosen.</param>
/// <param name="Status">How it went.</param>
/// <param name="Reason">Why, when the record carries one.</param>
/// <remarks>
///     ⚠ <b>The slot earns its place here more than anywhere else in the wave.</b> A stable agent
///     records the same action with the same status on tick after tick, so equal rows are the
///     <i>normal</i> content of this list rather than a corner case, and <c>BuildContext.For</c>
///     cannot reconcile two equal keys in one loop.
/// </remarks>
internal readonly record struct AgentLogRow(int Slot, string Tick, string Action, string Status, string Reason);

/// <summary>
///     ⚠ The cells that exist only so that markup can set an intrinsic tag's own <c>Text</c>.
/// </summary>
/// <remarks>
///     The panel ledger's shape 5 and its sanctioned escape; <see cref="FactName" /> carries the full
///     argument. <c>agent-name</c>, <c>agent-status</c> and <c>agent-detail</c> each appear in two
///     different rows, which is why they are declared once here rather than per list.
/// </remarks>
internal sealed class AgentName : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-name";
}

/// <inheritdoc cref="AgentName" />
internal sealed class AgentMark : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-mark";
}

/// <inheritdoc cref="AgentName" />
internal sealed class AgentValue : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-value";
}

/// <inheritdoc cref="AgentName" />
internal sealed class AgentDetail : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-detail";
}

/// <inheritdoc cref="AgentName" />
internal sealed class AgentPlanner : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-planner";
}

/// <inheritdoc cref="AgentName" />
internal sealed class AgentAsset : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-asset";
}

/// <inheritdoc cref="AgentName" />
internal sealed class AgentAction : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-action";
}

/// <inheritdoc cref="AgentName" />
internal sealed class AgentStatus : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-status";
}

/// <inheritdoc cref="AgentName" />
internal sealed class AgentOrigin : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-origin";
}

/// <inheritdoc cref="AgentName" />
internal sealed class AgentReason : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-reason";
}

/// <inheritdoc cref="AgentName" />
internal sealed class AgentSymptom : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-symptom";
}

/// <inheritdoc cref="AgentName" />
internal sealed class AgentTick : UiElement {
    /// <inheritdoc />
    protected override string TagName => "agent-tick";
}
