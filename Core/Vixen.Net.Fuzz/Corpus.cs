// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Net.Fuzz;

/// <summary>The inputs worth keeping for one target.</summary>
/// <remarks>
///     <para>
///         <b>Kept for producing a behaviour nothing before it produced</b>, which is the whole of
///         the guidance here — see <see cref="IFuzzTarget.Run" /> for what that signature is and
///         what it is not. An input that decodes exactly as far as the last thousand did adds
///         nothing to work from, and a corpus that keeps everything is a corpus the mutator picks
///         uselessly out of.
///     </para>
///     <para>
///         <b>The on-disk half is for regressions, not for growth.</b> A crashing input is written
///         next to the code and committed, and every later build replays it before it fuzzes
///         anything — so a defect that was found once is a test from then on, which is the
///         difference between fuzzing and having fuzzed. The grown corpus is deliberately *not*
///         persisted: it would be a large binary directory whose contents depend on the machine that
///         produced it, and the seeds plus a fixed generator seed reproduce a run without it.
///     </para>
/// </remarks>
public sealed class Corpus {
    /// <summary>How many inputs are kept before new ones start replacing old ones.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A corpus is a working set, and an unbounded one is a memory leak wearing a
    ///         hat.</b> Learnt the hard way: with a signature that could not saturate, one target
    ///         kept a million inputs inside a second and the test host died under the memory
    ///         pressure of a full-solution run — a failure with no failing test in it, because the
    ///         process never got as far as writing its results.
    ///     </para>
    ///     <para>
    ///         Four thousand is far more than the mutator can usefully draw from anyway: it picks
    ///         one at random per case, so a corpus ten times the size is a corpus each of whose
    ///         entries is visited a tenth as often.
    ///     </para>
    /// </remarks>
    public const int MaxEntries = 4096;

    /// <summary>How many distinct behaviours are remembered before novelty stops being tracked.</summary>
    /// <remarks>
    ///     The backstop for a signature that is finer-grained than the thing it is meant to
    ///     summarise. Past this the target has demonstrated that nearly everything looks new to it,
    ///     which means the guidance is worth nothing on that target — so it is switched off rather
    ///     than paid for, and the run carries on mutating what it already has.
    /// </remarks>
    public const int MaxSignatures = 1 << 16;

    /// <summary>How often an input is kept once novelty has stopped meaning anything.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Only reached when a target has said its behaviour space is too large for the
    ///         signature to summarise</b> — see <see cref="IFuzzTarget.NoveltyGuides" />. Past
    ///         <see cref="MaxSignatures" /> such a target keeps one input in this many regardless of
    ///         what it did, so the pool the mutator draws from goes on turning over.
    ///     </para>
    ///     <para>
    ///         <b>Which is a fix rather than a feature.</b> Saturation used to stop two different
    ///         things at once: it stopped the signature table growing, which is the memory bound it
    ///         was there for, and it stopped the corpus growing at all, which nothing wanted. A run
    ///         that saturates in its first second then spends the rest of an hour mutating whatever
    ///         the first few thousand cases happened to leave behind. Only the first of those is
    ///         worth keeping.
    ///     </para>
    ///     <para>
    ///         Sixty-four is slow enough that the protected seeds still dominate what the mutator
    ///         picks, and fast enough that a long run replaces the unprotected pool many times over.
    ///     </para>
    /// </remarks>
    public const int Sample = 64;

    readonly List<byte[]> entries = [];
    readonly HashSet<long> signatures = [];
    ulong offered;
    int protectedCount;

    /// <summary>Whether an input is kept for being novel, or sampled once novelty saturates.</summary>
    /// <remarks>Set from <see cref="IFuzzTarget.NoveltyGuides" />. True is what every decoder wants.</remarks>
    public bool NoveltyGuides { get; set; } = true;

    /// <summary>How many inputs are held.</summary>
    public int Count => entries.Count;

    /// <summary>How many distinct behaviours have been seen.</summary>
    public int SignatureCount => signatures.Count;

    /// <summary>The inputs, in the order they were kept.</summary>
    public IReadOnlyList<byte[]> Entries => entries;

    /// <summary>Adds an input if its behaviour is one not seen before.</summary>
    /// <param name="input">The input.</param>
    /// <param name="signature">What the target did with it.</param>
    /// <returns>Whether it was kept.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input" /> is null.</exception>
    public bool Offer(byte[] input, long signature) {
        ArgumentNullException.ThrowIfNull(input);
        offered++;

        // Saturated. See MaxSignatures: the guidance has proved worthless on this target, so it is
        // switched off rather than paid for, and the signature table never grows again.
        if (signatures.Count >= MaxSignatures) {
            // For a target that expected to saturate, the pool goes on turning over anyway — see
            // Sample. For one that did not, freezing it is the older and more conservative answer:
            // a decoder whose signature cannot saturate has a signature that is wrong, and the
            // finding is that rather than the corpus.
            if (NoveltyGuides || offered % Sample != 0) {
                return false;
            }
        } else if (!signatures.Add(signature)) {
            return false;
        }

        if (entries.Count < MaxEntries) {
            entries.Add(input);

            return true;
        }

        // Replaced rather than refused. A corpus that stops accepting is a corpus frozen at whatever
        // the first few thousand cases happened to be, which is the shapes reachable from the seeds
        // in one or two mutations and nothing further out.
        //
        // Never over the protected entries, though. The seeds are the only well-formed inputs there
        // are — everything else in here is a mutant — so letting them be evicted would, over a long
        // enough run, leave a corpus made entirely of malformed packets and a fuzzer that had
        // quietly stopped exercising any decoder past its first refusal.
        var replaceable = entries.Count - protectedCount;
        entries[protectedCount + (int)(offered % (ulong)replaceable)] = input;

        return true;
    }

    /// <summary>Marks everything added so far as never to be evicted.</summary>
    /// <remarks>
    ///     Called once the seeds and the committed regressions are in. Both are inputs somebody
    ///     chose, and neither is something the mutator could produce again by accident.
    /// </remarks>
    public void Protect() => protectedCount = Math.Min(entries.Count, MaxEntries - 1);

    /// <summary>Adds an input regardless of whether it is novel, for seeds and regressions.</summary>
    /// <param name="input">The input.</param>
    /// <exception cref="ArgumentNullException"><paramref name="input" /> is null.</exception>
    public void Add(byte[] input) {
        ArgumentNullException.ThrowIfNull(input);
        entries.Add(input);
    }

    /// <summary>Picks one, by index modulo how many there are.</summary>
    /// <param name="index">Any number.</param>
    /// <returns>An input, or an empty one if the corpus is empty.</returns>
    public byte[] Pick(ulong index) => entries.Count == 0 ? [] : entries[(int)(index % (ulong)entries.Count)];

    /// <summary>Reads the committed regression inputs for a target.</summary>
    /// <param name="directory">The corpus directory, which need not exist.</param>
    /// <param name="target">Which target's inputs to read.</param>
    /// <returns>Them, oldest name first. Empty if there are none.</returns>
    /// <remarks>
    ///     Files rather than a blob, one input each, named after the target and the hash of the
    ///     bytes — so two machines that find the same defect write the same file name and the second
    ///     one does not add a duplicate to the review.
    /// </remarks>
    public static IReadOnlyList<byte[]> ReadRegressions(string directory, string target) {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(target);

        var folder = Path.Combine(directory, target);

        if (!Directory.Exists(folder)) {
            return [];
        }

        var files = Directory.GetFiles(folder, "*.bin");
        Array.Sort(files, StringComparer.Ordinal);

        var inputs = new List<byte[]>(files.Length);

        foreach (var file in files) {
            inputs.Add(File.ReadAllBytes(file));
        }

        return inputs;
    }

    /// <summary>Writes an input that failed, so it is replayed on every later build.</summary>
    /// <param name="directory">The corpus directory. Created if it is not there.</param>
    /// <param name="target">Which target it failed.</param>
    /// <param name="input">The bytes.</param>
    /// <returns>The path written.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static string WriteRegression(string directory, string target, byte[] input) {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(input);

        var folder = Path.Combine(directory, target);
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{Fingerprint(input):x16}.bin"));
        File.WriteAllBytes(path, input);

        return path;
    }

    /// <summary>A stable 64-bit name for a run of bytes.</summary>
    /// <param name="input">The bytes.</param>
    /// <returns>Their fingerprint.</returns>
    /// <remarks>
    ///     FNV-1a, and not for its collision resistance: this names a file in a directory somebody
    ///     reads, and the property that matters is that the same bytes get the same name on every
    ///     machine. <c>string.GetHashCode</c> and friends are randomised per process and would name
    ///     the same input differently every run.
    /// </remarks>
    public static ulong Fingerprint(ReadOnlySpan<byte> input) {
        var hash = 14695981039346656037ul;

        foreach (var value in input) {
            hash ^= value;
            hash *= 1099511628211ul;
        }

        return hash;
    }
}
