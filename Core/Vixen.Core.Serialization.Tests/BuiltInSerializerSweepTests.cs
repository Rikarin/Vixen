// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Xunit;

namespace Vixen.Core.Serialization.Tests;

/// <summary>
///     Every serializer <c>BuiltInSerializers</c> declares, run once against the edges of its own
///     type — and a claim that this list cannot fall behind that file.
/// </summary>
/// <remarks>
///     <para>
///         <b>The shape <a href="https://github.com/Rikarin/Vixen/issues/338">#338</a> asks for in the
///         serializers, which is an executable claim that a named path is exercised rather than a
///         percentage.</b> ⚠ <b>Twenty of the twenty-five built-ins had never been serialized by this
///         suite</b>, and the reason is that every contract in <c>Contracts.cs</c> is built out of
///         <c>int</c>, <c>float</c>, <c>double</c>, <c>string</c>, an enum and collections of those —
///         so <c>sbyte</c>, <c>ushort</c>, <c>char</c>, <see cref="Half" />, <see cref="decimal" />,
///         <see cref="Guid" />, <see cref="DateTime" />, <see cref="DateTimeOffset" />,
///         <see cref="TimeSpan" />, <c>AssetId</c>, <c>SubAssetId</c>, <c>AssetReference</c>,
///         <c>ObjectId</c>, <c>Entity</c> and <c>ComponentTypeId</c> did not appear in the test
///         project at all. A coverage percentage would have said the assembly was well covered and
///         been right.
///     </para>
///     <para>
///         ⚠ <b>The completeness claim is the half that keeps this from rotting.</b> A table of
///         twenty-five entries is one somebody adds a twenty-sixth serializer beside and does not
///         extend, and nothing would say so — the sweep would go on passing over the twenty-five it
///         knows. <see cref="TheSweepCoversEveryBuiltInSerializerTheFileDeclares" /> reads the nested
///         types back off the assembly instead, so the file itself is the enumeration.
///     </para>
///     <para>
///         The values are the edges rather than samples, because what a hand-written wire form gets
///         wrong is a boundary: a sign lost on a cast, a scale dropped from a
///         <see cref="decimal" />, a <see cref="DateTimeKind" /> that was never written, a negative
///         zero normalised away by a comparison that should have been on bits.
///         <c>SerializationTests</c> already sweeps <c>int</c>, <c>float</c> and <c>string</c> over
///         generated values with CsCheck; this is the other axis — every type, once.
///     </para>
/// </remarks>
public sealed class BuiltInSerializerSweepTests {
    /// <summary>What to write and read back for each type the built-in file declares a serializer for.</summary>
    static readonly IReadOnlyDictionary<Type, Action> Sweeps = new Dictionary<Type, Action> {
        [typeof(bool)] = static () => RoundTrips(true, false),
        [typeof(byte)] = static () => RoundTrips(byte.MinValue, byte.MaxValue, (byte)0x5a),
        [typeof(sbyte)] = static () => RoundTrips(sbyte.MinValue, sbyte.MaxValue, (sbyte)-1),
        [typeof(short)] = static () => RoundTrips(short.MinValue, short.MaxValue, (short)-1),
        [typeof(ushort)] = static () => RoundTrips(ushort.MinValue, ushort.MaxValue, (ushort)0xbeef),
        [typeof(int)] = static () => RoundTrips(int.MinValue, int.MaxValue, -1, 0),
        [typeof(uint)] = static () => RoundTrips(uint.MinValue, uint.MaxValue, 0xdeadbeefu),
        [typeof(long)] = static () => RoundTrips(long.MinValue, long.MaxValue, -1L),
        [typeof(ulong)] = static () => RoundTrips(ulong.MinValue, ulong.MaxValue, 0xdead_beef_feed_faceUL),

        // ⚠ A lone surrogate is the edge here: a char is a UTF-16 code unit and not a rune, so a
        // wire form that went through a string encoder would refuse or replace this one.
        [typeof(char)] = static () => RoundTrips('\0', 'a', '￿', '\ud83d'),

        // The three float widths are compared by bits rather than by value, because that is what
        // the writer promises and because == says -0 equals 0 and NaN equals nothing.
        [typeof(Half)] = static () => {
            RoundTrips(Half.MinValue, Half.MaxValue, Half.Epsilon);
            SameBits(Half.NaN, static value => BitConverter.HalfToUInt16Bits(value));
            SameBits(Half.NegativeZero, static value => BitConverter.HalfToUInt16Bits(value));
        },
        [typeof(float)] = static () => {
            RoundTrips(float.MinValue, float.MaxValue, float.Epsilon, float.PositiveInfinity);
            SameBits(float.NaN, BitConverter.SingleToUInt32Bits);
            SameBits(-0f, BitConverter.SingleToUInt32Bits);
        },
        [typeof(double)] = static () => {
            RoundTrips(double.MinValue, double.MaxValue, double.Epsilon, double.NegativeInfinity);
            SameBits(double.NaN, BitConverter.DoubleToUInt64Bits);
            SameBits(-0d, BitConverter.DoubleToUInt64Bits);
        },

        // ⚠ Scale, not only value: 1.00m and 1m are equal and are different decimals, and the
        // writer's four `decimal.GetBits` words are what keeps them different. `Assert.Equal` on
        // decimals would pass on a serializer that dropped the scale entirely.
        [typeof(decimal)] = static () => {
            RoundTrips(decimal.MinValue, decimal.MaxValue, decimal.MinusOne, 0.1m);
            Assert.Equal(decimal.GetBits(1.00m), decimal.GetBits(RoundTrip(1.00m)));
        },

        [typeof(string)] = static () => RoundTrips(string.Empty, "a", "ünïcödé", new string('x', 1024)),
        [typeof(Guid)] = static () => RoundTrips(Guid.Empty, Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")),

        // ⚠ The kind is written as a byte beside the ticks, so all three of them have to come back —
        // a DateTime whose kind is lost is a timestamp that shifts by the machine's offset the next
        // time somebody converts it. ⚠ And `Assert.Equal` cannot see that at all: `DateTime.Equals`
        // compares ticks and ignores `Kind`, so a writer that stamped every value `Utc` passes a
        // plain round-trip assertion. Found by sabotage, which is the only way that is ever found.
        [typeof(DateTime)] = static () => {
            RoundTrips(DateTime.MinValue, DateTime.MaxValue);

            foreach (var kind in Enum.GetValues<DateTimeKind>()) {
                var written = new DateTime(2026, 9, 5, 13, 45, 30, kind);
                var read = RoundTrip(written);

                Assert.Equal(written.Ticks, read.Ticks);
                Assert.Equal(kind, read.Kind);
            }
        },

        // ⚠ The same trap one type along, and worse: `DateTimeOffset.Equals` compares the *instant*,
        // so a value read back with the offset folded into the clock time is equal to the one that
        // was written. `EqualsExact` is the comparison that reads both halves.
        [typeof(DateTimeOffset)] = static () => {
            RoundTrips(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

            foreach (var offset in new[] { TimeSpan.FromHours(-7.5), TimeSpan.Zero, TimeSpan.FromHours(14) }) {
                var written = new DateTimeOffset(2026, 9, 5, 13, 45, 30, offset);

                Assert.True(written.EqualsExact(RoundTrip(written)), $"the {offset} offset did not survive");
            }
        },
        [typeof(TimeSpan)] = static () => RoundTrips(TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.FromTicks(-1)),

        [typeof(AssetId)] = static () => RoundTrips(default, new AssetId(Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"))),
        [typeof(SubAssetId)] = static () => RoundTrips(default, new SubAssetId(uint.MaxValue)),
        [typeof(AssetReference)] = static () => RoundTrips(
            default,
            new AssetReference(new(Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")), new(7u))
        ),

        // Both halves, in order: an id written high-then-low and read low-then-high is a perfectly
        // symmetrical bug that only an asymmetric value can see.
        [typeof(ObjectId)] = static () => RoundTrips(default, new ObjectId(1UL, 2UL), new ObjectId(ulong.MaxValue, 0UL)),

        // The world id is the half that is easy to drop: it is a second field on a struct whose
        // packed form already looks complete.
        [typeof(Entity)] = static () => RoundTrips(
            default,
            Entity.FromPacked(0xdead_beef_feed_faceUL),
            Entity.FromPacked(1UL, 3),
            Entity.FromPacked(1UL, -1)
        ),

        // Variable-length, so the wide values are the ones that exercise the continuation bytes.
        [typeof(ComponentTypeId)] = static () => RoundTrips(default, new ComponentTypeId(1), new ComponentTypeId(int.MaxValue))
    };

    /// <summary>Every built-in serializer round-trips the edges of the type it is for.</summary>
    [Fact]
    public void EveryBuiltInSerializerRoundTripsTheEdgesOfItsType() {
        foreach (var (type, sweep) in Sweeps) {
            Assert.True(
                SerializerRegistry.IsRegistered(type),
                $"{type.Name} is swept here and is not registered, so the sweep is over a serializer "
                + "the registry would never hand out."
            );

            sweep();
        }
    }

    /// <summary>
    ///     ⚠ The table above is the file's list, read back off the assembly rather than retyped.
    /// </summary>
    /// <remarks>
    ///     Both directions, and the second is not symmetry for its own sake: an entry here for a
    ///     serializer that has been deleted is a sweep passing over a registration that no longer
    ///     happens, which reads exactly like coverage and is not.
    /// </remarks>
    [Fact]
    public void TheSweepCoversEveryBuiltInSerializerTheFileDeclares() {
        var builtIns = typeof(SerializerRegistry).Assembly.GetType(
            "Vixen.Core.Serialization.BuiltInSerializers",
            throwOnError: true
        )!;

        var declared = builtIns
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Select(SerializedType)
            .OfType<Type>()
            .ToList();

        // ⚠ The instrument's own check, and the reason it is here rather than implied: a rename of
        // the nested types, or of the class, would leave an empty enumeration — and an empty
        // enumeration satisfies every "for each declared" assertion below without running one.
        Assert.NotEmpty(declared);

        foreach (var type in declared) {
            Assert.True(
                Sweeps.ContainsKey(type),
                $"BuiltInSerializers declares a serializer for {type.Name} and this sweep does not "
                + "cover it, so nothing in this suite ever writes one."
            );
        }

        foreach (var type in Sweeps.Keys) {
            Assert.Contains(type, declared);
        }
    }

    /// <summary>The <c>T</c> of a <c>DataSerializer&lt;T&gt;</c>, or null for anything else.</summary>
    static Type? SerializedType(Type candidate) {
        for (var type = candidate.BaseType; type is not null; type = type.BaseType) {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DataSerializer<>)) {
                return type.GenericTypeArguments[0];
            }
        }

        return null;
    }

    static void RoundTrips<T>(params T[] values) {
        foreach (var value in values) {
            Assert.Equal(value, RoundTrip(value));
        }
    }

    /// <summary>
    ///     A round trip judged on bits, for the values whose equality operator lies about them.
    /// </summary>
    static void SameBits<T, TBits>(T value, Func<T, TBits> bits) => Assert.Equal(bits(value), bits(RoundTrip(value)));

    static T RoundTrip<T>(T value) => Serializer.Read<T>(Serializer.ToBytes(value));
}
