// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Vixen.Generators.Shared;

namespace Vixen.Editor.Plugin.Generator;

/// <summary>Turns an assembly's declaration attributes into one <c>Declared.Register(PluginContext)</c>.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D3's in-tree half, which was designed and never built, and doc 50 § D5.</b> The
///         attributes — <c>[EditorMenu]</c>, <c>[EditorSubmenu]</c>, <c>[CustomInspector]</c>,
///         <c>[CustomDrawer]</c>, <c>[EditorTool]</c>, <c>[CreateAssetMenu]</c>, <c>[Overlay]</c>,
///         <c>[DrawGizmo]</c> and <c>[EditorIcon]</c> — were read by a bounded reflective scan of a
///         plugin's or a script's assembly and by nothing else, so an in-tree module that wrote one got
///         nothing, and no in-tree module wrote one. This writes the registration at compile time: no
///         reflection, ADR-002 intact, and a module's <c>Activate</c> calls it in one line.
///     </para>
///     <para>
///         ⚠ <b>The generated code is the scan's code with the reflection taken out</b>, and that is
///         a decision about how the two tiers stay equal rather than a coincidence. Each declaration
///         becomes the attribute itself, constructed with the arguments it was written with, handed
///         to the same record constructor the scan calls with the same defaults; placement of menu
///         lines is <c>DeclaredMenus</c>, which the scan calls too. What is left to disagree about is
///         what each reader <i>found</i>, which <c>DeclaredParityTests</c> compares on one fixture.
///     </para>
///     <para>
///         ⚠ <b>Stricter than the scan, deliberately.</b> The scan reads public members and passes
///         silently over a private one carrying an attribute; the generated code can only reach what
///         it can name, so a declaration it cannot reach is a compile error (<c>VXP0002</c>) rather
///         than a line that quietly is not there.
///     </para>
///     <para>
///         ⚠ <b>Not packed into <c>Vixen.Editor.Plugin</c>'s package, and that is the answer to
///         #1283's "say which".</b> A plugin off a disk is scanned after its <c>Activate</c> runs; if
///         that <c>Activate</c> also called a generated <c>Register</c>, every declaration would be
///         registered twice and the second command would throw. So an in-tree module gets this
///         generator, an out-of-tree plugin gets the scan, and the two never meet.
///         <c>docs/GeneratorPackagingExempt.txt</c> carries the line.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class DeclaredRegistrationGenerator : IIncrementalGenerator {
    const string EditorMenu = "Vixen.Editor.Plugin.EditorMenuAttribute";
    const string EditorSubmenu = "Vixen.Editor.Plugin.EditorSubmenuAttribute";
    const string CustomInspector = "Vixen.Editor.Inspector.CustomInspectorAttribute";
    const string CustomDrawer = "Vixen.Editor.Inspector.CustomDrawerAttribute";
    const string EditorTool = "Vixen.Editor.SceneView.EditorToolAttribute";
    const string CreateAssetMenu = "Vixen.Editor.Core.CreateAssetMenuAttribute";
    const string Overlay = "Vixen.Editor.SceneView.OverlayAttribute";
    const string DrawGizmo = "Vixen.Editor.SceneView.DrawGizmoAttribute";
    const string EditorIcon = "Vixen.Editor.Ui.EditorIconAttribute";

    const string StringIdType = "Vixen.Ui.StringId";

    static readonly DiagnosticDescriptor Shape = new(
        "VXP0001",
        "A declaration has the wrong shape for its attribute",
        "{0}",
        "Vixen.Plugin",
        DiagnosticSeverity.Error,
        true
    );

    static readonly DiagnosticDescriptor Unreachable = new(
        "VXP0002",
        "A declaration the generated registration cannot reach",
        "{0}",
        "Vixen.Plugin",
        DiagnosticSeverity.Error,
        true
    );

    static readonly DiagnosticDescriptor Untitled = new(
        "VXP0003",
        "A Title that names no StringId, or names one two classes declare",
        "{0}",
        "Vixen.Plugin",
        DiagnosticSeverity.Error,
        true
    );

    static readonly DiagnosticDescriptor ShortPath = new(
        "VXP0004",
        "A path with too few parts",
        "{0}",
        "Vixen.Plugin",
        DiagnosticSeverity.Error,
        true
    );

    static readonly DiagnosticDescriptor Lineal = new(
        "VXP0005",
        "A menu line for a command registered elsewhere names an Id or a Title",
        "{0}",
        "Vixen.Plugin",
        DiagnosticSeverity.Error,
        true
    );

    static readonly ImmutableDictionary<string, DiagnosticDescriptor> Descriptors =
        new[] { Shape, Unreachable, Untitled, ShortPath, Lineal }.ToImmutableDictionary(static item => item.Id);

    /// <summary>The order the scan visits one type's declarations in, so the two register in the same order.</summary>
    enum Group {
        Submenu,
        MenuMethod,
        MenuField,
        Inspector,
        Drawer,
        Tool,
        AssetKind,
        Overlay,
        Gizmo,
        Icon
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var found = ImmutableArray.Create(
            Read(context, EditorMenu, static node => node is MethodDeclarationSyntax or VariableDeclaratorSyntax, Menus),
            Read(context, EditorSubmenu, static node => node is TypeDeclarationSyntax, Submenus),
            Read(context, CustomInspector, static node => node is MethodDeclarationSyntax, Inspectors),
            Read(context, CustomDrawer, static node => node is TypeDeclarationSyntax, Drawers),
            Read(context, EditorTool, static node => node is TypeDeclarationSyntax, Tools),
            Read(context, CreateAssetMenu, static node => node is MethodDeclarationSyntax, AssetKinds),
            Read(context, Overlay, static node => node is MethodDeclarationSyntax, Overlays),
            Read(context, DrawGizmo, static node => node is MethodDeclarationSyntax, Gizmos),
            Read(context, EditorIcon, static node => node is TypeDeclarationSyntax, Icons)
        );

        var all = found[0];

        for (var index = 1; index < found.Length; index++) {
            all = all.Combine(found[index]).Select(static (pair, _) => pair.Left.AddRange(pair.Right));
        }

        var assembly = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName ?? "Declared");

        context.RegisterSourceOutput(all.Combine(assembly), static (production, pair) => Emit(production, pair.Left, pair.Right));
    }

    static IncrementalValueProvider<ImmutableArray<Declaration>> Read(
        IncrementalGeneratorInitializationContext context,
        string attribute,
        Func<SyntaxNode, bool> accepts,
        Func<GeneratorAttributeSyntaxContext, ImmutableArray<Declaration>> read
    ) =>
        context.SyntaxProvider
            .ForAttributeWithMetadataName(attribute, (node, _) => accepts(node), (syntax, _) => new EquatableArray<Declaration>(read(syntax)))
            .SelectMany(static (declarations, _) => declarations)
            .Collect();

    // ================================================================= Emission

    static void Emit(SourceProductionContext production, ImmutableArray<Declaration> found, string assembly) {
        var declarations = found.Distinct().ToImmutableArray();
        var failed = false;

        foreach (var declaration in declarations) {
            foreach (var problem in declaration.Problems) {
                production.ReportDiagnostic(problem.ToDiagnostic());
                failed = true;
            }
        }

        // ⚠ Nothing at all for an assembly that declares nothing, which is the question
        // `docs/GeneratorPackagingExempt.txt` tells everybody to ask of a generator: a compilation
        // that uses none of the attributes gets no `Declared` type, so a module calling one it has
        // no declarations for is a compile error rather than a registration that does nothing.
        // And nothing when something was refused, because the build is failing anyway and half a
        // registration would only add errors about the half.
        if (declarations.IsEmpty || failed) {
            return;
        }

        var ordered = declarations
            .OrderBy(static item => item.File, StringComparer.Ordinal)
            .ThenBy(static item => item.TypePosition)
            .ThenBy(static item => item.Group)
            .ThenBy(static item => item.MemberPosition)
            .ThenBy(static item => item.Index)
            .ToList();

        var text = new StringBuilder();

        text.AppendLine("// <auto-generated/>");
        text.AppendLine("// Vixen.Editor.Plugin.Generator — the registration this assembly's declaration attributes describe.");
        text.AppendLine("#nullable enable");
        text.AppendLine();
        text.Append("namespace ").Append(Namespace(assembly)).AppendLine(";");
        text.AppendLine();
        text.AppendLine("/// <summary>Registers everything this assembly declares with an attribute, through one plugin context.</summary>");
        text.AppendLine("/// <remarks>");
        text.AppendLine("///     Generated by <c>Vixen.Editor.Plugin.Generator</c>. Call <see cref=\"Register\" /> last in the");
        text.AppendLine("///     module's <c>Activate</c>: a hand registration made first wins over a declaration of the same");
        text.AppendLine("///     thing, which is the rule the scan a disk plugin gets follows too.");
        text.AppendLine("/// </remarks>");
        text.AppendLine("[global::System.CodeDom.Compiler.GeneratedCode(\"Vixen.Editor.Plugin.Generator\", \"1.0\")]");
        text.AppendLine("internal static partial class Declared {");
        text.AppendLine("    /// <summary>Registers every declaration, each in the context's registration scope.</summary>");
        text.AppendLine("    /// <param name=\"context\">The module's context.</param>");
        text.AppendLine("    public static void Register(global::Vixen.Editor.Plugin.PluginContext context) {");
        text.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(context);");
        text.AppendLine();

        var menus = ordered.Any(static item => item.Group <= (int)Group.MenuField);

        if (ordered.Any(static item => item.Group > (int)Group.MenuField && item.Group != (int)Group.Drawer)) {
            text.AppendLine("        var registry = context.Services.Require<global::Vixen.Editor.Core.IEditorRegistry>();");
        }

        if (menus) {
            text.AppendLine("        var menus = new global::Vixen.Editor.Plugin.DeclaredMenus(context);");
        }

        text.AppendLine();

        foreach (var declaration in ordered) {
            text.AppendLine("        {");

            foreach (var line in declaration.Statement!.Split('\n')) {
                text.Append("            ").AppendLine(line.TrimEnd('\r'));
            }

            text.AppendLine("        }");
        }

        if (menus) {
            text.AppendLine();
            text.AppendLine("        menus.Apply();");
        }

        text.AppendLine("    }");
        text.AppendLine("}");

        production.AddSource("Declared.Register.g.cs", SourceText.From(text.ToString(), Encoding.UTF8));
    }

    /// <summary>The assembly's name as a namespace — which is what an in-tree module's root namespace is.</summary>
    static string Namespace(string assembly) {
        var parts = assembly.Split('.')
            .Select(static part => {
                var cleaned = new string(part.Select(static c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

                if (cleaned.Length == 0 || char.IsDigit(cleaned[0])) {
                    cleaned = "_" + cleaned;
                }

                return SyntaxFacts.GetKeywordKind(cleaned) != SyntaxKind.None ? "@" + cleaned : cleaned;
            });

        return string.Join(".", parts);
    }

    // ============================================================== [EditorMenu]

    static ImmutableArray<Declaration> Menus(GeneratorAttributeSyntaxContext syntax) {
        var builder = ImmutableArray.CreateBuilder<Declaration>();
        var member = syntax.TargetSymbol;

        foreach (var attribute in syntax.Attributes) {
            var problems = new List<Finding>();
            var where = Where(attribute, member);
            var name = member.ContainingType.Name + "." + member.Name;
            var path = Text(attribute.ConstructorArguments, 0);
            var id = Named(attribute, "Id") as string ?? "";
            var title = Named(attribute, "Title") as string ?? "";
            var strings = Named(attribute, "Strings") as ITypeSymbol;
            var made = Construct(attribute);
            string? statement = null;
            var group = Group.MenuMethod;

            if (!member.IsStatic || member.DeclaredAccessibility != Accessibility.Public) {
                problems.Add(Problem(Shape, where,
                    $"'{name}' carries [EditorMenu] and is not public and static. The scan a disk plugin gets reads public static members only, so a declaration it would skip is refused here rather than registered in one tier and not the other."));
            } else if (!Reachable(member.ContainingType, syntax.SemanticModel.Compilation)
                       || !TypeArgumentsReachable(attribute, syntax.SemanticModel.Compilation)) {
                problems.Add(Problem(Unreachable, where,
                    $"'{name}' carries [EditorMenu] inside a type the generated Declared class cannot name. Make every type around it at least internal."));
            } else if (member is IMethodSymbol method) {
                var target = Qualified(method.ContainingType) + "." + method.Name;

                if (method.Parameters.Length != 0) {
                    problems.Add(Problem(Shape, where,
                        $"'{name}' carries [EditorMenu] and takes arguments. A menu item is a verb with nothing to pass it, so the method has to take none."));
                } else if (method.ReturnsVoid) {
                    Parts(path, 2, "a menu line", "\"Tools/Do It\"", where, problems);

                    var resolved = Title(title, strings, syntax.SemanticModel.Compilation, name, where, problems);

                    statement = $"var declared = {made};\nmenus.Command(declared.Path, {target}, declared.Id, {resolved ?? "null"}, declared.Priority, declared.Separator);";
                } else if (IsStrings(method.ReturnType)) {
                    Parts(path, 1, "a menu", "\"Scene\"", where, problems);
                    CheckLineal(id, title, strings, name, where, problems);

                    statement = $"var declared = {made};\nmenus.Lines(declared.Path, {target}() ?? global::System.Array.Empty<string>(), declared.Priority, declared.Separator);";
                } else {
                    problems.Add(Problem(Shape, where,
                        $"'{name}' carries [EditorMenu] and returns {method.ReturnType.Name}. A verb returns nothing; a run of lines for commands registered elsewhere returns IEnumerable<string>."));
                }
            } else if (member is IFieldSymbol field) {
                group = Group.MenuField;

                if (field.Type.SpecialType != SpecialType.System_String) {
                    problems.Add(Problem(Shape, where,
                        $"'{name}' carries [EditorMenu] and is not a string. On a field the attribute puts a line in a menu for the command whose id the field holds."));
                } else {
                    Parts(path, 1, "a menu", "\"Scene\"", where, problems);
                    CheckLineal(id, title, strings, name, where, problems);

                    statement = $"var declared = {made};\nmenus.Line(declared.Path, {Qualified(field.ContainingType)}.{field.Name}, declared.Priority, declared.Separator);";
                }
            }

            builder.Add(Make(member.ContainingType, group, member, attribute, statement, problems));
        }

        return builder.ToImmutable();
    }

    static void CheckLineal(string id, string title, ITypeSymbol? strings, string name, Location where, List<Finding> problems) {
        if (id.Length != 0 || title.Length != 0 || strings is not null) {
            problems.Add(Problem(Lineal, where,
                $"'{name}' names an Id or a Title on a line for a command registered elsewhere. That command already has both; only a verb, which makes its command, can say them."));
        }
    }

    static bool IsStrings(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true, TypeArguments: [{ SpecialType: SpecialType.System_String }] } named
        && named.ConstructedFrom.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T;

    // =========================================================== [EditorSubmenu]

    static ImmutableArray<Declaration> Submenus(GeneratorAttributeSyntaxContext syntax) {
        if (syntax.TargetSymbol is not INamedTypeSymbol type) {
            return ImmutableArray<Declaration>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<Declaration>();

        foreach (var attribute in Own(type, EditorSubmenu)) {
            var problems = new List<Finding>();
            var where = Where(attribute, type);
            var path = Text(attribute.ConstructorArguments, 0);
            var title = Named(attribute, "Title") as string ?? "";
            var strings = Named(attribute, "Strings") as ITypeSymbol;

            Parts(path, 2, "a submenu", "\"Scene/Geometry\"", where, problems);

            if (!TypeArgumentsReachable(attribute, syntax.SemanticModel.Compilation)) {
                problems.Add(Problem(Unreachable, where,
                    $"[EditorSubmenu(\"{path}\")] on {type.Name} names a Strings class the generated Declared class cannot name."));
            }

            var resolved = Title(title, strings, syntax.SemanticModel.Compilation, $"[EditorSubmenu(\"{path}\")] on {type.Name}", where, problems);
            var statement = $"var declared = {Construct(attribute)};\nmenus.Submenu(declared.Path, {resolved ?? "null"}, declared.After, declared.Priority);";

            builder.Add(Make(type, Group.Submenu, type, attribute, statement, problems));
        }

        return builder.ToImmutable();
    }

    // ========================================================== [CustomInspector]

    static ImmutableArray<Declaration> Inspectors(GeneratorAttributeSyntaxContext syntax) =>
        Methods(
            syntax,
            Group.Inspector,
            "[CustomInspector]",
            "`static void (UiElement body, EditTarget target)`. That is what a custom inspector is: it fills the body from the target.",
            static method => method.ReturnsVoid
                && method.Parameters.Length == 2
                && Is(method.Parameters[0].Type, "Vixen.Ui.UiElement")
                && Is(method.Parameters[1].Type, "Vixen.Editor.Core.EditTarget"),
            static (_, target) =>
                $"context.Owns(registry.Add(new global::Vixen.Editor.Inspector.CustomInspector(declared.Target, {target}, declared.Order)));"
        );

    // ============================================================ [CustomDrawer]

    static ImmutableArray<Declaration> Drawers(GeneratorAttributeSyntaxContext syntax) =>
        Classes(
            syntax,
            CustomDrawer,
            Group.Drawer,
            "[CustomDrawer]",
            "is not a concrete IPropertyDrawer with a parameterless constructor. The editor makes one and registers it.",
            "Vixen.Editor.Inspector.IPropertyDrawer",
            static (_, type) =>
                $"var drawer = new {type}();\n"
                + "context.With<global::Vixen.Editor.Inspector.DrawerRegistry>(\n"
                + "    drawers => {\n"
                + "        if (declared.ForAttribute) {\n"
                + "            drawers.ForAttribute(declared.Target, drawer);\n"
                + "        } else {\n"
                + "            drawers.ForType(declared.Target, drawer);\n"
                + "        }\n"
                + "    },\n"
                + "    drawers => drawers.Remove(drawer)\n"
                + ");"
        );

    // ============================================================== [EditorTool]

    static ImmutableArray<Declaration> Tools(GeneratorAttributeSyntaxContext syntax) =>
        Classes(
            syntax,
            EditorTool,
            Group.Tool,
            "[EditorTool]",
            "is not a concrete IViewportInput with a parameterless constructor. The editor makes one and offers it in the scene pane.",
            "Vixen.Editor.SceneView.IViewportInput",
            static (_, type) =>
                "var id = string.IsNullOrEmpty(declared.Id) ? global::Vixen.Editor.Plugin.DeclaredMenus.Derive(\"tools\", declared.Title) : declared.Id;\n"
                + $"context.Owns(registry.Add(new global::Vixen.Editor.SceneView.SceneTool(id, declared.Title, new {type}(), declared.Target, declared.Order)));"
        );

    // ========================================================= [CreateAssetMenu]

    static ImmutableArray<Declaration> AssetKinds(GeneratorAttributeSyntaxContext syntax) =>
        Methods(
            syntax,
            Group.AssetKind,
            "[CreateAssetMenu]",
            "`static string ()`. It returns what to write into the new file, which is the one thing the menu line cannot say for itself.",
            static method => method.ReturnType.SpecialType == SpecialType.System_String && method.Parameters.Length == 0,
            static (_, target) =>
                "var id = string.IsNullOrEmpty(declared.Id) ? global::Vixen.Editor.Plugin.DeclaredMenus.Derive(\"assets.create\", declared.Title) : declared.Id;\n"
                + "var name = string.IsNullOrEmpty(declared.DefaultName) ? declared.Title : declared.DefaultName;\n"
                + "context.Owns(registry.Add(new global::Vixen.Editor.Core.NewAssetKind(id, declared.Title, declared.Extension, name, Opens: declared.Opens, Order: declared.Order) {\n"
                + $"    Build = static () => {target}() ?? string.Empty\n"
                + "}));",

            // The extension is the one argument whose value, not whose shape, the scan checks — and a
            // compiler can read a constant as well as a loader can.
            static attribute => Text(attribute.ConstructorArguments, 1) is var extension
                && !extension.StartsWith('.')
                    ? $"'{extension}' is not an extension. It is what the new file is called after the dot, including it — \".dialogue\"."
                    : null
        );

    // ================================================================= [Overlay]

    static ImmutableArray<Declaration> Overlays(GeneratorAttributeSyntaxContext syntax) =>
        Methods(
            syntax,
            Group.Overlay,
            "[Overlay]",
            "`static void (UiElement host, SceneViewport pane)`. That is what an overlay is: it fills the host for one pane.",
            static method => method.ReturnsVoid
                && method.Parameters.Length == 2
                && Is(method.Parameters[0].Type, "Vixen.Ui.UiElement")
                && Is(method.Parameters[1].Type, "Vixen.Editor.SceneView.SceneViewport"),
            static (_, target) =>
                "var id = string.IsNullOrEmpty(declared.Id) ? global::Vixen.Editor.Plugin.DeclaredMenus.Derive(\"overlays\", declared.Title) : declared.Id;\n"
                + $"context.Owns(registry.Add(new global::Vixen.Editor.SceneView.SceneOverlay(id, declared.Title, {target}, declared.Corner, declared.Order)));"
        );

    // ============================================================== [DrawGizmo]

    static ImmutableArray<Declaration> Gizmos(GeneratorAttributeSyntaxContext syntax) =>
        Methods(
            syntax,
            Group.Gizmo,
            "[DrawGizmo]",
            "`static void (GizmoDraw draw, object component, GizmoPlacement placement, bool selected)`. The component arrives boxed because the attribute names its type at run time.",
            static method => method.ReturnsVoid
                && method.Parameters.Length == 4
                && Is(method.Parameters[0].Type, "Vixen.Editor.SceneView.GizmoDraw")
                && method.Parameters[1].Type.SpecialType == SpecialType.System_Object
                && Is(method.Parameters[2].Type, "Vixen.Editor.SceneView.GizmoPlacement")
                && method.Parameters[3].Type.SpecialType == SpecialType.System_Boolean,
            static (_, target) =>
                $"context.Owns(registry.Add(new global::Vixen.Editor.SceneView.ComponentGizmo(declared.Target, {target}, declared.SelectedOnly, declared.Order)));"
        );

    // ============================================================== [EditorIcon]

    static ImmutableArray<Declaration> Icons(GeneratorAttributeSyntaxContext syntax) {
        if (syntax.TargetSymbol is not INamedTypeSymbol type) {
            return ImmutableArray<Declaration>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<Declaration>();

        foreach (var attribute in Own(type, EditorIcon)) {
            var problems = new List<Finding>();
            string? statement = null;

            if (!Reachable(type, syntax.SemanticModel.Compilation)) {
                problems.Add(Problem(Unreachable, Where(attribute, type),
                    $"'{type.Name}' carries [EditorIcon] and the generated Declared class cannot name it. Make it and every type around it at least internal."));
            } else {
                // ⚠ A bad icon is a notification and the module still loads, exactly as in the scan —
                // an icon is decoration, and refusing a feature over a path string would be the editor
                // holding it hostage over a picture.
                statement = $"var declared = {Construct(attribute)};\n"
                    + "if (declared.Resolve(context.Directory, out var reason) is { } art) {\n"
                    + $"    context.Owns(registry.Add(new global::Vixen.Editor.Ui.TypeIcon(typeof({Qualified(type)}), art, declared.Order)));\n"
                    + "} else {\n"
                    + "    context.Shell.Notifications.Show(\n"
                    + "        $\"{context.Manifest.Name}: an icon could not be read\",\n"
                    + "        global::Vixen.Editor.Ui.NotificationSeverity.Warning,\n"
                    + $"        $\"[EditorIcon] on '{type.Name}' {{reason}}.\"\n"
                    + "    );\n"
                    + "}";
            }

            builder.Add(Make(type, Group.Icon, type, attribute, statement, problems));
        }

        return builder.ToImmutable();
    }

    // =================================================================== Shared

    static ImmutableArray<Declaration> Methods(
        GeneratorAttributeSyntaxContext syntax,
        Group group,
        string attributeName,
        string shape,
        Func<IMethodSymbol, bool> fits,
        Func<IMethodSymbol, string, string> register,
        Func<AttributeData, string?>? invalid = null
    ) {
        if (syntax.TargetSymbol is not IMethodSymbol method) {
            return ImmutableArray<Declaration>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<Declaration>();
        var name = method.ContainingType.Name + "." + method.Name;

        foreach (var attribute in syntax.Attributes) {
            var problems = new List<Finding>();
            var where = Where(attribute, method);
            string? statement = null;

            if (!method.IsStatic || method.DeclaredAccessibility != Accessibility.Public) {
                problems.Add(Problem(Shape, where,
                    $"'{name}' carries {attributeName} and is not public and static. The scan a disk plugin gets reads public static methods only, so a declaration it would skip is refused here rather than registered in one tier and not the other."));
            } else if (!fits(method)) {
                problems.Add(Problem(Shape, where, $"'{name}' carries {attributeName} and is not a {shape}"));
            } else if (invalid?.Invoke(attribute) is { } reason) {
                problems.Add(Problem(Shape, where, reason));
            } else if (!Reachable(method.ContainingType, syntax.SemanticModel.Compilation)
                       || !TypeArgumentsReachable(attribute, syntax.SemanticModel.Compilation)) {
                problems.Add(Problem(Unreachable, where,
                    $"'{name}' carries {attributeName}, and the generated Declared class cannot name it or the type it names. Make every type involved at least internal."));
            } else {
                statement = $"var declared = {Construct(attribute)};\n{register(method, Qualified(method.ContainingType) + "." + method.Name)}";
            }

            builder.Add(Make(method.ContainingType, group, method, attribute, statement, problems));
        }

        return builder.ToImmutable();
    }

    static ImmutableArray<Declaration> Classes(
        GeneratorAttributeSyntaxContext syntax,
        string attributeName,
        Group group,
        string label,
        string shape,
        string contract,
        Func<INamedTypeSymbol, string, string> register
    ) {
        if (syntax.TargetSymbol is not INamedTypeSymbol type) {
            return ImmutableArray<Declaration>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<Declaration>();

        foreach (var attribute in Own(type, attributeName)) {
            var problems = new List<Finding>();
            var where = Where(attribute, type);
            string? statement = null;

            var fits = type is { TypeKind: TypeKind.Class, IsAbstract: false }
                && type.AllInterfaces.Any(candidate => Is(candidate, contract))
                && type.InstanceConstructors.Any(static constructor =>
                    constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public);

            if (!fits) {
                problems.Add(Problem(Shape, where, $"'{type.Name}' carries {label} and {shape}"));
            } else if (!Reachable(type, syntax.SemanticModel.Compilation)
                       || !TypeArgumentsReachable(attribute, syntax.SemanticModel.Compilation)) {
                problems.Add(Problem(Unreachable, where,
                    $"'{type.Name}' carries {label}, and the generated Declared class cannot name it or the type it names. Make every type involved at least internal."));
            } else {
                statement = $"var declared = {Construct(attribute)};\n{register(type, Qualified(type))}";
            }

            builder.Add(Make(type, group, type, attribute, statement, problems));
        }

        return builder.ToImmutable();
    }

    /// <summary>All of a type's attributes of one kind, whichever of its declarations carries them.</summary>
    /// <remarks>
    ///     ⚠ <b>The symbol's, not the node's, and deduplicated in <see cref="Emit" />.</b> A partial type
    ///     with the attribute on two of its declarations is visited once per declaration, and each
    ///     visit reads every attribute the type has — so the visits produce identical declarations and
    ///     <c>Distinct</c> keeps one. Gating on "the type's first declaration" instead was the first
    ///     draft, and it would have registered nothing at all for a type whose attribute sat on a later
    ///     part, because the pipeline only visits the parts that carry one.
    /// </remarks>
    static IEnumerable<AttributeData> Own(INamedTypeSymbol type, string attribute) =>
        type.GetAttributes().Where(data => data.AttributeClass?.ToDisplayString() == attribute);

    /// <summary>The C# that constructs this attribute exactly as it was written.</summary>
    /// <remarks>
    ///     ⚠ <b>The attribute itself, rather than its values copied into the call</b>, so a default the
    ///     attribute declares — <c>Corner</c>'s <c>TopRight</c>, <c>Opens</c>' <c>true</c> — is the
    ///     attribute's default in both tiers, and a change to one cannot leave the generated code
    ///     carrying the old one.
    /// </remarks>
    static string Construct(AttributeData attribute) {
        var text = new StringBuilder("new ");

        text.Append(Qualified(attribute.AttributeClass!)).Append('(');
        text.Append(string.Join(", ", attribute.ConstructorArguments.Select(Literal)));
        text.Append(')');

        if (attribute.NamedArguments.Length != 0) {
            text.Append(" { ");
            text.Append(string.Join(", ", attribute.NamedArguments.Select(static pair => pair.Key + " = " + Literal(pair.Value))));
            text.Append(" }");
        }

        return text.ToString();
    }

    static string Literal(TypedConstant constant) {
        if (constant.IsNull) {
            return "null";
        }

        switch (constant.Kind) {
            case TypedConstantKind.Type:
                return TypeOf((ITypeSymbol)constant.Value!);

            case TypedConstantKind.Enum:
                return $"(({Qualified(constant.Type!)}) ({Convert.ToString(constant.Value, CultureInfo.InvariantCulture)}))";

            case TypedConstantKind.Array:
                return $"new {Qualified(((IArrayTypeSymbol)constant.Type!).ElementType)}[] {{ {string.Join(", ", constant.Values.Select(Literal))} }}";

            default:
                return constant.Value switch {
                    string value => SymbolDisplay.FormatLiteral(value, true),
                    char value => SymbolDisplay.FormatLiteral(value, true),
                    bool value => value ? "true" : "false",
                    float value => value.ToString("R", CultureInfo.InvariantCulture) + "f",
                    double value => value.ToString("R", CultureInfo.InvariantCulture) + "d",
                    decimal value => value.ToString(CultureInfo.InvariantCulture) + "m",
                    long value => value.ToString(CultureInfo.InvariantCulture) + "L",
                    ulong value => value.ToString(CultureInfo.InvariantCulture) + "UL",
                    uint value => value.ToString(CultureInfo.InvariantCulture) + "U",
                    IFormattable value => $"(({Qualified(constant.Type!)}) {value.ToString(null, CultureInfo.InvariantCulture)})",
                    _ => constant.ToCSharpString()
                };
        }
    }

    static string TypeOf(ITypeSymbol type) {
        if (type is INamedTypeSymbol { IsUnboundGenericType: true } unbound) {
            var definition = unbound.OriginalDefinition;
            var container = definition.ContainingType is { } outer
                ? Qualified(outer)
                : "global::" + definition.ContainingNamespace.ToDisplayString();

            return $"typeof({container}.{definition.Name}<{new string(',', definition.Arity - 1)}>)";
        }

        return $"typeof({Qualified(type)})";
    }

    static string Qualified(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    static bool Is(ITypeSymbol type, string metadataName) => type.ToDisplayString() == metadataName;

    /// <summary>Whether the generated class, in this assembly, can write this type's name.</summary>
    static bool Reachable(ITypeSymbol type, Compilation compilation) {
        switch (type) {
            case IArrayTypeSymbol array:
                return Reachable(array.ElementType, compilation);

            case INamedTypeSymbol named:
                for (ISymbol? at = named; at is INamedTypeSymbol current; at = current.ContainingType) {
                    var local = SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, compilation.Assembly);

                    var visible = current.DeclaredAccessibility switch {
                        Accessibility.Public => true,
                        Accessibility.Internal or Accessibility.ProtectedOrInternal => local,
                        _ => false
                    };

                    if (!visible) {
                        return false;
                    }
                }

                return named.TypeArguments.All(argument => argument is ITypeParameterSymbol || Reachable(argument, compilation));

            default:
                return true;
        }
    }

    static bool TypeArgumentsReachable(AttributeData attribute, Compilation compilation) =>
        attribute.ConstructorArguments.Concat(attribute.NamedArguments.Select(static pair => pair.Value))
            .All(constant => constant.Kind != TypedConstantKind.Type
                || constant.IsNull
                || Reachable((ITypeSymbol)constant.Value!, compilation));

    /// <summary>The <c>StringId</c> a <c>Title</c> names, as C#, or null for no title.</summary>
    /// <remarks>
    ///     The scan's rule, at compile time: the class <c>Strings</c> names, or the one class of this
    ///     assembly whose name ends in <c>Strings</c> declaring a public static <c>StringId</c> of that
    ///     name. Two is refused rather than guessed; none is refused rather than shown as a literal.
    /// </remarks>
    static string? Title(
        string title,
        ITypeSymbol? strings,
        Compilation compilation,
        string name,
        Location where,
        List<Finding> problems
    ) {
        if (title.Length == 0) {
            if (strings is not null) {
                problems.Add(Problem(Untitled, where, $"'{name}' names a Strings class and no Title to find in it."));
            }

            return null;
        }

        IEnumerable<INamedTypeSymbol> candidates = strings is INamedTypeSymbol named
            ? [named]
            : AllTypes(compilation.Assembly.GlobalNamespace).Where(static type => type.Name.EndsWith("Strings", StringComparison.Ordinal));

        var found = candidates
            .Where(type => type.GetMembers(title).Any(member => member.IsStatic
                && member.DeclaredAccessibility == Accessibility.Public
                && member switch {
                    IPropertySymbol property => property.Type.ToDisplayString() == StringIdType,
                    IFieldSymbol field => field.Type.ToDisplayString() == StringIdType,
                    _ => false
                }))
            .ToList();

        switch (found.Count) {
            case 1 when Reachable(found[0], compilation):
                return Qualified(found[0]) + "." + title;

            case 1:
                problems.Add(Problem(Unreachable, where,
                    $"'{name}' has Title = \"{title}\", which {found[0].Name} declares, and the generated Declared class cannot name {found[0].Name}."));

                return null;

            case 0:
                problems.Add(Problem(Untitled, where,
                    $"'{name}' has Title = \"{title}\", and {(strings is not null ? strings.Name : "no class of its assembly whose name ends in Strings")} declares a public static StringId called {title}."));

                return null;

            default:
                problems.Add(Problem(Untitled, where,
                    $"'{name}' has Title = \"{title}\", which {string.Join(" and ", found.Select(static type => type.Name))} both declare. Say which with Strings = typeof(…)."));

                return null;
        }
    }

    static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol space) {
        foreach (var member in space.GetMembers()) {
            if (member is INamespaceSymbol inner) {
                foreach (var type in AllTypes(inner)) {
                    yield return type;
                }
            } else if (member is INamedTypeSymbol type) {
                foreach (var nested in Nested(type)) {
                    yield return nested;
                }
            }
        }
    }

    static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) {
        yield return type;

        foreach (var inner in type.GetTypeMembers()) {
            foreach (var nested in Nested(inner)) {
                yield return nested;
            }
        }
    }

    static void Parts(string path, int minimum, string what, string example, Location where, List<Finding> problems) {
        var parts = path.Split('/').Select(static part => part.Trim()).Count(static part => part.Length != 0);

        if (parts < minimum) {
            problems.Add(Problem(ShortPath, where,
                $"'{path}' is not a path to {what}. It needs at least {minimum} part{(minimum == 1 ? "" : "s")} — {example}."));
        }
    }

    static string Text(ImmutableArray<TypedConstant> arguments, int index) =>
        index < arguments.Length && arguments[index].Value is string text ? text : "";

    static object? Named(AttributeData attribute, string name) {
        foreach (var pair in attribute.NamedArguments) {
            if (pair.Key == name) {
                return pair.Value.Kind == TypedConstantKind.Type ? pair.Value.Value as ITypeSymbol : pair.Value.Value;
            }
        }

        return null;
    }

    static Location Where(AttributeData attribute, ISymbol symbol) =>
        attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
        ?? symbol.Locations.FirstOrDefault()
        ?? Location.None;

    static Finding Problem(DiagnosticDescriptor descriptor, Location where, string message) {
        var span = where.GetLineSpan();

        return new(
            descriptor.Id,
            message,
            where.SourceTree?.FilePath ?? "",
            where.SourceSpan.Start,
            where.SourceSpan.Length,
            span.StartLinePosition.Line,
            span.StartLinePosition.Character,
            span.EndLinePosition.Line,
            span.EndLinePosition.Character
        );
    }

    static Declaration Make(
        INamedTypeSymbol type,
        Group group,
        ISymbol member,
        AttributeData attribute,
        string? statement,
        List<Finding> problems
    ) {
        var declared = type.DeclaringSyntaxReferences.FirstOrDefault();
        var at = member.DeclaringSyntaxReferences.FirstOrDefault();
        var index = member.GetAttributes().IndexOf(attribute);

        return new(
            declared?.SyntaxTree.FilePath ?? "",
            declared?.Span.Start ?? 0,
            (int)group,
            at?.Span.Start ?? 0,
            index < 0 ? 0 : index,
            problems.Count == 0 ? statement : null,
            new(problems.ToImmutableArray())
        );
    }

    /// <summary>One declaration, already written as C#, and where it sits for ordering.</summary>
    sealed record Declaration(
        string File,
        int TypePosition,
        int Group,
        int MemberPosition,
        int Index,
        string? Statement,
        EquatableArray<Finding> Problems
    );

    /// <summary>A diagnostic, as values, so the pipeline can tell two runs apart without a <c>Location</c>.</summary>
    sealed record Finding(
        string Id,
        string Message,
        string File,
        int Start,
        int Length,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn
    ) {
        public Diagnostic ToDiagnostic() =>
            Diagnostic.Create(
                Descriptors[Id],
                File.Length == 0
                    ? Location.None
                    : Location.Create(
                        File,
                        new TextSpan(Start, Length),
                        new LinePositionSpan(new(StartLine, StartColumn), new(EndLine, EndColumn))
                    ),
                Message
            );
    }
}
