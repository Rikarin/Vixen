// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;

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
    public static string SdkVersion => ProjectScaffold.SdkVersion;

    /// <summary>Writes the project.</summary>
    /// <param name="template">Which template: <c>game</c>, <c>app</c> or <c>lib</c>.</param>
    /// <param name="name">The project's name. Becomes the assembly name and the root namespace.</param>
    /// <param name="directory">Where to write it. Created if it is not there.</param>
    /// <param name="output">Where to report what was written.</param>
    /// <returns>Success, or a usage error with the reason already written.</returns>
    /// <remarks>
    ///     ⚠ <b>The decisions moved to <see cref="ProjectScaffold" /> and what is left here is the
    ///     console.</b> That is the split <c>ImportRunner</c> and <c>ContentPipeline</c> already
    ///     make, and it happened for the same reason: the editor's New Project needs the four
    ///     decisions — which template, is the name usable, what would be overwritten, what to write
    ///     — and does not need a <see cref="TextWriter" />.
    /// </remarks>
    public static ExitCode Run(string template, string name, string directory, TextWriter output) {
        ArgumentNullException.ThrowIfNull(output);

        var root = Path.GetFullPath(directory);
        var result = ProjectScaffold.Write(template, name, root);

        if (result.Error.Length > 0) {
            output.WriteLine(result.Error);

            // The list is the console's half: a caller that is not a terminal has `All` itself.
            if (!TemplateCatalog.TryFind(template ?? string.Empty, out _)) {
                foreach (var known in TemplateCatalog.All) {
                    output.WriteLine($"  {known.ShortName,-8} {known.Description}");
                }
            }

            return ExitCode.UsageError;
        }

        if (result.Collisions.Count > 0) {
            output.WriteLine($"Nothing was written: {result.Collisions.Count} file(s) are already there.");

            foreach (var path in result.Collisions) {
                output.WriteLine($"  {path}");
            }

            return ExitCode.UsageError;
        }

        TemplateCatalog.TryFind(template, out var chosen);
        output.WriteLine($"Created {chosen.ShortName} '{name}' in {root}");

        foreach (var path in result.Written) {
            output.WriteLine($"  {path}");
        }

        if (chosen.Id is "vixen-game") {
            output.WriteLine();
            output.WriteLine("  dotnet run     — build the content and play it");
            output.WriteLine("  vixen build    — publish it for a target");
        }

        // A plugin is the one scaffold that is not run — it is loaded by something else, from a
        // folder it has to be put in first. Neither of those two facts is in any file the template
        // wrote, so they are said here.
        if (chosen.Id is "vixen-plugin") {
            output.WriteLine();
            output.WriteLine("  Change 'id' in plugin.yaml: it is a reverse-domain name and nobody can guess yours.");
            output.WriteLine("  dotnet build   — the output directory is a plugin folder, manifest included");
            output.WriteLine($"  Copy it to <project>/Plugins/{name}/, then Reload Plugins in the editor.");
        }

        // ⚠ A batch head reads the content beside its own binary, and a freshly scaffolded one has
        // none — so `dotnet run` on it reports nothing to check and reads as broken. Where the
        // content comes from is the one thing about this template that is in no file it wrote.
        if (chosen.Id is "vixen-tool") {
            output.WriteLine();
            output.WriteLine("  dotnet run     — checks the content beside the binary; a new project has none");
            output.WriteLine("  dotnet run -- --vixen-loose-content <game>/Build/<target>   — point it at a real build");
            output.WriteLine("  --vixen-capture <path> opens a device and writes a frame; --vixen-frames N runs more than one");
        }

        // Five directories and no idea which one to open is a worse first minute than one project
        // and no advice at all, so the multi-project scaffold says where to start and what the one
        // thing it cannot do for you is.
        if (chosen.Id is "vixen-mmo") {
            output.WriteLine();
            output.WriteLine($"  {name}.Contracts  the wire, seen by everybody");
            output.WriteLine($"  {name}.Shared     the rules the client and the realm both run");
            output.WriteLine($"  {name}.Realm      a shard: launched with --realm-spec, drained over stdin");
            output.WriteLine($"  {name}.Client     the player's half");
            output.WriteLine($"  {name}.Content    maps and definitions, built once per profile");
            output.WriteLine();
            output.WriteLine("  A realm needs a spec to be anything. Start one by hand with:");
            output.WriteLine($"    dotnet run --project {name}.Realm -- --realm-spec \\");
            output.WriteLine("      \"shard=<guid>;map=maps/<yours>;region=eu;build=0.1.0;content=0;kind=Public;\\");
            output.WriteLine("       host=127.0.0.1;port=7777;soft=100;hard=120;tick=30;seed=1\"");
            output.WriteLine();
            output.WriteLine("  See docs/guide/live for what places one for you.");
        }

        return ExitCode.Success;
    }
}
