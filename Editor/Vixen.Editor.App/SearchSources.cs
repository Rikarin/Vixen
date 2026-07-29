// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;

namespace Vixen.Editor.App;

/// <summary>Every asset in the project, by name and by path.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's A8: the palette's machinery over <i>content</i> rather than over commands.</b>
///         The contract is deliberately "a source does its own matching" — <see cref="IPaletteSource" />
///         says so — which is what lets this scan the database's own dictionary rather than being
///         handed a scorer and told to walk something.
///     </para>
///     <para>
///         ⚠ <b>It scans rather than indexes, and that is a decision with a stated limit.</b>
///         <c>AssetDatabase.Entries</c> is a dictionary of a few thousand entries in an ordinary
///         project and the scorer is a substring walk, so one keystroke is a few thousand cheap
///         comparisons — measurably nothing beside the layout pass that follows it. Doc 20 notes
///         that "the asset source can index rather than scan"; at a hundred thousand assets it
///         should, and the seam for that is this class rather than the palette.
///     </para>
///     <para>
///         ⚠ <b>Folders are included.</b> They carry a GUID like any other asset — which is what
///         makes moving one not break what is inside it — and "where is my Materials folder" is a
///         question people ask a search box.
///     </para>
/// </remarks>
sealed class AssetSearchSource : IPaletteSource {
    readonly EditorProject project;
    readonly Action<AssetId> chosen;

    /// <summary>Searches a project's assets.</summary>
    /// <param name="project">The project.</param>
    /// <param name="chosen">What choosing a result does.</param>
    public AssetSearchSource(EditorProject project, Action<AssetId> chosen) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(chosen);

        this.project = project;
        this.chosen = chosen;
    }

    /// <inheritdoc />
    public string Category => "Asset";

    /// <inheritdoc />
    public void Search(string query, List<PaletteItem> results, int limit) {
        ArgumentNullException.ThrowIfNull(results);

        foreach (var entry in project.Assets.Entries) {
            var score = Math.Max(FuzzyMatcher.Score(query, entry.Name), FuzzyMatcher.Score(query, entry.Path));

            if (score == FuzzyMatcher.NoMatch) {
                continue;
            }

            var asset = entry.Guid;
            var found = entry;

            results.Add(
                new PaletteItem(entry.Name, Category, score, entry.Path, () => chosen(asset)) {
                    // ⚠ Deferred: the palette keeps twenty of these and previews exactly one.
                    Preview = () => Describe(found)
                }
            );
        }
    }

    /// <summary>What the preview pane says about an asset.</summary>
    /// <remarks>
    ///     The referrer count is the part worth the space: doc 20 says Find References belongs in
    ///     three places at once and <see cref="ReferenceIndex" /> answers it already, so a search
    ///     result that says "four things point at this" is the cheapest of the three.
    /// </remarks>
    string Describe(AssetEntry entry) {
        var referrers = project.References.ReferrersOf(entry.Guid).Count;

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{entry.Path}{Environment.NewLine}{(entry.IsFolder ? "Folder" : entry.ImporterTag ?? "No importer")} · {referrers} referrer(s)"
        );
    }
}

/// <summary>Every entity in the open scene, by name.</summary>
/// <remarks>
///     ⚠ <b>The document rather than the world, because a name is the document's.</b> An ECS entity
///     is a handle and carries no name at all; <c>SceneDocument.NameOf</c> is the table that gives it
///     one, which is also why a play-mode restore has to remap it. Searching the world would find
///     nothing to show.
/// </remarks>
sealed class EntitySearchSource : IPaletteSource {
    readonly Func<SceneDocument> document;
    readonly Action<Entity> chosen;

    /// <summary>Searches whichever scene is open.</summary>
    /// <param name="document">Asked each time, because the editor can load another scene into it.</param>
    /// <param name="chosen">What choosing a result does.</param>
    public EntitySearchSource(Func<SceneDocument> document, Action<Entity> chosen) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chosen);

        this.document = document;
        this.chosen = chosen;
    }

    /// <inheritdoc />
    public string Category => "Entity";

    /// <inheritdoc />
    public void Search(string query, List<PaletteItem> results, int limit) {
        ArgumentNullException.ThrowIfNull(results);

        var scene = document();

        // Hoisted, because it is the same answer for every match and reading a signal per result is
        // work proportional to the scene rather than to the query.
        var title = scene.Title.Peek();
        var preview = string.Create(CultureInfo.CurrentCulture, $"Entity in {title}");

        foreach (var entity in scene.Entities) {
            var name = scene.NameOf(entity);
            var score = FuzzyMatcher.Score(query, name);

            if (score == FuzzyMatcher.NoMatch) {
                continue;
            }

            var found = entity;

            results.Add(new PaletteItem(name, Category, score, title, () => chosen(found)) { Preview = () => preview });
        }
    }
}
