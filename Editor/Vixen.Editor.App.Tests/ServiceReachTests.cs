// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui.Documents;
using Vixen.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 50 § D6's <c>ServiceReachTest</c>: a context service's verb that nothing outside the service names.</summary>
/// <remarks>
///     <para>
///         <b>CLAUDE.md's commonest defect, applied to the new surface</b> (#1296): every public method
///         on a published context service is a use case, and one that no command, menu, module or panel
///         calls is a finished thing nothing calls. This greps for callers, not for the type — over
///         <c>.cs</c> <i>and</i> <c>.vxml</c> under <c>Editor/</c>, outside the tests and outside the file
///         that declares the interface.
///     </para>
///     <para>
///         ⚠ <b>A name match, not a binding, and it errs green.</b> A caller is <c>.Name</c> anywhere in
///         production source, so a method called <c>Open</c> is reached by every other <c>Open</c> in the
///         editor. What it can see is the method whose name nothing else uses — which is exactly the
///         shape a forgotten verb has. Narrowing it to a semantic answer is a Roslyn walk, and is owed.
///     </para>
///     <para>
///         ⚠ <b>Shrink-only, the <c>CheckWhitespace</c> shape.</b> <see cref="ExemptFile" /> lists what is
///         unreached today; a new unreached method fails, and so does a listed one that has gained a
///         caller. Regenerate with <c>VIXEN_REGENERATE=1</c> and read the diff — a line added is a verb
///         nobody can reach.
///     </para>
/// </remarks>
public sealed class ServiceReachTests {
    /// <summary>The committed list of methods nothing reaches yet, relative to the repository root.</summary>
    const string ExemptFile = "Editor/Vixen.Editor.App.Tests/ServiceReach.txt";

    /// <summary>The context services doc 50 § D2 publishes, whose methods are use cases.</summary>
    static readonly Type[] Services = [
        typeof(ISceneEditing),
        typeof(IContentLibrary),
        typeof(ISourceControl),
        typeof(IProjectSession),
        typeof(IPlaySession),
        typeof(IPlayerDelivery),
        typeof(IDocumentHost),
        typeof(IWorkspace)
    ];

    [Fact]
    public void Every_context_service_verb_has_a_caller_outside_the_service() {
        var root = RepositoryFiles.Root;
        var sources = Production(root);
        var measured = new List<string>();
        var reached = new List<string>();

        foreach (var service in Services) {
            var declaring = Declaring(sources, service);
            var others = sources.Where(source => !string.Equals(source.Path, declaring, StringComparison.Ordinal)).ToList();

            foreach (var name in Verbs(service)) {
                var line = service.Name + "." + name;
                var call = new Regex(@"\." + Regex.Escape(name) + @"\b", RegexOptions.CultureInvariant);

                if (others.Any(source => call.IsMatch(source.Text))) {
                    reached.Add(line);
                } else {
                    measured.Add(line);
                }
            }
        }

        // The instrument first: a walk that read no source reaches nothing and would agree with a list of
        // everything. `ISceneEditing.Group` is Ctrl+G, registered by `SceneModule`.
        Assert.Contains("ISceneEditing.Group", reached);
        Assert.True(reached.Count > 50, $"only {reached.Count} verbs reached — did the walk read the tree?");

        var path = Path.Combine(root, ExemptFile.Replace('/', Path.DirectorySeparatorChar));

        if (Environment.GetEnvironmentVariable("VIXEN_REGENERATE") == "1") {
            File.WriteAllText(path, Render(measured), new UTF8Encoding(false));
        }

        var recorded = File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0 && !line.StartsWith('#')).ToList()
            : [];

        var unreached = measured.Except(recorded).ToList();
        var stale = recorded.Except(measured).ToList();

        Assert.True(
            unreached.Count == 0 && stale.Count == 0,
            $"""
             ⚠ A context service verb nothing outside its service names — give it a caller (a command, a
             menu line, a module) or take it off the interface:
             {string.Join("\n", unreached.Select(line => "  " + line))}

             Listed in {ExemptFile} and reached now — delete the line (the list may only shrink):
             {string.Join("\n", stale.Select(line => "  " + line))}
             """
        );
    }

    /// <summary>The service's own methods, by name, without accessors or event plumbing.</summary>
    static IEnumerable<string> Verbs(Type service) =>
        service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    /// <summary>The file that declares the interface, which is where its implementation's own calls are too.</summary>
    static string Declaring(IReadOnlyList<(string Path, string Text)> sources, Type service) {
        var declaration = new Regex(@"\binterface\s+" + Regex.Escape(service.Name) + @"\b", RegexOptions.CultureInvariant);

        return sources.Single(source => declaration.IsMatch(source.Text)).Path;
    }

    /// <summary>Every production <c>.cs</c> and <c>.vxml</c> under <c>Editor/</c>, comments stripped.</summary>
    static List<(string Path, string Text)> Production(string root) =>
        [
            .. RepositoryFiles.Files(Path.Combine(root, "Editor"), "*.cs", "*.vxml")
                .Where(static path => !path.Contains(".Tests", StringComparison.Ordinal)
                    && !path.Contains("Vixen.Editor.Testing", StringComparison.Ordinal))
                .Select(static path => (path, Uncommented(File.ReadAllText(path))))
        ];

    /// <summary>The text without <c>//</c> comments, so a remark naming a verb is not a caller.</summary>
    static string Uncommented(string text) =>
        string.Join('\n', text.Split('\n').Select(static line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line));

    static string Render(IEnumerable<string> lines) {
        var text = new StringBuilder()
            .Append("# Doc 50 § D6's ServiceReachTest: context service verbs nothing outside the service names yet.\n")
            .Append("# Shrink-only. Regenerate with VIXEN_REGENERATE=1 on ServiceReachTests, and read the diff.\n\n");

        foreach (var line in lines.Order(StringComparer.Ordinal)) {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }
}
