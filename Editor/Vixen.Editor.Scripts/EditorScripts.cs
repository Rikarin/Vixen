// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Editor.Assets;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;

namespace Vixen.Editor.Scripts;

/// <summary>What a project's editor scripts are, after a build and a load.</summary>
/// <param name="Build">What the compiler produced and said.</param>
/// <param name="Loaded">Whether an assembly is loaded and active.</param>
/// <param name="Menus">How many menu items the scripts contributed.</param>
/// <param name="Plugins">How many <see cref="IEditorPlugin" />s in them were activated.</param>
public readonly record struct ScriptState(ScriptBuild Build, bool Loaded, int Menus, int Plugins);

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
    public ScriptState State { get; private set; } = new(ScriptBuild.None, Loaded: false, 0, 0);

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
        var module = new ScriptModule(assembly);

        var plugin = host.Activate(PluginId, PluginName, module, context);

        State = new(build, plugin.State == PluginState.Active, module.Menus, module.Plugins);
        Rebuilt?.Invoke(State);

        return State;
    }

    /// <summary>Takes the scripts back out, if any are loaded.</summary>
    /// <returns>Whether anything was loaded to unload.</returns>
    public bool Unload() {
        var unloaded = host.Unload(PluginId);

        State = State with { Loaded = false, Menus = 0, Plugins = 0 };
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
sealed class ScriptModule(Assembly assembly) : IEditorPlugin {
    readonly List<IEditorPlugin> plugins = [];

    /// <summary>How many menu items the scripts declared.</summary>
    public int Menus { get; private set; }

    /// <summary>How many <see cref="IEditorPlugin" />s the scripts declared.</summary>
    public int Plugins => plugins.Count;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Every menu item is collected before any is registered, because <c>Priority</c> orders
    ///     them against each other.</b> Registering as they are found would make the order the one the
    ///     compiler happened to enumerate types in, and the attribute's priority a number nothing
    ///     read — which is the "attribute that looks like a mechanism" this document declined to ship
    ///     twice already.
    /// </remarks>
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        List<(EditorMenuAttribute Item, MethodInfo Method)> items = [];

        foreach (var type in Types()) {
            Menu(type, items);
            Plugin(context, type);
            Importer(type);
        }

        // ⚠ A stable sort, so a tie is discovery order — which is file order, because
        // `ScriptCompiler.Sources` sorts the files. That is stable between two runs on one machine
        // and between two machines, which is the most an unpriced menu can promise.
        foreach (var (item, method) in items.OrderBy(entry => entry.Item.Priority)) {
            Register(context, item, method);
            Menus++;
        }
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

    static void Menu(Type type, List<(EditorMenuAttribute Item, MethodInfo Method)> into) {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)) {
            if (method.GetCustomAttribute<EditorMenuAttribute>() is not { } item) {
                continue;
            }

            if (method.GetParameters().Length != 0) {
                throw new PluginException(
                    $"'{type.Name}.{method.Name}' carries [EditorMenu] and takes arguments. A menu item "
                    + "is a verb with nothing to pass it, so the method has to take none."
                );
            }

            into.Add((item, method));
        }
    }

    /// <summary>Puts one menu item's command in the shell and its line in the menu.</summary>
    /// <remarks>
    ///     ⚠ <b>The path creates whatever of itself does not exist.</b> <c>"Tools/My Thing/Do It"</c>
    ///     finds or adds <c>Tools</c>, then <c>My Thing</c> under it, then the line — so two scripts
    ///     naming the same menu land in the same one and neither has to know the other exists. Menus
    ///     compose because nobody owns the tree.
    /// </remarks>
    static void Register(PluginContext context, EditorMenuAttribute item, MethodInfo method) {
        var parts = item.Path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2) {
            throw new PluginException(
                $"'{item.Path}' is not a menu path. It needs at least a menu and a line — \"Tools/Do It\"."
            );
        }

        var id = string.IsNullOrEmpty(item.Id) ? Derive(item.Path) : item.Id;
        var label = parts[^1];

        var command = context.AddCommand(
            id,
            new StringId("editor.command." + id, label),

            // ⚠ Invoked rather than turned into a delegate. A script's method belongs to a
            // collectible context, and a `CreateDelegate` held by the command registry would keep
            // that context alive after the scripts were unloaded — which is the leak this whole
            // arrangement exists to avoid, arriving through the one line that looked like a
            // micro-optimisation.
            () => method.Invoke(null, null)
        );

        _ = command;

        var group = context.FindMenu("editor.menu." + parts[0].ToLowerInvariant())
            ?? context.AddMenu(new StringId("editor.menu." + parts[0].ToLowerInvariant(), parts[0]));

        for (var level = 1; level < parts.Length - 1; level++) {
            group = context.AddSubmenu(group, new StringId($"editor.menu.{id}.{level}", parts[level]));
        }

        context.AddMenuItem(group, id);
    }

    /// <summary>The command id a path with no declared one gets.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the path, which means renaming the item drops the user's keybinding.</b>
    ///     That is the cost of not making every script author invent an id, and
    ///     <c>EditorMenuAttribute.Id</c> is how anybody who minds opts out.
    /// </remarks>
    static string Derive(string path) =>
        "scripts." + path.Replace('/', '.').Replace(' ', '-').ToLowerInvariant();

    /// <summary>Refuses an asset importer a script declared, and says why.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A project script cannot declare an asset importer, and this is where that is said
    ///         out loud.</b> Doc 36 § F8 made importers contributable — <c>ImporterContributions</c>,
    ///         published through <c>PluginServices</c> — and a packaged plugin can add one. This tier
    ///         cannot, and the reason is structural rather than an oversight.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An importer's name is its settings type's <c>[DataContract]</c> alias</b>, which is
    ///         the tag a <c>.meta</c> file carries and part of the cache key —
    ///         <c>AssetImporter&lt;T&gt;.Name</c> reads it out of <c>TypeRegistry</c>. That descriptor
    ///         is written by <c>Vixen.Core.Reflection.Generator</c>, and the settings' serializer by
    ///         <c>Vixen.Core.Serialization.Generator</c>. A loose <c>.cs</c> file gets neither: this
    ///         assembly compiles it with <c>CSharpCompilation.Create</c> and no generator driver.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The failure without this message is an <c>InvalidOperationException</c> from
    ///         inside <c>ImporterRegistry.Add</c> saying the settings type has no descriptor</b> —
    ///         true, unactionable, and about a type the author did write a <c>[DataContract]</c> on.
    ///         What would close the gap is running the generators over the script compilation, which
    ///         needs them shipped beside the editor and located at run time; doc 36 § P5 names it.
    ///     </para>
    /// </remarks>
    static void Importer(Type type) {
        if (type.IsAbstract || !type.IsClass || !typeof(IAssetImporter).IsAssignableFrom(type)) {
            return;
        }

        throw new PluginException(
            $"'{type.Name}' is an asset importer, and a project's Editor/ folder cannot declare one. An "
            + "importer is named by its settings type's [DataContract] alias, which a source generator "
            + "writes — and editor scripts are compiled without generators, so the alias would not exist. "
            + "Ship it as a plugin instead: a plugin has a build, and `ImporterContributions` is published "
            + "for exactly this."
        );
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
