// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.HotReload;

namespace Vixen.Editor.Terrain;

/// <summary>Doc 31's toolset, registering itself through the door a third party comes through.</summary>
/// <remarks>
///     <para>
///         <b>Two modes, five panels, and everything that binds them to the scene in front of
///         them.</b> All of it used to be two partials of the editor's application —
///         <c>EditorTerrainPanels</c> and <c>EditorTerrainSession</c>, 1,340 lines — which meant the
///         terrain tools could only exist in an editor that had been compiled with them. Doc 36 § F2
///         is the finding; this type is the answer for this feature.
///     </para>
///     <para>
///         ⚠ <b>What it takes from the host, it asks for.</b> The project and the scene, through
///         <c>PluginServices.Require</c>. A host that has not got one refuses the module with a
///         sentence naming it, rather than throwing a null reference from inside
///         <see cref="Activate" />.
///     </para>
///     <para>
///         ⚠ <b>Two hooks that had to be built for this, because the application used to do both by
///         hand.</b> <c>PluginContext.OnUpdate</c> — the brush follows the entity selection, and
///         nothing raises an event about that — and <c>EditorDocument.Saved</c>, because a scene
///         names a heightfield and a foliage file beside itself and saving one without the others
///         leaves a project whose ground exists only in a process that has exited. Both were a line
///         the application had to know to write, and the second feature with sidecars would have been
///         a second line.
///     </para>
/// </remarks>
public sealed partial class TerrainModule : IEditorPlugin {
    /// <summary>What the host activates it under, and what a plugin depending on it names.</summary>
    public const string ModuleId = "vixen.terrain";

    /// <summary>What a plugin-management panel calls it.</summary>
    public const string ModuleName = "Terrain";

    EditorProject project = null!;
    SceneDocument document = null!;
    EditorShell shell = null!;
    IEditorRegistry extensions = null!;

    /// <summary>The project the tools read their assets out of and write their files into.</summary>
    EditorProject Project => project;

    /// <summary>The scene the brushes are pointed at.</summary>
    SceneDocument Scene => document;

    /// <summary>Its world, which is where a terrain entity lives.</summary>
    World World => document.World;

    /// <summary>The chrome the panels and the modes are registered into.</summary>
    EditorShell Shell => shell;

    /// <summary>What has been contributed, including this module's own.</summary>
    /// <remarks>
    ///     ⚠ <b>The host's, held so a panel built later can read it.</b> A panel factory runs when
    ///     the panel is opened, which is long after <see cref="Activate" /> — and
    ///     <c>EditorRegistry.Default</c> is process-wide, so a panel reaching for that would find
    ///     whatever some other editor in the process registered.
    /// </remarks>
    IEditorRegistry Extensions => extensions;

    /// <summary>Where the scene file is, which is what the foliage file sits beside.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off the document's writer rather than held.</b> The application used to hold a
    ///     <c>scenePath</c> field and this code read it; a module cannot, and a copy of its own would
    ///     be a second answer that drifts the first time somebody uses Save As. Empty for a scene
    ///     that has never been written, which <see cref="FoliagePathOf" /> answers with nothing.
    /// </remarks>
    string ScenePath => Scene.Writer is SceneFileWriter file ? file.Path : string.Empty;

    /// <summary>One "label: value" row in a derived-facts readout.</summary>
    /// <remarks>
    ///     ⚠ <b>A copy of the application's own four lines rather than a contract for it.</b> Doc 20's
    ///     <c>(derived)</c> convention is a shape — a row, a name, a value — and the styling is the
    ///     theme's; a shared helper would be an assembly reference for four lines of element
    ///     building, which is the trade `PluginServices`' own remarks warn against.
    /// </remarks>
    static void Fact(UiElement into, string label, string value) {
        var row = into.Add("fact-row");

        row.Add("fact-name").Text = label;
        row.Add("fact-value").Add("text").Text = value;
    }

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        project = context.Services.Require<EditorProject>();
        document = context.Services.Require<SceneDocument>();
        shell = context.Shell;

        // ⚠ Before the panels, because a panel factory closes over `Extensions`. It runs when the
        // panel is opened rather than now, so a null here would be a panel that reads the
        // process-wide registry — but assigning it after registering the factories is a reading
        // order nobody should have to check.
        extensions = context.Services.Require<IEditorRegistry>();

        TerrainPanels();
        RegisterTerrainModes();

        // ⚠ Contributed rather than fetched. The viewport's presenter needs to know what ground to
        // draw and where; the application used to read a property off this code because both were
        // the same class. A contribution is how the presenter finds it without either end naming the
        // other — and it goes away when the module does, so a pane cannot be left drawing terrain
        // out of an unloaded assembly.
        var registry = extensions;

        context.Owns(registry.Add(TerrainScene));

        // ⚠ And the pictures for the five file kinds this module introduces. The Project panel draws
        // them without knowing terrain exists, which is doc 36 § D6's claim and the reason this is
        // here rather than in the application's own `StandardIcons`: an asset type whose icon cannot
        // be declared by whoever introduced it is an asset type that is visibly second-class in the
        // panel that shows it, which is F3's problem in the other pane.
        foreach (var icon in TerrainIcons) {
            context.Owns(registry.Add(icon));
        }

        // ⚠ Doc 36 § P4, and F7's answer. `TerrainBrushInspector` is a `.vxml` file with no `@code`
        // block: every row in it is a `<PropertyField Path="…" />`, resolved against the edit target
        // the inspector hands over and drawn by whatever drawer claims the member. What the markup
        // adds is the grouping — a brush is a shape, a stroke and a pattern, and the generated
        // inspector can only draw the members in the order they were declared.
        //
        // ⚠ Mounted through the host's reload host rather than built directly, which is what makes it
        // an authoring path rather than a slower way to write C#: edit the `.vxml`, and the panel is
        // different a second later. A host that publishes none still gets the panel — see
        // `MarkupInspector.Of`.
        context.Owns(
            registry.Add(
                new CustomInspector(
                    typeof(TerrainBrushSettings),
                    MarkupInspector.Of<TerrainBrushInspector>(
                        context.Services.TryGet<HotReloadHost>(out var reload) ? reload : null
                    )
                )
            )
        );

        // ⚠ The brush follows the *entity* selection, and nothing raises an event about that. Once a
        // frame is what the application used to do by hand; this is the same call, asked for rather
        // than written into the loop.
        context.OnUpdate(_ => FollowTerrainSelection());

        // ⚠ With the scene, not on a verb of its own. A scene names a heightfield and a foliage file
        // beside itself; saving one without the others is a project in which the ground the scene was
        // saved with only exists in a process that has since exited.
        document.Saved += Written;
        context.OnUnload(() => document.Saved -= Written);
    }

    void Written(EditorDocument saved) {
        SaveTerrains();
        SaveFoliage();
    }

    /// <summary>Makes a panel claim a command context when it is pressed in.</summary>
    /// <remarks>
    ///     ⚠ <b>On the capture leg, so it lands before anything inside the panel handles the
    ///     press.</b> Leaving a context matters as much as entering one: clicking a terrain slider
    ///     has to stop Delete meaning "delete the selected entity". The application has a copy of
    ///     this for its own panels; it is eight lines over public API, which is cheaper than a
    ///     contract for it.
    /// </remarks>
    void Contextual(DockPanel panel, string context) =>
        panel.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action == PointerAction.Pressed) {
                    Shell.Context = context;
                }
            },
            RoutingStrategy.Capture,
            handledEventsToo: true
        );
}
