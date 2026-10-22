// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.HotReload;

namespace Vixen.Editor.App;

/// <summary>Doc 20 § B7's verbs — Revert, Show History and the revisions panel — as a module over <see cref="ISourceControl" />.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50's source-control context (#1505).</b> The revisions panel, <c>assets.history</c> and
///         <c>assets.revert</c> were the application's, over a provider it found privately and only once the
///         content browser had been opened. They come through the <see cref="PluginContext" /> a third party
///         is handed now, over the service a third party can <c>Require</c>, and leave the editor with the
///         module when it is switched off.
///     </para>
///     <para>
///         ⚠ <b>Each verb asks first, and none can be undone</b> — as before. Both write files on disk and
///         the undo stack holds documents. The asking is here, the writing is the service's, which asks
///         nobody.
///     </para>
///     <para>
///         ⚠ <b>The status column stays the browser's.</b> The content browser is the application's panel,
///         and it reads <see cref="ISourceControl.Status" /> and redraws on <see cref="ISourceControl.Swept" />;
///         that binding is a reader of the service, not a verb, so it did not move here. Nor did the Assets
///         menu's layout, which names these two lines and skips them when this module is off.
///     </para>
/// </remarks>
sealed class SourceControlModule : IEditorPlugin {
    /// <summary>What the host activates it under.</summary>
    public const string ModuleId = "vixen.source-control";

    /// <summary>What the plugin manager calls it.</summary>
    public const string ModuleName = "Source Control";

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        var sourceControl = context.Services.Require<ISourceControl>();
        var project = context.Services.Require<EditorProject>();
        var selection = project.Selection;

        var revisions = context.Publish(
            new AssetRevisions(context.Shell, sourceControl, project, context.Services.Require<HotReloadHost>())
        );

        context.AddPanel(revisions.Descriptor());
        context.OnUpdate(_ => revisions.Update());

        // ⚠ Enabled by a *provider* rather than by a preference: an editor opened on a project that is not
        // in a working tree greys these. `IsKnown` rather than "is there a provider", because the detection
        // happens on the pool during the first sweep — a line that lit up only after the answer arrived is
        // honest, and one that lit up before it would offer a verb with nothing behind it.
        context.AddCommand(
            new EditorCommand(
                "assets.revert",
                EditorStrings.CommandAssetsRevert,
                () => Revert(context.Shell, sourceControl, project)
            ) {
                Category = EditorStrings.CategoryAssets,
                Enablement = () => sourceControl.IsKnown && selection.Count > 0
            }
        );

        // ⚠ The third and fourth verbs arrive as one line because they are one panel: "Diff" is what the
        // panel draws under whichever row is picked. One asset, because the panel has a Restore button.
        context.AddCommand(
            new EditorCommand("assets.history", EditorStrings.CommandAssetsHistory, revisions.Open) {
                Category = EditorStrings.CategoryAssets,
                Enablement = () => sourceControl.IsKnown && selection.Count == 1
            }
        );
    }

    /// <summary>Throws away the changes to whatever is selected, after asking.</summary>
    /// <param name="shell">Where the question is asked.</param>
    /// <param name="sourceControl">What reverts.</param>
    /// <param name="project">Whose selection.</param>
    /// <remarks>
    ///     ⚠ <b>Asked first, and the question names the files rather than counting them.</b> This is the one
    ///     verb in the editor that destroys work source control has not been told about, and it cannot be
    ///     undone by the undo stack — the file on disk is what changes, and the stack holds documents.
    /// </remarks>
    static void Revert(EditorShell shell, ISourceControl sourceControl, EditorProject project) {
        if (sourceControl.Name is not { } name || project.Selection.Count == 0) {
            return;
        }

        List<string> paths = [];

        foreach (var asset in project.Selection) {
            if (project.Assets.TryGetByGuid(asset, out var entry) && !entry.IsFolder) {
                paths.Add(entry.Path);
            }
        }

        if (paths.Count == 0) {
            return;
        }

        var names = string.Join(Environment.NewLine, paths.Take(5));

        _ = Ask();

        async Task Ask() {
            var asked = await shell.Dialogs.ConfirmAsync(
                    paths.Count == 1 ? $"Revert '{Path.GetFileName(paths[0])}'?" : $"Revert {paths.Count} files?",
                    $"{name} will put them back as they were committed. This cannot be undone."
                    + Environment.NewLine
                    + names
                    + (paths.Count > 5 ? Environment.NewLine + $"…and {paths.Count - 5} more" : string.Empty),
                    "Revert",
                    danger: true
                )
                .ConfigureAwait(true);

            if (asked) {
                sourceControl.Revert(paths);
            }
        }
    }
}
