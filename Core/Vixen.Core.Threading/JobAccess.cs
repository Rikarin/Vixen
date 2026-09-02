// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Threading;

/// <summary>
///     What a job reads and what it writes, as opaque resource ids — the declaration the safety
///     system checks concurrent jobs against.
/// </summary>
/// <remarks>
///     <para>
///         <b>The ids mean nothing here.</b> This assembly cannot know what a resource is; the one
///         consumer that does is the ECS, which passes <c>ComponentTypeId.Value</c>. Any other
///         producer of declarations only has to agree with itself on a numbering — two subsystems
///         numbering different things the same way would report conflicts that are not there, which
///         is why the numbering belongs to one registry rather than to each caller.
///     </para>
///     <para>
///         <b>A write implies a read.</b> Otherwise a job that only writes <c>X</c> and one that only
///         reads <c>X</c> would look disjoint, which is the one combination that is definitely a
///         race. <see cref="Reads" /> therefore always contains <see cref="Writes" />.
///     </para>
///     <para>
///         ⚠ <b><see cref="None" /> and <see cref="Everything" /> are not opposites.</b>
///         <see cref="None" /> means <em>undeclared</em> — the scheduler has nothing to check and
///         skips the job entirely, which is what every job outside the ECS is.
///         <see cref="Everything" /> means <em>declared, and touching all of it</em>, so it conflicts
///         with every other declared job. The ECS maps an undeclared <em>system</em> onto
///         <see cref="Everything" />, because "I did not say" already means "conflicts with
///         everything" there; a texture decode that never declared anything maps onto
///         <see cref="None" /> and is not policed against it.
///     </para>
/// </remarks>
public sealed class JobAccess {
    /// <summary>Nothing declared, so nothing is checked. What every job has unless one is declared.</summary>
    public static JobAccess None { get; } = new(isEverything: false);

    /// <summary>Declared as touching everything, so it conflicts with every other declared job.</summary>
    public static JobAccess Everything { get; } = new(isEverything: true);

    readonly ulong[] readWords = [];
    readonly ulong[] writeWords = [];

    /// <summary>The resource ids read, including the ones written, ascending.</summary>
    public IReadOnlyList<int> Reads { get; } = [];

    /// <summary>The resource ids written, ascending.</summary>
    public IReadOnlyList<int> Writes { get; } = [];

    /// <summary>Whether this declares that it touches everything.</summary>
    public bool IsEverything { get; }

    /// <summary>Whether this declares nothing, and so is not checked at all.</summary>
    public bool IsUndeclared => !IsEverything && Reads.Count == 0;

    /// <summary>Builds a declaration.</summary>
    /// <param name="reads">The resource ids read.</param>
    /// <param name="writes">The resource ids written. Each is also a read.</param>
    /// <exception cref="ArgumentOutOfRangeException">A resource id is negative.</exception>
    public JobAccess(ReadOnlySpan<int> reads, ReadOnlySpan<int> writes) {
        var written = new SortedSet<int>();
        var read = new SortedSet<int>();

        foreach (var id in writes) {
            ArgumentOutOfRangeException.ThrowIfNegative(id);
            written.Add(id);
            read.Add(id);
        }

        foreach (var id in reads) {
            ArgumentOutOfRangeException.ThrowIfNegative(id);
            read.Add(id);
        }

        if (read.Count == 0) {
            return;
        }

        readWords = new ulong[(read.Max >> 6) + 1];
        writeWords = written.Count == 0 ? [] : new ulong[(written.Max >> 6) + 1];

        foreach (var id in read) {
            readWords[id >> 6] |= 1ul << (id & 63);
        }

        foreach (var id in written) {
            writeWords[id >> 6] |= 1ul << (id & 63);
        }

        Reads = [.. read];
        Writes = [.. written];
    }

    JobAccess(bool isEverything) => IsEverything = isEverything;

    /// <summary>Whether two jobs that ran at the same time would race.</summary>
    /// <param name="other">The other job's declaration.</param>
    /// <returns>Whether one of them writes something the other touches.</returns>
    /// <remarks>
    ///     An undeclared job never conflicts. That is deliberately the opposite of the ECS's
    ///     system-level rule, and the difference is which question is being asked: a system that
    ///     declares nothing is asking the scheduler to order it, and the cautious answer is to order
    ///     it against everything. A job that declares nothing has not opted into the safety system at
    ///     all, and treating it as touching everything would make the detector fire on every asset
    ///     import that happened to overlap a frame.
    /// </remarks>
    public bool ConflictsWith(JobAccess other) {
        ArgumentNullException.ThrowIfNull(other);

        if (IsUndeclared || other.IsUndeclared) {
            return false;
        }

        if (IsEverything || other.IsEverything) {
            return true;
        }

        return Overlaps(writeWords, other.readWords) || Overlaps(other.writeWords, readWords);
    }

    /// <summary>Renders the two sets, which is what the safety system's message shows.</summary>
    /// <returns>The declaration in text.</returns>
    public override string ToString() {
        if (IsEverything) {
            return "everything";
        }

        if (IsUndeclared) {
            return "undeclared";
        }

        var readOnly = Reads.Where(id => !Writes.Contains(id)).ToArray();
        var parts = new List<string>();

        if (readOnly.Length > 0) {
            parts.Add($"reads({string.Join(", ", readOnly)})");
        }

        if (Writes.Count > 0) {
            parts.Add($"writes({string.Join(", ", Writes)})");
        }

        return string.Join(" ", parts);
    }

    static bool Overlaps(ulong[] left, ulong[] right) {
        var shared = Math.Min(left.Length, right.Length);

        for (var word = 0; word < shared; word++) {
            if ((left[word] & right[word]) != 0) {
                return true;
            }
        }

        return false;
    }
}
