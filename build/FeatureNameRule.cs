// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;

namespace Vixen.Build;

/// <summary>
///     Doc 50 § D6's <c>FeatureNameRule</c>: a shell assembly naming a type, a namespace, an extension
///     literal, a command-id prefix or a panel id that a feature assembly owns fails
///     <c>CheckArchitecture</c>, outside a shrink-only exemption list that <i>is</i> the audit.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a grep and not the compiler.</b> <c>ApplicationReferenceRule</c> reads project
///         references, and a project reference is the coarsest thing a shell can know about a feature
///         by: the extension literal <c>".vxshadergraph"</c> in the import pipeline, the panel id
///         <c>"profiler"</c> in a layout preset and the command id <c>"terrain.sculpt"</c> in a menu
///         need no reference at all, and doc 36 § P3 recorded that a fully-qualified name in a
///         <c>case</c> pattern produced <b>no diagnostic</b> when the reference was removed. So this
///         reads source — <c>.cs</c> <b>and</b> <c>.vxml</c>, because a view's <c>&lt;code&gt;</c> block
///         is production C# — with comments stripped, since a remark explaining why the shell no
///         longer names Terrain is not the shell naming Terrain.
///     </para>
///     <para>
///         <b>The vocabulary is derived, not listed</b>, so a new feature assembly or a new type in one
///         joins the rule without an edit here:
///     </para>
///     <list type="bullet">
///         <item>a <b>namespace</b> <c>Vixen.Editor.&lt;Feature&gt;</c>, for every feature assembly;</item>
///         <item>a <b>type</b> declared in a feature assembly and declared nowhere outside one — a name a
///         runtime or shell assembly also declares is not the feature's to own;</item>
///         <item>an <b>extension literal</b> <c>".vx…"</c> spelled in a feature and in no runtime source
///         (<c>Core/</c>, <c>Platform/</c>, <c>Tools/</c>, …), so <c>.vxanim</c>, which the runtime
///         owns, is not a feature's;</item>
///         <item>a <b>panel id</b> a feature registers — a literal handed to <c>AddPanel</c> or
///         <c>RegisterPanel</c>, or the value of a <c>const string …Panel… = "…"</c>;</item>
///         <item>a <b>command-id prefix</b> — the feature's own name lowercased (<c>terrain.</c>,
///         <c>blockout.</c>) and the first segment of any dotted panel id it registers
///         (<c>foliage.</c>).</item>
///     </list>
///     <para>
///         ⚠ <b>Types and namespaces are matched in code, ids and extensions in string literals</b>, and
///         neither in comments. A display string reading "Water" is not a type, and a type named
///         <c>Water</c> in code is not a string.
///     </para>
///     <para>
///         ⚠ <b>One named false positive, exempted on purpose and not by the list.</b>
///         <c>Editor/Vixen.Editor.Host/EditorModules.cs</c> is the list of the editor's own features —
///         naming each one is the whole of its job and doc 36 § P3 says it is the only thing the editor
///         needs to know about one. It is in <see cref="ByDesign" />, which a list entry would not be:
///         an entry there could never become clean, so a shrink-only list holding it would be a list
///         with a row that asserts nothing. Every other hit is in the exemption file.
///     </para>
///     <para>
///         ⚠ <b>Its count is not doc 50 § F9's 110, and the difference is a definition, not a
///         miss.</b> F9's audit also counted runtime names a shell holds — <c>TerrainComponent</c>,
///         <c>WaterZoneComponent</c>, <c>BuoyancyBody</c> live in <c>Core/</c>, and a shell naming an
///         engine component is not a shell naming an editor feature — and members that spell a
///         feature's word (<c>ModeArt.Blockout</c>, <c>MaterialIcons.Terrain</c>), which no derivation
///         can tell from the runtime's <c>Vixen.Rendering.Terrain</c>. F9's 35 <c>EditorStrings</c>
///         ids were moved by #1301 before this rule existed. Measured 2026-10-19: 109 rows, 159
///         occurrences — App 63, Assets 41, Host 4, Ui 1, and Core, Inspector and SceneView none.
///     </para>
///     <para>
///         <b>What it cannot see.</b> A feature's name spelled through a constant the shell declares
///         itself (<c>"vx" + "layer"</c>), a type reached by reflection over a string the rule does not
///         recognise as an id, and anything a <c>using</c> alias renames. It reads names, and a shell
///         that wants to hide one can; review is for that.
///     </para>
/// </remarks>
static class FeatureNameRule {
    /// <summary>The exemption list, relative to the repository root.</summary>
    public const string ExemptionFile = "docs/FeatureNameExempt.txt";

    /// <summary>The command that rewrites the list from the tree.</summary>
    public const string Regenerate = "./build.sh CheckArchitecture --update-exemptions";

    /// <summary>The shell: the tiers doc 50 § D1 puts above the domain, and the domain kernel.</summary>
    public static readonly string[] Shell = [
        "Vixen.Editor.App",
        "Vixen.Editor.Ui",
        "Vixen.Editor.Host",
        "Vixen.Editor.Core",
        "Vixen.Editor.SceneView",
        "Vixen.Editor.Inspector",
        "Vixen.Editor.Assets"
    ];

    /// <summary><c>Editor/</c> projects that are neither shell nor feature.</summary>
    /// <remarks>
    ///     The plugin contract, which every feature references and which names none; and the test
    ///     harness, which opens the whole editor on purpose.
    /// </remarks>
    public static readonly string[] Neither = ["Vixen.Editor.Plugin", "Vixen.Editor.Testing"];

    /// <summary>Shell files whose job is naming features. See the class remarks.</summary>
    public static readonly string[] ByDesign = ["Editor/Vixen.Editor.Host/EditorModules.cs"];

    /// <summary>Top-level directories whose declarations make a type name not a feature's to own.</summary>
    static readonly string[] Outside = ["Benchmarks", "Core", "Gameplay", "Live", "Platform", "Raven", "Samples", "Testing", "Tools"];

    /// <summary>Directories under which an extension literal belongs to the runtime rather than to a feature.</summary>
    static readonly string[] Runtime = ["Core", "Gameplay", "Live", "Platform", "Raven", "Samples", "Tools"];

    static readonly Regex TypeDeclaration = new(@"\b(?:class|struct|interface|enum|record)\s+([A-Z][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);
    static readonly Regex Identifier = new(@"(?<![A-Za-z0-9_.])[A-Z][A-Za-z0-9_]*", RegexOptions.CultureInvariant);
    static readonly Regex MemberDeclaration = new(@"([A-Za-z_][A-Za-z0-9_]*|[>\]?])\s+([A-Z][A-Za-z0-9_]*)\s*(?:\(|\{\s*(?:get|set|init)\b|=>)", RegexOptions.CultureInvariant);

    /// <summary>Words that put a type name before <c>(</c> without declaring a member called it.</summary>
    static readonly HashSet<string> NotADeclarator = new(StringComparer.Ordinal) {
        "new", "return", "throw", "await", "is", "as", "case", "in", "out", "ref", "else", "yield", "using",
        "typeof", "nameof", "when", "not", "and", "or", "class", "struct", "record", "interface", "enum"
    };

    static readonly Regex Extension =new(@"^\.vx[a-z0-9]+$", RegexOptions.CultureInvariant);
    static readonly Regex PanelRegistration = new(@"\b(?:AddPanel|RegisterPanel)\(\s*""([^""]+)""", RegexOptions.CultureInvariant);
    static readonly Regex PanelConstant = new(@"const\s+string\s+\w*Panel\w*\s*=\s*""([^""]+)""", RegexOptions.CultureInvariant);
    static readonly Regex DottedId = new(@"^[a-z][a-z0-9-]*(\.[a-z0-9-]+)+\.?$", RegexOptions.CultureInvariant);

    /// <summary>One shell file naming one feature name some number of times.</summary>
    /// <param name="File">The file, relative to the root, forward slashes.</param>
    /// <param name="Name">The name.</param>
    /// <param name="Count">How many times.</param>
    public readonly record struct Finding(string File, string Name, int Count) {
        /// <summary>The line the exemption file holds for it.</summary>
        public string Key => $"{File} {Name}";
    }

    /// <summary>Everything a feature owns, by kind.</summary>
    /// <param name="Features">The feature assemblies.</param>
    /// <param name="Namespaces">Their namespaces.</param>
    /// <param name="Types">Type names declared only in features.</param>
    /// <param name="Extensions">Extension literals only features spell.</param>
    /// <param name="PanelIds">Panel ids features register.</param>
    /// <param name="Prefixes">Command-id prefixes, each without its dot.</param>
    public sealed record Vocabulary(
        IReadOnlyList<string> Features,
        IReadOnlyList<string> Namespaces,
        IReadOnlySet<string> Types,
        IReadOnlySet<string> Extensions,
        IReadOnlySet<string> PanelIds,
        IReadOnlySet<string> Prefixes
    );

    /// <summary>A source file split into what is code and what is a string literal, comments dropped.</summary>
    /// <param name="Code">The code, with every string and comment replaced by a space.</param>
    /// <param name="Strings">Each string literal's contents.</param>
    public readonly record struct Lexed(string Code, IReadOnlyList<string> Strings);

    /// <summary>The feature assemblies: every <c>Editor/</c> project that is not shell, contract, harness, test or generator.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Their names, ordered.</returns>
    public static List<string> Features(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var editor = Path.Combine(root, "Editor");

        if (!Directory.Exists(editor)) {
            return [];
        }

        return Directory.EnumerateDirectories(editor)
            .Where(directory => Directory.EnumerateFiles(directory, "*.csproj").Any())
            .Select(Path.GetFileName)
            .Select(name => name!)
            .Where(name => name.StartsWith("Vixen.Editor.", StringComparison.Ordinal))
            .Where(name => !name.EndsWith(".Tests", StringComparison.Ordinal) && !name.EndsWith(".Generator", StringComparison.Ordinal))
            .Where(name => !Shell.Contains(name, StringComparer.Ordinal) && !Neither.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Production source under a directory: <c>.cs</c> and <c>.vxml</c>, no tests, no build output.</summary>
    /// <param name="directory">Where to look.</param>
    /// <param name="markup">Whether <c>.vxml</c> counts.</param>
    /// <returns>Absolute paths.</returns>
    public static IEnumerable<string> Sources(string directory, bool markup = true) {
        if (!Directory.Exists(directory)) {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)) {
            if (!file.EndsWith(".cs", StringComparison.Ordinal) && !(markup && file.EndsWith(".vxml", StringComparison.Ordinal))) {
                continue;
            }

            // ⚠ Relative to where the walk started: an agent's checkout lives under `.claude/`, and
            // judging the absolute path would exclude the whole tree it is checking.
            var parts = Path.GetRelativePath(directory, file).Replace('\\', '/').Split('/');

            if (parts.Any(part =>
                    part is "obj" or "bin" or "node_modules" or ".claude"
                    || part.EndsWith(".Tests", StringComparison.Ordinal))) {
                continue;
            }

            yield return file;
        }
    }

    /// <summary>Builds the vocabulary from the tree.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Everything the features own.</returns>
    public static Vocabulary Measure(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var features = Features(root);
        var types = new HashSet<string>(StringComparer.Ordinal);
        var extensions = new HashSet<string>(StringComparer.Ordinal);
        var panelIds = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var feature in features) {
            prefixes.Add(feature["Vixen.Editor.".Length..].Split('.')[0].ToLowerInvariant());

            foreach (var file in Sources(Path.Combine(root, "Editor", feature))) {
                var text = File.ReadAllText(file);
                var lexed = Lex(text);

                // ⚠ Top-level types only. A feature's nested `Note`, `Corner` or `Layout` is a word
                // every assembly uses for something of its own, and counting them made the first run
                // of this rule report `Texture` seven times in StandardIcons — an enum member's name,
                // not the shader graph's. A view's markup declares one top-level type, its file name;
                // what its `<code>` block declares is nested in it.
                if (file.EndsWith(".vxml", StringComparison.Ordinal)) {
                    types.Add(Path.GetFileNameWithoutExtension(file));
                } else {
                    types.UnionWith(TopLevelTypes(lexed.Code));
                }

                foreach (var literal in lexed.Strings) {
                    if (Extension.IsMatch(literal)) {
                        extensions.Add(literal);
                    }
                }

                // The registrations read the raw text, because the literal is the argument and the
                // lexer has replaced it with a space in the code stream.
                foreach (Match match in PanelRegistration.Matches(text)) {
                    panelIds.Add(match.Groups[1].Value);
                }

                foreach (Match match in PanelConstant.Matches(text)) {
                    panelIds.Add(match.Groups[1].Value);
                }
            }
        }

        foreach (var id in panelIds.Where(id => id.Contains('.', StringComparison.Ordinal))) {
            prefixes.Add(id[..id.IndexOf('.', StringComparison.Ordinal)]);
        }

        // ⚠ What is declared or spelled outside the features is not theirs to own. A type name the
        // runtime also declares (a feature's nested `Settings`) would otherwise flag every shell
        // file that names the runtime's, and `.vxanim` — which `Core/Vixen.Animation` owns — would
        // flag the import pipeline for importing what the engine ships.
        var outside = Outside.SelectMany(top => Sources(Path.Combine(root, top), markup: false))
            .Concat(Shell.Concat(Neither).SelectMany(project => Sources(Path.Combine(root, "Editor", project), markup: false)));

        foreach (var file in outside) {
            var text = File.ReadAllText(file);

            foreach (Match match in TypeDeclaration.Matches(text)) {
                types.Remove(match.Groups[1].Value);
            }

            // ⚠ And a member of the same name. `EditorShell.LayoutCommand(string)` is a method and
            // `NodeGraph.LayoutCommand` a static class; `StandardIcons.Texture` is a property and
            // `NodeGraph.Texture` a port-value record. In a shell file the word is the shell's own.
            foreach (Match match in MemberDeclaration.Matches(text)) {
                if (!NotADeclarator.Contains(match.Groups[1].Value)) {
                    types.Remove(match.Groups[2].Value);
                }
            }
        }

        foreach (var file in Runtime.SelectMany(top => Sources(Path.Combine(root, top), markup: false))) {
            var text = File.ReadAllText(file);

            if (!text.Contains("\".vx", StringComparison.Ordinal)) {
                continue;
            }

            foreach (var literal in Lex(text).Strings) {
                extensions.Remove(literal);
            }
        }

        var namespaces = features.Select(feature => "Vixen.Editor." + feature["Vixen.Editor.".Length..].Split('.')[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new(features, namespaces, types, extensions, panelIds, prefixes);
    }

    /// <summary>The types a file declares at namespace level.</summary>
    /// <param name="code">The file's code stream, from <see cref="Lex" />.</param>
    /// <returns>Their names.</returns>
    /// <remarks>
    ///     Depth is counted over braces in the code stream, which <see cref="Lex" /> has already
    ///     emptied of strings and comments, so a brace in a literal cannot move it. Namespace level is
    ///     depth 0 under a file-scoped namespace or none, and 1 inside a block one.
    /// </remarks>
    public static List<string> TopLevelTypes(string code) {
        ArgumentNullException.ThrowIfNull(code);

        var block = Regex.IsMatch(code, @"\bnamespace\s+[\w.]+\s*\{");
        var level = block ? 1 : 0;
        var names = new List<string>();
        var depth = 0;
        var position = 0;

        foreach (Match match in TypeDeclaration.Matches(code)) {
            for (; position < match.Index; position++) {
                depth += code[position] switch {
                    '{' => 1,
                    '}' => -1,
                    _ => 0
                };
            }

            if (depth == level) {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>Every feature name every shell file holds.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="vocabulary">What the features own.</param>
    /// <returns>One finding per file and name, ordered.</returns>
    public static List<Finding> Scan(string root, Vocabulary vocabulary) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(vocabulary);

        var findings = new List<Finding>();

        foreach (var project in Shell) {
            foreach (var file in Sources(Path.Combine(root, "Editor", project))) {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

                if (ByDesign.Contains(relative, StringComparer.Ordinal)) {
                    continue;
                }

                var text = File.ReadAllText(file);

                if (file.EndsWith(".vxml", StringComparison.Ordinal)) {
                    text = Regex.Replace(text, "<!--.*?-->", " ", RegexOptions.Singleline);
                }

                foreach (var (name, count) in Names(Lex(text), vocabulary)) {
                    findings.Add(new(relative, name, count));
                }
            }
        }

        return [.. findings.OrderBy(finding => finding.Key, StringComparer.Ordinal)];
    }

    /// <summary>The feature names in one lexed file, with their counts.</summary>
    /// <param name="lexed">The file.</param>
    /// <param name="vocabulary">What the features own.</param>
    /// <returns>Name → count, ordered.</returns>
    public static SortedDictionary<string, int> Names(Lexed lexed, Vocabulary vocabulary) {
        ArgumentNullException.ThrowIfNull(vocabulary);

        var names = new SortedDictionary<string, int>(StringComparer.Ordinal);

        void Count(string name) => names[name] = names.GetValueOrDefault(name) + 1;

        foreach (var space in vocabulary.Namespaces) {
            foreach (Match _ in Regex.Matches(lexed.Code, @"(?<![A-Za-z0-9_.])" + Regex.Escape(space) + @"(?![A-Za-z0-9_])")) {
                Count(space);
            }
        }

        foreach (Match match in Identifier.Matches(lexed.Code)) {
            if (vocabulary.Types.Contains(match.Value) && !IsCallOrMember(lexed.Code, match)) {
                Count(match.Value);
            }
        }

        foreach (var literal in lexed.Strings) {
            if (vocabulary.Extensions.Contains(literal) || vocabulary.PanelIds.Contains(literal)) {
                Count(literal);
            } else if (DottedId.IsMatch(literal) && vocabulary.Prefixes.Contains(literal[..literal.IndexOf('.', StringComparison.Ordinal)])) {
                Count(literal);
            }
        }

        return names;
    }

    /// <summary>Whether an identifier is a method being called or declared rather than a type.</summary>
    /// <param name="code">The code stream.</param>
    /// <param name="match">The identifier.</param>
    /// <returns>True for <c>Inspecting(…)</c>, false for <c>new Inspecting(…)</c>.</returns>
    /// <remarks>
    ///     The application has a method called <c>Inspecting</c>, and a feature view of the same name
    ///     would otherwise make every call to it a feature name. A type is followed by <c>(</c> only
    ///     after <c>new</c>.
    /// </remarks>
    static bool IsCallOrMember(string code, Match match) {
        var after = match.Index + match.Length;

        while (after < code.Length && code[after] is ' ' or '\t') {
            after++;
        }

        if (after >= code.Length || code[after] != '(') {
            return false;
        }

        var before = match.Index;

        while (before > 0 && char.IsWhiteSpace(code[before - 1])) {
            before--;
        }

        return !(before >= 3 && string.CompareOrdinal(code, before - 3, "new", 0, 3) == 0);
    }

    /// <summary>Splits C# (or a <c>.vxml</c> with its XML comments already gone) into code and strings.</summary>
    /// <param name="text">The source.</param>
    /// <returns>The code with comments and literals blanked, and the literals.</returns>
    /// <remarks>
    ///     Line and block comments, regular, verbatim, interpolated and raw strings, and character
    ///     literals — the last only when it closes within eight characters, so an apostrophe in a
    ///     <c>.vxml</c> label ("Don't") is text rather than the start of a literal that swallows the
    ///     file. Interpolation holes stay inside the literal; an id built from one is not an id the
    ///     rule can read anyway.
    /// </remarks>
    public static Lexed Lex(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var code = new StringBuilder(text.Length);
        var strings = new List<string>();
        var index = 0;

        while (index < text.Length) {
            var c = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (c == '/' && next == '/') {
                while (index < text.Length && text[index] != '\n') {
                    index++;
                }

                continue;
            }

            if (c == '/' && next == '*') {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? text.Length : end + 2;
                code.Append(' ');
                continue;
            }

            if (c is '$' or '@' or '"') {
                var start = index;

                while (index < text.Length && text[index] is '$' or '@') {
                    index++;
                }

                if (index >= text.Length || text[index] != '"') {
                    code.Append(text, start, index - start);
                    continue;
                }

                var verbatim = text.AsSpan(start, index - start).Contains('@');
                var quotes = 0;

                while (index + quotes < text.Length && text[index + quotes] == '"') {
                    quotes++;
                }

                if (quotes >= 3) {
                    var fence = new string('"', quotes);
                    var end = text.IndexOf(fence, index + quotes, StringComparison.Ordinal);
                    var stop = end < 0 ? text.Length : end;

                    strings.Add(text[(index + quotes)..stop]);
                    index = end < 0 ? text.Length : end + quotes;
                } else if (quotes == 2 && !verbatim) {
                    strings.Add(string.Empty);
                    index += 2;
                } else {
                    var literal = new StringBuilder();
                    index++;

                    while (index < text.Length) {
                        var d = text[index];

                        if (verbatim && d == '"' && index + 1 < text.Length && text[index + 1] == '"') {
                            literal.Append('"');
                            index += 2;
                            continue;
                        }

                        if (!verbatim && d == '\\' && index + 1 < text.Length) {
                            literal.Append(d).Append(text[index + 1]);
                            index += 2;
                            continue;
                        }

                        if (d == '"' || (!verbatim && d == '\n')) {
                            index++;
                            break;
                        }

                        literal.Append(d);
                        index++;
                    }

                    strings.Add(literal.ToString());
                }

                code.Append(' ');
                continue;
            }

            if (c == '\'') {
                var close = -1;

                for (var probe = index + 1; probe < Math.Min(text.Length, index + 10); probe++) {
                    if (text[probe] == '\n') {
                        break;
                    }

                    if (text[probe] == '\'' && text[probe - 1] != '\\') {
                        close = probe;
                        break;
                    }
                }

                if (close > index + 1) {
                    index = close + 1;
                    code.Append(' ');
                    continue;
                }
            }

            code.Append(c);
            index++;
        }

        return new(code.ToString(), strings);
    }

    /// <summary>Reads the exemption list.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>Key (<c>file name</c>) → (count, reason).</returns>
    public static Dictionary<string, (int Count, string Reason)> Parse(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var rows = new Dictionary<string, (int, string)>(StringComparer.Ordinal);

        foreach (var raw in text.Split('\n')) {
            var line = raw.TrimEnd('\r');

            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) {
                continue;
            }

            var fields = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length < 3 || !int.TryParse(fields[2], out var count)) {
                throw new FormatException($"'{line}' in {ExemptionFile} is not '<file> <name> <count> [reason]'.");
            }

            rows[$"{fields[0]} {fields[1]}"] = (count, fields.Length > 3 ? fields[3] : string.Empty);
        }

        return rows;
    }

    /// <summary>Reads the committed list, or an empty one.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Key → (count, reason).</returns>
    public static Dictionary<string, (int Count, string Reason)> Exempt(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var path = Path.Combine(root, ExemptionFile.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(path) ? Parse(File.ReadAllText(path)) : new(StringComparer.Ordinal);
    }

    /// <summary>Why the rule is checking nothing, if it is.</summary>
    /// <param name="vocabulary">What the features own.</param>
    /// <returns>The reason, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ Each kind is checked on its own, because a vocabulary with types and no extensions is a
    ///     rule that has silently stopped reading one of its four questions — the state a lexer that
    ///     stopped returning literals would leave it in, with the type half still green.
    /// </remarks>
    public static string? Vacuity(Vocabulary vocabulary) {
        ArgumentNullException.ThrowIfNull(vocabulary);

        if (vocabulary.Features.Count == 0) {
            return "No feature assembly was found under Editor/, so the feature-name rule is checking nothing.";
        }

        if (vocabulary.Types.Count == 0 || vocabulary.Extensions.Count == 0 || vocabulary.PanelIds.Count == 0 || vocabulary.Prefixes.Count == 0) {
            return $"The feature vocabulary is missing a kind (types {vocabulary.Types.Count}, extensions "
                + $"{vocabulary.Extensions.Count}, panel ids {vocabulary.PanelIds.Count}, prefixes "
                + $"{vocabulary.Prefixes.Count}) — the rule has stopped reading one of its questions.";
        }

        return null;
    }

    /// <summary>Everything wrong with the shell against the list.</summary>
    /// <param name="findings">What <see cref="Scan" /> found.</param>
    /// <param name="exempt">The list.</param>
    /// <returns>One sentence per violation.</returns>
    public static List<string> Violations(IReadOnlyList<Finding> findings, IReadOnlyDictionary<string, (int Count, string Reason)> exempt) {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(exempt);

        var violations = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var finding in findings) {
            seen.Add(finding.Key);

            if (!exempt.TryGetValue(finding.Key, out var row)) {
                violations.Add(
                    $"{finding.File} names {finding.Name} ({finding.Count}×), which a feature assembly owns. Doc 50 § D1: "
                    + "the shell knows a feature by the module list and nothing else — reach it through PluginContext, a "
                    + "contribution or a service. If this is genuinely the shell's, argue it in review and run "
                    + $"`{Regenerate}`."
                );
            } else if (finding.Count > row.Count) {
                violations.Add(
                    $"{finding.File} names {finding.Name} {finding.Count}× and {ExemptionFile} allows {row.Count}. The list "
                    + "may only shrink — a shell that names a feature more often than it did is the coupling growing."
                );
            } else if (finding.Count < row.Count) {
                violations.Add(
                    $"{finding.File} names {finding.Name} {finding.Count}× and {ExemptionFile} still says {row.Count}. Lower "
                    + $"the count (or run `{Regenerate}`) so the room cannot be grown back into."
                );
            }
        }

        foreach (var key in exempt.Keys.Where(key => !seen.Contains(key)).Order(StringComparer.Ordinal)) {
            violations.Add(
                $"{ExemptionFile} lists '{key}', which the shell no longer names. Delete the line — the list is what is "
                + "still owed and may only shrink."
            );
        }

        return violations;
    }

    /// <summary>The list, written from the findings, keeping each surviving row's reason.</summary>
    /// <param name="findings">What <see cref="Scan" /> found.</param>
    /// <param name="previous">The list as it was.</param>
    /// <returns>The file's text.</returns>
    public static string Render(IReadOnlyList<Finding> findings, IReadOnlyDictionary<string, (int Count, string Reason)> previous) {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(previous);

        var text = new StringBuilder();

        text.Append("# Feature names the editor's shell still holds (doc 50 § D6, #1278). Read by CheckArchitecture through\n")
            .Append("# build/FeatureNameRule.cs. One row per shell file and name: `<file> <name> <count> [reason]`.\n")
            .Append("#\n")
            .Append("# ⚠ This list can only SHRINK. A shell file naming a feature name with no row fails, a count above its\n")
            .Append("# row fails, and a row whose count is above the file's — or whose file no longer names it — fails too,\n")
            .Append("# so every doc 50 phase deletes lines from here in the commit that moves the name.\n")
            .Append("#\n")
            .Append("# Shell: Vixen.Editor.{App,Ui,Host,Core,SceneView,Inspector,Assets}. A feature is every other Editor/\n")
            .Append("# project but Plugin, Testing, tests and generators. The vocabulary — namespaces, types declared only in\n")
            .Append("# features, .vx extension literals no runtime source spells, panel ids features register, and command-id\n")
            .Append("# prefixes — is derived by the rule; see its remarks. Comments are not read.\n")
            .Append("#\n")
            .Append("# Regenerate after a merge with `").Append(Regenerate).Append("` — and read the\n")
            .Append("# diff: a row that appeared or grew is the shell learning a feature's name.\n\n");

        foreach (var finding in findings) {
            text.Append(finding.File).Append(' ').Append(finding.Name).Append(' ').Append(finding.Count);

            if (previous.TryGetValue(finding.Key, out var row) && row.Reason.Length > 0) {
                text.Append(' ').Append(row.Reason);
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}
