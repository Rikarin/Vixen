// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;

namespace Vixen.Build;

/// <summary>
///     Doc 36 § P3's third exit criterion — "<c>CheckArchitecture</c> gains a rule that fails the
///     build if a feature assembly is referenced again" — as a function of project files.
/// </summary>
/// <remarks>
///     <para>
///         <b>Without it every row P3 removes can come back in silence.</b> Blockout and Terrain were
///         decoupled at real cost — 1,460 lines out of the application, two new extension points, a
///         contract moved into <c>Core/Vixen.Rendering.Terrain</c> — and until this rule existed
///         nothing failed when either was referenced again. For F2, the finding doc 36 says matters
///         most, that is the difference between a fix and a tidy-up.
///     </para>
///     <para>
///         ⚠ <b>One list, and there used to be two.</b> <see cref="Allowed" /> is what P3's exit
///         permits for ever. Beside it stood <c>NotYetMoved</c>, what the application still named
///         with an issue behind each — shrink-only, because a name in it that was no longer referenced
///         failed too. ⚠ <b>Deleted rather than emptied</b> (#1302), once #1297 took the asset editors
///         off as its last name: an empty shrink-only list asserts nothing, and the half of the rule
///         that read it could only ever answer "nothing owed". What is left is the rule that
///         anything not in <see cref="Allowed" /> fails.
///     </para>
///     <para>
///         ⚠ <b>The exit list omits <c>Assets</c> and is corrected here.</b> Doc 36's P3 says
///         "<c>Core</c>, <c>Ui</c>, <c>Plugin</c>, <c>Inspector</c>, <c>SceneView</c> and nothing
///         else", and its own table then records that <c>Assets</c> is the import pipeline with
///         "nothing" in the column for what would move it — an editor that cannot import without a
///         plugin is not an editor. A rule encoding the sentence rather than the table would fail the
///         build today for a reference nobody intends to remove.
///     </para>
///     <para>
///         <b>Project files, not assemblies.</b> <c>Assembly.GetReferencedAssemblies</c> lists what
///         the compiler emitted a reference for, so a <c>ProjectReference</c> nothing has used yet is
///         invisible to it — and a reference nobody has used yet is exactly the state a feature
///         creeping back in starts from. <c>PluginReferenceRule</c> makes the same argument for the
///         same reason.
///     </para>
///     <para>
///         ⚠ <b>Analyzer references are not feature references and are skipped.</b> A
///         <c>ProjectReference</c> with <c>OutputItemType="Analyzer"</c> and
///         <c>ReferenceOutputAssembly="false"</c> contributes a generator to the compilation and
///         nothing to the closure — <c>Vixen.Editor.Inspector.Generator</c> is one, and counting it
///         would make the rule fail on the arrangement every project in this repository uses to reach
///         a source generator at all.
///     </para>
/// </remarks>
static class ApplicationReferenceRule {
    /// <summary>The editor application, whose reference list this is about.</summary>
    public const string Application = "Vixen.Editor.App";

    /// <summary>
    ///     The <c>Editor/</c> assemblies P3's exit permits the application to reference for ever.
    /// </summary>
    /// <remarks>
    ///     The shell, the plugin contract, the project model, the import pipeline, and the two panels
    ///     that are more than chrome. Joining those is what the application is; everything else is a
    ///     feature and belongs behind <c>PluginContext</c>.
    /// </remarks>
    public static readonly string[] Allowed = [
        "Vixen.Editor.Ui",
        "Vixen.Editor.Plugin",
        "Vixen.Editor.Core",
        "Vixen.Editor.Assets",
        "Vixen.Editor.Inspector",
        "Vixen.Editor.SceneView"
    ];


    /// <summary>The <c>Editor/</c> assemblies one project references, analyzers excluded.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="projectFile">The project to read.</param>
    /// <returns>Their assembly names, ordered.</returns>
    public static List<string> EditorReferences(string root, string projectFile) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(projectFile);

        var directory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var editor = Path.GetFullPath(Path.Combine(root, "Editor")) + Path.DirectorySeparatorChar;

        return XDocument.Load(projectFile)
            .Descendants("ProjectReference")

            // ⚠ An analyzer contributes a generator and nothing to the closure. Every project here
            // reaches a source generator this way, so counting one would make the rule fail on the
            // arrangement the repository is built out of.
            .Where(element => element.Attribute("OutputItemType")?.Value != "Analyzer")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => Path.GetFullPath(Path.Combine(directory, value!.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(path => path.StartsWith(editor, StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Why the rule is checking nothing, if it is.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="projectFiles">Every project file the gate reads.</param>
    /// <returns>The reason, or <see langword="null" /> when there is a real subject.</returns>
    /// <remarks>
    ///     ⚠ <b>A rule whose subject has been renamed reports "no violations" and means "I read
    ///     nothing".</b> That is the failure this repository has found four times, most recently in
    ///     <c>PluginReferenceRule</c>'s own subject set
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/872">#872</a>), so the application has to
    ///     be found and it has to reference something under <c>Editor/</c> before an empty violation
    ///     list means anything.
    /// </remarks>
    public static string? Vacuity(string root, IEnumerable<string> projectFiles) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(projectFiles);

        if (Find(projectFiles) is not { } project) {
            return $"No project called {Application} was found, so the application-reference rule is checking nothing.";
        }

        return EditorReferences(root, project).Count == 0
            ? $"{Application} references nothing under Editor/, which cannot be true of the assembly that "
            + "joins the editor together — the rule is reading the wrong file."
            : null;
    }

    /// <summary>Everything wrong with the application's reference list.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="projectFiles">Every project file the gate reads.</param>
    /// <returns>One sentence per violation.</returns>
    public static List<string> Violations(string root, IEnumerable<string> projectFiles) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(projectFiles);

        if (Find(projectFiles) is not { } project) {
            return [];
        }

        var referenced = EditorReferences(root, project);
        var violations = new List<string>();

        foreach (var reference in referenced) {
            if (Allowed.Contains(reference, StringComparer.Ordinal)) {
                continue;
            }

            violations.Add(
                $"{Application} references {reference}, which is a feature assembly. Doc 36 § P3: a feature "
                + $"registers through PluginContext from an assembly that cannot see {Application}. If this "
                + "reference is genuinely the application's job, say so in ApplicationReferenceRule.Allowed "
                + "with the argument."
            );
        }

        return violations;
    }

    static string? Find(IEnumerable<string> projectFiles) =>
        projectFiles.FirstOrDefault(path =>
            string.Equals(Path.GetFileNameWithoutExtension(path), Application, StringComparison.Ordinal)
        );
}
