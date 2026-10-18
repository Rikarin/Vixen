// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     Doc 50 § D6's <c>ReachTest</c>: the editor a user launches has every standard module's menu
///     lines, Create ▸ entries, panels and modes — asserted by effect, on an <c>EditorSession</c>
///     opened with <c>EditorModules.Standard()</c> exactly as <c>EditorHost</c> opens one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P2's rule: a declaration is built when something does something with it.</b>
///         Nothing asserted that the shipped editor has the menu its modules register, which is how
///         <c>TexturingModule</c> came to be activated by nothing (#1276) while its own suite, which
///         activates it by hand, stayed green.
///     </para>
///     <para>
///         <b>Two questions, neither a second copy of <c>Standard()</c>.</b> Which modules should
///         ship is read from the modules' own declarations — every <c>const string ModuleId</c> in
///         production source under <c>Editor/</c> — and each is either in <c>Standard()</c> or on
///         <see cref="NotStandard" /> with its reason. What each one reaches is a manifest,
///         <c>ModuleReach.txt</c>, <i>measured</i> from the session rather than written by hand: every
///         menu line by its title ids and command id, every Create ▸ kind, every contributed Scene-menu
///         line, every panel and every mode,
///         attributed to the module whose unload takes it away. A line missing from the session that
///         the manifest holds is a registration that stopped reaching the user.
///     </para>
///     <para>
///         ⚠ <b>A line a module leaves behind on unload is attributed to <c>editor</c>, not to the
///         module</b>, and that is a finding rather than a defect of the test: it is exactly the panel
///         or mode the module registered round <c>PluginContext</c> (see
///         <c>docs/ModuleRegistrationExempt.txt</c>). When P1 moves those through the contract, their
///         lines move to the module's name in the regenerated manifest.
///     </para>
///     <para>
///         ⚠ <b>The first measurement found a module whose menus reach nobody.</b> Blockout's five
///         Scene submenus are not in the manifest because they are not in the editor: the modules are
///         activated inside <c>Commands()</c> before <c>SceneMenu()</c> creates the Scene menu, so
///         <c>FindMenu</c> answers null and the module returns, as it would in a thumbnail renderer
///         (#1483). The manifest records what is, so the fix shows up as lines gained.
///     </para>
///     <para>
///         Regenerate with <c>VIXEN_REGENERATE=1</c> on this test, and read the diff: a line that
///         went is a menu entry, panel or mode a user no longer has.
///     </para>
/// </remarks>
public class ModuleReachTests {
    const string ManifestFile = "Editor/Vixen.Editor.App.Tests/ModuleReach.txt";

    /// <summary>
    ///     Modules that declare an id and do not ship in <c>Standard()</c>, each with why. ⚠
    ///     Shrink-only: a module here that joins <c>Standard()</c> fails until its line goes.
    /// </summary>
    static readonly Dictionary<string, string> NotStandard = new(StringComparer.Ordinal) {
        ["vixen.texturing"] = "#1276 — decided 'add to EditorModules.Standard', not landed; until it does the layer "
            + "stack, painting and Bake Material reach no user of Vixen.Editor.Host"
    };

    static readonly Regex Declared = new("""const\s+string\s+ModuleId\s*=\s*"([^"]+)"\s*;""", RegexOptions.CultureInvariant);

    static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "Vixen.slnx"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>Every module id production source under <c>Editor/</c> declares.</summary>
    static SortedSet<string> DeclaredModules(string root) {
        var ids = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Editor"), "*.cs", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (relative.Split('/').Any(part => part is "obj" or "bin" || part.EndsWith(".Tests", StringComparison.Ordinal))) {
                continue;
            }

            foreach (Match match in Declared.Matches(File.ReadAllText(file))) {
                ids.Add(match.Groups[1].Value);
            }
        }

        return ids;
    }

    /// <summary>What a user can reach in the session as it stands, one line per thing.</summary>
    static SortedSet<string> Reach(EditorSession editor) {
        var lines = new SortedSet<string>(StringComparer.Ordinal);

        void Walk(MenuGroup group, string path) {
            foreach (var entry in group.Entries) {
                switch (entry) {
                    case MenuCommand command:
                        lines.Add($"menu {path} {command.CommandId}");
                        break;
                    case MenuDynamic dynamic:
                        foreach (var id in dynamic.CommandIds()) {
                            lines.Add($"menu {path} {id}");
                        }

                        break;
                    case MenuSubmenu submenu:
                        Walk(submenu.Group, path + "/" + submenu.Group.Title.Id);
                        break;
                }
            }
        }

        foreach (var menu in editor.Shell.Menus.Menus) {
            Walk(menu, menu.Title.Id);
        }

        foreach (var kind in editor.Extensions.All<NewAssetKind>()) {
            lines.Add($"create {kind.Id}");
        }

        // The Scene menu's contributed lines — the context menu and the menu bar's Scene menu read
        // these rather than `Menus`, and they are where a mode's verbs reach the user.
        foreach (var item in editor.Extensions.All<SceneMenuItem>()) {
            lines.Add($"scene-menu {item.CommandId}");
        }

        foreach (var panel in editor.Shell.Workspace.Panels) {
            lines.Add($"panel {panel.Id}");
        }

        foreach (var mode in editor.Shell.Modes.Modes) {
            lines.Add($"mode {mode.Id}");
        }

        return lines;
    }

    /// <summary>The session's reach, attributed by unloading each module in turn, last first.</summary>
    static List<string> Measure() {
        using var editor = EditorSession.Start();

        var standard = EditorModules.Standard();
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        var before = Reach(editor);

        foreach (var line in before) {
            owner[line] = "editor";
        }

        foreach (var (id, _, _) in standard.Reverse()) {
            Assert.True(editor.Plugins.Unload(id), $"The standard module '{id}' could not be unloaded, so its reach cannot be attributed.");
            editor.Frame();

            var after = Reach(editor);

            foreach (var line in before.Except(after)) {
                owner[line] = id;
            }

            before = after;
        }

        return [.. owner.Select(row => $"{row.Value} {row.Key}").Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void Every_module_the_editor_declares_ships_in_the_standard_list_or_says_why_not() {
        var declared = DeclaredModules(Root());
        var standard = EditorModules.Standard().Select(module => module.Id).ToHashSet(StringComparer.Ordinal);

        // The instrument: a grep that stopped matching would find nothing and agree with anything.
        Assert.Contains("vixen.blockout", declared);
        Assert.Contains("vixen.water", declared);

        var missing = declared.Where(id => !standard.Contains(id) && !NotStandard.ContainsKey(id)).ToList();

        Assert.True(
            missing.Count == 0,
            "Declared by a module and activated by nothing a user launches: " + string.Join(", ", missing)
            + ". Add it to EditorModules.Standard(), or to NotStandard with the reason it ships some other way."
        );

        var shipped = NotStandard.Keys.Where(standard.Contains).ToList();

        Assert.True(shipped.Count == 0, "In Standard() and still on NotStandard: " + string.Join(", ", shipped) + ". Delete the line.");

        // And the reverse: Standard() naming an id nothing declares is a list that has drifted from
        // the modules it lists.
        Assert.Empty(standard.Except(declared));
    }

    [Fact]
    public void Every_standard_module_reaches_the_user_with_what_the_manifest_records() {
        var measured = Measure();
        var root = Root();
        var path = Path.Combine(root, ManifestFile.Replace('/', Path.DirectorySeparatorChar));

        if (Environment.GetEnvironmentVariable("VIXEN_REGENERATE") == "1") {
            File.WriteAllText(path, Render(measured), new UTF8Encoding(false));
        }

        // The instrument first: a reach walk that read nothing, or attributed nothing, agrees with an
        // empty manifest. The editor's own menus are read, and every module's unload is accounted for.
        Assert.Contains(measured, line => line.StartsWith("editor menu ", StringComparison.Ordinal));

        var silent = EditorModules.Standard()
            .Select(module => module.Id)
            .Where(id => !measured.Any(line => line.StartsWith(id + " ", StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            silent.Order(StringComparer.Ordinal).SequenceEqual(Unattributed.Keys.Order(StringComparer.Ordinal)),
            $"Standard modules whose unload takes away nothing a user can see: [{string.Join(", ", silent)}]; "
            + $"Unattributed lists [{string.Join(", ", Unattributed.Keys)}]. A module here either adds nothing visible "
            + "or registers it round PluginContext so its unload leaves it behind; the list may only shrink."
        );

        var recorded = File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0 && !line.StartsWith('#')).ToList()
            : [];

        var lost = recorded.Except(measured).ToList();
        var gained = measured.Except(recorded).ToList();

        Assert.True(
            lost.Count == 0 && gained.Count == 0,
            $"""
             What the editor reaches is not what {ManifestFile} records.

             ⚠ In the manifest and not in the editor — a registration that stopped reaching the user:
             {string.Join("\n", lost.Select(line => "  " + line))}

             In the editor and not in the manifest — new reach; regenerate after reading:
             {string.Join("\n", gained.Select(line => "  " + line))}

             Re-run with VIXEN_REGENERATE=1 to write it back, once the first list is understood.
             """
        );
    }

    /// <summary>
    ///     Standard modules whose unload takes away nothing in the reach walk, each with why. ⚠
    ///     Shrink-only, both ways: a module that starts being taken back, or one that stops, fails.
    /// </summary>
    static readonly Dictionary<string, string> Unattributed = new(StringComparer.Ordinal) {
        // ⚠ Not quiet: five panels and two modes — and they stay on unload, because every one is
        // registered round PluginContext. Its reach is in the manifest under `editor`.
        ["vixen.terrain"] = "registers its panels and modes on the shell directly, so unloading it takes nothing back (#740, #1287)",

        // Genuinely quiet: play systems and a collider service, nothing a menu, panel or mode shows.
        ["vixen.terrain.physics"] = "contributes play systems and ITerrainColliders, nothing a user sees"
    };

    static string Render(IEnumerable<string> lines) {
        var text = new StringBuilder();

        text.Append("# What the editor a user launches reaches, by module (doc 50 § D6, #1280). Measured by ModuleReachTests\n")
            .Append("# from an EditorSession opened the way EditorHost opens one, and attributed by unloading each standard\n")
            .Append("# module in turn: `<owner> <kind> <id>`. A line owned by `editor` that names a feature is one its module\n")
            .Append("# left behind on unload. Regenerate with VIXEN_REGENERATE=1 on that test, and read the diff.\n\n");

        foreach (var line in lines) {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }
}
