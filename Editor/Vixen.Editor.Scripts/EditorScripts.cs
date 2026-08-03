// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Reflection;
using Vixen.Editor.Assets;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;

namespace Vixen.Editor.Scripts;

/// <summary>What a project's editor scripts are, after a build and a load.</summary>
/// <param name="Build">What the compiler produced and said.</param>
/// <param name="Loaded">Whether an assembly is loaded and active.</param>
/// <param name="Plugins">How many <see cref="IEditorPlugin" />s in them were activated.</param>
/// <param name="Importers">How many asset importers they contributed.</param>
public readonly record struct ScriptState(ScriptBuild Build, bool Loaded, int Plugins, int Importers = 0);

/// <summary>A project's <c>Editor/</c> folder, compiled and loaded like a plugin.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P5, and Unity's headline workflow.</b> Drop a <c>.cs</c> file into a project's
///         <c>Editor/</c> folder, and its menu item is there without restarting the editor. A compile
///         error is a list somebody can read, not a crash and not a silence.
///     </para>
///     <para>
///         ⚠ <b>A script is a plugin, and that is the whole design.</b> The compiled assembly goes
///         into a <see cref="PluginLoadContext" /> and through <c>PluginHost.Activate</c>, so it gets
///         the registration scope, the rollback-on-throw, the diagnostics, the plugin manager's row
///         and the unload that a plugin dropped in a folder gets. A script host that reimplemented any
///         of those would be a second answer to a question that already has one — and the one it would
///         get wrong is the unload, which is where every leak in this part of the editor lives.
///     </para>
///     <para>
///         ⚠ <b>One assembly for the whole folder, not one per file.</b> Scripts refer to each other
///         — a menu item calls a helper in the file next to it — and a compilation unit per file
///         would make that impossible for no gain. It also means one build, one unload and one
///         reload: a save rebuilds everything, which for a folder of a dozen files is tens of
///         milliseconds.
///     </para>
///     <para>
///         ⚠ <b>A failed build leaves the previous one loaded, deliberately.</b> Somebody halfway
///         through typing a method name should not lose the menu they were about to use. What they
///         get is the errors and the editor they had; what they must not get is an editor whose tools
///         silently vanished because of a missing semicolon.
///     </para>
/// </remarks>
public sealed class EditorScripts {
    /// <summary>The id the plugin host holds a project's scripts under.</summary>
    public const string PluginId = "vixen.project.editor-scripts";

    /// <summary>What a plugin-management panel calls them.</summary>
    public const string PluginName = "Project Editor Scripts";

    readonly PluginHost host;
    readonly string projectRoot;
    readonly string output;

    /// <summary>Watches one project's editor scripts.</summary>
    /// <param name="host">Where a compiled script assembly is activated.</param>
    /// <param name="projectRoot">The project's root.</param>
    /// <param name="output">Where the built assembly is written — the project's library folder.</param>
    public EditorScripts(PluginHost host, string projectRoot, string output) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentException.ThrowIfNullOrEmpty(output);

        this.host = host;
        this.projectRoot = projectRoot;
        this.output = output;
    }

    /// <summary>What the last build produced, and what it loaded.</summary>
    public ScriptState State { get; private set; } = new(ScriptBuild.None, Loaded: false, 0);

    /// <summary>Raised after every build, whether or not it produced anything.</summary>
    /// <remarks>
    ///     What the errors panel listens to. Raised for a failure as well as a success, because a
    ///     panel that only heard about builds that worked would go on showing the errors from two
    ///     saves ago.
    /// </remarks>
    public event Action<ScriptState>? Rebuilt;

    /// <summary>Compiles the project's editor scripts and loads what came out.</summary>
    /// <returns>What happened.</returns>
    public ScriptState Rebuild() {
        var build = ScriptCompiler.Compile(projectRoot, output);

        if (build.AssemblyPath is null) {
            // ⚠ The previous assembly is left alone. See the class remarks: an editor whose tools
            // vanish because of a missing semicolon is worse than one showing yesterday's build.
            State = State with { Build = build };
            Rebuilt?.Invoke(State);

            return State;
        }

        // ⚠ Before the load, and it has to be. The context reads the file into memory rather than
        // mapping it, so the *file* was already free — what is not free is the id, and two script
        // assemblies active under one id is what `PluginHost.Activate` refuses.
        host.Unload(PluginId);

        var context = new PluginLoadContext(build.AssemblyPath, "vixen-scripts:" + Path.GetFileName(projectRoot));
        var assembly = context.LoadPlugin();
        var module = new ScriptModule(host, assembly);

        var plugin = host.Activate(PluginId, PluginName, module, context);

        State = new(build, plugin.State == PluginState.Active, module.Plugins, module.Importers);
        Rebuilt?.Invoke(State);

        return State;
    }

    /// <summary>Takes the scripts back out, if any are loaded.</summary>
    /// <returns>Whether anything was loaded to unload.</returns>
    public bool Unload() {
        var unloaded = host.Unload(PluginId);

        State = State with { Loaded = false, Plugins = 0, Importers = 0 };
        return unloaded;
    }
}

/// <summary>One project's script assembly, as the thing the plugin host activates.</summary>
/// <remarks>
///     ⚠ <b>The one place in the editor that enumerates an assembly's types, and the reason is
///     bounded.</b> ADR-002 forbids assembly scanning as a way of building the editor, for two
///     reasons that both hold: a scan reads metadata a trimmed publish has deleted, and start-up cost
///     grows with what is installed. Neither applies to an assembly the editor compiled from source
///     seconds ago, in a folder it is watching, in a process that has no publish. What a project's
///     script author cannot do is run a source generator over a loose <c>.cs</c> file, and that is the
///     whole of why this tier is different from the other two.
/// </remarks>
/// <summary>One project's script assembly, as the thing the plugin host activates.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The declarations are read by the host's scanners, not by this class.</b> Doc 36 § D3:
///         <c>[EditorMenu]</c>, <c>[CustomInspector]</c>, <c>[CustomDrawer]</c> and
///         <c>[EditorTool]</c> mean the same thing in a project's <c>Editor/</c> folder as in a
///         packaged plugin, and they do because both go through <c>PluginHost.Declared</c>. This
///         class used to read <c>[EditorMenu]</c> itself, which is how the two tiers came to disagree
///         about three of the four.
///     </para>
///     <para>
///         What is left here is what is genuinely the script tier's: instantiating the
///         <c>IEditorPlugin</c>s a script declared, and refusing an asset importer with the reason.
///     </para>
/// </remarks>
sealed class ScriptModule(PluginHost host, Assembly assembly) : IEditorPlugin {
    readonly List<IEditorPlugin> plugins = [];

    /// <summary>How many <see cref="IEditorPlugin" />s the scripts declared.</summary>
    public int Plugins => plugins.Count;

    /// <summary>How many asset importers the scripts contributed.</summary>
    public int Importers { get; private set; }

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var type in Types()) {
            Plugin(context, type);
            Importer(context, type);
        }

        // ⚠ After the scripts' own plugins, so a hand-written registration in a script beats an
        // attribute in the same folder — the same order a packaged plugin gets, where `Activate` runs
        // before the scan. "The code I wrote wins over the attribute I forgot about" is the rule, and
        // the two tiers have to agree about it.
        host.Declared(context, assembly);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Each script plugin's own <c>Deactivate</c> is called, and a throw in one does not stop
    ///     the next.</b> Everything they *registered* goes with the scope whatever happens here, which
    ///     is why this can afford to keep going: the worst a badly behaved script can do is fail to
    ///     tidy up something the editor was not tracking.
    /// </remarks>
    public void Deactivate() {
        foreach (var plugin in plugins) {
            try {
                plugin.Deactivate();
            } catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
                // Swallowed here and reported by the host, which owns the diagnostics list. A script
                // that throws on the way out must not stop the assembly being unloaded.
                _ = exception;
            }
        }

        plugins.Clear();

        // ⚠ The descriptors go with the assembly, and the order matters as much as the act. A
        // `TypeDescriptor` holds the settings `Type` and a factory closed over it, so one left behind
        // names a type in a context that is being unloaded — which keeps that context alive, so the
        // unload never completes and the next build cannot overwrite the file. `ProjectAssemblies`
        // evicts the same four registries for the same reason.
        TypeRegistry.Evict(assembly);
        Importers = 0;
    }

    /// <summary>Every type the script assembly declares that it can load.</summary>
    /// <remarks>
    ///     ⚠ <b>A load failure is not fatal.</b> <c>GetTypes</c> throws when a type references
    ///     something the context could not resolve, and hands back the ones it could — which for a
    ///     script referencing an assembly the editor has not loaded is exactly the useful half.
    /// </remarks>
    Type[] Types() {
        try {
            return assembly.GetTypes();
        } catch (ReflectionTypeLoadException failure) {
            return [.. failure.Types.Where(type => type is not null).Select(type => type!)];
        }
    }

    /// <summary>Contributes an asset importer a script declared.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 36 § F8 and § D3's <c>[AssetImporter(".fbx")]</c>, under the name it has:
    ///         <c>[Importer]</c>.</b> A game author defines an importer for their own format in the
    ///         project's <c>Editor/</c> folder, the imported asset appears in the Project view, and a
    ///         runtime component in <c>Assets/</c> takes a reference to it. That whole pipeline is the
    ///         point, and this is the first step of it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The settings type is described by reflection first, and that is the only thing
    ///         that was ever missing.</b> An importer is <i>named</i> by its settings'
    ///         <c>[DataContract]</c> alias, which <c>TypeRegistry</c> answers and a generator normally
    ///         writes — and a script is compiled without one. <c>YamlSerializer</c> and
    ///         <c>ArtifactKey</c> both go through the same registry, so one descriptor is the whole
    ///         fix. See <see cref="ReflectedTypes" />, and why it may only exist in the editor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Through the published contributions, not the static.</b> A host running two
    ///         editors publishes two, and a script reaching for <c>Default</c> would write to
    ///         whichever the process happened to make first. A host that publishes none gets a script
    ///         whose importer is not registered, which is the degradation every optional service gets.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An importer is contributed, not applied to what is already imported.</b> The
    ///         assets in the project were imported by whatever claimed them before this loaded; a file
    ///         the new importer claims picks it up on the next import of that file. Re-importing the
    ///         project on every script rebuild would be minutes of work for a keystroke.
    ///     </para>
    /// </remarks>
    void Importer(PluginContext context, Type type) {
        if (type.IsAbstract || !type.IsClass || !typeof(IAssetImporter).IsAssignableFrom(type)) {
            return;
        }

        if (type.GetConstructor(Type.EmptyTypes) is null) {
            throw new PluginException(
                $"'{type.Name}' is an asset importer with no parameterless constructor, so nothing could "
                + "make one. An importer is constructed by the editor and asked what it claims."
            );
        }

        if (Attribute.GetCustomAttribute(type, typeof(ImporterAttribute)) is null) {
            throw new PluginException(
                $"'{type.Name}' is an asset importer with no [Importer] attribute, so nothing knows which "
                + "files it claims. Write [Importer(\".myext\")] on the class."
            );
        }

        var importer = (IAssetImporter) Activator.CreateInstance(type)!;

        // ⚠ Before `Name` is read, and `ImporterRegistry.Add` reads it. Without the descriptor the
        // failure is "WidgetImportSettings has no descriptor" from inside the registry — true,
        // unactionable, and about a type the author did put [DataContract] on.
        try {
            ReflectedTypes.Register(importer.SettingsType);
        } catch (InvalidOperationException failure) {
            throw new PluginException($"'{type.Name}' cannot be registered: {failure.Message}");
        }

        if (context.Services.TryGet<ImporterContributions>(out var importers)) {
            context.Owns(importers.Add(importer));
        }

        Importers++;
    }

    void Plugin(PluginContext context, Type type) {
        if (type.IsAbstract || !type.IsClass || !typeof(IEditorPlugin).IsAssignableFrom(type)) {
            return;
        }

        if (type.GetConstructor(Type.EmptyTypes) is null) {
            throw new PluginException(
                $"'{type.Name}' is an IEditorPlugin with no parameterless constructor, so nothing could "
                + "make one. A script's plugin is constructed by the editor and handed its context."
            );
        }

        var plugin = (IEditorPlugin) Activator.CreateInstance(type)!;

        // ⚠ Handed the *same* context this module was activated with, so everything a script plugin
        // registers is owned by the one scope the whole script assembly shares. That is right rather
        // than convenient: the assembly is compiled, loaded and unloaded as a unit, so a scope per
        // script would be several scopes that can only ever be disposed together.
        plugin.Activate(context);
        plugins.Add(plugin);
    }
}
