// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Vixen.Editor.Core;

/// <summary>One file a template writes.</summary>
/// <param name="Path">Where it goes, relative to the project directory, with <c>/</c> separators.</param>
/// <param name="Content">What goes in it.</param>
public sealed record TemplateFile(string Path, byte[] Content);

/// <summary>The <c>dotnet new</c> templates, read out of this assembly.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Here rather than in <c>Tools/Vixen.Cli</c>, which is where it was, and for
///         <c>ProjectWorkspace</c>'s reason.</b> The editor's New Project made two directories and
///         called it a project — so every project born in the editor had no <c>.csproj</c>, and Build
///         and Run was greyed for all of them with a message naming a terminal command. An editor
///         that cannot finish the project it just made is failing doc 20's second bar on the first
///         screen a new user sees. The scaffold had to be reachable from both heads, and a second
///         copy of it is the thing this type was created to prevent one level down.
///     </para>
///     <para>
///         <b>There is one tree of template files and two things that instantiate it.</b>
///         <c>Tools/Vixen.Templates</c> owns the files; <c>dotnet new</c> reads them out of the
///         template package, and this reads the same files out of this assembly, which is where the
///         build embedded them. That is the whole reason this type exists: the previous arrangement
///         held the scaffold as C# string literals beside a template pack that did not exist yet,
///         and the moment the pack existed there would have been two copies of every file with
///         nothing keeping them equal.
///     </para>
///     <para>
///         <b>Why the files are the source and the C# is generated from them, rather than the other
///         way round.</b> <c>ScaffoldRunner</c>'s remarks used to say the pack should be generated
///         from its strings. It is the wrong direction: <c>dotnet new</c> consumes real files with
///         real names and a <c>.template.config/template.json</c> beside them that no C# string
///         could produce, so generating the pack means generating something no human reviews.
///         Reading them the other way is a fifty-line reader and both sides consume exactly what
///         ships.
///     </para>
///     <para>
///         <b>What this deliberately does not implement.</b> The template engine has conditionals,
///         computed symbols, renames and post actions. This reads <c>sourceName</c> substitution and
///         nothing else, and the templates are written to need nothing else — a second, partial
///         implementation of a templating language is a thing that silently disagrees with the real
///         one.
///     </para>
/// </remarks>
public static class TemplateCatalog {
    /// <summary>The prefix the build gives every embedded template file.</summary>
    /// <remarks>
    ///     Set as an explicit <c>LogicalName</c> in <c>Vixen.Editor.Core.csproj</c> rather than left to the
    ///     default naming, which would fold the directory separators into dots and make
    ///     <c>Shaders/ui.vert.spv</c> and <c>Shaders.ui.vert.spv</c> the same name.
    /// </remarks>
    const string Prefix = "Vixen.Templates/";

    /// <summary>The token every template writes where a package version belongs.</summary>
    /// <remarks>
    ///     Replaced here with the version below, and replaced at pack time by
    ///     <c>Vixen.Templates.csproj</c> with the version of the package being built. An SDK version
    ///     cannot be an MSBuild property — <c>&lt;Project Sdk="Vixen.Sdk/x.y.z"&gt;</c> has to be
    ///     literal — so there is nowhere for a template to defer the question to.
    /// </remarks>
    public const string VersionToken = "VIXEN_PACKAGE_VERSION";

    /// <summary>Every template this tool can write.</summary>
    public static IReadOnlyList<ProjectTemplate> All { get; } = Read();

    /// <summary>Finds a template by any of its short names, case-insensitively.</summary>
    /// <param name="shortName">What the user typed: <c>game</c>, <c>app</c>, <c>lib</c>.</param>
    /// <param name="template">The template, if there is one.</param>
    /// <returns>Whether there was.</returns>
    public static bool TryFind(string shortName, out ProjectTemplate template) {
        foreach (var candidate in All) {
            if (candidate.ShortNames.Contains(shortName, StringComparer.OrdinalIgnoreCase)) {
                template = candidate;
                return true;
            }
        }

        template = null!;

        return false;
    }

    static ProjectTemplate[] Read() {
        var assembly = typeof(TemplateCatalog).Assembly;
        var byTemplate = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);

        foreach (var resource in assembly.GetManifestResourceNames()) {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal)) {
                continue;
            }

            var relative = resource[Prefix.Length..];
            var separator = relative.IndexOf('/', StringComparison.Ordinal);

            if (separator <= 0) {
                continue;
            }

            var id = relative[..separator];

            if (!byTemplate.TryGetValue(id, out var files)) {
                byTemplate[id] = files = new(StringComparer.Ordinal);
            }

            files[relative[(separator + 1)..]] = Bytes(assembly, resource);
        }

        return byTemplate
            .Select(entry => Describe(entry.Key, entry.Value))
            .OrderBy(template => template.Id, StringComparer.Ordinal)
            .ToArray();
    }

    static byte[] Bytes(Assembly assembly, string resource) {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var memory = new MemoryStream();

        stream.CopyTo(memory);

        return memory.ToArray();
    }

    /// <summary>Reads a template's own metadata rather than a second copy of it.</summary>
    /// <remarks>
    ///     ⚠ <b><c>template.json</c> is the manifest, so this parses it.</b> The alternative — a
    ///     table of short names and source names in this file — is exactly the drift this type
    ///     exists to prevent, one level up: <c>dotnet new vixen-lib</c> and <c>vixen new lib</c>
    ///     would be answering to different names for the same directory.
    /// </remarks>
    static ProjectTemplate Describe(string id, Dictionary<string, byte[]> files) {
        const string manifest = ".template.config/template.json";

        if (!files.Remove(manifest, out var json)) {
            throw new InvalidOperationException($"Template '{id}' has no {manifest}.");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new(
            id,
            Text(root, "name") ?? id,
            Text(root, "description") ?? string.Empty,
            Text(root, "sourceName") ?? throw new InvalidOperationException($"Template '{id}' has no sourceName."),
            ShortNames(root),
            files
        );
    }

    static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    ///     The names this tool answers to, which are the template's own with the <c>vixen-</c>
    ///     prefix taken off.
    /// </summary>
    /// <remarks>
    ///     `dotnet new vixen-game` needs the prefix because it shares a namespace with every other
    ///     template on the machine; `vixen new game` does not, and asking somebody to type the
    ///     tool's own name back at it would be silly. One list, two spellings of it.
    /// </remarks>
    static string[] ShortNames(JsonElement root) {
        if (!root.TryGetProperty("shortName", out var value)) {
            return [];
        }

        var names = value.ValueKind is JsonValueKind.Array
            ? value.EnumerateArray().Select(entry => entry.GetString()).OfType<string>()
            : [value.GetString() ?? string.Empty];

        return names
            .Select(name => name.StartsWith("vixen-", StringComparison.Ordinal) ? name["vixen-".Length..] : name)
            .Where(name => name.Length > 0)
            .ToArray();
    }

    /// <summary>Whether a file is one to substitute into, or one to copy across untouched.</summary>
    /// <param name="content">The file's bytes.</param>
    /// <returns>Whether it is text.</returns>
    /// <remarks>
    ///     A NUL byte, which is how <c>git</c> answers the same question and is right for the same
    ///     reason: the alternative is a list of extensions, and the file that is not on it is a
    ///     shader with a project name silently rewritten into the middle of its bytecode.
    /// </remarks>
    public static bool IsTextFile(ReadOnlySpan<byte> content) => !content.Contains((byte) 0);

    /// <summary>Applies the two substitutions to a file's bytes.</summary>
    internal static byte[] Substitute(byte[] content, string sourceName, string projectName, string version) {
        if (!IsTextFile(content)) {
            return content;
        }

        var text = Encoding.UTF8.GetString(content)
            .Replace(sourceName, projectName, StringComparison.Ordinal)
            .Replace(VersionToken, version, StringComparison.Ordinal);

        return Encoding.UTF8.GetBytes(text);
    }
}

/// <summary>A template, and what it writes.</summary>
public sealed class ProjectTemplate {
    readonly Dictionary<string, byte[]> files;

    internal ProjectTemplate(
        string id,
        string name,
        string description,
        string sourceName,
        IReadOnlyList<string> shortNames,
        Dictionary<string, byte[]> files
    ) {
        Id = id;
        Name = name;
        Description = description;
        SourceName = sourceName;
        ShortNames = shortNames;

        this.files = files;
    }

    /// <summary>Its directory in the template pack — <c>vixen-game</c>.</summary>
    public string Id { get; }

    /// <summary>What `dotnet new list` calls it — "Vixen Game".</summary>
    public string Name { get; }

    /// <summary>One line about what it writes.</summary>
    public string Description { get; }

    /// <summary>The placeholder name every file in it is written against.</summary>
    public string SourceName { get; }

    /// <summary>What `vixen new` answers to: <c>game</c>, or <c>lib</c> and <c>library</c>.</summary>
    public IReadOnlyList<string> ShortNames { get; }

    /// <summary>The name this tool prints and the one it offers first.</summary>
    public string ShortName => ShortNames.Count > 0 ? ShortNames[0] : Id;

    /// <summary>Writes the template out for a project of the given name.</summary>
    /// <param name="projectName">What the project is called.</param>
    /// <param name="version">What to put where the templates ask for a package version.</param>
    /// <returns>Every file, in a stable order, with its path relative to the project directory.</returns>
    public IReadOnlyList<TemplateFile> Instantiate(string projectName, string version) {
        ArgumentException.ThrowIfNullOrEmpty(projectName);
        ArgumentNullException.ThrowIfNull(version);

        return files
            .Select(file => new TemplateFile(
                    file.Key.Replace(SourceName, projectName, StringComparison.Ordinal),
                    TemplateCatalog.Substitute(file.Value, SourceName, projectName, version)
                )
            )
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>What writing a template into a directory did, or why it did nothing.</summary>
/// <param name="Written">The files written, project-relative, in the order they were written.</param>
/// <param name="Collisions">
///     What was already there, project-relative. Non-empty means nothing was written at all.
/// </param>
/// <param name="Error">Why the request was refused before anything was tried, or empty.</param>
public readonly record struct ScaffoldResult(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Collisions,
    string Error
) {
    /// <summary>Whether the project was written.</summary>
    public bool Succeeded => Error.Length == 0 && Collisions.Count == 0;
}

/// <summary>Writes a new project from a template.</summary>
/// <remarks>
///     <para>
///         <b>The half of <c>vixen new</c> that is not a console.</b> Which template, whether the
///         name can be a namespace, what would be overwritten and what to write: all four are
///         decisions, and the CLI's copy of them was the only copy — which is why the editor's New
///         Project made two directories instead. The same split
///         <c>ContentPipeline</c> made from <c>ImportRunner</c>, for the same reason and with the
///         same shape: a result somebody formats rather than lines somebody prints.
///     </para>
///     <para>
///         ⚠ <b>Nothing is overwritten, and every collision is found before anything is written.</b>
///         A scaffolder that clobbers is one nobody runs twice, and "I pointed it at the wrong
///         directory" is the ordinary mistake rather than the exotic one — so a half-scaffolded
///         directory must not be a state this can leave behind.
///     </para>
/// </remarks>
public static class ProjectScaffold {
    /// <summary>The version a new project pins, for the SDK and for every package it references.</summary>
    /// <remarks>
    ///     Read from this assembly rather than written down, so a scaffolded project asks for the
    ///     engine that matches the thing that scaffolded it. A hard-coded version here is one that
    ///     silently goes stale and produces projects that will not restore.
    /// </remarks>
    public static string SdkVersion { get; } =
        typeof(ProjectScaffold).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.1.0";

    /// <summary>Whether a name can be both an assembly name and a namespace.</summary>
    /// <param name="name">The proposed name.</param>
    /// <returns>Whether it will do.</returns>
    /// <remarks>
    ///     Checked rather than left to the compiler, because the compiler's complaint arrives after
    ///     the files exist and names a generated line rather than the argument that caused it.
    /// </remarks>
    public static bool IsUsableName(string name) =>
        name is { Length: > 0 }
        && char.IsLetter(name[0])
        && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '.');

    /// <summary>The nearest usable project name to a directory's own.</summary>
    /// <param name="directoryName">What the folder is called.</param>
    /// <returns>A name <see cref="IsUsableName" /> accepts.</returns>
    /// <remarks>
    ///     ⚠ <b>For the editor, where the name is not typed — it is whatever folder somebody picked
    ///     in a file dialog.</b> "my game (2)" is an ordinary thing to call a directory and an
    ///     impossible thing to call a namespace, and refusing to make a project out of it would be
    ///     the editor rejecting a folder the user had just created in its own picker. The CLI does
    ///     not use this: there the name is an argument, so saying it is unusable is useful and
    ///     silently changing it would not be.
    /// </remarks>
    public static string NameFrom(string directoryName) {
        var cleaned = new string(
            [.. (directoryName ?? string.Empty)
                .Where(character => char.IsLetterOrDigit(character) || character is '_' or '.')]
        ).TrimStart('.', '_');

        // A leading digit is the other way a folder name fails, and prefixing beats dropping: `2024`
        // becoming `024` would be a project named after a mangling of what somebody typed.
        return cleaned.Length > 0 && char.IsLetter(cleaned[0]) ? cleaned : "Game" + cleaned;
    }

    /// <summary>Writes a template into a directory.</summary>
    /// <param name="template">Which template: <c>game</c>, <c>app</c> or <c>lib</c>.</param>
    /// <param name="name">The project's name. Becomes the assembly name and the root namespace.</param>
    /// <param name="directory">Where to write it. Created if it is not there.</param>
    /// <returns>What was written, or what stopped it.</returns>
    /// <exception cref="IOException">The directory could not be written to.</exception>
    public static ScaffoldResult Write(string template, string name, string directory) {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        if (!TemplateCatalog.TryFind(template ?? string.Empty, out var chosen)) {
            return new([], [], $"'{template}' is not a template. There are {TemplateCatalog.All.Count}.");
        }

        if (!IsUsableName(name)) {
            return new(
                [],
                [],
                $"'{name}' cannot be a project name. It has to start with a letter and hold only "
                + "letters, digits, underscores and dots — it becomes both an assembly name and a "
                + "namespace."
            );
        }

        var root = Path.GetFullPath(directory);
        var files = chosen.Instantiate(name, SdkVersion);
        var collisions = files.Where(file => File.Exists(Path.Combine(root, file.Path))).Select(file => file.Path).ToList();

        if (collisions.Count > 0) {
            return new([], collisions, string.Empty);
        }

        foreach (var file in files) {
            var path = Path.Combine(root, file.Path);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Content);
        }

        return new([.. files.Select(file => file.Path)], [], string.Empty);
    }
}
