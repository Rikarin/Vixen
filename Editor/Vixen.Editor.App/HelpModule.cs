// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>Doc 20's Help menu, as a module over <see cref="IExternalLinks" />.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D3: the application's own verbs go through the front door</b> (#1296). These
///         were <c>EditorParity.HelpCommands</c> and a line of <c>EditorApplication.Commands</c>,
///         registered straight into the shell over a private <c>Browse</c>; they are registered
///         through the <see cref="PluginContext" /> a third party is handed now, and what they need
///         of the host — a link opener and the data directory — is what a third party would be handed.
///     </para>
///     <para>
///         ⚠ <b>Registered by hand, not by <c>[EditorMenu]</c>, for the reason <c>PlayModule</c>
///         gives.</b> The shell's own Help menu names these ids (<c>EditorShell</c>'s default bar),
///         and a menu line naming a command nobody registered is skipped — so switching this module
///         off in the plugin manager takes the six lines off the menu with it.
///     </para>
/// </remarks>
sealed class HelpModule : IEditorPlugin {
    /// <summary>What the host activates it under.</summary>
    public const string ModuleId = "vixen.help";

    /// <summary>What the plugin manager calls it.</summary>
    public const string ModuleName = "Help";

    /// <summary>Where the manual lives, until there is a documentation site to point at.</summary>
    const string DocumentationUrl = "https://github.com/Rikarin/Vixen/tree/master/docs";

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var links = context.Services.Require<IExternalLinks>();
        var data = context.Services.Require<EditorDataDirectory>();
        var shell = context.Shell;

        // The fifth of doc 20's dangling ids, and the shell has named it since it was written.
        Link(context, links, "help.documentation", EditorStrings.CommandDocumentation, DocumentationUrl);
        Link(context, links, "help.api-reference", EditorStrings.CommandHelpApiReference, DocumentationUrl + "/README.md");
        Link(context, links, "help.release-notes", EditorStrings.CommandHelpReleaseNotes, DocumentationUrl + "/14-roadmap.md");
        Link(context, links, "help.report-bug", EditorStrings.CommandHelpReportBug, "https://github.com/Rikarin/Vixen/issues/new");
        Link(context, links, "help.show-log-folder", EditorStrings.CommandHelpShowLogFolder, new Uri(data.Path).AbsoluteUri);

        context.AddCommand(
            new EditorCommand(
                "help.about",
                EditorStrings.CommandAbout,
                () => shell.Notifications.Show(
                    "Vixen Editor",
                    NotificationSeverity.Info,
                    typeof(EditorApplication).Assembly.GetName().Version?.ToString() ?? "development build"
                )
            ) {
                Category = EditorStrings.CategoryHelp
            }
        );
    }

    static void Link(PluginContext context, IExternalLinks links, string id, StringId title, string url) =>
        context.AddCommand(
            new EditorCommand(id, title, () => links.Open(url)) {
                Category = EditorStrings.CategoryHelp,
                Enablement = () => links.CanOpen
            }
        );
}
