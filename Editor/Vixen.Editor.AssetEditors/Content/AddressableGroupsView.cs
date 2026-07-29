// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Content;

/// <summary>The addressable groups a project defines, one group's policy, and what a build would say.</summary>
/// <remarks>
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
public sealed class AddressableGroupsView : Control {
    readonly List<AssetEntry> groups = [];

    EditorProject? project;
    Func<BuildPlan>? analyser;

    /// <inheritdoc />
    protected override string TagName => "group-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The groups the project defines.</summary>
    public TreeView Groups { get; private set; } = null!;

    /// <summary>The selected group's policy.</summary>
    public InspectorView Policy { get; private set; } = null!;

    /// <summary>The button that runs the analysis.</summary>
    public Button Analyse { get; private set; } = null!;

    /// <summary>What the analysis said, worst first.</summary>
    public UiElement Analysis { get; private set; } = null!;

    /// <summary>Shown when no group is selected.</summary>
    public EmptyState Empty { get; private set; } = null!;

    /// <summary>The document for the group being shown, or <see langword="null" />.</summary>
    public AddressableGroupDocument? Selected { get; private set; }

    /// <summary>Raised when a different group is selected.</summary>
    public event Action<AddressableGroupsView, AddressableGroupDocument>? GroupOpened;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Groups = Part<TreeView>();

        Groups.SelectionChanged += tree => {
            // One at a time: a policy editor over two groups would be a multi-object edit across two
            // files, which is a document-per-file model this does not have.
            if (tree.Selection.Count == 1 && tree.Selection.First().Tag is AssetEntry entry) {
                Open(entry);
            }
        };

        Empty = Part<EmptyState>();
        Empty.Title = "No group selected";
        Empty.Description = "A .vxgroup is a policy: how its assets are packed, compressed and shipped.";

        Policy = Part<InspectorView>();
        Policy.Search.SetStyle("display", "none");
        Policy.AddClass("hidden");

        Analyse = Part<Button>();
        Analyse.Label = "Analyse build";

        Analysis = Part("analysis-list");

        AddHandler<ClickEvent>(static (element, args) => ((AddressableGroupsView) element).Chosen(args));
    }

    /// <summary>Shows a project's groups.</summary>
    /// <param name="opened">The project.</param>
    /// <param name="analysis">What produces a plan, or <see langword="null" /> if nothing can.</param>
    public void Show(EditorProject opened, Func<BuildPlan>? analysis = null) {
        ArgumentNullException.ThrowIfNull(opened);

        project = opened;
        analyser = analysis;

        Analyse.Disabled = analysis is null;
        Rebuild();
    }

    /// <summary>Rebuilds the group list from the asset database as it stands.</summary>
    public void Rebuild() {
        while (Groups.Root.Children.Count > 0) {
            Groups.Root.Remove(Groups.Root.Children[^1]);
        }

        groups.Clear();

        if (project is not { } opened) {
            Groups.Refresh();
            return;
        }

        // Ordered by path so that two machines scanning one checkout show the same list — the same
        // rule the asset database applies to its own insertion order, and for the same reason.
        foreach (var entry in opened.Assets.Entries
                     .Where(asset => !asset.IsFolder
                         && string.Equals(
                             Path.GetExtension(asset.Path),
                             AddressableGroupDocument.Extension,
                             StringComparison.OrdinalIgnoreCase
                         ))
                     .OrderBy(asset => asset.Path, StringComparer.Ordinal)) {
            groups.Add(entry);
            Groups.Root.Add(Path.GetFileNameWithoutExtension(entry.Path), entry);
        }

        Groups.Refresh();
    }

    /// <summary>How many groups the project defines.</summary>
    public int Count => groups.Count;

    /// <summary>Opens one group's policy.</summary>
    /// <param name="entry">Which group.</param>
    /// <returns>The document editing it.</returns>
    public AddressableGroupDocument Open(AssetEntry entry) {
        var opened = project ?? throw new InvalidOperationException("No project is being shown.");

        // Through the project's own document list, so opening the same group twice is one document
        // with one undo history rather than two views racing to save the same file.
        if (!opened.TryGetDocument(entry.Guid, out var existing)
            || existing is not AddressableGroupDocument document) {
            document = new(opened, entry.Guid, opened.Paths.Absolute(entry.Path));
        }

        Selected = document;

        Policy.RemoveClass("hidden");
        Empty.AddClass("hidden");

        Policy.EditedDocument = document;
        Policy.Inspect(document.Policy);

        GroupOpened?.Invoke(this, document);
        return document;
    }

    /// <summary>Runs the analysis and lists what it said.</summary>
    /// <returns>How many errors it found, or -1 when nothing can analyse.</returns>
    public int Run() {
        while (Analysis.Children.Count > 0) {
            Analysis.Children[^1].Remove();
        }

        if (analyser is not { } produce) {
            Row("build", "Nothing here can run an analysis: the host has not supplied a planner.", error: false);
            return -1;
        }

        var plan = produce();
        var errors = 0;

        foreach (var diagnostic in plan.Diagnostics) {
            var isError = diagnostic.Severity == ImportSeverity.Error;

            if (isError) {
                errors++;
            }

            Row(diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Message, isError);
        }

        // Said even when there is nothing wrong, because a panel that emptied itself on success is
        // one nobody can tell apart from a panel that did not run.
        Row(
            "plan",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{plan.Assets.Length} addressable assets across {plan.Groups.Length} groups."
            ),
            error: false
        );

        return errors;
    }

    /// <summary>How many rows the analysis produced.</summary>
    public int AnalysisRows => Analysis.Children.Count;

    void Row(string stage, string message, bool error) {
        var row = Analysis.Add("analysis-row", null, error ? "error" : "warning");

        row.Add("analysis-stage").Text = stage;
        row.Add("analysis-message").Text = message;
    }

    void Chosen(ClickEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, Analyse)) {
                Run();
                args.Handled = true;

                return;
            }
        }
    }
}

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
