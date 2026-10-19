// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>Doc 20's Build menu and its Build Settings window, as a module over <see cref="IPlayerDelivery" />.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D3: the application's own verbs go through the front door</b> (#1295). These
///         were <c>EditorBuilds.PlayerBuildCommands</c> and <c>BuildPanels</c>, registered straight
///         into the shell over private methods; they are registered through the
///         <see cref="PluginContext" /> a third party is handed now, over the service a third party
///         can <c>Require</c> — so Build and Run is a name for <see cref="IPlayerDelivery.Build" />
///         and not the place it lives.
///     </para>
///     <para>
///         ⚠ <b>Registered by hand, not by <c>[EditorMenu]</c>, for the reason <c>PlayModule</c>
///         gives.</b> The Build menu's lines are placed by the application's <c>ParityMenus</c>,
///         which composes the whole bar in doc 20's Part C order, and a menu line naming a command
///         nobody registered is skipped — so switching this module off in the plugin manager takes
///         Build Settings, Build and Run and both radio submenus off the bar with it.
///     </para>
/// </remarks>
sealed class DeliveryModule : IEditorPlugin {
    /// <summary>What the host activates it under.</summary>
    public const string ModuleId = "vixen.delivery";

    /// <summary>What the plugin manager calls it.</summary>
    public const string ModuleName = "Delivery";

    /// <summary>What the Build Settings panel is called in an arrangement.</summary>
    internal const string BuildPanel = "build-settings";

    IPlayerDelivery delivery = null!;
    EditorShell shell = null!;

    /// <summary>The Build Settings panel while it is open, or <see langword="null" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Held for the reason <c>historyView</c> is, and released the same way.</b> Its two
    ///     buttons are derived from <see cref="IPlayerDelivery.Refusal" />, which changes for reasons
    ///     that are not edits to this panel — a build starting, a build finishing, a <c>.csproj</c>
    ///     arriving in a checkout. Nothing was asking it again, so the buttons stayed as they were at
    ///     the moment the panel opened: enabled for the whole of a build that had already started, and
    ///     greyed after the thing they were greyed for had been fixed. The menu line never had this
    ///     because <c>MenuPresenter</c> asks <c>CanExecute</c> every time it draws.
    /// </remarks>
    BuildSettingsView? buildView;

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        delivery = context.Services.Require<IPlayerDelivery>();
        shell = context.Shell;

        Panel(context);
        Commands(context);

        // ⚠ Through the context, so an unload takes the subscription back with the panel — a
        // handler left on a service the application outlives would keep this module reachable.
        context.With<IPlayerDelivery>(
            service => service.Changed += RefreshBuildPanel,
            service => service.Changed -= RefreshBuildPanel
        );
    }

    /// <summary>Asks the panel to look at the project again, if it is open.</summary>
    void RefreshBuildPanel() => buildView?.Rebuild();

    /// <summary>Shows the settings again, for a change made somewhere that is not the panel.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Show</c> rather than <c>Rebuild</c>, because the three fields are the part that
    ///     moved.</b> Picking Target ▸ Android from the menu writes the same setting the panel's own
    ///     picker writes — doc 20's A4 rule is kept by there being one field — but a panel that was
    ///     already open went on displaying the old one, which is the same disagreement between a menu
    ///     and a window that having one setting was supposed to prevent.
    /// </remarks>
    void ShowBuildSettings() => buildView?.Show(delivery.Settings);

    void Panel(PluginContext context) =>
        context.AddPanel(
            new PanelDescriptor(
                BuildPanel,
                EditorStrings.PanelBuild,
                panel => {
                    var view = panel.Add<BuildSettingsView>();

                    // ⚠ Pulled, all three, and the panel's remarks say why: this factory runs again
                    // on every reopen, so an answer captured once would be one from a project state
                    // the user has since changed.
                    view.Catalogue = delivery.Scenes;
                    view.Trouble = delivery.SceneTrouble;
                    view.Refusal = delivery.Refusal;

                    view.Changed += _ => delivery.SaveSettings();
                    view.BuildRequested += (_, launch) => delivery.Build(launch);

                    view.Show(delivery.Settings);
                    buildView = view;
                }
            ) {
                // ⚠ The other half of holding it. A reference kept past the tab's own close button
                // is the defect `PanelDescriptor.Closed` was added for — see `EditorApplication`'s
                // undo history, which was polled for the rest of the session after being closed once.
                Closed = () => buildView = null
            }
        );

    /// <summary>Doc 20's Build menu, less the two lines that belong to other milestones.</summary>
    void Commands(PluginContext context) {
        Verb(context, "build.settings", EditorStrings.CommandBuildSettings, () => shell.Workspace.Open(BuildPanel));

        Verb(
            context,
            "build.run",
            EditorStrings.CommandBuildRun,
            () => delivery.Build(launch: true),

            // ⚠ Not merely "is a build running". A menu line that greyed only for that would still
            // be pressable on a project with no .csproj in it, and the failure would arrive a
            // minute later from `dotnet` rather than now from the thing that knows.
            enabled: () => delivery.Refusal() is null
        );

        context.AddDefaultBinding("build.run", new KeyChord(InputKey.B, ModifierKeys.Control));

        // ⚠ A seventh line above Part C's six, and it is the one a fresh project is on. An empty
        // target means "whatever machine this is", which is not any of the six and has to be
        // selectable — a Target submenu with no tick at all would read as a setting that had failed
        // to load rather than as the default.
        Target("build.target-host", $"This Machine ({ProjectWorkspace.HostTarget})", string.Empty);

        foreach (var target in PlayerBuild.Targets) {
            Target(BuildIds.Target(target), target, target);
        }

        foreach (var variant in PlayerBuild.Variants) {
            var chosen = variant;

            context.AddCommand(
                new EditorCommand(
                    BuildIds.Variant(variant),
                    EditorStrings.BuildConfigurations[variant.ToLowerInvariant()],
                    () => {
                        delivery.Settings.Variant = chosen;
                        delivery.SaveSettings();
                        ShowBuildSettings();
                    }
                ) {
                    Category = EditorStrings.CategoryBuild,
                    RadioGroup = "build.configuration",
                    Checked = () => string.Equals(delivery.Settings.Variant, chosen, StringComparison.Ordinal)
                }
            );
        }

        void Target(string id, string label, string value) =>
            context.AddCommand(
                new EditorCommand(id, new StringId("editor.command." + id, label), () => {
                    delivery.Settings.Target = value;
                    delivery.SaveSettings();
                    ShowBuildSettings();
                }
                ) {
                    Category = EditorStrings.CategoryBuild,
                    RadioGroup = "build.target",

                    // Greyed rather than absent, with the sentence saying why — Web is on the menu
                    // because it is a platform this engine ships on, and is not something this build
                    // produces an application from.
                    Unavailable = value.Length > 0 && PlayerBuild.WhyNotPublishable(value) is { } why
                        ? new StringId("editor.planned." + id, why)
                        : default,

                    Checked = () => string.Equals(delivery.Settings.Target, value, StringComparison.Ordinal)
                }
            );
    }

    static void Verb(PluginContext context, string id, StringId title, Action run, Func<bool>? enabled = null) =>
        context.AddCommand(
            new EditorCommand(id, title, run) {
                Category = EditorStrings.CategoryBuild,
                Enablement = enabled
            }
        );

    /// <summary>The ids the Build menu is composed from, so a literal cannot drift from a registration.</summary>
    /// <inheritdoc cref="EditorApplication.ViewportIds" select="remarks" />
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
}
