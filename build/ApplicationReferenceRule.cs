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
///         ⚠ <b>Two lists, and the second is what makes the criterion move rather than describe.</b>
///         <see cref="Allowed" /> is what P3's exit permits for ever. <see cref="NotYetMoved" /> is
///         what the application still names today, each with an issue behind it — and a name in
///         <i>that</i> list which is no longer referenced is <b>also</b> a violation. So the list can
///         only shrink: the batch that finally dereferences the profiler is told to delete the line,
///         in the same run, rather than leaving a rule that has quietly stopped asserting anything.
///         <c>CheckWhitespace</c>'s exemption file is the same shape and the same reason.
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

    /// <summary>
    ///     What the application still references, each with the reason it has not moved. ⚠ This list
    ///     may only shrink: a name here that is no longer referenced fails too.
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item>
    ///             <c>Vixen.Editor.AssetEditors</c> — ten names across seven files, measured twice by
    ///             deleting the line and compiling. The registry is one tenth of it and the
    ///             arbitration the other names carry is what P3 says belongs in the application.
    ///             <a href="https://github.com/Rikarin/Vixen/issues/88">#88</a>.
    ///         </item>
    ///         <item>
    ///             <c>Vixen.Editor.NodeGraph</c> — one call, <c>NodeGraphTheme.Install</c>, the
    ///             user-agent sheet the four graph panels are drawn with
    ///             (<a href="https://github.com/Rikarin/Vixen/issues/917">#917</a>). ⚠ It is not in
    ///             doc 36's table, which lists four names and not five.
    ///         </item>
    ///         <item>
    ///             <c>Vixen.Editor.Profiler</c> — the diagnostics report, which aggregates the
    ///             project, the scene, the log ring and the last capture. Moving it needs the log ring
    ///             and the data directory published through <c>PluginServices</c>.
    ///         </item>
    ///         <item>
    ///             <c>Vixen.Editor.Debugger</c> — the report, <b>and</b> device deploy in
    ///             <c>EditorBuilds</c>. ⚠ <b>This bullet used to say the deploy was waiting on a
    ///             build-step contribution point that does not exist; it exists now</b> —
    ///             <c>BuildStep</c> in <c>Vixen.Editor.Assets.Content</c>, D4's last row — so what is
    ///             left is the move rather than the mechanism. The names that keep the reference are
    ///             <c>IDeviceDeploy</c>, <c>DeviceEntry</c>, <c>DeviceKind</c> and
    ///             <c>DeviceStatus</c>, and a deploy expressed as a contribution still has to say
    ///             <i>which device</i>.
    ///             <a href="https://github.com/Rikarin/Vixen/issues/400">#400</a>,
    ///             <a href="https://github.com/Rikarin/Vixen/issues/399">#399</a>.
    ///         </item>
    ///         <item>
    ///             <c>Vixen.Editor.Diagnostics</c> — the module that joins the two above to a project,
    ///             a scene and a device. Its reference <i>is</i> the <c>Activate</c> call, so it goes
    ///             when the two above do and not before.
    ///         </item>
    ///     </list>
    /// </remarks>
    public static readonly string[] NotYetMoved = [
        "Vixen.Editor.AssetEditors",
        "Vixen.Editor.NodeGraph",
        "Vixen.Editor.Profiler",
        "Vixen.Editor.Debugger",
        "Vixen.Editor.Diagnostics"
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
            if (Allowed.Contains(reference, StringComparer.Ordinal)
                || NotYetMoved.Contains(reference, StringComparer.Ordinal)) {
                continue;
            }

            violations.Add(
                $"{Application} references {reference}, which is a feature assembly. Doc 36 § P3: a feature "
                + $"registers through PluginContext from an assembly that cannot see {Application}. If this "
                + "reference is genuinely the application's job, say so in ApplicationReferenceRule.Allowed "
                + "with the argument."
            );
        }

        foreach (var moved in NotYetMoved.Where(name => !referenced.Contains(name, StringComparer.Ordinal))) {
            violations.Add(
                $"{Application} no longer references {moved}, so delete it from "
                + "ApplicationReferenceRule.NotYetMoved. That list is what is still owed and it may only "
                + "shrink — a name left in it is a rule that has stopped asserting anything about that "
                + "assembly."
            );
        }

        return violations;
    }

    static string? Find(IEnumerable<string> projectFiles) =>
        projectFiles.FirstOrDefault(path =>
            string.Equals(Path.GetFileNameWithoutExtension(path), Application, StringComparison.Ordinal)
        );
}
