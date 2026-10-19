// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Reactive;

namespace Vixen.Ui;

/// <summary>The documents most recently opened or saved, by where they live, newest first.</summary>
/// <remarks>
///     <para>
///         <b>Keyed on <see cref="IEditableDocument.Location" /> and on nothing else</b> (#656). A
///         document that has never been saved has no location and is not recent in any sense a menu can
///         reopen; one that is saved under a new name is the new name, which is why
///         <see cref="Track" /> follows the signal rather than reading it once.
///     </para>
///     <para>
///         ⚠ <b>A location is compared as written</b>, ordinally. Whether two spellings of a path are
///         one file is the platform's question and the host's — the editor hands this absolute paths
///         from one function — and a list that guessed would merge two files on a case-sensitive
///         volume.
///     </para>
///     <para>
///         ⚠ <b>Forgetting is explicit, never a side-effect of a file being missing.</b> A path on a
///         volume that is not mounted is the entry somebody most wants back, so a host greys it and
///         keeps it — the rule the editor's recent-projects list already follows.
///     </para>
///     <para>
///         It holds strings, not documents, so it is what a host saves and restores between runs —
///         <see cref="Restore" /> takes the list back without the reordering <see cref="Note" />
///         does.
///     </para>
/// </remarks>
public sealed class RecentDocuments {
    readonly List<string> locations = [];

    /// <summary>An empty list.</summary>
    /// <param name="capacity">How many it keeps; the oldest beyond it is dropped.</param>
    public RecentDocuments(int capacity = 10) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>How many it keeps.</summary>
    public int Capacity { get; }

    /// <summary>The locations, newest first.</summary>
    public IReadOnlyList<string> Locations => locations;

    /// <summary>Raised after the list changed, and not when a note changed nothing.</summary>
    public event Action<RecentDocuments>? Changed;

    /// <summary>Puts a location at the front, once.</summary>
    /// <param name="location">Where a document lives.</param>
    /// <returns>Whether the list changed — <see langword="false" /> for the one already at the front.</returns>
    public bool Note(string location) {
        ArgumentException.ThrowIfNullOrEmpty(location);

        if (locations.Count > 0 && string.Equals(locations[0], location, StringComparison.Ordinal)) {
            return false;
        }

        locations.RemoveAll(existing => string.Equals(existing, location, StringComparison.Ordinal));
        locations.Insert(0, location);

        if (locations.Count > Capacity) {
            locations.RemoveRange(Capacity, locations.Count - Capacity);
        }

        Changed?.Invoke(this);
        return true;
    }

    /// <summary>Takes a location off the list.</summary>
    /// <param name="location">The location.</param>
    /// <returns>Whether it was on it.</returns>
    public bool Forget(string location) {
        ArgumentNullException.ThrowIfNull(location);

        if (locations.RemoveAll(existing => string.Equals(existing, location, StringComparison.Ordinal)) == 0) {
            return false;
        }

        Changed?.Invoke(this);
        return true;
    }

    /// <summary>Replaces the list with one a host kept, in its order, without duplicates and within the capacity.</summary>
    /// <param name="saved">The locations, newest first.</param>
    public void Restore(IEnumerable<string> saved) {
        ArgumentNullException.ThrowIfNull(saved);

        locations.Clear();

        foreach (var location in saved) {
            if (locations.Count == Capacity) {
                break;
            }

            if (!string.IsNullOrEmpty(location) && !locations.Contains(location, StringComparer.Ordinal)) {
                locations.Add(location);
            }
        }

        Changed?.Invoke(this);
    }

    /// <summary>Notes a document's location now and every time it changes to another.</summary>
    /// <param name="document">The document.</param>
    /// <param name="scheduler">The scheduler of the UI document it is shown in.</param>
    /// <returns>What stops following it.</returns>
    /// <remarks>
    ///     ⚠ <b>A null location is skipped, not forgotten.</b> Null is "never saved", and the first
    ///     save is the moment a new document becomes recent — so a document opened untitled and saved
    ///     a minute later arrives on the list then, which a read at open would have missed.
    /// </remarks>
    public IDisposable Track(IEditableDocument document, EffectScheduler? scheduler = null) {
        ArgumentNullException.ThrowIfNull(document);

        return new Effect(
            () => {
                if (document.Location.Value is { Length: > 0 } location) {
                    Note(location);
                }
            },
            scheduler
        );
    }
}
