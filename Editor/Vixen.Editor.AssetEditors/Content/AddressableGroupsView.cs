// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Ui;

namespace Vixen.Editor.AssetEditors.Content;

/// <summary>The addressable groups a project defines, one group's policy, and what a build would say.</summary>
/// <remarks>
///     <para>
///         <b>The panel is <c>AddressableGroupsView.vxml</c>; this file exists only to make it public
///         and sealed</b> (#89). The markup compiler emits a partial class with no accessibility
///         modifier, which is <c>internal</c>, and this one is constructed from the application and
///         read by two test suites. The file header there carries the port's model decision.
///     </para>
///     <para>
///         Doc 11's three things for this editor: the group list, the per-group policy, and doc 08's
///         analysis view. The first two are this control's; the third is deliberately not.
///     </para>
///     <para>
///         ⚠ <b>The analysis is the real planner, run by the host.</b> <see cref="BuildPlanner" />
///         needs the import cache — an analysis that reimplemented its rules would be a second set of
///         rules, and the way that drift shows up is a panel saying a project is fine and the build
///         refusing it. So the panel takes a delegate that produces a <see cref="BuildPlan" />, and
///         the application hands it one that runs against a <c>ProjectWorkspace</c> on a background
///         task. A panel with no analyser shows the list and says analysis is unavailable.
///     </para>
///     <para>
///         ⚠ <b>The list is the project's <c>.vxgroup</c> files, not the groups a build invented.</b>
///         A project that configures nothing still builds, in a <c>Default</c> group the planner
///         reports; that group has no file and so is not in this list, and the analysis is where it
///         appears. Showing it here would offer a policy editor for a group that does not exist.
///     </para>
/// </remarks>
public sealed partial class AddressableGroupsView;

/// <summary>Opens an addressable group.</summary>
/// <remarks>
///     ⚠ <b>The view is the whole project's group list, not just this file's.</b> Editing one group's
///     compression in isolation is not the question anybody has — "which of my groups is remote" and
///     "what would a build say about this" are — so double-clicking one <c>.vxgroup</c> opens the
///     list with that group selected. The document is still one file's, which is what keeps Ctrl+S
///     meaning something specific.
/// </remarks>
public sealed class AddressableGroupEditorFactory : IAssetEditorFactory {
    /// <summary>What produces a build plan for the analysis, or <see langword="null" />.</summary>
    /// <remarks>
    ///     Settable rather than a constructor argument, because the application wires it once it has
    ///     a workspace — which is later than the moment the editors are registered.
    /// </remarks>
    public Func<BuildPlan>? Analyser { get; set; }

    /// <inheritdoc />
    public string Name => "Addressable Group";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [AddressableGroupDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return new AddressableGroupDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<AddressableGroupsView>();
        view.Show(document.Project, Analyser);

        if (document.Project.Assets.TryGetByGuid(document.Asset, out var entry)) {
            view.Open(entry);
        }

        return view;
    }
}
