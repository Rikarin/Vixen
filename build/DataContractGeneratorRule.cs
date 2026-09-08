// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;

namespace Vixen.Build;

/// <summary>
///     A <c>[DataContract]</c> in a project that names neither registration generator, as a function
///     of project files and their sources.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The attribute compiles and does nothing, and nothing anywhere says so.</b>
///         <c>DataContractAttribute</c> lives in <c>Vixen.Core</c> and arrives transitively through
///         any reference; the two generators that turn it into a registration are analyzers, and
///         <b>an analyzer does not flow through a <c>ProjectReference</c></b>. So a project that
///         names neither compiles clean, runs clean, and registers nothing — the type is absent from
///         <c>TypeRegistry</c> and from <c>SerializerRegistry</c> under the name it states.
///         <c>Vixen.Editor.Terrain</c> carried eight of them
///         (<a href="https://github.com/Rikarin/Vixen/issues/989">#989</a>), and the guard that
///         caught them is a runtime test in that one assembly's own suite
///         (<a href="https://github.com/Rikarin/Vixen/issues/1005">#1005</a>).
///     </para>
///     <para>
///         ⚠ <b>Either generator satisfies the rule, and that is the difference between this and a
///         rule with seventy false positives.</b> <c>Vixen.Core.Reflection.Generator</c> emits the
///         <c>TypeRegistry</c> half and <c>Vixen.Core.Serialization.Generator</c> the
///         <c>SerializerRegistry</c> half, and wanting one without the other is an ordinary choice —
///         <c>Vixen.Editor.Assets</c> names the reflection one for its thirty-eight importers and no
///         serializer, deliberately. Measured on this tree: requiring the serialization generator
///         alone reports eighteen projects, requiring either reports seven. The defect #989 names is
///         "neither", so "neither" is what this refuses.
///     </para>
///     <para>
///         ⚠ <b>Text over the sources, not a Roslyn symbol walk, and the limits are stated.</b> A
///         line whose first non-space characters are <c>[DataContract</c> is an application; a line
///         inside a doc comment or a <c>//</c> comment is not, which is what keeps the seventy-odd
///         files that <em>mention</em> the attribute out. A raw string literal holding C# source can
///         still fool it — generator fixtures do exactly that — which is what the exemption file is
///         for, one line and one reason each.
///     </para>
///     <para>
///         <b>A pure function so that its answer can be read without running the gate</b>, the shape
///         <see cref="PluginReferenceRule" /> records: <c>CheckArchitecture</c> is one caller and
///         <c>DataContractGeneratorRuleTests</c> is the other, over this repository and over
///         synthetic fixtures that make it fire. The subject set comes from
///         <see cref="PluginReferenceRule.ProjectFiles" /> rather than from a second glob, which is
///         #872's lesson.
///     </para>
/// </remarks>
static class DataContractGeneratorRule {
    /// <summary>The generator that emits the <c>SerializerRegistry</c> half.</summary>
    public const string SerializationGenerator = "Vixen.Core.Serialization.Generator";

    /// <summary>The generator that emits the <c>TypeRegistry</c> half.</summary>
    public const string ReflectionGenerator = "Vixen.Core.Reflection.Generator";

    /// <summary>The package that carries both, for a project outside this repository.</summary>
    /// <remarks>
    ///     A <c>PackageReference</c> ships analyzers, which is precisely why the omission is only
    ///     reachable from inside this tree — the same sentence <c>Vixen.Editor.Terrain.csproj</c>
    ///     records about <c>Vixen.Ui.Generators</c> and VX4003.
    /// </remarks>
    public const string Package = "Vixen.Core";

    /// <summary>The name of the file that lists the projects allowed to be in this state.</summary>
    public const string ExemptionFile = "docs/DataContractGeneratorExempt.txt";

    /// <summary>Every source file a project compiles, by convention rather than by evaluation.</summary>
    /// <param name="projectFile">The project to read around.</param>
    /// <returns>Absolute paths, forward-slashed.</returns>
    /// <remarks>
    ///     <para>
    ///         The SDK's default glob is "everything under the project directory", and the one thing
    ///         that reliably ends it is another project file — so the walk stops at a directory that
    ///         holds one rather than trying to evaluate <c>DefaultItemExcludes</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>.vxml</c> as well as <c>.cs</c>.</b> A view's <c>&lt;code&gt;</c> block is
    ///         production C#, and a sweep that reads only <c>*.cs</c> reports a gap that is not there
    ///         — silently, because a clean grep looks like evidence.
    ///     </para>
    /// </remarks>
    public static List<string> Sources(string projectFile) {
        ArgumentNullException.ThrowIfNull(projectFile);

        var directory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var sources = new List<string>();

        Walk(directory, true, sources);
        sources.Sort(StringComparer.Ordinal);
        return sources;

        static void Walk(string directory, bool root, List<string> into) {
            var files = Directory.GetFiles(directory);

            if (!root && Array.Exists(files, file => file.EndsWith(".csproj", StringComparison.Ordinal))) {
                return;
            }

            foreach (var file in files) {
                if (file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".vxml", StringComparison.Ordinal)) {
                    into.Add(file.Replace('\\', '/'));
                }
            }

            foreach (var child in Directory.GetDirectories(directory)) {
                var name = Path.GetFileName(child);

                if (name is "bin" or "obj" or ".git") {
                    continue;
                }

                Walk(child, false, into);
            }
        }
    }

    /// <summary>Where a source file applies the attribute.</summary>
    /// <param name="source">The file to read.</param>
    /// <returns>One-based line numbers.</returns>
    public static List<int> Applications(string source) {
        ArgumentNullException.ThrowIfNull(source);

        var lines = File.ReadAllLines(source);
        var found = new List<int>();

        for (var index = 0; index < lines.Length; index++) {
            var text = lines[index].TrimStart();

            if (text.StartsWith("///", StringComparison.Ordinal)
                || text.StartsWith("//", StringComparison.Ordinal)
                || text.StartsWith('*')) {
                continue;
            }

            if (text.StartsWith("[DataContract", StringComparison.Ordinal)) {
                found.Add(index + 1);
            }
        }

        return found;
    }

    /// <summary>Whether a project names either generator, or the package that carries both.</summary>
    /// <param name="projectFile">The project to read.</param>
    /// <returns>True when the attribute in that project would register something.</returns>
    public static bool NamesAGenerator(string projectFile) {
        ArgumentNullException.ThrowIfNull(projectFile);

        var document = XDocument.Load(projectFile);

        foreach (var reference in document.Descendants("ProjectReference")) {
            var include = reference.Attribute("Include")?.Value;

            if (include is null) {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));

            if (string.Equals(name, SerializationGenerator, StringComparison.Ordinal)
                || string.Equals(name, ReflectionGenerator, StringComparison.Ordinal)) {
                return true;
            }
        }

        // ⚠ A PackageReference and NOT a ProjectReference, and the difference is the whole defect.
        // A package ships its analyzers; a ProjectReference to `Vixen.Core` carries the attribute and
        // leaves both generators behind. Accepting the project form here read three projects as clean
        // that are not — caught by the exemption file's own reverse direction, which is the argument
        // for having one.
        foreach (var package in document.Descendants("PackageReference")) {
            if (string.Equals(package.Attribute("Include")?.Value, Package, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every project that applies the attribute and names neither generator.</summary>
    /// <param name="projects">Paths to the project files to check.</param>
    /// <returns>Project names, ordered.</returns>
    public static List<string> Offenders(IEnumerable<string> projects) {
        ArgumentNullException.ThrowIfNull(projects);

        var offenders = new List<string>();

        foreach (var project in projects) {
            if (NamesAGenerator(project)) {
                continue;
            }

            if (Sources(project).Exists(source => Applications(source).Count > 0)) {
                offenders.Add(Path.GetFileNameWithoutExtension(project));
            }
        }

        offenders.Sort(StringComparer.Ordinal);
        return offenders;
    }

    /// <summary>How many projects in the subject set apply the attribute at all.</summary>
    /// <param name="projects">Paths to the project files to check.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    ///     ⚠ <b>The rule's subject set, which is the half of a rule nothing checks.</b> A walk that
    ///     found no application anywhere would report "no violations" and mean "I read nothing" — the
    ///     shape this repository has been caught by often enough to have a name for it. The gate
    ///     asserts this is non-zero before it believes an empty violation list.
    /// </remarks>
    public static int Subjects(IEnumerable<string> projects) {
        ArgumentNullException.ThrowIfNull(projects);

        var count = 0;

        foreach (var project in projects) {
            if (Sources(project).Exists(source => Applications(source).Count > 0)) {
                count++;
            }
        }

        return count;
    }

    /// <summary>The project names the exemption file allows to be offenders.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The names, in file order, with blank and <c>#</c> lines dropped.</returns>
    /// <remarks>
    ///     One name per line, and everything after the first space on a line is the reason it is
    ///     there. A line with no reason is still read; a reason is what makes the list reviewable.
    /// </remarks>
    public static List<string> Exempt(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var path = Path.Combine(root, ExemptionFile.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path)) {
            return [];
        }

        var names = new List<string>();

        foreach (var line in File.ReadAllLines(path)) {
            var text = line.Trim();

            if (text.Length == 0 || text.StartsWith('#')) {
                continue;
            }

            var space = text.IndexOf(' ', StringComparison.Ordinal);
            names.Add(space < 0 ? text : text[..space]);
        }

        return names;
    }

    /// <summary>What the gate reports.</summary>
    /// <param name="root">The repository root, for the exemption file.</param>
    /// <param name="projects">Paths to the project files to check.</param>
    /// <returns>One message per violation; empty when the tree agrees with the exemption file.</returns>
    /// <remarks>
    ///     ⚠ <b>An exempted project that has become clean is a violation too</b>, which is what makes
    ///     the list only ever shrink — the property <c>CheckWhitespace</c>'s and
    ///     <c>CheckDocComments</c>' lists have and the reason a stale line cannot sit there hiding a
    ///     project that has since been fixed and re-broken.
    /// </remarks>
    public static List<string> Violations(string root, IEnumerable<string> projects) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(projects);

        var offenders = Offenders(projects);
        var exempt = Exempt(root);
        var violations = new List<string>();

        foreach (var offender in offenders) {
            if (!exempt.Contains(offender, StringComparer.Ordinal)) {
                violations.Add(
                    $"{offender} applies [DataContract] and names neither {SerializationGenerator} nor "
                    + $"{ReflectionGenerator} as an Analyzer ProjectReference, so nothing it declares is "
                    + $"registered under the name it states. Add one, or add a line to {ExemptionFile} "
                    + "saying why the attribute is decoration here."
                );
            }
        }

        foreach (var name in exempt) {
            if (!offenders.Contains(name, StringComparer.Ordinal)) {
                violations.Add(
                    $"{name} is listed in {ExemptionFile} and no longer needs to be — it either names a "
                    + "generator now or applies no [DataContract]. Remove the line; the list can only shrink."
                );
            }
        }

        return violations;
    }
}
