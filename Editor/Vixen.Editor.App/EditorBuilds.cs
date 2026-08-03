// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Debugger;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>Doc 20's B7: the Build Settings window, Build and Run, and deploying to a device.</summary>
/// <remarks>
///     <para>
///         <b>The last two rows of doc 20's B7 that are the editor's rather than a provider's.</b>
///         What a player build <i>is</i> — import, pack, publish — is
///         <see cref="ContentTasks.BuildPlayer" />, over <see cref="PlayerBuild" />, which is
///         <c>vixen build</c>'s own sequence rather than a second one beside it. What is here is the
///         part an editor adds: a window to choose in, a menu that is a second view of the same
///         choices, and the answer to "what does Deploy mean for this device".
///     </para>
///     <para>
///         ⚠ <b>The menu and the window write the same settings object and neither owns it.</b> Doc
///         20's A4 rule — two writers to one setting is how a window and a menu tick come to disagree
///         — is kept by there being one setting: <c>Target ▸</c> ticks itself from
///         <c>PlayerBuildSettings.Target</c> and sets it, and the window's picker does the same. What
///         would break the rule is a menu that remembered a target of its own.
///     </para>
///     <para>
///         ⚠ <b>Deploying is answered here rather than in the device panel</b>, which is
///         <c>DeviceManagerView</c>'s own argument turned round: what "deploy" means differs per kind
///         of device — this machine is a publish and a launch, a phone is <c>adb install</c> — and
///         only the application knows which of those it can do. It can do the first one.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>What the Build Settings panel is called in an arrangement.</summary>
    internal const string BuildPanel = "build-settings";

    /// <summary>Where <c>dotnet publish</c>'s output goes, which is the console like everything else.</summary>
    BuildLog? buildLog;

    /// <summary>The Build Settings panel while it is open, or <see langword="null" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Held for the reason <c>historyView</c> is, and released the same way.</b> Its two
    ///     buttons are derived from <see cref="BuildRefusal" />, which changes for reasons that are
    ///     not edits to this panel — a build starting, a build finishing, a <c>.csproj</c> arriving
    ///     in a checkout. Nothing was asking it again, so the buttons stayed as they were at the
    ///     moment the panel opened: enabled for the whole of a build that had already started, and
    ///     greyed after the thing they were greyed for had been fixed. The menu line never had this
    ///     because <c>MenuPresenter</c> asks <c>CanExecute</c> every time it draws.
    /// </remarks>
    BuildSettingsView? buildView;

    /// <summary>The project's build settings, which is the one copy everything reads and writes.</summary>
    PlayerBuildSettings Builds => project.Settings.Get<PlayerBuildSettings>();

    void BuildPanels() =>
        Shell.RegisterPanel(
            new PanelDescriptor(
                BuildPanel,
                new StringId("editor.panel.build", "Build Settings"),
                panel => {
                    var view = panel.Add<BuildSettingsView>();

                    // ⚠ Pulled, all three, and the panel's remarks say why: this factory runs again
                    // on every reopen, so an answer captured once would be one from a project state
                    // the user has since changed.
                    view.Catalogue = SceneCatalogue;
                    view.Trouble = SceneTrouble;
                    view.Refusal = BuildRefusal;

                    view.Changed += _ => SaveBuildSettings();
                    view.BuildRequested += (_, launch) => StartPlayerBuild(launch);

                    view.Show(Builds);
                    buildView = view;
                }
            ) {
                // ⚠ The other half of holding it. A reference kept past the tab's own close button
                // is the defect `PanelDescriptor.Closed` was added for — see `EditorApplication`'s
                // undo history, which was polled for the rest of the session after being closed once.
                Closed = () => buildView = null
            }
        );

    /// <summary>Asks the panel to look at the project again, if it is open.</summary>
    /// <remarks>
    ///     ⚠ <b>Called when something happens rather than once a frame, and the difference is a
    ///     syscall.</b> <see cref="BuildRefusal" /> enumerates the project root looking for a
    ///     <c>.csproj</c>; a menu asks it when somebody opens a menu, which is rare, and a panel
    ///     polled every frame would ask it sixty times a second for an answer that changes when a
    ///     build starts, when one ends, and almost never otherwise.
    /// </remarks>
    void RefreshBuildPanel() => buildView?.Rebuild();

    /// <summary>Shows the settings again, for a change made somewhere that is not the panel.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Show</c> rather than <c>Rebuild</c>, because the three fields are the part that
    ///     moved.</b> Picking Target ▸ Android from the menu writes the same setting the panel's own
    ///     picker writes — doc 20's A4 rule is kept by there being one field — but a panel that was
    ///     already open went on displaying the old one, which is the same disagreement between a menu
    ///     and a window that having one setting was supposed to prevent.
    /// </remarks>
    void ShowBuildSettings() => buildView?.Show(Builds);

    /// <summary>Every scene in the project, project-relative, in path order.</summary>
    /// <remarks>
    ///     From the editor's own database rather than from the disk, so a scene created a moment ago
    ///     is offered and a file somebody dropped in without a rescan is not — which is the same
    ///     answer every other panel gives about what the project contains.
    /// </remarks>
    IEnumerable<string> SceneCatalogue() =>
        project.Assets.Entries
            .Where(static entry =>
                !entry.IsFolder
                && entry.Path.EndsWith(SceneSerializer.Extension, StringComparison.OrdinalIgnoreCase)
            )
            .Select(static entry => entry.Path)
            .Order(StringComparer.Ordinal);

    /// <summary>What would stop a listed scene shipping, or <see langword="null" /> when nothing would.</summary>
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
    string? SceneTrouble(string path) {
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

    // ── The verbs ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Doc 20's Build menu, less the two lines that belong to other milestones.</summary>
    void PlayerBuildCommands() {
        Verb(
            "build.settings",
            new StringId("editor.command.build.settings", "Build Settings…"),
            CategoryBuild,
            () => Shell.Workspace.Open(BuildPanel)
        );

        Verb(
            "build.run",
            new StringId("editor.command.build.run", "Build and Run"),
            CategoryBuild,
            () => StartPlayerBuild(launch: true),

            // ⚠ Not merely "is a build running". A menu line that greyed only for that would still
            // be pressable on a project with no .csproj in it, and the failure would arrive a
            // minute later from `dotnet` rather than now from the thing that knows.
            enabled: () => BuildRefusal() is null
        );

        Shell.Keys.SetDefault("build.run", new KeyChord(InputKey.B, ModifierKeys.Control));

        // ⚠ A seventh line above Part C's six, and it is the one a fresh project is on. An empty
        // target means "whatever machine this is", which is not any of the six and has to be
        // selectable — a Target submenu with no tick at all would read as a setting that had failed
        // to load rather than as the default.
        Target("build.target-host", $"This Machine ({ProjectWorkspaceTarget})", string.Empty);

        foreach (var target in PlayerBuild.Targets) {
            Target(BuildIds.Target(target), target, target);
        }

        foreach (var variant in PlayerBuild.Variants) {
            var chosen = variant;

            Shell.Commands.Add(
                new EditorCommand(
                    BuildIds.Variant(variant),
                    new StringId("editor.command." + BuildIds.Variant(variant), variant),
                    () => {
                        Builds.Variant = chosen;
                        SaveBuildSettings();
                        ShowBuildSettings();
                    }
                ) {
                    Category = CategoryBuild,
                    RadioGroup = "build.configuration",
                    Checked = () => string.Equals(Builds.Variant, chosen, StringComparison.Ordinal)
                }
            );
        }

        void Target(string id, string label, string value) =>
            Shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, label), () => {
                        Builds.Target = value;
                        SaveBuildSettings();
                        ShowBuildSettings();
                    }
                ) {
                    Category = CategoryBuild,
                    RadioGroup = "build.target",

                    // Greyed rather than absent, with the sentence saying why — Web is on the menu
                    // because it is a platform this engine ships on, and is not something this build
                    // produces an application from.
                    Unavailable = value.Length > 0 && PlayerBuild.WhyNotPublishable(value) is { } why
                        ? new StringId("editor.planned." + id, why)
                        : default,

                    Checked = () => string.Equals(Builds.Target, value, StringComparison.Ordinal)
                }
            );
    }

    /// <summary>The ids the Build menu is composed from, so a literal cannot drift from a registration.</summary>
    /// <inheritdoc cref="ViewportIds" select="remarks" />
    internal static class BuildIds {
        /// <summary>The id of the command that builds for a target.</summary>
        public static string Target(string target) => "build.target-" + target.ToLowerInvariant();

        /// <summary>And of the one that chooses a variant.</summary>
        public static string Variant(string variant) => "build.configuration-" + variant.ToLowerInvariant();

        /// <summary>Every target id, in menu order, with the host's first.</summary>
        public static string[] Targets { get; } = ["build.target-host", .. PlayerBuild.Targets.Select(Target)];

        /// <summary>Every variant id, in menu order.</summary>
        public static string[] Variants { get; } = [.. PlayerBuild.Variants.Select(Variant)];
    }

    // ── Running one ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Why a player build cannot start, or <see langword="null" /> when it can.</summary>
    /// <remarks>
    ///     One method, read by the menu line's enablement, by the window's two buttons and by the
    ///     device panel's Deploy. Three surfaces asking three differently-worded questions is how one
    ///     of them ends up enabled when the other two are not.
    /// </remarks>
    string? BuildRefusal() {
        if (PlayerBuild.WhyNotPublishable(ResolvedTarget) is { } why) {
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

    /// <summary>Which target a build would use: the setting, or this machine when it is empty.</summary>
    string ResolvedTarget => Builds.Target is { Length: > 0 } target ? target : ProjectWorkspaceTarget;

    /// <summary>Builds the player, and optionally runs it.</summary>
    /// <param name="launch">Whether to start what came out.</param>
    /// <param name="device">The device a deploy is for, so its row can say what is happening.</param>
    /// <remarks>
    ///     ⚠ <b>Everything is resolved here, on the frame thread, and handed over whole.</b> The
    ///     panel stays editable while a build runs — deliberately, since the next build is usually a
    ///     different variant — so a task that read the settings when it got round to publishing would
    ///     produce an artefact nobody asked for. See <see cref="PlayerBuildRequest" />.
    /// </remarks>
    void StartPlayerBuild(bool launch, DeviceEntry? device = null) {
        if (BuildRefusal() is { } refusal) {
            Shell.Notifications.Show("Nothing was built", NotificationSeverity.Warning, refusal);
            return;
        }

        var settings = Builds;
        var target = ResolvedTarget;

        PlayerBuild.TryDescribe(target, out var shape);
        PlayerBuild.TryFindProjectFile(project.Paths.Root, out var projectFile);

        if (launch && !shape.Runnable) {
            Shell.Notifications.Show(
                "Nothing was built",
                NotificationSeverity.Warning,
                $"A {target} build is {shape.Artefact}, which this machine cannot start. Build it and install it."
            );

            return;
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
            Shell.Notifications.Show(
                $"{unshippable.Count} scene(s) in the build cannot ship",
                NotificationSeverity.Error,
                string.Join(Environment.NewLine, unshippable.Select(scene => $"{scene.Path}: {scene.Trouble}"))
            );

            return;
        }

        if (device is not null) {
            diagnostics.Devices.Mark(device.Id, DeviceStatus.Deploying);
        }

        content.BuildPlayer(
            new PlayerBuildRequest(target, shape, settings.Variant, projectFile, OutputDirectory(settings, target), launch),
            buildLog ??= new BuildLog(log),
            _ => {
                if (device is not null) {
                    // Back to what discovery would say about it. A row left reading Deploying after
                    // a build that failed is a row somebody waits on.
                    diagnostics.Devices.Mark(device.Id, DeviceStatus.Available);
                }
            }
        );
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

    /// <summary>Writes the build settings, which is what a menu tick and a field both do.</summary>
    /// <remarks>
    ///     ⚠ <b>Written immediately rather than behind an Apply</b>, and <c>BuildSettingsView</c>'s
    ///     remarks give the reason: Build reads these fields, so an edit that had not been committed
    ///     would mean the button building something other than what is on screen. The two settings
    ///     doc 20's A4 protects with an Apply are the two that cost something to change; none of
    ///     these costs anything until a build runs.
    /// </remarks>
    void SaveBuildSettings() {
        project.Settings.MarkChanged<PlayerBuildSettings>();

        try {
            project.Settings.Save<PlayerBuildSettings>();
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not save the build settings", NotificationSeverity.Error, exception.Message);
        }
    }

    // ── Deploying ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Why a device cannot be deployed to, or <see langword="null" /> when it can.</summary>
    /// <remarks>
    ///     ⚠ <b>Only this machine, and the reason for each of the others is the provider that would
    ///     have found it.</b> Doc 20's E4 left device discovery behind <c>adb</c> and the vendor SDKs;
    ///     installing is the same tool as finding, so a kind of device the editor cannot discover is
    ///     necessarily one it cannot install to. Saying which tool is missing is what makes that a
    ///     state rather than a refusal.
    /// </remarks>
    internal string? DeployRefusal(DeviceEntry device) =>
        device.Kind switch {
            DeviceKind.Local => BuildRefusal(),
            DeviceKind.Mobile => "Installing on a phone is `adb install` or a provisioned .ipa, which needs the "
                + "IDeviceProvider that would have found it.",
            DeviceKind.Console => "A console takes its build through the vendor's own SDK.",
            DeviceKind.Browser => "A Web player is doc 17's packaging step, which this build does not produce.",
            _ => "A build reaches another machine over `vixen content serve` or a share; the editor does not copy to it."
        };

    /// <summary>What Deploy does for the one kind of device this editor can reach.</summary>
    /// <remarks>
    ///     ⚠ <b>Build, install, launch — and <i>not</i> attach.</b> Doc 20's B7 asks for all four and
    ///     three of them are here; the fourth needs something on the other side to answer, and doc
    ///     13's runtime half of the remote inspector is not written — see <c>RemoteInspectorClient</c>.
    ///     The Attach button beside this one is what will do it the day a player listens, and it is
    ///     enabled by the endpoint the device reports rather than by this having run.
    /// </remarks>
    internal void Deploy(DeviceEntry device) {
        if (device.Kind is not DeviceKind.Local) {
            Shell.Notifications.Show("Not deployed", NotificationSeverity.Warning, DeployRefusal(device) ?? string.Empty);
            return;
        }

        StartPlayerBuild(launch: true, device);
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
