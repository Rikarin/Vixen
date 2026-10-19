// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Microsoft.Extensions.Logging;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;

namespace Vixen.Editor.App;

/// <summary>Doc 20's B7, as the editor's <see cref="IPlayerDelivery" />: build settings, and building a player.</summary>
/// <remarks>
///     <para>
///         <b>Moved, not rewritten</b> — doc 50 § P2f (#1295). Every member here was a private method
///         of <c>EditorBuilds.cs</c>, a partial of the application closed over its fields; the window
///         and the Build menu are <see cref="DeliveryModule" />'s now, and Deploy is the diagnostics
///         module's, both over this.
///     </para>
///     <para>
///         <b>The last two rows of doc 20's B7 that are the editor's rather than a provider's.</b>
///         What a player build <i>is</i> — import, pack, publish — is
///         <see cref="ContentTasks.BuildPlayer" />, over <see cref="PlayerBuild" />, which is
///         <c>vixen build</c>'s own sequence rather than a second one beside it. What is here is the
///         part an editor adds: the answer to "why not, or nothing", and a request resolved on the
///         frame thread and handed over whole.
///     </para>
///     <para>
///         ⚠ <b>The menu and the window write the same settings object and neither owns it.</b> Doc
///         20's A4 rule — two writers to one setting is how a window and a menu tick come to disagree
///         — is kept by there being one setting: <c>Target ▸</c> ticks itself from
///         <c>PlayerBuildSettings.Target</c> and sets it, and the window's picker does the same. What
///         would break the rule is a menu that remembered a target of its own.
///     </para>
/// </remarks>
internal sealed class PlayerDelivery : IPlayerDelivery {
    readonly EditorProject project;
    readonly ContentTasks content;
    readonly NotificationCenter notifications;
    readonly EditorLog log;
    readonly IEditorRegistry extensions;

    /// <summary>Where <c>dotnet publish</c>'s output goes, which is the console like everything else.</summary>
    BuildLog? buildLog;

    /// <summary>Builds the service over the open project and the one queue builds run on.</summary>
    /// <param name="project">The open project.</param>
    /// <param name="content">The task queue, which knows an import and a build must not run at once.</param>
    /// <param name="notifications">Where a refusal is said.</param>
    /// <param name="log">The editor's log, which is the build log.</param>
    /// <param name="extensions">Where a plugin's <see cref="BuildStep" /> comes from.</param>
    public PlayerDelivery(
        EditorProject project,
        ContentTasks content,
        NotificationCenter notifications,
        EditorLog log,
        IEditorRegistry extensions
    ) {
        this.project = project;
        this.content = content;
        this.notifications = notifications;
        this.log = log;
        this.extensions = extensions;

        // And its two buttons are greyed while anything is running, which is a fact about the task
        // rather than about anything a panel did.
        content.BusyChanged += Restate;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public PlayerBuildSettings Settings => project.Settings.Get<PlayerBuildSettings>();

    /// <inheritdoc />
    public string Target => Settings.Target is { Length: > 0 } target ? target : ProjectWorkspace.HostTarget;

    /// <summary>Says that <see cref="Refusal" /> may have a different answer now.</summary>
    /// <remarks>
    ///     ⚠ <b>Called when something happens rather than once a frame, and the difference is a
    ///     syscall.</b> <see cref="Refusal" /> enumerates the project root looking for a
    ///     <c>.csproj</c>; a menu asks it when somebody opens a menu, which is rare, and a panel
    ///     polled every frame would ask it sixty times a second for an answer that changes when a
    ///     build starts, when one ends, and almost never otherwise.
    /// </remarks>
    public void Restate() => Changed?.Invoke();

    /// <inheritdoc />
    /// <remarks>
    ///     From the editor's own database rather than from the disk, so a scene created a moment ago
    ///     is offered and a file somebody dropped in without a rescan is not — which is the same
    ///     answer every other panel gives about what the project contains.
    /// </remarks>
    public IEnumerable<string> Scenes() =>
        project.Assets.Entries
            .Where(static entry =>
                !entry.IsFolder
                && entry.Path.EndsWith(SceneSerializer.Extension, StringComparison.OrdinalIgnoreCase)
            )
            .Select(static entry => entry.Path)
            .Order(StringComparer.Ordinal);

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The panel's copy of the check <c>ContentPipeline</c> makes, and the two have to
    ///         agree.</b> A build resolves every entry to an address and refuses one it cannot — so a
    ///         row that read "In build" for a scene the build would reject would be the panel actively
    ///         misleading somebody about the thing it is for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Having no address is no longer one of the answers.</b> Every asset is addressable
    ///         by its path, so the two ways a listed scene can still fail are that it names nothing —
    ///         somebody's rename arriving in a checkout — and that it was deliberately excluded, which
    ///         is the build list and the sidecar contradicting each other.
    ///     </para>
    /// </remarks>
    public string? SceneTrouble(string path) {
        if (!project.Assets.TryGetByPath(path, out _)) {
            return "Missing";
        }

        try {
            return AssetMetaFile.ReadFile(AssetMetaFile.PathFor(project.Paths.Absolute(path)))
                is { Addressable.Excluded: true }
                    ? "Excluded"
                    : null;
        } catch (Exception failure) when (failure is IOException or YamlParseException or YamlBindingException) {
            // A sidecar somebody is halfway through hand-editing reads as unshippable, which is what
            // the build would say about it too — it is the planner's "no readable sidecar".
            return "No sidecar";
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     One method, read by the menu line's enablement, by the window's two buttons and by the
    ///     device panel's Deploy. Three surfaces asking three differently-worded questions is how one
    ///     of them ends up enabled when the other two are not.
    /// </remarks>
    public string? Refusal() {
        if (PlayerBuild.WhyNotPublishable(Target) is { } why) {
            return why;
        }

        if (content.IsBusy) {
            return "An import or a build is already running.";
        }

        return PlayerBuild.TryFindProjectFile(project.Paths.Root, out _)
            ? null
            : $"There is no .csproj in '{project.Paths.Root}', so there is nothing to publish. "
            + "`vixen new game` writes one.";
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Everything is resolved here, on the frame thread, and handed over whole.</b> The
    ///     panel stays editable while a build runs — deliberately, since the next build is usually a
    ///     different variant — so a task that read the settings when it got round to publishing would
    ///     produce an artefact nobody asked for. See <see cref="PlayerBuildRequest" />.
    /// </remarks>
    public bool Build(bool launch, Action? finished = null) {
        if (Refusal() is { } refusal) {
            notifications.Show("Nothing was built", NotificationSeverity.Warning, refusal);
            return false;
        }

        var settings = Settings;
        var target = Target;

        PlayerBuild.TryDescribe(target, out var shape);
        PlayerBuild.TryFindProjectFile(project.Paths.Root, out var projectFile);

        if (launch && !shape.Runnable) {
            notifications.Show(
                "Nothing was built",
                NotificationSeverity.Warning,
                $"A {target} build is {shape.Artefact}, which this machine cannot start. Build it and install it."
            );

            return false;
        }

        // ⚠ Refused here rather than warned about, because the content build refuses it too. The
        // scene list is what the manifest a player boots from is made of, so an entry that names
        // nothing or names something no bundle carries stops the pack — and finding that out now
        // rather than after a full import is the difference between a sentence and a minute.
        var unshippable = settings.Scenes
            .Select(path => (Path: path, Trouble: SceneTrouble(path)))
            .Where(scene => scene.Trouble is not null)
            .ToList();

        if (unshippable.Count > 0) {
            notifications.Show(
                $"{unshippable.Count} scene(s) in the build cannot ship",
                NotificationSeverity.Error,
                string.Join(Environment.NewLine, unshippable.Select(scene => $"{scene.Path}: {scene.Trouble}"))
            );

            return false;
        }

        content.BuildPlayer(
            new PlayerBuildRequest(
                target,
                shape,
                settings.Variant,
                projectFile,
                OutputDirectory(settings, target),
                launch,

                // ⚠ Doc 36 § D4's build-step row, read here for the reason everything else on this
                // line is read here: on the frame thread, once, and handed over. A plugin can be
                // unloaded while its build runs, so a list read later is a list that may name a
                // delegate over an assembly that has gone.
                extensions.All<BuildStep>()
            ),
            buildLog ??= new BuildLog(log),
            _ => finished?.Invoke()
        );

        return true;
    }

    /// <summary>Where the artefact goes: what was typed, or the default the CLI uses.</summary>
    /// <remarks>
    ///     A relative path is relative to the project rather than to whatever directory the editor
    ///     was started from — the setting is committed and shared, and a path resolved against a
    ///     working directory would mean a different place on every machine that opened it.
    /// </remarks>
    string OutputDirectory(PlayerBuildSettings settings, string target) =>
        settings.OutputPath is { Length: > 0 } typed
            ? Path.IsPathRooted(typed) ? typed : Path.Combine(project.Paths.Root, typed)
            : content.DefaultOutput(target);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Written immediately rather than behind an Apply</b>, and <c>BuildSettingsView</c>'s
    ///     remarks give the reason: Build reads these fields, so an edit that had not been committed
    ///     would mean the button building something other than what is on screen. The two settings
    ///     doc 20's A4 protects with an Apply are the two that cost something to change; none of
    ///     these costs anything until a build runs.
    /// </remarks>
    public void SaveSettings() {
        project.Settings.MarkChanged<PlayerBuildSettings>();

        try {
            project.Settings.Save<PlayerBuildSettings>();
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            notifications.Show("Could not save the build settings", NotificationSeverity.Error, exception.Message);
        }
    }

    /// <summary>A <see cref="TextWriter" /> over the editor's log, for a process's own output.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The console is the build log, and that is the whole reason this exists.</b>
    ///         <c>vixen build</c> writes to a terminal; an editor has none, so a publish whose output
    ///         went to <c>Console.Out</c> would be a build that failed with its reason written to a
    ///         handle nobody has. Every line lands in the ring the Console panel reads, where it can
    ///         be filtered, searched and read after the toast has gone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>MSBuild's own severity is read back out of the line.</b> A compiler error logged
    ///         as information is one the console's level filter hides by default — which would be the
    ///         panel actively concealing the thing somebody opened it for. The forms are
    ///         <c>: error CS1002:</c> and <c>: warning CS0168:</c>, which is what every MSBuild
    ///         logger matches on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Locked, because the two ends of a captured process are two threads.</b> The
    ///         standard output and standard error readers each raise on their own, and a ring entry
    ///         assembled from halves of two lines is a log line nobody can search for.
    ///     </para>
    /// </remarks>
    sealed class BuildLog(EditorLog log) : TextWriter {
        readonly StringBuilder pending = new();
        readonly Lock gate = new();

        /// <inheritdoc />
        public override Encoding Encoding => Encoding.UTF8;

        /// <inheritdoc />
        public override void Write(char value) {
            lock (gate) {
                if (value is '\n') {
                    Emit(pending.ToString());
                    pending.Clear();
                } else if (value is not '\r') {
                    pending.Append(value);
                }
            }
        }

        /// <inheritdoc />
        public override void WriteLine(string? value) {
            lock (gate) {
                Emit(value ?? string.Empty);
            }
        }

        /// <inheritdoc />
        public override void WriteLine() {
            lock (gate) {
                Emit(pending.ToString());
                pending.Clear();
            }
        }

        void Emit(string line) {
            // A publish writes blank lines to separate its sections, and a ring of blank entries is
            // a ring with less of the build in it.
            if (string.IsNullOrWhiteSpace(line)) {
                return;
            }

            log.Write(
                line.Contains(": error", StringComparison.Ordinal) ? LogLevel.Error
                : line.Contains(": warning", StringComparison.Ordinal) ? LogLevel.Warning
                : LogLevel.Information,
                line.TrimEnd()
            );
        }
    }
}
