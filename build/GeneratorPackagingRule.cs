// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;

namespace Vixen.Build;

/// <summary>
///     A generator or analyzer project that no packable library carries into its package, as a
///     function of the project files alone.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An analyzer does not flow through a <c>ProjectReference</c>, so in-tree evidence
///         cannot answer this question at all.</b> Every consumer inside this repository names the
///         generator itself; a consumer outside it has only what the <c>.nupkg</c> carries. No
///         <c>*.Generator</c> here is packable in its own right — <c>Directory.Build.props</c>'s
///         COMPILER PLUGIN profile sets <c>IsPackable=false</c> for all thirteen — so the only way one
///         ships is inside the library it belongs to, as a <c>TfmSpecificPackageFile</c> under
///         <c>analyzers/dotnet/cs</c>. That is what this reads.
///     </para>
///     <para>
///         ⚠ <b>Matched on "some packable library packs this generator", never on the sibling
///         directory</b>, which is <a href="https://github.com/Rikarin/Vixen/issues/1165">#1165</a>'s
///         own warning and not a hypothetical: <c>Vixen.Ui.Markup.Generators</c> is packed by
///         <c>Vixen.Ui</c> and there is no <c>Vixen.Ui.Markup</c> package to pack it, so a name-prefix
///         rule reports a false positive for the one generator that is already correct.
///     </para>
///     <para>
///         <b>Not every generator belongs in a package, which is what the exemption file is for.</b>
///         Three of the four this rule found are deliberate and the reasons are different in kind:
///         one emits at post-initialisation and would emit a <em>second</em> copy of the same partial
///         in a consumer's compilation; one is a rule about this repository's own code that
///         <c>Directory.Build.props</c> does not even apply to <c>Editor/</c> or <c>Platform/</c>;
///         one warns unconditionally when the compilation has no <c>Syntax.xml</c>, so shipping it
///         would warn every consumer that is not writing a language. A rule with no exemptions here
///         would be wrong three times out of four.
///     </para>
///     <para>
///         <b>A pure function so that its answer can be read without running the gate</b>, the shape
///         <see cref="PluginReferenceRule" /> records and <see cref="ComponentGeneratorRule" />
///         follows: <c>CheckArchitecture</c> is one caller and <c>GeneratorPackagingRuleTests</c> is
///         the other.
///     </para>
///     <para>
///         ⚠ <b>What it reads is the intent, and the bytes are still the proof.</b> This says a
///         library names the generator in a target that <c>TargetsForTfmSpecificContentInPackage</c>
///         runs; whether <c>dotnet pack</c> then put a DLL in the archive is a question only the
///         archive answers, and <c>PackageContents</c> is where that kind of check lives. The
///         two are complements: this one runs in seconds and catches the omission, that one runs
///         under <c>Pack</c> and catches the target that silently produced nothing.
///     </para>
/// </remarks>
static class GeneratorPackagingRule {
    /// <summary>The name of the file that lists the generators deliberately not packed.</summary>
    public const string ExemptionFile = "docs/GeneratorPackagingExempt.txt";

    /// <summary>Whether a project name is a compiler plugin by the profile's own three suffixes.</summary>
    /// <param name="name">The project's file name without extension.</param>
    /// <returns>True for a generator or analyzer project.</returns>
    /// <remarks>
    ///     The same three suffixes <c>Directory.Build.props</c> switches the COMPILER PLUGIN profile
    ///     on with, spelled once here. A project that is a generator by any other means is not one to
    ///     the build either, so agreeing with the profile is the whole of correctness.
    /// </remarks>
    public static bool IsCompilerPlugin(string name) =>
        name is not null
        && !name.EndsWith(".Tests", StringComparison.Ordinal)
        && (name.EndsWith(".Generator", StringComparison.Ordinal)
            || name.EndsWith(".Generators", StringComparison.Ordinal)
            || name.EndsWith(".Analyzers", StringComparison.Ordinal));

    /// <summary>Whether a project ships as a package.</summary>
    /// <param name="projectFile">The project to read.</param>
    /// <returns>True unless it is a test, a compiler plugin, or says <c>IsPackable=false</c> itself.</returns>
    /// <remarks>
    ///     ⚠ <b>Absence means yes</b>, exactly as it does in <c>Build.Api.cs</c> and in
    ///     <c>ApiCoverageTests</c> — <c>Directory.Build.props</c> sets no <c>IsPackable</c> for
    ///     <c>Editor/</c>, <c>Tools/</c> or <c>Raven/</c> at all, so the SDK default of <c>true</c>
    ///     stands there and those projects really do produce a <c>.nupkg</c>
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/641">#641</a>). Reading the property out
    ///     of the profile instead would need an MSBuild evaluation, which is the cost this whole file
    ///     exists to avoid.
    /// </remarks>
    public static bool IsPackableLibrary(string projectFile) {
        ArgumentNullException.ThrowIfNull(projectFile);

        var name = Path.GetFileNameWithoutExtension(projectFile);

        if (name.EndsWith(".Tests", StringComparison.Ordinal) || IsCompilerPlugin(name)) {
            return false;
        }

        var document = XDocument.Load(projectFile);

        foreach (var property in document.Descendants("IsPackable")) {
            if (string.Equals(property.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every generator one project carries into its own package.</summary>
    /// <param name="projectFile">The project to read.</param>
    /// <returns>Generator project names, ordered.</returns>
    /// <remarks>
    ///     <para>
    ///         The two halves have to agree or nothing is packed: a target name listed in
    ///         <c>TargetsForTfmSpecificContentInPackage</c>, and a <c>&lt;Target&gt;</c> of that name
    ///         invoking the generator's project file. ⚠ <b>Reading either half alone is how this
    ///         reports a package that does not ship what it names</b> — a <c>Target</c> nothing runs
    ///         produces no error at pack time and no file in the archive, and a name in the property
    ///         with no target behind it is an MSBuild warning at most.
    ///     </para>
    ///     <para>
    ///         The semicolon list is split rather than searched, so <c>$(…)</c> — which is always the
    ///         first entry, because these targets append — drops out with the empty entries.
    ///     </para>
    /// </remarks>
    public static List<string> Packs(string projectFile) {
        ArgumentNullException.ThrowIfNull(projectFile);

        var document = XDocument.Load(projectFile);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in document.Descendants("TargetsForTfmSpecificContentInPackage")) {
            foreach (var entry in property.Value.Split(';')) {
                var name = entry.Trim();

                if (name.Length > 0 && !name.StartsWith("$(", StringComparison.Ordinal)) {
                    targets.Add(name);
                }
            }
        }

        var packed = new List<string>();

        foreach (var target in document.Descendants("Target")) {
            var name = target.Attribute("Name")?.Value;

            if (name is null || !targets.Contains(name)) {
                continue;
            }

            foreach (var call in target.Descendants("MSBuild")) {
                var projects = call.Attribute("Projects")?.Value;

                if (projects is null) {
                    continue;
                }

                foreach (var entry in projects.Split(';')) {
                    var referenced = Path.GetFileNameWithoutExtension(entry.Trim().Replace('\\', '/'));

                    if (IsCompilerPlugin(referenced)) {
                        packed.Add(referenced);
                    }
                }
            }
        }

        packed.Sort(StringComparer.Ordinal);
        return packed;
    }

    /// <summary>Every generator project in the tree — the set the rule asks its question about.</summary>
    /// <param name="projects">Paths to the project files to check.</param>
    /// <returns>Generator project names, ordered.</returns>
    /// <remarks>
    ///     ⚠ <b>Every one of them, not only the ones some library names as an <c>Analyzer</c>
    ///     reference.</b> <c>Vixen.Core.IO.Analyzers</c> is referenced by nothing in any
    ///     <c>.csproj</c> — <c>Directory.Build.props:485</c> adds it to every non-generator
    ///     <c>Core/</c> project at once — so a subject set built from the references would not
    ///     contain it, and the rule would be silent about the one generator whose packaging is
    ///     genuinely a judgement call.
    /// </remarks>
    public static List<string> Subjects(IEnumerable<string> projects) {
        ArgumentNullException.ThrowIfNull(projects);

        return projects
            .Select(project => Path.GetFileNameWithoutExtension(project))
            .Where(IsCompilerPlugin)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every generator some packable library carries into a package.</summary>
    /// <param name="projects">Paths to the project files to check.</param>
    /// <returns>Generator project names, ordered.</returns>
    public static List<string> Packaged(IEnumerable<string> projects) {
        ArgumentNullException.ThrowIfNull(projects);

        return projects
            .Where(IsPackableLibrary)
            .SelectMany(Packs)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every generator no packable library ships.</summary>
    /// <param name="projects">Paths to the project files to check.</param>
    /// <returns>Generator project names, ordered.</returns>
    public static List<string> Offenders(IEnumerable<string> projects) {
        ArgumentNullException.ThrowIfNull(projects);

        var files = projects.ToList();
        var packaged = Packaged(files);

        return Subjects(files)
            .Where(subject => !packaged.Contains(subject, StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>The generator names the exemption file allows to go unpacked.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The names, in file order, with blank and <c>#</c> lines dropped.</returns>
    /// <remarks>
    ///     One name per line, and everything after the first space on a line is the reason it is
    ///     there — the arrangement <c>docs/ComponentGeneratorExempt.txt</c> already uses. A line with
    ///     no reason is still read; a reason is what makes the list reviewable.
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
    ///     ⚠ <b>An exempted generator that has become packed is a violation too</b>, which is what
    ///     makes the list only ever shrink — the property <c>CheckWhitespace</c>'s and
    ///     <c>CheckDocComments</c>' lists have, and the reason a stale line cannot sit there hiding a
    ///     library that has since stopped packing its generator again.
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
                    $"{offender} is packed by no library, so a consumer with a PackageReference gets the "
                    + "attributes it reads and none of the code it writes. An analyzer does not flow through "
                    + "a ProjectReference, so nothing in this tree can notice. Name it in the owning "
                    + "library's TargetsForTfmSpecificContentInPackage target — Vixen.Core.Reflection is the "
                    + $"pattern — or add a line to {ExemptionFile} saying why it does not belong in a package."
                );
            }
        }

        foreach (var name in exempt) {
            if (!offenders.Contains(name, StringComparer.Ordinal)) {
                violations.Add(
                    $"{name} is listed in {ExemptionFile} and no longer needs to be — a packable library "
                    + "packs it now, or the project is gone. Remove the line; the list can only shrink."
                );
            }
        }

        return violations;
    }
}
