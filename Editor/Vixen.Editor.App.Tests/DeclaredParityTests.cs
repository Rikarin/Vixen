// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Plugin;
using Vixen.Editor.Plugin.Generator;
using Vixen.Editor.Plugin.Generator.Tests;
using Vixen.Editor.Plugin.Tests;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 50 § D6's <c>DeclaredParityTest</c>: the generator and the scan, on one fixture, member for member.</summary>
/// <remarks>
///     <para>
///         <b>Two readers of one set of attributes.</b> A disk plugin and a project script are read by
///         <c>DeclaredContributions</c>, by reflection, when the editor loads them; an in-tree module
///         by <c>Vixen.Editor.Plugin.Generator</c>, at compile time. A declaration moved from a
///         plugin into the tree — or the other way — has to mean the same thing, so one fixture
///         assembly is compiled through the generator and then registered both ways into two
///         identical hosts, and everything each host ends up holding is described and compared.
///         <c>ReflectedTypeTests</c> is the shape, for the importer descriptor.
///     </para>
///     <para>
///         ⚠ <b>Verify the instrument first.</b> A comparator handed two hosts that both registered
///         nothing calls them identical, so the description is also held to containing every kind —
///         and the generated half is sabotage-checked by registering one host with a declaration
///         dropped and asserting the comparison then fails.
///     </para>
/// </remarks>
public class DeclaredParityTests {
    static readonly Lazy<Assembly> Fixture = new(Build);

    sealed class Module(Action<PluginContext> activate) : IEditorPlugin {
        public void Activate(PluginContext context) => activate(context);
    }

    /// <summary>Something with a decimal on it, so the declared drawer has a member to resolve for.</summary>
    sealed class Probe {
        public decimal Amount;
    }

    sealed class Host : IDisposable {
        public Host() {
            Shell = new EditorShell(1280f, 800f);
            Registry = new EditorRegistry();
            Drawers = DrawerRegistry.CreateDefault();
            Plugins = new PluginHost(Shell, new PluginServices().Add<IEditorRegistry>(Registry).Add(Drawers));
            Baseline = Commands(Shell);
        }

        public EditorShell Shell { get; }

        public EditorRegistry Registry { get; }

        public DrawerRegistry Drawers { get; }

        public PluginHost Plugins { get; }

        public HashSet<string> Baseline { get; }

        public void Dispose() => Shell.Dispose();
    }

    /// <summary>The fixture, compiled through the generator and loaded once for the whole class.</summary>
    static Assembly Build() {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "Fixture",
            [CSharpSyntaxTree.ParseText(DeclaredFixture.Source, path: "Fixture.cs")],
            references,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        CSharpGeneratorDriver.Create(new DeclaredRegistrationGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var problems);

        Assert.Empty(problems);

        using var bytes = new MemoryStream();
        var emitted = generated.Emit(bytes);

        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));

        return Assembly.Load(bytes.ToArray());
    }

    static void Scan(PluginContext context) => new DeclaredContributions().Scan(context, Fixture.Value);

    static void Generated(PluginContext context) =>
        Fixture.Value.GetType("Fixture.Declared", throwOnError: true)!
            .GetMethod("Register", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [context]);

    static HashSet<string> Commands(EditorShell shell) => [.. shell.Commands.Commands.Select(static command => command.Id)];

    /// <summary>Everything one host holds that a declaration could have put there, as text.</summary>
    static string Describe(Host host) {
        var text = new StringBuilder();

        text.AppendLine("commands:");

        foreach (var command in host.Shell.Commands.Commands.Where(command => !host.Baseline.Contains(command.Id))) {
            text.AppendLine($"  {command.Id} '{command.Title.Id}' '{command.Title.Source}'");
        }

        text.AppendLine("menus:");

        foreach (var menu in host.Shell.Menus.Menus) {
            text.AppendLine($"  {menu.Title.Id}[{Render(menu)}]");
        }

        foreach (var item in host.Registry.All<CustomInspector>()) {
            text.AppendLine($"inspector: {item.Target.FullName} {item.Order}");
        }

        foreach (var item in host.Registry.All<SceneTool>()) {
            text.AppendLine($"tool: {item.Id} '{item.Title}' {item.Input.GetType().FullName} {item.Target?.FullName} {item.Order}");
        }

        foreach (var item in host.Registry.All<NewAssetKind>()) {
            text.AppendLine(
                $"asset: {item.Id} '{item.Title.Id}' '{item.Title.Source}' {item.Extension} '{item.DefaultName}' {item.Opens} {item.Order} "
                + $"'{item.NewContents().ReplaceLineEndings("\\n")}'"
            );
        }

        foreach (var item in host.Registry.All<SceneOverlay>()) {
            text.AppendLine($"overlay: {item.Id} '{item.Title.Id}' '{item.Title.Source}' {item.Corner} {item.Order}");
        }

        foreach (var item in host.Registry.All<ComponentGizmo>()) {
            text.AppendLine($"gizmo: {item.Target.FullName} {item.SelectedOnly} {item.Order}");
        }

        foreach (var item in host.Registry.All<TypeIcon>()) {
            text.AppendLine($"icon: {item.Target.FullName} {item.Order} {item.Art.ViewBox} {item.Art.Paths.Count}");
        }

        var member = new InspectorMember<Probe, decimal>("Amount", static probe => ref probe.Amount);

        text.AppendLine($"drawer: {host.Drawers.Resolve(member)?.GetType().FullName}");

        return text.ToString();
    }

    static string Render(MenuGroup group) => string.Join(" ", group.Entries.Select(Render));

    static string Render(MenuEntry entry) => entry switch {
        MenuCommand(var id) => id,
        MenuSeparator => "|",
        MenuSubmenu(var group) => $"{group.Title.Id}:'{group.Title.Source}'[{Render(group)}]",
        MenuDynamic(var ids) => "dynamic(" + string.Join(" ", ids()) + ")",
        _ => "?"
    };

    [Fact]
    public void The_generator_and_the_scan_register_the_same_things() {
        using var scanned = new Host();
        using var generated = new Host();

        Assert.Equal(PluginState.Active, scanned.Plugins.Activate("fixture", "Fixture", new Module(Scan)).State);
        Assert.Equal(PluginState.Active, generated.Plugins.Activate("fixture", "Fixture", new Module(Generated)).State);

        var expected = Describe(scanned);

        Assert.Equal(expected, Describe(generated));

        // ⚠ And the description is of something. Every kind is present, the title is the declaration's
        // and not the path's, the submenu is where `After` put it, and the separator is where it was
        // declared — so a comparison that passes is a comparison of two full registries.
        foreach (var present in new[] {
                     "fixture.bake 'fixture.command.bake' 'Bake It'",
                     "scripts.tools.things.audit 'editor.command.scripts.tools.things.audit' 'Audit'",
                     "fixture.menu.things:'Things'[editor.menu.tools.things.deeper:'Deeper'[fixture.one fixture.two | scripts.tools.things.deeper.sweep] scripts.tools.things.audit fixture.bake | fixture.existing]",
                     "inspector: Fixture.Widget 3",
                     "tool: tools.sculpt 'Sculpt' Fixture.SculptTool Fixture.Widget 4",
                     "asset: assets.create.dialogue-table 'editor.command.assets.create.dialogue-table' 'Dialogue Table' .dialogue 'New Dialogue' False 0 'id: 1\\n'",
                     "asset: assets.create.ledger 'fixture.create.ledger' 'Ledger Book' .ledger 'Ledger' True 0 'rows: []\\n'",
                     "overlay: overlays.declared-overlay 'editor.overlay.overlays.declared-overlay' 'Declared Overlay' BottomLeft 0",
                     "overlay: fixture.compass 'fixture.overlay.compass' 'Compass Rose' TopRight 0",
                     "gizmo: Fixture.Widget True 5",
                     "icon: Fixture.Widget 2 ",
                     "drawer: Fixture.MoneyDrawer"
                 }) {
            Assert.Contains(present, expected, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_verb_runs_the_declared_method_whichever_reader_registered_it() {
        using var scanned = new Host();
        using var generated = new Host();

        scanned.Plugins.Activate("fixture", "Fixture", new Module(Scan));
        generated.Plugins.Activate("fixture", "Fixture", new Module(Generated));

        var verbs = Fixture.Value.GetType("Fixture.Verbs", throwOnError: true)!;
        var baked = verbs.GetField("Baked")!;
        var before = (int)baked.GetValue(null)!;

        Assert.True(scanned.Shell.Commands.Execute("fixture.bake"));
        Assert.True(generated.Shell.Commands.Execute("fixture.bake"));

        Assert.Equal(before + 2, (int)baked.GetValue(null)!);
    }

    /// <summary>Unloading either takes back everything it registered, menus included.</summary>
    [Fact]
    public void Unloading_either_leaves_the_host_as_it_found_it() {
        foreach (var register in new Action<PluginContext>[] { Scan, Generated }) {
            using var host = new Host();
            var empty = Describe(host);

            host.Plugins.Activate("fixture", "Fixture", new Module(register));

            Assert.NotEqual(empty, Describe(host));
            Assert.True(host.Plugins.Unload("fixture"));
            Assert.Equal(empty, Describe(host));
        }
    }

    /// <summary>
    ///     ⚠ <b>The comparison is not an instrument that cannot fail.</b> A host registered by the
    ///     generated code and then stripped of one declaration's effect must no longer compare equal —
    ///     which is what proves <see cref="Describe" /> sees the difference a missing declaration makes.
    /// </summary>
    [Fact]
    public void The_comparison_sees_one_missing_declaration() {
        using var scanned = new Host();
        using var generated = new Host();

        scanned.Plugins.Activate("fixture", "Fixture", new Module(Scan));
        generated.Plugins.Activate("fixture", "Fixture", new Module(Generated));

        // Remove one line: the effect of deleting `[EditorMenu]` from `Verbs.Existing`.
        var things = generated.Shell.Menus.Menus
            .Single(menu => menu.Title.Id == "editor.menu.tools")
            .Entries.OfType<MenuSubmenu>()
            .Single(entry => entry.Group.Title.Id == "fixture.menu.things")
            .Group;

        things.Remove(things.Entries.OfType<MenuCommand>().Single(entry => entry.CommandId == "fixture.existing"));

        Assert.NotEqual(Describe(scanned), Describe(generated));
    }

    /// <summary>
    ///     ⚠ <b>A claim the scan's own remark made and its code did not keep</b>: "two declarations
    ///     naming one menu land in it together". It made a submenu per declaration, with an id built
    ///     from the <i>command's</i> id — so a plugin's two verbs under <c>Tools/My Thing</c> were two
    ///     <i>My Thing</i> lines, each with one item. Red against the scan before it moved onto
    ///     <c>DeclaredMenus</c>.
    /// </summary>
    [Fact]
    public void Two_declared_verbs_under_one_submenu_share_it() {
        using var folder = new PluginFolder();

        folder.Write(
            "shared",
            """
            using Vixen.Editor.Plugin;

            public static class Verbs {
                [EditorMenu("Tools/My Thing/A")]
                public static void A() { }

                [EditorMenu("Tools/My Thing/B")]
                public static void B() { }
            }

            public sealed class Shared : IEditorPlugin {
                public void Activate(PluginContext context) { }
            }
            """
        );

        using var host = new Host();

        host.Plugins.Scanners.Add(new DeclaredContributions());

        Assert.Single(host.Plugins.Load(PluginDiscovery.Scan(folder.Root)).Activated);

        var tools = host.Shell.Menus.Menus.Single(menu => menu.Title.Source == "Tools");
        var thing = Assert.Single(tools.Entries.OfType<MenuSubmenu>(), entry => entry.Group.Title.Source == "My Thing");

        Assert.Equal(["scripts.tools.my-thing.a", "scripts.tools.my-thing.b"], thing.Group.Entries.OfType<MenuCommand>().Select(entry => entry.CommandId));
    }

    /// <summary>#1284: a title naming nothing is a load failure from the scan, never a silent literal.</summary>
    /// <remarks>The generator's half — a compile error, <c>VXP0003</c> — is in <c>Vixen.Editor.Plugin.Generator.Tests</c>.</remarks>
    [Fact]
    public void A_title_naming_nothing_refuses_the_plugin_with_a_diagnostic() {
        using var folder = new PluginFolder();

        folder.Write(
            "untitled",
            """
            using Vixen.Editor.Plugin;

            public static class Verbs {
                [EditorMenu("Tools/Go", Title = "Nowhere")]
                public static void Go() { }
            }

            public sealed class Untitled : IEditorPlugin {
                public void Activate(PluginContext context) { }
            }
            """
        );

        using var host = new Host();

        host.Plugins.Scanners.Add(new DeclaredContributions());

        var report = host.Plugins.Load(PluginDiscovery.Scan(folder.Root));

        Assert.Empty(report.Activated);

        var diagnostic = Assert.Single(report.Diagnostics);

        Assert.Equal(PluginSeverity.Error, diagnostic.Severity);
        Assert.Contains("Title = \"Nowhere\"", diagnostic.Message, StringComparison.Ordinal);

        // Nothing it declared is left behind, which is the rollback a failed activation owes.
        Assert.Null(host.Shell.Commands["scripts.tools.go"]);
    }

    /// <summary>#1284: a Create ▸ line's or an overlay's title naming nothing refuses the plugin the same way.</summary>
    /// <remarks>The generator's half is the four <c>VXP0003</c> rows in <c>DeclaredRegistrationTests</c>.</remarks>
    [Theory]
    [InlineData("""[CreateAssetMenu("Thing", ".thing", Title = "Nowhere")] public static string Make() => "";""")]
    [InlineData("""[Overlay("Over", Title = "Nowhere")] public static void Build(UiElement host, IViewportPane pane) { }""")]
    public void A_contribution_title_naming_nothing_refuses_the_plugin_with_a_diagnostic(string member) {
        using var folder = new PluginFolder();

        folder.Write(
            "untitled",
            $$"""
            using Vixen.Editor.Core;
            using Vixen.Editor.Plugin;
            using Vixen.Editor.SceneView;
            using Vixen.Ui;

            public static class Kinds {
                {{member}}
            }

            public sealed class Untitled : IEditorPlugin {
                public void Activate(PluginContext context) { }
            }
            """
        );

        using var host = new Host();

        host.Plugins.Scanners.Add(new DeclaredContributions());

        var report = host.Plugins.Load(PluginDiscovery.Scan(folder.Root));

        Assert.Empty(report.Activated);

        var diagnostic = Assert.Single(report.Diagnostics);

        Assert.Equal(PluginSeverity.Error, diagnostic.Severity);
        Assert.Contains("Title = \"Nowhere\"", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(host.Registry.All<NewAssetKind>());
        Assert.Empty(host.Registry.All<SceneOverlay>());
    }
}
