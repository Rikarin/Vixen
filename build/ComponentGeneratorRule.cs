// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;

namespace Vixen.Build;

/// <summary>
///     A type carrying <c>[Component]</c> <em>and</em> <c>[DataContract]</c> in a project that can
///     see <c>SceneComponentRegistry</c> and does not name the generator that would declare it, as a
///     function of project files and their sources.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is strictly narrower than <see cref="DataContractGeneratorRule" /> and is not
///         covered by it.</b> That rule refuses a project naming <em>neither</em> registration
///         generator; a project naming the two <c>Vixen.Core.*</c> ones and not
///         <c>Vixen.Engine.Generators</c> reads clean there and is missing the third declaration.
///         <c>Vixen.Ai.Nodes</c> and <c>Vixen.Ai.Perception</c> were both in exactly that state
///         (<a href="https://github.com/Rikarin/Vixen/issues/1056">#1056</a>), and both read clean.
///     </para>
///     <para>
///         ⚠ <b>The symptom is silence three ways.</b> The type serialises, its alias resolves, and
///         nothing throws — what is missing is
///         <c>SceneComponentRegistry.Declare&lt;T&gt;()</c>, so the Add Component menu does not offer
///         it and a <c>.vxscene</c> naming it fails at load. Found by hand three times running
///         (<a href="https://github.com/Rikarin/Vixen/issues/989">#989</a> →
///         <a href="https://github.com/Rikarin/Vixen/issues/1005">#1005</a> →
///         <a href="https://github.com/Rikarin/Vixen/issues/1056">#1056</a>), which is what a rule is
///         for.
///     </para>
///     <para>
///         ⚠ <b>The pair on one type, never either attribute alone.</b> <c>[Component]</c> by itself
///         is a bridge handle — <c>PhysicsBody</c> and the renderer's equivalent — and is correct
///         with nothing generated for it, which is why
///         <c>ComponentRegistrationGenerator</c> emits for the conjunction and this rule refuses the
///         same conjunction. <c>[DataContract]</c> by itself is the other rule's business.
///     </para>
///     <para>
///         ⚠ <b>And only where the registry is reachable, because the generator itself is a no-op
///         otherwise.</b> <c>ComponentRegistrationGenerator</c> selects on
///         <c>compilation.GetTypeByMetadataName("Vixen.Engine.Scenes.SceneComponentRegistry")</c> and
///         emits nothing when it is absent — <c>Vixen.Net</c> declares components without referencing
///         <c>Vixen.Engine</c> deliberately. A rule that ignored that would demand an analyzer
///         reference that cannot produce a line of code.
///     </para>
///     <para>
///         <b>A pure function so that its answer can be read without running the gate</b>, the shape
///         <see cref="PluginReferenceRule" /> records and <see cref="DataContractGeneratorRule" />
///         follows: <c>CheckArchitecture</c> is one caller and <c>ComponentGeneratorRuleTests</c> is
///         the other. The subject set and the source walk come from those two types rather than from
///         a third glob, which is #872's lesson — a rule's subject set is the half nothing checks,
///         and it is the half that gets transcribed.
///     </para>
/// </remarks>
static class ComponentGeneratorRule {
    /// <summary>The generator that emits <c>SceneComponentRegistry.Declare&lt;T&gt;()</c>.</summary>
    public const string Generator = "Vixen.Engine.Generators";

    /// <summary>The assembly holding the registry, and therefore what makes the generator emit.</summary>
    public const string Registry = "Vixen.Engine";

    /// <summary>The name of the file that lists the projects allowed to be in this state.</summary>
    public const string ExemptionFile = "docs/ComponentGeneratorExempt.txt";

    /// <summary>Where a source file declares a type carrying both attributes.</summary>
    /// <param name="source">The file to read.</param>
    /// <returns>One-based line numbers of the first attribute of each such run.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>An attribute run rather than "one line follows the other".</b> The two are written
    ///         in either order and with other attributes between them, so what is matched is the
    ///         block of attribute lines ending at a declaration — and a comma list
    ///         (<c>[Component, DataContract("x")]</c>) is read as the two attributes it is, because
    ///         nothing makes the compiler prefer one form.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Text over the sources, not a Roslyn symbol walk, and the limits are stated</b> —
    ///         the sentence <see cref="DataContractGeneratorRule.Applications" /> already carries. A
    ///         line inside a doc comment or a <c>//</c> comment is not an application; a raw string
    ///         literal holding C# source can still fool it, which is what the exemption file is for.
    ///     </para>
    /// </remarks>
    public static List<int> Applications(string source) {
        ArgumentNullException.ThrowIfNull(source);

        var lines = File.ReadAllLines(source);
        var found = new List<int>();
        var first = 0;
        var component = false;
        var contract = false;
        var depth = 0;

        for (var index = 0; index < lines.Length; index++) {
            var text = lines[index].TrimStart();

            if (depth == 0) {
                // ⚠ A comment and a blank line leave the run standing rather than ending it. A run is
                // ended by the declaration it belongs to, and nothing else has to end it — whereas a
                // blank line between two attributes is legal C#, so treating one as a boundary would
                // be a hole in the gate that reads exactly like a clean tree.
                if (text.Length == 0
                    || text.StartsWith("///", StringComparison.Ordinal)
                    || text.StartsWith("//", StringComparison.Ordinal)
                    || text.StartsWith('*')
                    || text.StartsWith("/*", StringComparison.Ordinal)) {
                    continue;
                }

                if (!text.StartsWith('[')) {
                    // A declaration, a member, a brace or a directive — whatever it is, the run of
                    // attributes above it belonged to it and is now over.
                    if (component && contract) {
                        found.Add(first);
                    }

                    first = 0;
                    component = false;
                    contract = false;
                    continue;
                }

                if (first == 0) {
                    first = index + 1;
                }
            }

            foreach (var name in Names(text, ref depth)) {
                component |= string.Equals(name, "Component", StringComparison.Ordinal);
                contract |= string.Equals(name, "DataContract", StringComparison.Ordinal);
            }
        }

        if (component && contract) {
            found.Add(first);
        }

        return found;
    }

    /// <summary>The attribute names on one line of an attribute run.</summary>
    /// <param name="text">The line, already trimmed of leading space.</param>
    /// <param name="depth">
    ///     The unclosed bracket depth carried in from the previous line, updated on the way out.
    /// </param>
    /// <returns>The names, without arguments and without a namespace qualifier.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Quoted text is skipped, and that is not paranoia about this tree.</b> A
    ///         <c>[DataContract("a, b")]</c> would otherwise split into two attributes and a
    ///         <c>[Foo("]")]</c> would close a bracket the compiler has not closed — both of which
    ///         turn into a silently wrong answer rather than an error, which is the failure this
    ///         whole rule exists to make loud.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An argument list is tracked separately from the brackets, because a name after a
    ///         comma inside one looks exactly like a second attribute.</b> <c>[Foo(1, Component)]</c>
    ///         is one attribute and an enum member, and reading it as two would make a project a
    ///         subject on the strength of something that is not an attribute at all.
    ///     </para>
    /// </remarks>
    static List<string> Names(string text, ref int depth) {
        var names = new List<string>();
        var quoted = false;
        var arguments = 0;
        var start = true;

        for (var index = 0; index < text.Length; index++) {
            var character = text[index];

            if (quoted) {
                if (character == '\\') {
                    index++;
                } else if (character == '"') {
                    quoted = false;
                }

                continue;
            }

            switch (character) {
                case '"':
                    quoted = true;
                    start = false;
                    continue;
                case '[':
                    depth++;
                    start = true;
                    continue;
                case ']':
                    depth--;
                    start = false;
                    continue;
                case '(':
                    arguments++;
                    start = false;
                    continue;
                case ')':
                    arguments--;
                    start = false;
                    continue;
                case ',' when arguments == 0:
                    start = true;
                    continue;
                case ' ':
                    continue;
            }

            // A name is read only where an attribute may start: just inside the brackets, or just
            // after a comma at that level. Anywhere else on the line is an argument.
            if (!start || depth < 1 || arguments > 0) {
                start = false;
                continue;
            }

            start = false;

            if (!char.IsLetter(character) && character != '_') {
                continue;
            }

            var end = index;

            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '.')) {
                end++;
            }

            var name = text[index..end];
            var dot = name.LastIndexOf('.');
            names.Add(dot < 0 ? name : name[(dot + 1)..]);
            index = end - 1;
        }

        return names;
    }

    /// <summary>Whether a project would compile <c>SceneComponentRegistry</c> into view.</summary>
    /// <param name="edges">Every project's direct references, from <see cref="PluginReferenceRule.Edges" />.</param>
    /// <param name="name">The project's assembly name.</param>
    /// <returns>Whether the generator would emit anything there.</returns>
    /// <remarks>
    ///     ⚠ <b>Transitive, and the assembly itself counts.</b> <c>Vixen.Engine</c> does not reference
    ///     <c>Vixen.Engine</c>, so a walk alone reads the one project that certainly holds the
    ///     registry as unable to see it — the shape that makes a rule quietly skip its own centre.
    ///     A <c>PackageReference</c> is handled by <see cref="NamesTheGenerator" /> instead, because
    ///     the package ships the analyzer and therefore answers both questions at once.
    /// </remarks>
    public static bool SeesTheRegistry(Dictionary<string, HashSet<string>> edges, string name) {
        ArgumentNullException.ThrowIfNull(edges);

        return string.Equals(name, Registry, StringComparison.Ordinal)
            || PluginReferenceRule.Reaches(edges, name, Registry);
    }

    /// <summary>Whether a project names the generator, or the package that carries it.</summary>
    /// <param name="projectFile">The project to read.</param>
    /// <returns>True when the pair in that project would be declared to the registry.</returns>
    /// <remarks>
    ///     ⚠ <b>A <c>PackageReference</c> to <c>Vixen.Engine</c> is enough and a
    ///     <c>ProjectReference</c> to it is not</b>, which is the whole difference. The package puts
    ///     the generator under <c>analyzers/dotnet/cs</c> — <c>Vixen.Engine.csproj</c>'s
    ///     <c>PackEngineGenerators</c> target, added because a project scaffolded from the
    ///     <c>vixen-game</c> template got no generator at all — and an analyzer does not flow through
    ///     a project reference. So this is only reachable from inside this tree, which is where the
    ///     rule runs.
    /// </remarks>
    public static bool NamesTheGenerator(string projectFile) {
        ArgumentNullException.ThrowIfNull(projectFile);

        var document = XDocument.Load(projectFile);

        foreach (var reference in document.Descendants("ProjectReference")) {
            var include = reference.Attribute("Include")?.Value;

            if (include is null) {
                continue;
            }

            if (string.Equals(
                    Path.GetFileNameWithoutExtension(include.Replace('\\', '/')),
                    Generator,
                    StringComparison.Ordinal
                )) {
                return true;
            }
        }

        foreach (var package in document.Descendants("PackageReference")) {
            if (string.Equals(package.Attribute("Include")?.Value, Registry, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every project the rule evaluates: it carries the pair and can see the registry.</summary>
    /// <param name="projects">Paths to the project files to check.</param>
    /// <returns>Project names, ordered.</returns>
    /// <remarks>
    ///     ⚠ <b>The rule's subject set, which is the half of a rule nothing checks.</b> Both halves
    ///     are in it on purpose: a walk that found no pair anywhere and a walk that could no longer
    ///     tell which projects see the registry both report "no violations" and mean "I read
    ///     nothing", and the second is the one a rename of <c>Vixen.Engine</c> would cause.
    /// </remarks>
    public static List<string> Subjects(IEnumerable<string> projects) {
        ArgumentNullException.ThrowIfNull(projects);

        var files = projects.ToList();
        var edges = PluginReferenceRule.Edges(files);
        var subjects = new List<string>();

        foreach (var project in files) {
            var name = Path.GetFileNameWithoutExtension(project);

            if (!SeesTheRegistry(edges, name)) {
                continue;
            }

            if (DataContractGeneratorRule.Sources(project).Exists(source => Applications(source).Count > 0)) {
                subjects.Add(name);
            }
        }

        subjects.Sort(StringComparer.Ordinal);
        return subjects;
    }

    /// <summary>Every subject that does not name the generator.</summary>
    /// <param name="projects">Paths to the project files to check.</param>
    /// <returns>Project names, ordered.</returns>
    public static List<string> Offenders(IEnumerable<string> projects) {
        ArgumentNullException.ThrowIfNull(projects);

        var files = projects.ToList();
        var subjects = Subjects(files);

        return files
            .Where(project => subjects.Contains(Path.GetFileNameWithoutExtension(project), StringComparer.Ordinal))
            .Where(project => !NamesTheGenerator(project))
            .Select(project => Path.GetFileNameWithoutExtension(project))
            .Order(StringComparer.Ordinal)
            .ToList();
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
    ///     <c>CheckDocComments</c>' lists have, and the reason a stale line cannot sit there hiding a
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
                    $"{offender} declares a type carrying [Component] and [DataContract], reaches {Registry}, "
                    + $"and does not name {Generator} as an Analyzer ProjectReference — so nothing calls "
                    + "SceneComponentRegistry.Declare<T>() for it. The type serialises and its alias resolves; "
                    + "what fails is the Add Component menu and a .vxscene that names it. Add the reference, or "
                    + $"add a line to {ExemptionFile} saying why."
                );
            }
        }

        foreach (var name in exempt) {
            if (!offenders.Contains(name, StringComparer.Ordinal)) {
                violations.Add(
                    $"{name} is listed in {ExemptionFile} and no longer needs to be — it either names "
                    + $"{Generator} now, no longer reaches {Registry}, or declares no [Component] [DataContract] "
                    + "type. Remove the line; the list can only shrink."
                );
            }
        }

        return violations;
    }
}
