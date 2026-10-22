// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Assets;

/// <summary>What source control says about one path.</summary>
/// <remarks>
///     ⚠ <b>Deliberately smaller than any provider's own vocabulary.</b> Git alone distinguishes
///     staged from unstaged and both from the index, and a browser column that drew five shades of
///     "changed" would be a column nobody can read across a panel. What a person scanning a folder
///     wants to know is whether a file is theirs to worry about, which is these six answers.
/// </remarks>
public enum SourceControlStatus : byte {
    /// <summary>Nothing has been asked, or there is no provider.</summary>
    Unknown,

    /// <summary>Tracked, and the same as the last commit.</summary>
    Unmodified,

    /// <summary>Tracked and changed.</summary>
    Modified,

    /// <summary>New, and staged to be committed.</summary>
    Added,

    /// <summary>Tracked, and gone from the working tree.</summary>
    Deleted,

    /// <summary>A merge left it in two minds.</summary>
    Conflicted,

    /// <summary>In the project and not in source control at all.</summary>
    Untracked
}

/// <summary>One commit that touched a file, as a history row shows it.</summary>
/// <param name="Id">The full revision, which is what a later command is given.</param>
/// <param name="ShortId">The abbreviation a person reads and quotes.</param>
/// <param name="Author">Who wrote it.</param>
/// <param name="When">When they did, in their own offset as the provider recorded it.</param>
/// <param name="Summary">The first line of the message, which is the row's label.</param>
/// <remarks>
///     ⚠ <b>Both ids, rather than one and an abbreviation rule.</b> How many characters are enough
///     to be unambiguous is a property of the repository — git decides it per repository and grows
///     it as the repository does — so a panel that truncated the full one itself would print an id
///     that resolves to two commits in exactly the repositories where that matters.
/// </remarks>
public sealed record SourceControlRevision(string Id, string ShortId, string Author, DateTimeOffset When, string Summary);

/// <summary>What changed in one file, at one revision or since the last commit.</summary>
/// <param name="IsText">Whether <see cref="Text" /> is a patch or a sentence about one.</param>
/// <param name="Text">The unified diff, or what there is to say when there cannot be one.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The flag is the whole design decision, and it is the one the issue said had to be
///         made first.</b> Most of a game project is not text: a diff line for a <c>.png</c>, a mesh
///         or a compiled <c>.vxscene</c> is a promise the viewer breaks the first time somebody uses
///         it. So a binary asset's diff is <em>not</em> a patch with the bytes elided — it is one
///         honest sentence saying it is binary and how its size moved, which is what git's own
///         <c>--stat</c> says about one and is the most any viewer can say without a per-format
///         comparer.
///     </para>
///     <para>
///         ⚠ <b>And that is why <see cref="IsText" /> is a field rather than something a reader
///         infers.</b> A panel that decided by looking for a leading <c>@@</c> would call an empty
///         diff binary, and a text file whose only change is a trailing newline produces exactly
///         that.
///     </para>
/// </remarks>
public sealed record SourceControlDiff(bool IsText, string Text);
