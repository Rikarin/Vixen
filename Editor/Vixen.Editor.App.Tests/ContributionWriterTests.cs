// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     Doc 50 § D6's <c>ContributionWriterTest</c>: every contribution kind something in the editor
///     reads from <c>IEditorRegistry</c> is written by something in the editor a user launches — or is
///     on a list, with its reason, that may only shrink.
/// </summary>
/// <remarks>
///     <para>
///         <b>A registry nobody writes is a list with a longer name.</b> Doc 50 § F8 found three kinds
///         with a reader and no production writer and three more written only by the attribute
///         scanner, which no production assembly is ever handed. Each is a contribution point a
///         plugin author is told to use and the editor's own features have never been through.
///     </para>
///     <para>
///         ⚠ <b>By effect, not by grep.</b> The writer is whatever puts an entry in the registry of an
///         editor opened the way <c>EditorHost</c> opens one — <c>EditorSession</c>, with
///         <c>EditorModules.Standard()</c> — and the test counts the entries. A grep for <c>new X(</c>
///         was tried first and could not see <c>BuiltInAssetKinds</c>' target-typed <c>new(…)</c>,
///         a loop that adds <c>StandardIcons.Assets</c> one by one, or a module that adds an
///         interface-typed seam through <c>Owns</c>; it reported writers missing that exist.
///     </para>
///     <para>
///         <b>The kinds are derived</b>: every <c>.All&lt;T&gt;()</c> in production <c>.cs</c> and
///         <c>.vxml</c> under <c>Editor/</c>, resolved to a type by name. ⚠ That is sixteen kinds,
///         not the twelve doc 50 § F8 counted — the four interface-typed seams
///         (<c>ITerrainScene</c>, <c>IVegetationScene</c>, <c>IWaterScene</c>, <c>IPlaySystems</c>)
///         are read through the same registry and were not in its list.
///     </para>
/// </remarks>
public class ContributionWriterTests {
    /// <summary>
    ///     Kinds an editor as shipped writes nothing of, each with why. ⚠ Shrink-only: a kind here that
    ///     acquires a writer fails the test until its line is deleted.
    /// </summary>
    static readonly Dictionary<string, string> Unwritten = new(StringComparer.Ordinal) {
        // Doc 50 § F8's three with a reader and no writer at all. Each is a contribution point the
        // editor's own features never use, so the first plugin through it is its first test.
        ["SettingsPage"] = "read by the Preferences and Project Settings windows; no first-party page is contributed",
        ["AssetPreview"] = "read by ThumbnailCache; every first-party preview is built in rather than contributed",
        ["BuildStep"] = "read by the player build; device deploy is still the application's own (#1295)",

        // And the three written only by `DeclaredContributions`, the attribute scanner — which runs
        // over a disk plugin's assembly or a project's scripts, never over a first-party one. Doc 50
        // § D5's generator is what would give them first-party authors (#1283).
        ["SceneTool"] = "written only by the [EditorTool] scan, which no shipped assembly is handed (#1283)",
        ["SceneOverlay"] = "written only by the [Overlay] scan, which no shipped assembly is handed (#1283)",
        ["ComponentGizmo"] = "written only by the [DrawGizmo] scan, which no shipped assembly is handed (#1283)"
    };

    static readonly Regex Reader = new(@"\.All<([A-Z][A-Za-z0-9_]*)>\(\)", RegexOptions.CultureInvariant);

    static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "Vixen.slnx"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>The kind names production code reads, by grep over <c>.cs</c> and <c>.vxml</c>.</summary>
    static SortedSet<string> ReadKinds(string root) {
        var kinds = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Editor"), "*.*", SearchOption.AllDirectories)) {
            if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".vxml", StringComparison.Ordinal)) {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (relative.Split('/').Any(part => part is "obj" or "bin" || part.EndsWith(".Tests", StringComparison.Ordinal))) {
                continue;
            }

            foreach (Match match in Reader.Matches(File.ReadAllText(file))) {
                kinds.Add(match.Groups[1].Value);
            }
        }

        return kinds;
    }

    /// <summary>Every editor assembly beside this one loaded, and every Vixen type, so a kind can be found by name.</summary>
    static List<Type> EditorTypes() {
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "Vixen.Editor.*.dll")) {
            try {
                Assembly.Load(AssemblyName.GetAssemblyName(path));
            } catch (Exception exception) when (exception is BadImageFormatException or FileLoadException) {
                // Not a managed assembly this process can load; nothing it declares is read here.
            }
        }

        return [
            .. AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name?.StartsWith("Vixen.", StringComparison.Ordinal) == true)
                .Where(assembly => !assembly.GetName().Name!.EndsWith(".Tests", StringComparison.Ordinal))
                .SelectMany(assembly => {
                    try {
                        return assembly.GetTypes();
                    } catch (ReflectionTypeLoadException exception) {
                        return exception.Types.OfType<Type>().ToArray();
                    }
                })
        ];
    }

    static int Count(IEditorRegistry registry, Type kind) {
        var all = typeof(IEditorRegistry).GetMethod(nameof(IEditorRegistry.All))!.MakeGenericMethod(kind);
        return ((IEnumerable) all.Invoke(registry, null)!).Cast<object>().Count();
    }

    /// <summary>What each kind holds in an editor opened the way the host opens one.</summary>
    static SortedDictionary<string, int> Census() {
        var root = Root();
        var names = ReadKinds(root);
        var census = new SortedDictionary<string, int>(StringComparer.Ordinal);

        using var editor = EditorSession.Start();

        // After the editor is up, so every assembly it reads a kind from is loaded.
        var types = EditorTypes();

        foreach (var name in names) {
            var candidates = types.Where(type => type.Name == name && (type.IsClass || type.IsInterface)).Distinct().ToList();

            Assert.True(
                candidates.Count == 1,
                $"The contribution kind '{name}' resolves to {candidates.Count} types ({string.Join(", ", candidates.Select(type => type.FullName))}); "
                + "a kind this test cannot name is one it cannot count."
            );

            census[name] = Count(editor.Extensions, candidates[0]);
        }

        return census;
    }

    [Fact]
    public void Every_kind_the_editor_reads_is_written_by_the_editor_a_user_launches() {
        var census = Census();

        // The instrument first. A grep that stopped matching, or a registry that is not the one the
        // application writes into, both read as "nothing is written" — so the kinds the editor
        // demonstrably writes on every start must be there and non-empty before an empty one means
        // anything: the Create ▸ kinds `BuiltInAssetKinds` adds, and the inspectors the modules add.
        Assert.True(census.Count >= 12, $"Only {census.Count} contribution kinds were read: {string.Join(", ", census.Keys)}.");
        Assert.True(census.GetValueOrDefault("NewAssetKind") > 0, "NewAssetKind read as empty — the census is reading the wrong registry.");
        Assert.True(census.GetValueOrDefault("CustomInspector") > 0, "CustomInspector read as empty — the census is reading the wrong registry.");

        var unwritten = census.Where(row => row.Value == 0).Select(row => row.Key).ToList();

        Assert.True(
            unwritten.Except(Unwritten.Keys).ToList() is { Count: 0 },
            "Read by the editor and written by nothing in it: "
            + string.Join(", ", unwritten.Except(Unwritten.Keys))
            + ". A registry nobody writes is a list with a longer name — give it a first-party writer, or add it to "
            + "Unwritten with the reason it is plugin-only. Census: "
            + string.Join(", ", census.Select(row => $"{row.Key}={row.Value}"))
        );

        Assert.True(
            Unwritten.Keys.Except(unwritten).ToList() is { Count: 0 },
            "On the Unwritten list and written now: " + string.Join(", ", Unwritten.Keys.Except(unwritten))
            + ". Delete the line — the list may only shrink."
        );
    }
}
