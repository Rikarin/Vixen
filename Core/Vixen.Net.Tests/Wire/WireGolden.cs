// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Xunit;

namespace Vixen.Net.Tests.Wire;

/// <summary>Compares an encoding against the bytes committed for it.</summary>
/// <remarks>
///     <para>
///         Same shape as Raven's golden tests and honouring the same <c>UPDATE_GOLDEN=1</c>, because
///         a repository with two golden conventions has one convention and one trap.
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
        public void Matches(string name) {
            var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Wire", "__wire__", name + ".txt");
            var actual = text.ToString();

            if (Update || !File.Exists(path)) {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, actual);

                Assert.Fail($"Golden '{name}.txt' was (re)generated. Read the diff — this is a wire format change — and re-run.");
            }

            var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

            if (!string.Equals(expected, actual, StringComparison.Ordinal)) {
                File.WriteAllText(path + ".actual", actual);
            }

            Assert.Equal(expected, actual);
        }

        static bool Update => Environment.GetEnvironmentVariable("UPDATE_GOLDEN") is "1" or "true";
    }
}
