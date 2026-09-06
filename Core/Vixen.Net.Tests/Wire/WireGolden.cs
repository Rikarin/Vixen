// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Testing;

namespace Vixen.Net.Tests.Wire;

/// <summary>Compares an encoding against the bytes committed for it.</summary>
/// <remarks>
///     <para>
///         <b>The listing is this file's; the comparison is <see cref="GoldenFile" />'s.</b> What is
///         domain-shaped here is how a wire case is <i>rendered</i> — hex for bytes, the raw bits for
///         a float, a name that says which encoder and which input — and that is what makes a
///         failure read <c>quantize/±1000·16/-0</c> and two byte strings rather than "something
///         moved". None of it is in the comparison, which was a hand-rolled copy of the one in
///         <c>Testing/GoldenFile.cs</c> (#843).
///     </para>
///     <para>
///         ⚠ <b>What adopting it buys is three refusals this file did not have</b>, and one of them
///         is a class of bug these suites are exposed to: a listing that compared <em>nothing</em>
///         used to pass. A golden that had to be created now fails rather than passes — a snapshot
///         nobody has read is not evidence — an empty rendering is refused even against an empty
///         committed golden, and the mismatch arrives as a unified diff with line numbers instead of
///         <c>Assert.Equal</c>'s sixty-character window around the first differing index. On a file
///         that is one line per case, that window is the least useful possible answer.
///     </para>
///     <para>
///         Same shape as Raven's golden tests and honouring the same <c>UPDATE_GOLDEN=1</c>, because
///         a repository with two golden conventions has one convention and one trap.
///         <c>VIXEN_UPDATE_GOLDEN</c> — which <c>build/Build.cs</c> sets from
///         <c>--update-golden</c> — now works here too, which it did not before.
///     </para>
///     <para>
///         <b>Text rather than a hash, and a line per case.</b> A hash over the whole corpus is one
///         bit of information — something moved — and the thing you then want to know is which
///         encoder and which input, on a machine you may not have. A hex line per named case makes a
///         failure a diff that says <c>quantize/±1000·16/-0</c> and the two byte strings, which is
///         the whole investigation rather than the start of one.
///     </para>
/// </remarks>
static class WireGolden {
    /// <summary>Starts a listing.</summary>
    /// <returns>Something to add cases to.</returns>
    public static Listing Begin() => new();

    /// <summary>One golden listing, built case by case.</summary>
    internal sealed class Listing {
        readonly StringBuilder text = new();

        /// <summary>Adds a case: a name, and the bytes it encoded to.</summary>
        /// <param name="name">What it is. Appears in the diff, so it should say enough to act on.</param>
        /// <param name="bytes">What it produced.</param>
        public Listing Case(string name, ReadOnlySpan<byte> bytes) {
            text.Append(name).Append(" = ").Append(Convert.ToHexString(bytes)).Append('\n');

            return this;
        }

        /// <summary>Adds a case whose output is a number rather than bytes.</summary>
        /// <param name="name">What it is.</param>
        /// <param name="value">What it produced.</param>
        public Listing Case(string name, uint value) {
            text.Append(name).Append(" = ").Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');

            return this;
        }

        /// <summary>Adds a case whose output is a float, written by its bits.</summary>
        /// <param name="name">What it is.</param>
        /// <param name="value">What it produced.</param>
        /// <remarks>
        ///     By its bits and not by <c>ToString</c>. A float formatted as text is a float that has
        ///     been through a formatter, and the shortest-round-trippable algorithm is the sort of
        ///     thing that gets improved in a servicing release — which would fail this suite on a
        ///     change to the runtime rather than a change to the wire.
        /// </remarks>
        public Listing Case(string name, float value) {
            text.Append(name)
                .Append(" = ")
                .Append(BitConverter.SingleToUInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture))
                .Append('\n');

            return this;
        }

        /// <summary>Compares what was built against what is committed.</summary>
        /// <param name="name">The fixture's file name, without an extension.</param>
        /// <remarks>
        ///     ⚠ The corpus stays at <c>Wire/__wire__/</c> rather than moving to the
        ///     <c>__golden__/</c> the plan document once named — <see cref="GoldenFile" /> takes the
        ///     path from its caller for exactly this reason, and moving several hundred committed
        ///     files to satisfy a directory name buys nothing a reviewer can see.
        /// </remarks>
        public void Matches(string name) =>
            GoldenFile.Matches(text.ToString(), GoldenFile.InProjectDirectory("Wire", "__wire__", name + ".txt"));
    }
}
