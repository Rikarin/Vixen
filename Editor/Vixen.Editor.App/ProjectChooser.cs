// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Platform;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.App;

/// <summary>Doc 20's A2: "which project" asked before anything else, and answered without a restart.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20 files New Project and Open Project as declared-and-disabled because "swapping a
///         project underneath a live editor" is a world, a scene, an asset database and every open
///         document.</b> That is true, and the way out is not to swap any of them: the editor is
///         <i>rebuilt</i> over the new root, in the same process and the same window, which is
///         exactly what happens today between two launches and is therefore the one path already
///         proven by every restart the test harness makes.
///     </para>
///     <para>
///         ⚠ <b>So this sets a pending root and asks to close</b>, through
///         <see cref="IProjectSession.RequestProject" />. <c>EditorHost.Run</c> returns, <c>Program</c>
///         builds another host over the same window, and everything the old editor owned is disposed
///         by the code that already disposes it on the way down — including the layout and the keymap,
///         which are persisted first. The unsaved-work prompt is the same one the close button goes
///         through.
///     </para>
///     <para>
///         ⚠ <b>Moved out of <c>EditorApplication</c> (#1296), because everything it reads is
///         published.</b> It kept <c>EditorProjects.cs</c> on the application as "the drawn startup
///         chooser, this editor's own dialog" — but the drawn chooser is <c>EditorShell.ChooseAsync</c>,
///         the recents and the request are <see cref="IProjectSession" />'s, and the picker, the frame
///         queue and the data directory are services a module is handed. <c>WorkspaceModule</c> builds
///         one for File ▸ New Project and Open Project; the application builds one for the start-up
///         latch. It holds no state, so two of them are the same dialog.
///     </para>
/// </remarks>
sealed class ProjectChooser {
    readonly EditorShell shell;
    readonly IProjectSession session;
    readonly INativeDialogs? dialogs;
    readonly Deferred deferred;
    readonly string dataDirectory;

    /// <summary>Asks through a shell, about a session's projects.</summary>
    /// <param name="shell">Where the chooser is drawn.</param>
    /// <param name="session">The recents, and what a choice is handed to.</param>
    /// <param name="dialogs">The OS's folder picker, or <see langword="null" /> where there is none.</param>
    /// <param name="deferred">What brings a picker's answer back to the frame thread.</param>
    /// <param name="dataDirectory">Where a folder picker starts.</param>
    public ProjectChooser(
        EditorShell shell,
        IProjectSession session,
        INativeDialogs? dialogs,
        Deferred deferred,
        string dataDirectory
    ) {
        this.shell = shell;
        this.session = session;
        this.dialogs = dialogs;
        this.deferred = deferred;
        this.dataDirectory = dataDirectory;
    }

    /// <summary>Whether there is a folder picker, which New Project and the two buttons need.</summary>
    public bool CanPick => dialogs is not null;

    /// <summary>The startup Project Browser: recent projects, Browse, and New.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A drawn dialog rather than an operating-system window, which is doc 20's A2 rule
    ///         applied to its own example.</b> A modal that is a real window cannot be photographed by
    ///         the golden suite and cannot be driven by the automation harness — and the first thing a
    ///         new user sees is precisely the screen a regression must not be able to hide in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Cancel is a real answer.</b> Somebody who opens this from the File menu and
    ///         changes their mind keeps the project they had; somebody who meets it at start-up keeps
    ///         the scratch project. Both are better than a dialog that will not close until a decision
    ///         is made about something the user was not thinking about.
    ///     </para>
    /// </remarks>
    public void Show() {
        _ = Ask();

        async Task Ask() {
            // ⚠ `Exists` is a stat call, so it is asked once per entry rather than once per use —
            // and a recent list is precisely where a path to an unmounted share lives.
            var entries = session.Recent.Select(entry => (entry.Path, entry.Name, entry.Opened, entry.Exists)).ToList();

            var chosen = await shell.ChooseAsync(
                EditorStrings.ProjectsTitle.Text,
                entries.Select(entry => entry.Path),
                path => entries.First(entry => entry.Path == path).Name,
                path => {
                    var entry = entries.First(candidate => candidate.Path == path);

                    return string.Create(
                        CultureInfo.CurrentCulture,
                        $"{path} · {(entry.Exists ? Ago(entry.Opened) : EditorStrings.ProjectsMissing.Text)}"
                    );
                },

                // ⚠ Listed rather than pruned when the directory has gone. A recent list that forgets
                // an entry the moment a volume is unplugged is one with no way back to it; greyed and
                // labelled is the honest state.
                path => entries.First(entry => entry.Path == path).Exists,
                dialog => {
                    if (entries.Count == 0) {
                        dialog.Body.Add<TextBlock>().Text = EditorStrings.ProjectsEmpty.Text;
                    }

                    Action(dialog, EditorStrings.ProjectsBrowse, BrowseToken, ControlVariant.Default);
                    Action(dialog, EditorStrings.ProjectsNew, CreateToken, ControlVariant.Primary);
                }
            ).ConfigureAwait(true);

            switch (chosen) {
                case null:
                    return;

                case BrowseToken:
                    Open();
                    break;

                case CreateToken:
                    Create();
                    break;

                default:
                    session.RequestProject(chosen);
                    break;
            }
        }
    }

    /// <summary>Asks for a folder and opens the project in it.</summary>
    public void Open() => PickDirectory("Open Project", session.RequestProject);

    /// <summary>Asks for a folder and makes a project there.</summary>
    public void Create() => PickDirectory("New Project", session.CreateProject);

    /// <summary>One of the two buttons that are not a project: Browse, and New.</summary>
    /// <remarks>
    ///     They go in the body rather than the footer because the footer is where Cancel lives, and a
    ///     row of three where one of them backs out is a row people misread.
    /// </remarks>
    void Action(DialogSession<string?> dialog, StringId label, string token, ControlVariant variant) {
        var button = dialog.Body.Add<Button>();

        button.AddClass("choice-action");
        button.Label = label.Text;
        button.Variant = variant;

        // Both need a folder picker, so both grey themselves out where there is none — the rule
        // Open Scene and Save As already follow.
        button.Disabled = !CanPick;
        button.Clicked += _ => dialog.Answer(token);
    }

    /// <summary>What the Browse button answers with, which cannot be a path.</summary>
    /// <remarks>
    ///     ⚠ <b>A sentinel rather than a second result type, and it is safe because it is not a
    ///     path.</b> The dialog answers a string and the two buttons that are not a project have to
    ///     answer something; a directory cannot be the empty-braced form below, so no real answer can
    ///     collide with either.
    /// </remarks>
    const string BrowseToken = "{browse}";

    /// <inheritdoc cref="BrowseToken" />
    const string CreateToken = "{create}";

    /// <summary>Asks the platform for a directory and does something with it.</summary>
    /// <remarks>
    ///     ⚠ The answer arrives on whatever thread the platform completed on, so it goes through
    ///     <see cref="Deferred" /> to reach the frame thread; a cancelled picker answers null and does
    ///     nothing; and a picker that threw is a notification rather than an exception in the loop.
    /// </remarks>
    void PickDirectory(string title, Action<string> then) {
        if (dialogs is null) {
            return;
        }

        deferred.When(
            dialogs.OpenFolderAsync(new FileDialogOptions { Title = title, InitialDirectory = dataDirectory }),
            path => {
                if (path is { Length: > 0 }) {
                    then(path);
                }
            },
            failure => shell.Notifications.Show("Could not choose a folder", NotificationSeverity.Error, failure.Message)
        );
    }

    /// <summary>How long ago something was, in the roughest terms that are still useful.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a date, because the question is "which of these was I last in".</b> Two projects
    ///     opened on the 3rd and the 14th of a month are told apart by reading and subtracting; "two
    ///     days ago" and "last month" are told apart at a glance, which is what a list of six is for.
    /// </remarks>
    static string Ago(DateTime when) {
        var elapsed = DateTime.UtcNow - when;

        return elapsed switch {
            { TotalMinutes: < 2 } => "just now",
            { TotalHours: < 1 } => string.Create(CultureInfo.CurrentCulture, $"{(int)elapsed.TotalMinutes} minutes ago"),
            { TotalDays: < 1 } => string.Create(CultureInfo.CurrentCulture, $"{(int)elapsed.TotalHours} hours ago"),
            { TotalDays: < 30 } => string.Create(CultureInfo.CurrentCulture, $"{(int)elapsed.TotalDays} days ago"),
            _ => when.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)
        };
    }
}
