// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>A GOAP domain: the tables that are authored, and the graph that is not.</summary>
/// <remarks>
///     <para>
///         doc 37 § Part 5's GOAP editor. Goals, actions and world keys are <b>tables</b>; conditions
///         and effects are rows on an action. The graph beside them is <b>derived and read-only</b>,
///         and it has no command stack — which <c>NodeGraphView</c> already supports, since <i>"no
///         stack means read-only"</i>.
///     </para>
///     <para>
///         The panel is <c>GoapDomainView.vxml</c>; this file is the accessibility modifier, the
///         three records its tables key on, and the cells that exist only so that markup can write an
///         intrinsic tag's own <c>Text</c>.
///     </para>
///     <para>
///         ⚠ <b>This is where "the node editor is mandatory" gets an honest answer.</b> crashkonijn
///         ships a GraphViewer rather than a graph editor, and that is right: the edges are computed
///         from which effects satisfy which conditions, so drawing them by hand would be authoring the
///         same fact twice and the two copies would disagree the first time somebody edited a
///         condition.
///     </para>
/// </remarks>
public sealed partial class GoapDomainView;

/// <summary>One goal's row, as the <c>@for</c> keys it.</summary>
/// <param name="Slot">Where it is in the document's goal list.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Priority">How badly it is wanted.</param>
/// <param name="Conditions">What has to hold for it to be met, as a person reads it.</param>
/// <remarks>
///     ⚠ <b>The whole record is the key</b> — the immutable-data half of the <c>@for</c> rule.
///     Nothing here is signal-backed, so a re-prioritised goal has to be a different key or the row
///     would keep the first number. The slot is in it because <c>BuildContext.For</c> cannot
///     reconcile two equal keys in one loop and nothing stops a domain naming two goals alike.
/// </remarks>
internal readonly record struct GoapGoalRow(int Slot, string Name, string Priority, string Conditions);

/// <summary>One action's row.</summary>
/// <param name="Slot">Where it is in the document's action list.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Task">Which task runs it.</param>
/// <param name="Cost">What the search pays for it.</param>
/// <param name="Conditions">What has to hold before it may run.</param>
/// <param name="Effects">What it changes, and in which direction.</param>
/// <inheritdoc cref="GoapGoalRow" path="/remarks" />
internal readonly record struct GoapActionRow(
    int Slot,
    string Name,
    string Task,
    string Cost,
    string Conditions,
    string Effects
);

/// <summary>One world key's row.</summary>
/// <param name="Slot">Where it is in the document's key list.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Source">Whether it is a constant, a sensor or a blackboard reading.</param>
/// <param name="Detail">Its value, or where it comes from.</param>
/// <remarks>
///     ⚠ Equal rows are the ordinary case here rather than the hypothetical one: a constant key
///     prints its own number and nothing stops two constants being <c>0</c>. Hence the slot.
/// </remarks>
internal readonly record struct GoapKeyRow(int Slot, string Name, string Source, string Detail);

/// <summary>
///     ⚠ The cells that exist only so that markup can set an intrinsic tag's own <c>Text</c>.
/// </summary>
/// <remarks>
///     The panel ledger's shape 5 and its sanctioned escape; <see cref="FactName" /> carries the full
///     argument.
/// </remarks>
internal sealed class GoapName : UiElement {
    /// <inheritdoc />
    protected override string TagName => "goap-name";
}

/// <inheritdoc cref="GoapName" />
internal sealed class GoapPriority : UiElement {
    /// <inheritdoc />
    protected override string TagName => "goap-priority";
}

/// <inheritdoc cref="GoapName" />
internal sealed class GoapDetail : UiElement {
    /// <inheritdoc />
    protected override string TagName => "goap-detail";
}

/// <inheritdoc cref="GoapName" />
internal sealed class GoapTask : UiElement {
    /// <inheritdoc />
    protected override string TagName => "goap-task";
}

/// <inheritdoc cref="GoapName" />
internal sealed class GoapCost : UiElement {
    /// <inheritdoc />
    protected override string TagName => "goap-cost";
}

/// <inheritdoc cref="GoapName" />
internal sealed class GoapEffects : UiElement {
    /// <inheritdoc />
    protected override string TagName => "goap-effects";
}

/// <inheritdoc cref="GoapName" />
internal sealed class GoapSource : UiElement {
    /// <inheritdoc />
    protected override string TagName => "goap-source";
}

/// <summary>Opens a <c>.vxgoap</c>.</summary>
public sealed class GoapDomainEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "GOAP Domain";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [GoapDomainDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new GoapDomainDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<GoapDomainView>();

        view.Show((GoapDomainDocument) document);

        return view;
    }
}
