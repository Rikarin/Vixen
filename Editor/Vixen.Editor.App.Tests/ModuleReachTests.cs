// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Testing;
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
    /// <remarks><c>vixen.texturing</c> left this list with #1276, which added it to <c>Standard()</c>.</remarks>
    static readonly Dictionary<string, string> NotStandard = new(StringComparer.Ordinal);

    /// <summary>The family a recent document's command id belongs to; the rest of the id is a hash of a path.</summary>
    const string RecentDocument = "file.recent-document.";

    static readonly Regex Declared = new("""const\s+string\s+ModuleId\s*=\s*"([^"]+)"\s*;""", RegexOptions.CultureInvariant);

    /// <summary>Every module id production source under <c>Editor/</c> declares, as git lists it (#1424).</summary>
    static SortedSet<string> DeclaredModules(string root) {
        var ids = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in RepositoryFiles.Files(Path.Combine(root, "Editor"), "*.cs")) {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (relative.Split('/').Any(part => part.EndsWith(".Tests", StringComparison.Ordinal))) {
                continue;
            }

            foreach (Match match in Declared.Matches(File.ReadAllText(file))) {
                ids.Add(match.Groups[1].Value);
            }
        }

        return ids;
    }

    /// <summary>What a user can reach in the session as it stands, one line per thing.</summary>
    /// <remarks>
    ///     ⚠ <b>A menu line counts only while its command is registered</b>, because that is what
    ///     <c>MenuPresenter.Fill</c> draws: a <see cref="MenuCommand" /> naming an id nobody registered is
    ///     skipped. Until this walk asked the same question, the transport and the Entity menu — lines
    ///     the application places and <c>PlayModule</c> and <c>SceneModule</c> register the commands
    ///     for (#1289, #1290) — were attributed to <c>editor</c>, and both modules read as taking away
    ///     nothing a user can see, while unloading either takes its lines off the bar.
    /// </remarks>
    static SortedSet<string> Reach(EditorSession editor) {
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        var commands = editor.Shell.Commands;

        void Walk(MenuGroup group, string path) {
            foreach (var entry in group.Entries) {
                switch (entry) {
                    case MenuCommand command when commands[command.CommandId] is not null:
                        lines.Add($"menu {path} {command.CommandId}");
                        break;
                    case MenuDynamic dynamic:
                        // ⚠ A recent document's id is derived from its path (#656), and a session's
                        // project lives in a fresh temporary directory — so the id is spelled as the
                        // family it belongs to, or the manifest would differ on every run.
                        foreach (var id in dynamic.CommandIds().Where(id => commands[id] is not null)) {
                            lines.Add(
                                id.StartsWith(RecentDocument, StringComparison.Ordinal)
                                    ? $"menu {path} {RecentDocument}*"
                                    : $"menu {path} {id}"
                            );
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
        var declared = DeclaredModules(RepositoryFiles.Root);
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
        var root = RepositoryFiles.Root;
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
    ///     The negative half (#1280): a standard module switched off in the plugin manager contributes
    ///     nothing to the next session, and the rest of the editor is unchanged.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Against the same manifest as the positive half, so the expectation is still measured
    ///         rather than written.</b> Each module is switched off through the manager's own button —
    ///         which is what writes the user's list — and the editor is restarted on the same data
    ///         directory, the way a person relaunches it. Every line the manifest attributes to the
    ///         module must be gone; every other line must be there.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second half is what makes the first falsifiable.</b> An editor that failed to
    ///         start any module, or lost its menu bar, would pass "the module's lines are absent" for
    ///         every row. And it is a stronger statement than unloading: a module registered round
    ///         <c>PluginContext</c> leaves its lines behind on unload, but a suppressed one is never
    ///         activated at all — so a line owned by <c>editor</c> that vanishes here is one the
    ///         manifest has attributed to the wrong owner.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_module_switched_off_in_the_plugin_manager_contributes_nothing_to_the_next_session() {
        var path = Path.Combine(RepositoryFiles.Root, ManifestFile.Replace('/', Path.DirectorySeparatorChar));
        var manifest = File.ReadAllLines(path)
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' ', 2))
            .ToList();

        var everything = manifest.Select(row => row[1]).ToHashSet(StringComparer.Ordinal);
        var failures = new StringBuilder();

        using var editor = EditorSession.Start();

        foreach (var (id, _, _) in EditorModules.Standard()) {
            PluginManagerViewTests.Toggle(editor, id);
            // Recorded rather than asserted, so one bad row does not hide what the others found.
            if (!editor.Plugins.IsSuppressed(id)) {
                failures.Append(id).Append("\n  the manager did not switch it off\n");
                continue;
            }

            editor.Restart();

            var reach = Reach(editor);
            var owned = manifest.Where(row => row[0] == id).Select(row => row[1]).ToHashSet(StringComparer.Ordinal);
            var left = owned.Where(reach.Contains).ToList();
            var lost = everything.Except(owned).Where(line => !reach.Contains(line)).ToList();

            if (left.Count > 0 || lost.Count > 0) {
                failures.Append(id).Append('\n');

                foreach (var line in left) {
                    failures.Append("  still reached: ").Append(line).Append('\n');
                }

                foreach (var line in lost) {
                    failures.Append("  lost with it:  ").Append(line).Append('\n');
                }
            }

            // Back on, which writes the list without it — so the next row's restart has one module off.
            PluginManagerViewTests.Toggle(editor, id);
            if (editor.Plugins.IsSuppressed(id)) {
                failures.Append(id).Append("\n  the manager did not switch it back on\n");
            }
        }

        Assert.True(failures.Length == 0, "Switched off in the plugin manager and restarted:\n" + failures);
    }

    /// <summary>
    ///     Standard modules whose unload takes away nothing in the reach walk, each with why. ⚠
    ///     Shrink-only, both ways: a module that starts being taken back, or one that stops, fails.
    /// </summary>
    /// <remarks>
    ///     <c>vixen.terrain</c> left this list with #1287, which moved its panels and modes through
    ///     <c>PluginContext</c> so its unload takes them back.
    /// </remarks>
    static readonly Dictionary<string, string> Unattributed = new(StringComparer.Ordinal) {
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
