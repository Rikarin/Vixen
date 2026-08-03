// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>Doc 36 § D3's attributes, read out of one loaded assembly.</summary>
/// <remarks>
///     <para>
///         <b>Four attributes, one scan, both tiers.</b> A plugin's assembly and a project's script
///         assembly get identical treatment — <c>[EditorMenu]</c>, <c>[CustomInspector]</c>,
///         <c>[CustomDrawer]</c> and <c>[EditorTool]</c> all work in both. Before this,
///         <c>[EditorMenu]</c> worked only in a script and the other three nowhere, which is an
///         asymmetry nobody could have predicted from reading the attributes.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Vixen.Editor.Plugin</c>, and that is what
///         <see cref="IContributionScanner" /> exists for.</b> The attributes name
///         <c>CustomInspector</c>, <c>DrawerRegistry</c> and <c>SceneTool</c>, which live in the
///         inspector and scene-view assemblies; the plugin contract must not reference either, or it
///         would reference every feature assembly that owns a contribution kind. That is P2's rule
///         and it is F2's problem one layer down.
///     </para>
///     <para>
///         ⚠ <b>The scan is bounded, which is why ADR-002 permits it.</b> That rule forbids assembly
///         scanning as a way of building the editor, for two reasons: a scan reads metadata a trimmed
///         publish has already deleted, and start-up cost grows with what is installed. Neither
///         applies to one assembly the editor has just loaded off disk or just compiled from a folder
///         it is watching — and the plugin loader already enumerates a plugin's types to find its
///         entry point, so the walk is not even new. In-tree code registers the records directly and
///         nothing scans it.
///     </para>
///     <para>
///         ⚠ <b>Everything is registered through the plugin's own context</b>, so it is in that
///         plugin's registration scope and goes when the plugin does. A scanner that reached for a
///         static would leave a dead type in a registry after an unload, which is the one failure the
///         whole plugin arrangement is built to prevent.
///     </para>
/// </remarks>
sealed class DeclaredContributions : IContributionScanner {
    /// <inheritdoc />
    public void Scan(PluginContext context, Assembly assembly) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assembly);

        var registry = context.Services.Require<IEditorRegistry>();

        List<(EditorMenuAttribute Item, MethodInfo Method)> menus = [];

        foreach (var type in Types(assembly)) {
            Menus(type, menus);
            Inspectors(context, registry, type);
            Drawer(context, type);
            Tool(context, registry, type);
            AssetKinds(context, registry, type);
            Overlays(context, registry, type);
            Gizmos(context, registry, type);
        }

        // ⚠ Collected before any is registered, because `Priority` orders them against each other.
        // Registering as they are found would make the order the one the runtime happened to
        // enumerate types in, and the priority a number nothing read.
        foreach (var (item, method) in menus.OrderBy(entry => entry.Item.Priority)) {
            Menu(context, item, method);
        }
    }

    /// <summary>Every type an assembly declares that can be loaded.</summary>
    /// <remarks>
    ///     ⚠ <b>A load failure is not fatal.</b> <c>GetTypes</c> throws when a type references
    ///     something the context could not resolve and hands back the ones it could — which for an
    ///     assembly referencing something this editor has not loaded is exactly the useful half.
    /// </remarks>
    static IEnumerable<Type> Types(Assembly assembly) {
        try {
            return assembly.GetTypes();
        } catch (ReflectionTypeLoadException failure) {
            return failure.Types.Where(type => type is not null).Select(type => type!);
        }
    }

    // ============================================================== [EditorMenu]

    static void Menus(Type type, List<(EditorMenuAttribute Item, MethodInfo Method)> into) {
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
    ///     finds or adds <c>Tools</c>, then <c>My Thing</c> under it, then the line — so two
    ///     declarations naming one menu land in it together and neither has to know the other exists.
    ///     Menus compose because nobody owns the tree.
    /// </remarks>
    static void Menu(PluginContext context, EditorMenuAttribute item, MethodInfo method) {
        var parts = item.Path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2) {
            throw new PluginException(
                $"'{item.Path}' is not a menu path. It needs at least a menu and a line — \"Tools/Do It\"."
            );
        }

        var id = string.IsNullOrEmpty(item.Id) ? Derive("scripts", item.Path) : item.Id;

        // ⚠ Invoked rather than turned into a delegate. The method belongs to a collectible context,
        // and a `CreateDelegate` held by the command registry would keep that context alive after the
        // assembly was unloaded — the leak this whole arrangement exists to avoid, arriving through
        // the one line that looks like a micro-optimisation.
        context.AddCommand(id, new StringId("editor.command." + id, parts[^1]), () => method.Invoke(null, null));

        var top = "editor.menu." + parts[0].ToLowerInvariant();
        var group = context.FindMenu(top) ?? context.AddMenu(new StringId(top, parts[0]));

        for (var level = 1; level < parts.Length - 1; level++) {
            group = context.AddSubmenu(group, new StringId($"editor.menu.{id}.{level}", parts[level]));
        }

        context.AddMenuItem(group, id);
    }

    // ========================================================== [CustomInspector]

    static void Inspectors(PluginContext context, IEditorRegistry registry, Type type) {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)) {
            if (method.GetCustomAttribute<CustomInspectorAttribute>() is not { } declared) {
                continue;
            }

            var parameters = method.GetParameters();

            if (method.ReturnType != typeof(void)
                || parameters.Length != 2
                || parameters[0].ParameterType != typeof(UiElement)
                || parameters[1].ParameterType != typeof(EditTarget)) {
                throw new PluginException(
                    $"'{type.Name}.{method.Name}' carries [CustomInspector] and is not a "
                    + "`static void (UiElement body, EditTarget target)`. That is what a custom inspector "
                    + "is: it fills the body from the target."
                );
            }

            var build = method.CreateDelegate<Action<UiElement, EditTarget>>();

            context.Owns(registry.Add(new CustomInspector(declared.Target, build, declared.Order)));
        }
    }

    // ============================================================ [CustomDrawer]

    static void Drawer(PluginContext context, Type type) {
        if (type.GetCustomAttribute<CustomDrawerAttribute>() is not { } declared) {
            return;
        }

        if (!typeof(IPropertyDrawer).IsAssignableFrom(type) || Instantiable(type) is not { } made) {
            throw new PluginException(
                $"'{type.Name}' carries [CustomDrawer] and is not a concrete IPropertyDrawer with a "
                + "parameterless constructor. The editor makes one and registers it."
            );
        }

        var drawer = (IPropertyDrawer) made;

        // ⚠ Through `With`, not by mutating `DrawerRegistry.Default`. The host publishes the registry
        // its inspector actually reads — a host running two editors publishes two — and `With` records
        // the removal so an unload takes the drawer back out.
        context.With<DrawerRegistry>(
            drawers => {
                if (declared.ForAttribute) {
                    drawers.ForAttribute(declared.Target, drawer);
                } else {
                    drawers.ForType(declared.Target, drawer);
                }
            },
            drawers => drawers.Remove(drawer)
        );
    }

    // ============================================================== [EditorTool]

    static void Tool(PluginContext context, IEditorRegistry registry, Type type) {
        if (type.GetCustomAttribute<EditorToolAttribute>() is not { } declared) {
            return;
        }

        if (!typeof(IViewportInput).IsAssignableFrom(type) || Instantiable(type) is not { } made) {
            throw new PluginException(
                $"'{type.Name}' carries [EditorTool] and is not a concrete IViewportInput with a "
                + "parameterless constructor. The editor makes one and offers it in the scene pane."
            );
        }

        var id = string.IsNullOrEmpty(declared.Id) ? Derive("tools", declared.Title) : declared.Id;

        context.Owns(
            registry.Add(new SceneTool(id, declared.Title, (IViewportInput) made, declared.Target, declared.Order))
        );
    }

    // ========================================================= [CreateAssetMenu]

    static void AssetKinds(PluginContext context, IEditorRegistry registry, Type type) {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)) {
            if (method.GetCustomAttribute<CreateAssetMenuAttribute>() is not { } declared) {
                continue;
            }

            if (method.ReturnType != typeof(string) || method.GetParameters().Length != 0) {
                throw new PluginException(
                    $"'{type.Name}.{method.Name}' carries [CreateAssetMenu] and is not a "
                    + "`static string ()`. It returns what to write into the new file, which is the one "
                    + "thing the menu line cannot say for itself."
                );
            }

            if (!declared.Extension.StartsWith('.')) {
                throw new PluginException(
                    $"'{declared.Extension}' is not an extension. It is what the new file is called "
                    + "after the dot, including it — \".dialogue\"."
                );
            }

            var id = string.IsNullOrEmpty(declared.Id)
                ? Derive("assets.create", declared.Title)
                : declared.Id;

            var name = string.IsNullOrEmpty(declared.DefaultName) ? declared.Title : declared.DefaultName;

            // ⚠ A delegate rather than the string it returns, so the method runs per file rather than
            // once when the plugin loaded — see `NewAssetKind.Build`. Invoked rather than turned into
            // a delegate for the reason `Menu` gives: a `CreateDelegate` held by the registry would
            // outlive the collectible context the method belongs to.
            context.Owns(
                registry.Add(
                    new NewAssetKind(id, declared.Title, declared.Extension, name, Opens: declared.Opens, Order: declared.Order) {
                        Build = () => (string?) method.Invoke(null, null) ?? string.Empty
                    }
                )
            );
        }
    }

    // ================================================================= [Overlay]

    static void Overlays(PluginContext context, IEditorRegistry registry, Type type) {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)) {
            if (method.GetCustomAttribute<OverlayAttribute>() is not { } declared) {
                continue;
            }

            var parameters = method.GetParameters();

            if (method.ReturnType != typeof(void)
                || parameters.Length != 2
                || parameters[0].ParameterType != typeof(UiElement)
                || parameters[1].ParameterType != typeof(SceneViewport)) {
                throw new PluginException(
                    $"'{type.Name}.{method.Name}' carries [Overlay] and is not a "
                    + "`static void (UiElement host, SceneViewport pane)`. That is what an overlay is: it "
                    + "fills the host for one pane."
                );
            }

            var id = string.IsNullOrEmpty(declared.Id) ? Derive("overlays", declared.Title) : declared.Id;
            var build = method.CreateDelegate<Action<UiElement, SceneViewport>>();

            context.Owns(
                registry.Add(new SceneOverlay(id, declared.Title, build, declared.Corner, declared.Order))
            );
        }
    }

    // ============================================================== [DrawGizmo]

    static void Gizmos(PluginContext context, IEditorRegistry registry, Type type) {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)) {
            if (method.GetCustomAttribute<DrawGizmoAttribute>() is not { } declared) {
                continue;
            }

            var parameters = method.GetParameters();

            if (method.ReturnType != typeof(void)
                || parameters.Length != 4
                || parameters[0].ParameterType != typeof(GizmoDraw)
                || parameters[1].ParameterType != typeof(object)
                || parameters[2].ParameterType != typeof(GizmoPlacement)
                || parameters[3].ParameterType != typeof(bool)) {
                throw new PluginException(
                    $"'{type.Name}.{method.Name}' carries [DrawGizmo] and is not a "
                    + "`static void (GizmoDraw draw, object component, GizmoPlacement placement, bool selected)`. "
                    + "The component arrives boxed because the attribute names its type at run time."
                );
            }

            var draw = method.CreateDelegate<GizmoDrawer>();

            context.Owns(
                registry.Add(new ComponentGizmo(declared.Target, draw, declared.SelectedOnly, declared.Order))
            );
        }
    }

    // =================================================================== Shared

    /// <summary>Makes one, or nothing if the type cannot be made.</summary>
    static object? Instantiable(Type type) =>
        type is { IsClass: true, IsAbstract: false } && type.GetConstructor(Type.EmptyTypes) is not null
            ? Activator.CreateInstance(type)
            : null;

    /// <summary>The id something with no declared one gets.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from what a person reads, which means renaming it drops their keybinding.</b>
    ///     That is the cost of not making every author invent an id, and the <c>Id</c> property on
    ///     both attributes is how anybody who minds opts out.
    /// </remarks>
    static string Derive(string prefix, string text) =>
        prefix + "." + text.Replace('/', '.').Replace(' ', '-').ToLowerInvariant();
}
