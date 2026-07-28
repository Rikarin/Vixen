// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>One line of the palette's result list.</summary>
/// <param name="Title">What it says.</param>
/// <param name="Category">Where it came from: <c>Command</c>, <c>Asset</c>, <c>Setting</c>.</param>
/// <param name="Score">How well it matched, from <see cref="FuzzyMatcher" />.</param>
/// <param name="Detail">What is shown on the right — a shortcut, a path — or <c>null</c>.</param>
/// <param name="Run">What choosing it does.</param>
public readonly record struct PaletteItem(string Title, string Category, int Score, string? Detail, Action Run);

/// <summary>Somewhere the palette looks.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The palette is not a command list, and building it as one is the mistake.</b> Doc 11
///         asks for "fuzzy search over commands, assets, scene objects, and settings" — four sources
///         with nothing in common except that each can answer "what of yours matches this?" — so the
///         palette takes an ordered list of these and merges what they return. The scene view's
///         source is not this assembly's business, and it never becomes it.
///     </para>
///     <para>
///         <b>A source does its own matching.</b> An asset source with a hundred thousand entries
///         wants an index rather than a linear scan through a scorer, and the palette that told it
///         how to search would prevent that. What the palette owns is the merge, the ranking and the
///         cap.
///     </para>
/// </remarks>
public interface IPaletteSource {
    /// <summary>What this source calls its results.</summary>
    string Category { get; }

    /// <summary>Adds what matches to the list.</summary>
    /// <param name="query">What was typed, possibly empty.</param>
    /// <param name="results">Where to put them.</param>
    /// <param name="limit">How many are worth returning; more will be discarded.</param>
    void Search(string query, List<PaletteItem> results, int limit);
}

/// <summary>The palette's view of the command registry.</summary>
/// <remarks>
///     ⚠ <b>A command that cannot run is shown and refuses, rather than being hidden.</b> A palette
///     that hides disabled commands is one where "Undo" disappearing means either that there is
///     nothing to undo or that the editor has forgotten how — and the user cannot tell which. It
///     also makes the list flicker as the selection changes underneath it.
/// </remarks>
public sealed class CommandPaletteSource : IPaletteSource {
    readonly CommandRegistry commands;
    readonly KeyMap keys;

    /// <summary>Searches a registry.</summary>
    /// <param name="commands">The commands.</param>
    /// <param name="keys">What their shortcuts are, shown against each result.</param>
    public CommandPaletteSource(CommandRegistry commands, KeyMap keys) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(keys);

        this.commands = commands;
        this.keys = keys;
    }

    /// <inheritdoc />
    public string Category => "Command";

    /// <inheritdoc />
    public void Search(string query, List<PaletteItem> results, int limit) {
        ArgumentNullException.ThrowIfNull(results);

        foreach (var command in commands.Commands) {
            if (command.IsHiddenFromPalette) {
                continue;
            }

            var title = command.Title.Text;
            var category = command.Category.Id is null ? Category : command.Category.Text;

            // Matched against the category as well, so `view reset` finds "Reset Layout" under
            // View. The two are scored together rather than separately because a query that spans
            // them is one query.
            var score = Math.Max(FuzzyMatcher.Score(query, title), FuzzyMatcher.Score(query, category + " " + title));

            if (score == FuzzyMatcher.NoMatch) {
                continue;
            }

            var chord = keys.ChordFor(command.Id);
            var id = command.Id;

            results.Add(new PaletteItem(title, category, score, chord.IsBound ? chord.Describe() : null, () => commands.Execute(id)));
        }
    }
}

/// <summary>A source over a list somebody else keeps.</summary>
/// <param name="Category">What to call its results.</param>
/// <param name="Entries">What is in it, asked each time the query changes.</param>
/// <param name="Chosen">What choosing one does.</param>
/// <remarks>
///     <para>
///         What an asset browser, a scene hierarchy and a settings page each register: they have the
///         list and know what to do with a choice, and neither of those is a reason to write a class.
///     </para>
///     <para>
///         ⚠ <b>It scores every entry and honours <c>limit</c> only when the query is empty.</b>
///         Stopping early on a query would return the first ten that matched rather than the ten
///         that matched best, which is the failure that makes a palette feel broken — the thing you
///         typed the name of is not in the list. A source over a hundred thousand assets should
///         index rather than delegate.
///     </para>
/// </remarks>
public sealed record DelegatePaletteSource(string Category, Func<IEnumerable<string>> Entries, Action<string> Chosen)
    : IPaletteSource {
    /// <inheritdoc />
    public void Search(string query, List<PaletteItem> results, int limit) {
        ArgumentNullException.ThrowIfNull(results);

        var found = 0;

        foreach (var entry in Entries()) {
            var score = FuzzyMatcher.Score(query, entry);

            if (score == FuzzyMatcher.NoMatch) {
                continue;
            }

            var chosen = entry;
            results.Add(new PaletteItem(entry, Category, score, null, () => Chosen(chosen)));

            if (query.Length == 0 && ++found >= limit) {
                return;
            }
        }
    }
}
