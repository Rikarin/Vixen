// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Gameplay;
using Vixen.Gameplay;
using Vixen.Samples.Mmo.Rules;

namespace Vixen.Samples.Mmo.Content.Tests;

/// <summary>What a diagnostic looked like, with the file that produced it.</summary>
/// <param name="Address">Where it came from.</param>
/// <param name="Message">What it said.</param>
public readonly record struct ContentProblem(string Address, string Message) {
    /// <inheritdoc />
    public override string ToString() => $"{Address}: {Message}";
}

/// <summary>The sample's authored tree, imported once through the real importer.</summary>
/// <remarks>
///     <para>
///         <b>The precedent is <c>ThirdPersonShooter.Frame.Tests</c>, and the reason is the same:</b> a
///         YAML mistake should fail a test rather than a launch. It reads the game's own files —
///         linked into the output, not copied into the source tree, because a second copy is the one
///         that drifts.
///     </para>
///     <para>
///         ⚠ <b>The address is the path, lowercased, without the extension</b>, which is what the
///         content pipeline does with an addressable and therefore what every cross-reference in the
///         tree is written against. Getting it wrong here would make the whole set resolve against
///         itself and against nothing the game will load.
///     </para>
///     <para>
///         ⚠ <b>The extension is cosmetic and the type tag decides</b> — doc 28 G-Q1. So this globs
///         all six and lets the importer refuse anything that is not a definition.
///     </para>
/// </remarks>
public sealed class AuthoredContent {
    static readonly string[] Extensions = [".vxdef", ".vxitem", ".vxquest", ".vxeffect", ".vxloot", ".vxrecipe"];

    AuthoredContent(DefinitionCatalog catalog, ImmutableArray<ContentProblem> problems, int files) {
        Catalog = catalog;
        Problems = problems;
        Files = files;
    }

    /// <summary>Everything the sample authors.</summary>
    public DefinitionCatalog Catalog { get; }

    /// <summary>What the importer refused, with the file that caused it.</summary>
    public ImmutableArray<ContentProblem> Problems { get; }

    /// <summary>How many files were read.</summary>
    public int Files { get; }

    /// <summary>The artefacts, as MmoLibraries.Load wants them.</summary>
    /// <remarks>Kept so a test can build the game's own libraries over exactly what the realm reads.</remarks>
    public ImmutableArray<(string Address, ReadOnlyMemory<byte> Bytes)> Definitions { get; private set; } = [];

    /// <summary>What the sample composes. Built before anything is read, and the reason it can be.</summary>
    public static GameplayComposition? Composition { get; private set; }

    /// <summary>Imports the tree.</summary>
    /// <returns>It.</returns>
    public static async Task<AuthoredContent> LoadAsync() {
        // ⚠ First, and it is not decoration — see MmoModules for what happens without it. Composing
        // is what loads the twenty assemblies whose module initializers fill SerializerRegistry, and
        // an unloaded assembly's definition type is a `!Tag` nothing in the build claims.
        var composition = MmoModules.Compose();

        Composition = composition;

        var root = Path.Combine(AppContext.BaseDirectory, "Assets");
        var builder = new DefinitionCatalogBuilder();

        // ⚠ The composition's tags go in before the content, and forgetting them is a mistake with a
        // very confusing shape. Most tags reach the table because a definition mentions them — but a
        // tag only *code* knows never does, and `Event.Kill` is the one that matters: it is the verb
        // a Kill objective counts, QuestModule declares it, and no quest file mentions it anywhere.
        // Without this every objective in the game compiles to one nothing can ever advance.
        foreach (var tag in composition.Tags) {
            builder.AddTag(tag);
        }
        var problems = ImmutableArray.CreateBuilder<ContentProblem>();
        var artefacts = ImmutableArray.CreateBuilder<(string, ReadOnlyMemory<byte>)>();
        var importer = new DefinitionImporter();
        var files = 0;

        // Ordered, so a failure names the same file on every machine and two runs build the same
        // tag table — a tag's index is its position in a pre-order walk, and the walk starts here.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal)) {
            var address = Address(root, file);
            var provider = new MemoryFileProvider();
            var path = new VirtualPath("/" + address + Path.GetExtension(file));

            files++;
            provider.Seed(path, await File.ReadAllTextAsync(file));

            var context = new ImportContext(
                AssetId.New(),
                path,
                importer.CreateSettings(),
                provider,
                importer.Name,
                "Tests"
            );

            var result = await importer.ImportAsync(context, CancellationToken.None);

            foreach (var diagnostic in result.Diagnostics.Where(entry => entry.Severity == ImportSeverity.Error)) {
                problems.Add(new(address, diagnostic.Message));
            }

            foreach (var artefact in result.Artifacts) {
                builder.Add(address, artefact.Content.Span);
                artefacts.Add((address, artefact.Content));
            }
        }

        return new(builder.Build(), problems.ToImmutable(), files) { Definitions = artefacts.ToImmutable() };
    }

    static string Address(string root, string file) =>
        Path.ChangeExtension(Path.GetRelativePath(root, file), null).Replace('\\', '/').ToLowerInvariant();
}
