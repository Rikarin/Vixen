// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Cli;

/// <summary>Writes a new project.</summary>
/// <remarks>
///     <para>
///         <b>Why this exists next to <c>dotnet new</c> rather than instead of it.</b>
///         [Doc 17](../../docs/plan/17-app-heads-and-shipping.md) specifies a template pack —
///         <c>dotnet new vixen-game</c> and its siblings — and that is the right thing for somebody
///         who has installed one. This is the version that works before anything is installed, which
///         is the state a person is in when they are deciding whether to try the engine at all.
///     </para>
///     <para>
///         <b>The two now produce the same output because they read the same files.</b>
///         <c>Tools/Vixen.Templates</c> holds one tree; the pack ships it and this assembly embeds
///         it, and <see cref="TemplateCatalog" /> is the fifty lines that apply the one substitution
///         the templates use. Until that existed the scaffold was C# string literals beside a
///         template pack that did not exist yet, which is two copies of every file waiting to
///         disagree.
///     </para>
///     <para>
///         <b>What it scaffolds against is the SDK, not a pile of package references.</b> A game
///         project says <c>&lt;Project Sdk="Vixen.Sdk/x.y.z"&gt;</c> and gets the
///         import-before-compile and content-build-after-build wiring with nothing else written down
///         — <c>Tools/Vixen.Sdk</c>'s whole point. The alternative, a template listing every
///         <c>PackageReference</c> the engine currently needs, is a template that is wrong one
///         release later.
///     </para>
///     <para>
///         <b>Nothing is overwritten.</b> A scaffolder that clobbers is one nobody runs twice, and
///         "I pointed it at the wrong directory" is the ordinary mistake rather than the exotic one.
///     </para>
/// </remarks>
public static class ScaffoldRunner {
    /// <summary>The version a new project pins, for the SDK and for every package it references.</summary>
    /// <remarks>
    ///     Read from this assembly rather than written down, so a scaffolded project asks for the
    ///     engine that matches the tool that scaffolded it. A hard-coded version here is one that
    ///     silently goes stale and produces projects that will not restore.
    /// </remarks>
    public static string SdkVersion { get; } =
        typeof(ScaffoldRunner).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.1.0";

    /// <summary>Writes the project.</summary>
    /// <param name="template">Which template: <c>game</c>, <c>app</c> or <c>lib</c>.</param>
    /// <param name="name">The project's name. Becomes the assembly name and the root namespace.</param>
    /// <param name="directory">Where to write it. Created if it is not there.</param>
    /// <param name="output">Where to report what was written.</param>
    /// <returns>Success, or a usage error with the reason already written.</returns>
    public static ExitCode Run(string template, string name, string directory, TextWriter output) {
        ArgumentNullException.ThrowIfNull(output);

        if (!TemplateCatalog.TryFind(template ?? string.Empty, out var chosen)) {
            output.WriteLine($"'{template}' is not a template. There are {TemplateCatalog.All.Count}:");

            foreach (var known in TemplateCatalog.All) {
                output.WriteLine($"  {known.ShortName,-8} {known.Description}");
            }

            return ExitCode.UsageError;
        }

        if (!IsUsableName(name)) {
            output.WriteLine(
                $"'{name}' cannot be a project name. It has to start with a letter and hold only "
                + "letters, digits, underscores and dots — it becomes both an assembly name and a "
                + "namespace."
            );

            return ExitCode.UsageError;
        }

        var root = Path.GetFullPath(directory);
        var files = chosen.Instantiate(name, SdkVersion);

        // Every collision is found before anything is written. A half-scaffolded directory is worse
        // than an untouched one, because the second is obviously a no-op and the first is not.
        var existing = files
            .Select(file => Path.Combine(root, file.Path))
            .Where(File.Exists)
            .ToList();

        if (existing.Count > 0) {
            output.WriteLine($"Nothing was written: {existing.Count} file(s) are already there.");

            foreach (var path in existing) {
                output.WriteLine($"  {Path.GetRelativePath(root, path)}");
            }

            return ExitCode.UsageError;
        }

        foreach (var file in files) {
            var path = Path.Combine(root, file.Path);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Content);
        }

        output.WriteLine($"Created {chosen.ShortName} '{name}' in {root}");

        foreach (var file in files) {
            output.WriteLine($"  {file.Path}");
        }

        if (chosen.Id is "vixen-game") {
            output.WriteLine();
            output.WriteLine("  dotnet run     — build the content and play it");
            output.WriteLine("  vixen build    — publish it for a target");
        }

        return ExitCode.Success;
    }

    /// <summary>
    ///     Whether a name can be both an assembly name and a namespace.
    /// </summary>
    /// <remarks>
    ///     Checked here rather than left to the compiler, because the compiler's complaint arrives
    ///     after the files exist and names a generated line rather than the argument that caused it.
    /// </remarks>
    static bool IsUsableName(string name) =>
        name.Length > 0
        && char.IsLetter(name[0])
        && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '.');
}
