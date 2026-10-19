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
///         entry point, so the walk is not even new. In-tree code is not scanned:
///         <c>Vixen.Editor.Plugin.Generator</c> writes a module's <c>Declared.Register</c> at compile
///         time from the same attributes (doc 50 § D5), and <c>DeclaredParityTests</c> compiles one
///         fixture through both and holds the two registries equal.
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
        var types = Types(assembly).ToList();

        // ⚠ Collected before any is placed, because `Priority` orders them against each other —
        // and placed by `DeclaredMenus`, which is what the generator's code calls too, so where a
        // path lands is decided in one place for both tiers.
        var menus = new DeclaredMenus(context);

        foreach (var type in types) {
            Submenus(types, type, menus);
            Menus(types, type, menus);
            Inspectors(context, registry, type);
            Drawer(context, type);
            Tool(context, registry, type);
            AssetKinds(types, context, registry, type);
            Overlays(types, context, registry, type);
            Gizmos(context, registry, type);
            Icons(context, registry, type);
        }

        menus.Apply();
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

    /// <summary>Every <c>[EditorMenu]</c> one type declares, in the order it declares them.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three shapes, decided by the member.</b> A <c>static void ()</c> is a verb — a command
    ///         and its line. A <c>static string</c> field is a line for a command something else
    ///         registers. A <c>static IEnumerable&lt;string&gt; ()</c> is a run of those. Anything else
    ///         carrying the attribute is a mistake, and refusing the plugin over it is what makes the
    ///         mistake visible.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Invoked rather than turned into a delegate.</b> The method belongs to a collectible
    ///         context, and a <c>CreateDelegate</c> held by the command registry would keep that context
    ///         alive after the assembly was unloaded — the leak this whole arrangement exists to avoid,
    ///         arriving through the one line that looks like a micro-optimisation.
    ///     </para>
    /// </remarks>
    static void Menus(IReadOnlyList<Type> types, Type type, DeclaredMenus menus) {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)) {
            if (method.GetCustomAttribute<EditorMenuAttribute>() is not { } item) {
                continue;
            }

            var where = $"{type.Name}.{method.Name}";

            if (method.GetParameters().Length != 0) {
                throw new PluginException(
                    $"'{where}' carries [EditorMenu] and takes arguments. A menu item is a verb with "
                    + "nothing to pass it, so the method has to take none."
                );
            }

            if (method.ReturnType == typeof(void)) {
                menus.Command(
                    item.Path,
                    () => method.Invoke(null, null),
                    item.Id,
                    Title(types, item.Title, item.Strings, where),
                    item.Priority,
                    item.Separator
                );
            } else if (method.ReturnType == typeof(IEnumerable<string>)) {
                Lineal(item, where);

                menus.Lines(
                    item.Path,
                    (IEnumerable<string>) (method.Invoke(null, null) ?? Array.Empty<string>()),
                    item.Priority,
                    item.Separator
                );
            } else {
                throw new PluginException(
                    $"'{where}' carries [EditorMenu] and returns {method.ReturnType.Name}. A verb "
                    + "returns nothing; a run of lines for commands registered elsewhere returns "
                    + "IEnumerable<string>."
                );
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)) {
            if (field.GetCustomAttribute<EditorMenuAttribute>() is not { } item) {
                continue;
            }

            var where = $"{type.Name}.{field.Name}";

            if (field.FieldType != typeof(string)) {
                throw new PluginException(
                    $"'{where}' carries [EditorMenu] and is not a string. On a field the attribute puts "
                    + "a line in a menu for the command whose id the field holds."
                );
            }

            Lineal(item, where);

            var id = (string?) (field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null));

            if (string.IsNullOrEmpty(id)) {
                throw new PluginException($"'{where}' carries [EditorMenu] and holds no command id.");
            }

            menus.Line(item.Path, id, item.Priority, item.Separator);
        }
    }

    /// <summary>Refuses what only a verb can say, on a line for somebody else's command.</summary>
    static void Lineal(EditorMenuAttribute item, string where) {
        if (item.Id.Length != 0 || item.Title.Length != 0 || item.Strings is not null) {
            throw new PluginException(
                $"'{where}' names an Id or a Title on a line for a command registered elsewhere. That "
                + "command already has both; only a verb, which makes its command, can say them."
            );
        }
    }

    /// <summary>Every <c>[EditorSubmenu]</c> one type declares.</summary>
    static void Submenus(IReadOnlyList<Type> types, Type type, DeclaredMenus menus) {
        foreach (var item in type.GetCustomAttributes<EditorSubmenuAttribute>()) {
            menus.Submenu(
                item.Path,
                Title(types, item.Title, item.Strings, $"[EditorSubmenu(\"{item.Path}\")] on {type.Name}"),
                item.After,
                item.Priority
            );
        }
    }

    /// <summary>A title, by the name of its declaration, or null for none.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 50 § D5's rule, and the generator applies the same one</b>: in the class named
    ///         when one is, otherwise in the one class of the declaring assembly whose name ends in
    ///         <c>Strings</c> and which has a public static <c>StringId</c> of that name. A name two
    ///         classes have is refused rather than guessed, because the guess would be whichever the
    ///         runtime enumerated first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Never a silent literal.</b> A title that resolved to nothing would otherwise come
    ///         out as its own member name — <c>MenuGeometry</c> on somebody's menu bar — which looks
    ///         like a missing translation rather than a broken declaration.
    ///     </para>
    /// </remarks>
    static StringId? Title(IReadOnlyList<Type> types, string name, Type? strings, string where) {
        if (string.IsNullOrEmpty(name)) {
            return strings is null
                ? null
                : throw new PluginException($"'{where}' names a Strings class and no Title to find in it.");
        }

        IEnumerable<Type> candidates = strings is not null
            ? [strings]
            : types.Where(static type => type.Name.EndsWith("Strings", StringComparison.Ordinal));

        List<(Type Type, StringId Id)> found = [];

        foreach (var candidate in candidates) {
            if (Declaration(candidate, name) is { } id) {
                found.Add((candidate, id));
            }
        }

        return found.Count switch {
            1 => found[0].Id,
            0 => throw new PluginException(
                $"'{where}' has Title = \"{name}\", and "
                + (strings is not null ? strings.Name : "no class of its assembly whose name ends in Strings")
                + $" declares a public static StringId called {name}."
            ),
            _ => throw new PluginException(
                $"'{where}' has Title = \"{name}\", which "
                + string.Join(" and ", found.Select(static entry => entry.Type.Name))
                + " both declare. Say which with Strings = typeof(…)."
            )
        };
    }

    static StringId? Declaration(Type type, string name) {
        if (type.GetProperty(name, BindingFlags.Public | BindingFlags.Static) is { } property
            && property.PropertyType == typeof(StringId)) {
            return (StringId) property.GetValue(null)!;
        }

        if (type.GetField(name, BindingFlags.Public | BindingFlags.Static) is { } field
            && field.FieldType == typeof(StringId)) {
            return (StringId) field.GetValue(null)!;
        }

        return null;
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

    static void AssetKinds(IReadOnlyList<Type> types, PluginContext context, IEditorRegistry registry, Type type) {
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
                ? Derive("assets.create", declared.Text)
                : declared.Id;

            var name = string.IsNullOrEmpty(declared.DefaultName) ? declared.Text : declared.DefaultName;

            // ⚠ A declaration when it names one, so the line follows a live language switch (#1284);
            // otherwise the text under the id the command's title always had, which is what a script
            // with no catalogue means. The generator writes the same two branches.
            var title = Title(types, declared.Title, declared.Strings, $"{type.Name}.{method.Name}")
                ?? Untranslated("editor.command.", id, declared.Text);

            // ⚠ A delegate rather than the string it returns, so the method runs per file rather than
            // once when the plugin loaded — see `NewAssetKind.Build`. Invoked rather than turned into
            // a delegate for the reason `Menu` gives: a `CreateDelegate` held by the registry would
            // outlive the collectible context the method belongs to.
            context.Owns(
                registry.Add(
                    new NewAssetKind(id, title, declared.Extension, name, Opens: declared.Opens, Order: declared.Order) {
                        Build = () => (string?) method.Invoke(null, null) ?? string.Empty
                    }
                )
            );
        }
    }

    // ================================================================= [Overlay]

    static void Overlays(IReadOnlyList<Type> types, PluginContext context, IEditorRegistry registry, Type type) {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)) {
            if (method.GetCustomAttribute<OverlayAttribute>() is not { } declared) {
                continue;
            }

            var parameters = method.GetParameters();

            if (method.ReturnType != typeof(void)
                || parameters.Length != 2
                || parameters[0].ParameterType != typeof(UiElement)
                || parameters[1].ParameterType != typeof(IViewportPane)) {
                throw new PluginException(
                    $"'{type.Name}.{method.Name}' carries [Overlay] and is not a "
                    + "`static void (UiElement host, IViewportPane pane)`. That is what an overlay is: it "
                    + "fills the host for one pane."
                );
            }

            var id = string.IsNullOrEmpty(declared.Id) ? Derive("overlays", declared.Text) : declared.Id;
            var title = Title(types, declared.Title, declared.Strings, $"{type.Name}.{method.Name}")
                ?? Untranslated("editor.overlay.", id, declared.Text);
            var build = method.CreateDelegate<Action<UiElement, IViewportPane>>();

            context.Owns(
                registry.Add(new SceneOverlay(id, title, build, declared.Corner, declared.Order))
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

    /// <summary>Doc 36 § D6: <c>[EditorIcon]</c> on a type, as the picture every surface serves.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one contribution kind whose attribute doc 36 wrote out and nothing implemented.</b>
    ///         <c>EditorArt</c> said so in its own remarks — "there is no SVG path parser in this
    ///         repository and its absence is a decision" — and what that cost a plugin author was a
    ///         component with no picture in the outliner, the inspector header, the Add Component list
    ///         and the Project panel. <see cref="SvgPath" /> is the parser; this is the four lines that
    ///         were waiting for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A bad icon is a diagnostic and the plugin still loads.</b> Every other declaration
    ///         here throws a <c>PluginException</c>, and that is right for them: a <c>[CustomInspector]</c>
    ///         on the wrong shape of class is a mistake that makes the plugin's own code unreachable.
    ///         An icon is decoration — refusing to load a terrain module because somebody fat-fingered
    ///         a path string would be the editor holding a feature hostage over a picture.
    ///     </para>
    /// </remarks>
    static void Icons(PluginContext context, IEditorRegistry registry, Type type) {
        if (type.GetCustomAttribute<EditorIconAttribute>() is not { } declared) {
            return;
        }

        if (declared.Resolve(context.Directory, out var reason) is not { } art) {
            context.Shell.Notifications.Show(
                $"{context.Manifest.Name}: an icon could not be read",
                NotificationSeverity.Warning,
                $"[EditorIcon] on '{type.Name}' {reason}."
            );

            return;
        }

        context.Owns(registry.Add(new TypeIcon(type, art, declared.Order)));
    }

    // =================================================================== Shared

    /// <summary>Makes one, or nothing if the type cannot be made.</summary>
    static object? Instantiable(Type type) =>
        type is { IsClass: true, IsAbstract: false } && type.GetConstructor(Type.EmptyTypes) is not null
            ? Activator.CreateInstance(type)
            : null;

    /// <summary>A title for a declaration that names no <c>StringId</c>: its text, under an id built from the contribution's.</summary>
    /// <remarks>
    ///     ⚠ <b>A run-time id on purpose, and in one place so <c>CheckStrings</c> counts it once.</b> It
    ///     is what a script with no catalogue means — the words it wrote, which no template can hold —
    ///     and <c>DeclaredMenus</c> builds a verb's the same way. A catalogue that wants the line
    ///     translated names a declaration with <c>Title</c> instead (#1284).
    /// </remarks>
    static StringId Untranslated(string prefix, string id, string text) => new StringId(prefix + id, text);

    /// <summary>The id something with no declared one gets — <see cref="DeclaredMenus.Derive" />, shared.</summary>
    static string Derive(string prefix, string text) => DeclaredMenus.Derive(prefix, text);
}
