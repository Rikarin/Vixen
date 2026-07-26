// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.Core.Tests;

/// <summary>
///     The four identity types. What matters about all of them is that they render and parse
///     without allocating, and that the text form and the byte form agree — those are the
///     properties every sidecar, catalogue and log line downstream leans on.
/// </summary>
public class IdentityTests {
    [Fact]
    public void An_asset_id_renders_as_32_undelimited_hex_digits() {
        var id = new AssetId(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));

        Assert.Equal("0123456789abcdef0123456789abcdef", id.ToString());
        Assert.Equal(AssetId.TextLength, id.ToString().Length);
    }

    [Fact]
    public void An_asset_id_round_trips_through_its_own_text_form() {
        var id = AssetId.New();

        Assert.True(AssetId.TryParse(id.ToString(), out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void An_asset_id_also_parses_the_dashed_form_a_human_pasted() {
        Assert.True(AssetId.TryParse("01234567-89ab-cdef-0123-456789abcdef", out var parsed));
        Assert.Equal("0123456789abcdef0123456789abcdef", parsed.ToString());
    }

    [Fact]
    public void An_asset_id_formats_into_a_caller_supplied_buffer() {
        var id = AssetId.New();
        Span<char> chars = stackalloc char[AssetId.TextLength];
        Span<byte> utf8 = stackalloc byte[AssetId.TextLength];

        Assert.True(id.TryFormat(chars, out var charsWritten));
        Assert.True(id.TryFormat(utf8, out var bytesWritten));

        Assert.Equal(AssetId.TextLength, charsWritten);
        Assert.Equal(AssetId.TextLength, bytesWritten);
        Assert.Equal(id.ToString(), new(chars));
        Assert.Equal(id.ToString(), Encoding.UTF8.GetString(utf8));
    }

    [Fact]
    public void An_empty_asset_id_says_so() {
        Assert.True(AssetId.Empty.IsEmpty);
        Assert.True(default(AssetId).IsEmpty);
        Assert.False(AssetId.New().IsEmpty);
    }

    [Fact]
    public void Asset_ids_order_by_their_guid() {
        var low = new AssetId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var high = new AssetId(Guid.Parse("00000000-0000-0000-0000-000000000002"));

        var alsoLow = new AssetId(Guid.Parse("00000000-0000-0000-0000-000000000001"));

        Assert.True(low < high);
        Assert.True(high > low);
        Assert.True(low <= alsoLow);
        Assert.True(high >= low);
    }

    [Fact]
    public void An_object_id_reads_and_writes_its_bytes_big_endian() {
        ReadOnlySpan<byte> bytes = [
            0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef,
            0xfe, 0xdc, 0xba, 0x98, 0x76, 0x54, 0x32, 0x10
        ];

        var id = ObjectId.FromBytes(bytes);

        Assert.Equal(0x0123456789abcdefUL, id.High);
        Assert.Equal(0xfedcba9876543210UL, id.Low);

        // The point of big-endian: the hex text reads in the same order as the bytes.
        Assert.Equal("0123456789abcdeffedcba9876543210", id.ToString());

        Span<byte> written = stackalloc byte[ObjectId.SizeInBytes];
        id.WriteTo(written);
        Assert.True(bytes.SequenceEqual(written));
    }

    [Fact]
    public void An_object_id_rejects_a_digest_of_the_wrong_size() {
        Assert.Throws<ArgumentException>(() => ObjectId.FromBytes(new byte[15]));
        Assert.Throws<ArgumentException>(() => ObjectId.FromBytes(new byte[17]));
    }

    [Fact]
    public void An_object_id_round_trips_through_its_text_form_in_either_case() {
        var id = new ObjectId(0xdeadbeefcafef00d, 0x0123456789abcdef);

        Assert.Equal("deadbeefcafef00d0123456789abcdef", id.ToString());
        Assert.Equal("DEADBEEFCAFEF00D0123456789ABCDEF", id.ToString("X", null));
        Assert.Equal(id, ObjectId.Parse(id.ToString()));
        Assert.Equal(id, ObjectId.Parse(id.ToString("X", null)));
    }

    [Fact]
    public void An_object_id_will_not_parse_padded_or_short_text() {
        // Guarding NumberStyles: HexNumber would have accepted a leading space and 31 digits.
        Assert.False(ObjectId.TryParse(" 123456789abcdef0123456789abcdef", out _));
        Assert.False(ObjectId.TryParse("0123456789abcdef0123456789abcde", out _));
        Assert.False(ObjectId.TryParse("0123456789abcdef0123456789abcdef0", out _));
        Assert.False(ObjectId.TryParse("0123456789abcdef0123456789abcdeg", out _));
    }

    [Fact]
    public void An_object_id_declines_a_buffer_that_is_too_short() {
        var id = new ObjectId(1, 2);
        Span<char> tooShort = stackalloc char[ObjectId.TextLength - 1];

        Assert.False(id.TryFormat(tooShort, out var written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Object_ids_order_by_high_half_then_low() {
        Assert.True(new ObjectId(1, 999) < new ObjectId(2, 0));
        Assert.True(new ObjectId(1, 1) < new ObjectId(1, 2));
        Assert.True(new ObjectId(2, 0) >= new ObjectId(2, 0));
    }

    [Fact]
    public void Equal_object_ids_hash_equally() {
        var left = new ObjectId(0xdeadbeefcafef00d, 0x0123456789abcdef);
        var right = ObjectId.FromBytes(Convert.FromHexString("deadbeefcafef00d0123456789abcdef"));

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void An_entity_handle_round_trips_through_its_packed_form() {
        var entity = new Entity(7, 3, 2);

        Assert.Equal(entity, Entity.FromPacked(entity.Packed, entity.WorldId));
        Assert.Equal(7, Entity.FromPacked(entity.Packed).Id);
        Assert.Equal(3, Entity.FromPacked(entity.Packed).Version);
    }

    /// <summary>
    ///     The packed form carries slot and version and not the world, so two entities of different
    ///     worlds can pack identically. That is deliberate — it is a sort key within one world — and
    ///     it is worth pinning, because a caller who packs across worlds gets a silent collision.
    /// </summary>
    [Fact]
    public void The_packed_form_does_not_carry_the_world() {
        Assert.Equal(new Entity(7, 3, 0).Packed, new Entity(7, 3, 9).Packed);
        Assert.NotEqual(new Entity(7, 3, 0), new Entity(7, 3, 9));
    }

    [Fact]
    public void Entities_of_different_worlds_are_not_equal_and_sort_apart() {
        Assert.True(new Entity(1, 1, 0) < new Entity(1, 1, 1));
        Assert.True(new Entity(9, 9, 0) < new Entity(1, 1, 1));
    }

    [Fact]
    public void An_entity_handle_round_trips_through_its_text_form() {
        var entity = new Entity(42, 5, 1);

        Assert.Equal("42:5@1", entity.ToString());
        Assert.True(Entity.TryParse("42:5@1", out var parsed));
        Assert.Equal(entity, parsed);
    }

    [Fact]
    public void An_entity_handle_formats_into_a_caller_supplied_buffer() {
        var entity = new Entity(42, 5, 1);
        Span<char> chars = stackalloc char[Entity.MaxTextLength];
        Span<byte> utf8 = stackalloc byte[Entity.MaxTextLength];

        Assert.True(entity.TryFormat(chars, out var charsWritten));
        Assert.True(entity.TryFormat(utf8, out var bytesWritten));
        Assert.Equal("42:5@1", new(chars[..charsWritten]));
        Assert.Equal("42:5@1", Encoding.UTF8.GetString(utf8[..bytesWritten]));
    }

    [Fact]
    public void The_longest_entity_handle_fits_its_declared_buffer_size() {
        var longest = new Entity(int.MinValue, int.MinValue, short.MinValue);
        Span<char> chars = stackalloc char[Entity.MaxTextLength];

        Assert.True(longest.TryFormat(chars, out var written));
        Assert.Equal(longest.ToString().Length, written);
    }

    [Fact]
    public void Slot_zero_is_the_null_entity_handle() {
        Assert.True(Entity.Null.IsNull);
        Assert.True(default(Entity).IsNull);
        Assert.False(new Entity(1, 0, 0).IsNull);
    }

    [Fact]
    public void A_bad_entity_handle_does_not_parse() {
        Assert.False(Entity.TryParse("42", out _));
        Assert.False(Entity.TryParse("42:5", out _));
        Assert.False(Entity.TryParse("42:@1", out _));
        Assert.False(Entity.TryParse(":5@1", out _));
        Assert.False(Entity.TryParse("-1:5@1", out _));
        Assert.False(Entity.TryParse("42@1:5", out _));
    }

    [Fact]
    public void An_uninitialised_component_type_id_is_invalid() {
        // Ids are assigned from 1 precisely so this holds: a zeroed struct must not alias a
        // real component type.
        Assert.False(default(ComponentTypeId).IsValid);
        Assert.Equal(ComponentTypeId.Invalid, default);
        Assert.True(new ComponentTypeId(1).IsValid);
    }

    [Fact]
    public void Component_type_ids_order_and_round_trip() {
        Assert.True(new ComponentTypeId(1) < new ComponentTypeId(2));
        Assert.Equal("17", new ComponentTypeId(17).ToString());
        Assert.True(ComponentTypeId.TryParse("17", null, out var parsed));
        Assert.Equal(new(17), parsed);
    }
}
