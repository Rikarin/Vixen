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
    readonly List<byte[]> entries = [];
    readonly HashSet<long> signatures = [];

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

        if (!signatures.Add(signature)) {
            return false;
        }

        entries.Add(input);

        return true;
    }

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
