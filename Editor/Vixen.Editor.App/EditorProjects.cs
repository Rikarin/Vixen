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
///         ⚠ <b>So this sets a pending root and asks to close.</b> <c>EditorHost.Run</c> returns,
///         <c>Program</c> builds another host over the same window, and everything the old editor
///         owned is disposed by the code that already disposes it on the way down — including the
///         layout and the keymap, which are persisted first. Half a dozen fields reassigned in place
///         would be half a dozen chances to leave a panel pointing at a dead world.
///     </para>
///     <para>
///         ⚠ <b>The unsaved-work prompt is the same one the close button goes through.</b> Opening
///         another project with dirty documents is one of the three cases doc 20's A2 names by name,
///         and an editor that lost an afternoon to a menu item is exactly as bad as one that lost it
///         to a window close.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>Where the editor should reopen, or <see langword="null" /> to stay closed.</summary>
    /// <remarks>
    ///     Read by the host after <see cref="RequestClose" /> has done its work. Null is the ordinary
    ///     case and means the user is quitting.
    /// </remarks>
    public string? PendingProject { get; private set; }

    /// <summary>What the editor has opened before, newest first.</summary>
    public ProjectHistory Recent { get; }

    /// <summary>Closes this project and reopens the editor over another.</summary>
    /// <param name="root">The new project's root directory, which need not exist yet.</param>
    /// <remarks>
    ///     Asks about unsaved work first, through the same prompt the window's close button uses —
    ///     which is why it is a request rather than a call.
    /// </remarks>
    public void RequestProject(string root) {
        ArgumentException.ThrowIfNullOrEmpty(root);

        var full = Path.GetFullPath(root);

        if (string.Equals(full, project.Paths.Root, StringComparison.Ordinal)) {
            Shell.Notifications.Show(project.Name, NotificationSeverity.Info, "That project is already open.");
            return;
        }

        PendingProject = full;
        RequestClose();

        // ⚠ Cleared when the prompt was declined, which `RequestClose` reports by leaving
        // `IsClosing` false. Without this, backing out of "save your changes?" would leave a pending
        // root that the *next* close — the one where they meant to quit — would silently act on.
        if (!IsClosing && !closing) {
            PendingProject = null;
        }
    }

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
    public void ShowProjectBrowser() {
        _ = Ask();

        async Task Ask() {
            // ⚠ `Exists` is a stat call, so it is asked once per entry rather than once per use —
            // and a recent list is precisely where a path to an unmounted share lives.
            var entries = Recent.Entries.Select(entry => (entry.Path, entry.Name, entry.Opened, entry.Exists)).ToList();

            var chosen = await ChooseAsync(
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
                session => {
                    if (entries.Count == 0) {
                        session.Body.Add<TextBlock>().Text = EditorStrings.ProjectsEmpty.Text;
                    }

                    Action(session, EditorStrings.ProjectsBrowse, BrowseToken, ControlVariant.Default);
                    Action(session, EditorStrings.ProjectsNew, CreateToken, ControlVariant.Primary);
                }
            ).ConfigureAwait(true);

            switch (chosen) {
                case null:
                    return;

                case BrowseToken:
                    PickProjectDirectory("Open Project", root => RequestProject(root));
                    break;

                case CreateToken:
                    PickProjectDirectory("New Project", CreateProject);
                    break;

                default:
                    RequestProject(chosen);
                    break;
            }
        }
    }

    /// <summary>One of the two buttons that are not a project: Browse, and New.</summary>
    /// <remarks>
    ///     They go in the body rather than the footer because the footer is where Cancel lives, and a
    ///     row of three where one of them backs out is a row people misread.
    /// </remarks>
    void Action(DialogSession<string?> session, StringId label, string token, ControlVariant variant) {
        var button = session.Body.Add<Button>();

        button.AddClass("choice-action");
        button.Label = label.Text;
        button.Variant = variant;

        // Both need a folder picker, so both grey themselves out where there is none — the rule
        // Open Scene and Save As already follow.
        button.Disabled = !services.CanPick;
        button.Clicked += _ => session.Answer(token);
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
    void PickProjectDirectory(string title, Action<string> then) =>
        Picked(
            dialogs => dialogs.OpenFolderAsync(new FileDialogOptions { Title = title, InitialDirectory = dataDirectory }),
            then,
            "Could not choose a folder"
        );

    /// <summary>Asks the platform something that answers with a path, and acts on the answer.</summary>
    /// <param name="ask">Which picker to open.</param>
    /// <param name="then">What to do with the path, which is only called when there is one.</param>
    /// <param name="failed">What to say if the picker itself threw.</param>
    /// <remarks>
    ///     ⚠ <b>Four things every one of these has to get right, written once.</b> There may be no
    ///     picker at all — a runtime question with a runtime answer; the answer arrives on whatever
    ///     thread the platform completed on, so it goes through <see cref="Deferred" /> to reach the
    ///     frame thread; a cancelled picker answers null and must do nothing; and a picker that threw
    ///     is a notification rather than an exception in the loop. Six call sites had four copies of
    ///     that between them.
    /// </remarks>
    void Picked(Func<INativeDialogs, ValueTask<string?>> ask, Action<string> then, string failed) {
        if (services.Dialogs is not { } dialogs) {
            return;
        }

        deferred.When(
            ask(dialogs),
            path => {
                if (path is { Length: > 0 }) {
                    then(path);
                }
            },
            failure => Shell.Notifications.Show(failed, NotificationSeverity.Error, failure.Message)
        );
    }

    /// <summary>Asks the user to pick one of a list, as a drawn dialog.</summary>
    /// <param name="title">What the dialog asks.</param>
    /// <param name="items">What to choose between.</param>
    /// <param name="label">What a row says.</param>
    /// <param name="detail">A second line, or <see langword="null" /> for none.</param>
    /// <param name="enabled">Whether a row can be chosen, for one that is listed and unreachable.</param>
    /// <param name="fill">Anything else the dialog needs, above the Cancel button.</param>
    /// <returns>What was chosen, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Drawn rather than native, because every one of these is a question about the
    ///     <i>project</i> rather than about the user's disk</b> — a destination folder carries a GUID
    ///     and a project-relative path, and a native picker would happily answer with a directory
    ///     outside the project, which is the one answer the operation cannot take.
    /// </remarks>
    Task<T?> ChooseAsync<T>(
        string title,
        IEnumerable<T> items,
        Func<T, string> label,
        Func<T, string?>? detail = null,
        Func<T, bool>? enabled = null,
        Action<DialogSession<T?>>? fill = null
    ) where T : class =>
        Shell.Dialogs.ShowAsync<T?>(
            title,
            session => {
                var list = session.Body.Add<UiElement>("choice-list");

                foreach (var item in items) {
                    var button = list.Add<Button>();
                    var chosen = item;

                    button.AddClass("choice");
                    button.Variant = ControlVariant.Subtle;
                    button.Label = label(item);
                    button.Disabled = enabled is not null && !enabled(item);

                    if (detail?.Invoke(item) is { Length: > 0 } second) {
                        button.Add<UiElement>("choice-detail").Text = second;
                    }

                    button.Clicked += _ => session.Answer(chosen);
                }

                fill?.Invoke(session);
                session.AddButton(EditorStrings.DialogCancel.Text, () => null);
            }
        );

    /// <summary>Writes a new game project and opens it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This used to make two directories and call it a project, and the consequence
    ///         showed up two milestones later.</b> A folder with an <c>Assets/</c> in it <i>is</i> a
    ///         project as far as <c>AssetDatabase</c> is concerned — but it has no <c>.csproj</c>, so
    ///         there is nothing for <c>dotnet publish</c> to publish, and E6's Build and Run was
    ///         greyed for every project the editor had ever made with a message naming a terminal
    ///         command. An editor that cannot finish the project it just created fails doc 20's
    ///         second bar on the first screen a new user sees.
    ///     </para>
    ///     <para>
    ///         <b>It writes the <c>game</c> template</b> — the same files <c>dotnet new vixen-game</c>
    ///         and <c>vixen new game</c> write, through the same <see cref="ProjectScaffold" />, so
    ///         there is no third answer to "what is in a new project".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The name is the folder's, cleaned rather than refused.</b> It is not typed here —
    ///         it is whatever directory somebody picked in a file dialog — and "my game (2)" is an
    ///         ordinary folder name and an impossible namespace. See <see cref="ProjectScaffold.NameFrom" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A directory that already holds some of those files is opened rather than
    ///         clobbered.</b> The scaffold finds every collision before it writes anything, and this
    ///         is the case where somebody pointed New Project at a project they already had: the
    ///         useful response is to open it and say what was left alone, not to refuse and not to
    ///         overwrite their <c>Program.cs</c>.
    ///     </para>
    /// </remarks>
    void CreateProject(string root) {
        try {
            var paths = new ProjectPaths(root);

            Directory.CreateDirectory(paths.Assets);
            Directory.CreateDirectory(paths.ProjectSettings);

            var scaffold = ProjectScaffold.Write("game", ProjectScaffold.NameFrom(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))), root);

            if (scaffold.Collisions.Count > 0) {
                Shell.Notifications.Show(
                    "Opened what was already there",
                    NotificationSeverity.Warning,
                    $"{scaffold.Collisions.Count} file(s) of the game template are already in that "
                    + "folder, so none of them were written."
                );
            } else if (scaffold.Error.Length > 0) {
                // Reported rather than fatal: the directories exist, so what opens is a project that
                // can be edited and cannot yet be published — which is the state this whole method
                // used to leave behind unconditionally.
                Shell.Notifications.Show("The project has no C# project file", NotificationSeverity.Warning, scaffold.Error);
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not create the project", NotificationSeverity.Error, exception.Message);
            return;
        }

        RequestProject(root);
    }

    /// <summary>Says so when the project was made by an engine newer than this one.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>.vxproj</c>'s reader, and the reason the file carries a version at all.</b>
    ///     Doc 08 has named the marker since it was written; what makes it more than a sentinel is
    ///     that opening a project built against a newer engine fails <i>later and stranger</i> — a
    ///     scene with a component this build has never heard of, a settings file with keys nothing
    ///     claims — and every one of those failures is more confusing than being told at the door.
    ///     A field nothing reads would teach people the file does not matter, which is the bar doc
    ///     20's A4 sets for a shipped setting and it applies here too.
    /// </remarks>
    void WarnIfNewerEngine() {
        if (ProjectMarker.TryRead(project.Paths.Root, out var marker) && ProjectMarker.IsFromTheFuture(marker)) {
            Shell.Notifications.Show(
                "This project was made with a newer Vixen",
                NotificationSeverity.Warning,
                $"It says engine {marker.Engine}; this editor is {ProjectScaffold.SdkVersion}. Anything it "
                + "contains that this build does not know about will be reported as it is met."
            );
        }
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
            { TotalHours: < 1 } => string.Create(CultureInfo.CurrentCulture, $"{(int) elapsed.TotalMinutes} minutes ago"),
            { TotalDays: < 1 } => string.Create(CultureInfo.CurrentCulture, $"{(int) elapsed.TotalHours} hours ago"),
            { TotalDays: < 30 } => string.Create(CultureInfo.CurrentCulture, $"{(int) elapsed.TotalDays} days ago"),
            _ => when.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)
        };
    }
}
