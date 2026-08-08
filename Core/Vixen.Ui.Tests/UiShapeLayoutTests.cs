// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     <see cref="UiShape" /> against the reflection of the shader that reads it, field by field.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap this closes: nothing checked, and the failure does not look like a failure.</b>
///         The record is copied into a storage buffer with <c>MemoryMarshal</c> and read back by
///         <c>UiBox</c>, whose alignment rules are not C#'s. Four files have to agree —
///         <see cref="UiShape" />, <c>Editor/Vixen.Editor.Host/Shaders/Ui.rvn</c>,
///         <c>SoftwareUiRasterizer</c>, and the committed <c>UiBox.frag.spv</c> and
///         <c>UiBox.reflect.json</c> beside the shader source. A disagreement between the first and
///         the last is not a compile error and not an exception: it is a box drawn with another box's
///         parameters, which looks like a bug in the geometry.
///     </para>
///     <para>
///         ⚠ <b>And in this record's case it does not even look wrong.</b> Growing it from eighty
///         bytes to a hundred and twelve appended two lanes and repurposed two that were previously
///         zero — <c>size.w</c>'s gradient flag became the shape, whose <c>1</c> is
///         <see cref="GradientShape.Linear" />, and <c>axis.w</c>'s declared padding became the
///         interpolation space, whose <c>0</c> is <see cref="GradientSpace.Linear" />. So a stale
///         module still draws two-stop linear gradients perfectly and silently ignores everything
///         from offset eighty on. There is no garbage frame to notice. This test is the notice.
///     </para>
///     <para>
///         ⚠ <b>Read from the committed reflection rather than written down here.</b> A second copy of
///         the offsets in a test file is a third thing to keep in step, and it would agree with itself
///         while disagreeing with the module the editor actually loads. The <c>.reflect.json</c> is
///         what the compiler emitted from the <c>.rvn</c> that produced the <c>.spv</c>, so pinning
///         against it pins against the binary. What it cannot catch on its own is a <c>.rvn</c> edited
///         and never recompiled — <c>./build.sh CheckShaders</c> is that half, and the two together
///         close the loop.
///     </para>
/// </remarks>
public class UiShapeLayoutTests {
    /// <summary>The C# property, and the shader field it has to sit on top of, in order.</summary>
    static readonly (string Property, string Field)[] Lanes = [
        ("Size", "size"),
        ("RadiiX", "radiiX"),
        ("RadiiY", "radiiY"),
        ("Axis", "axis"),
        ("End", "endColour"),
        ("Mid", "midColour"),
        ("Stops", "stops")
    ];

    [Fact]
    public void The_record_is_the_size_the_shader_was_compiled_against() {
        Assert.Equal(Reflected().Size, Marshal.SizeOf<UiShape>());
    }

    [Fact]
    public void Every_lane_sits_at_the_offset_the_shader_reads_it_from() {
        var members = Reflected().Members;

        foreach (var (property, field) in Lanes) {
            Assert.True(
                members.TryGetValue(field, out var member),
                $"`Ui.rvn`'s UiShape has no `{field}`, which `UiShape.{property}` is supposed to be."
            );

            Assert.Equal(member.Offset, OffsetOf(property));

            // Every lane is a `float4` on both sides. A scalar beside a vector is the specific
            // mistake std430 and sequential layout disagree about, so its size is worth asserting
            // rather than assuming from the type name.
            Assert.Equal(16, member.Size);
        }
    }

    /// <summary>
    ///     A field on one side and not the other, which is the shape a half-finished change leaves.
    /// </summary>
    /// <remarks>
    ///     ⚠ Counted rather than only looked up, because <see cref="Lanes" /> is written by hand and a
    ///     lane added to both the shader and the record but not to that table would slip past every
    ///     other assertion here.
    /// </remarks>
    [Fact]
    public void Neither_side_has_a_lane_the_other_does_not() {
        Assert.Equal(
            Lanes.Select(lane => lane.Field).Order(StringComparer.Ordinal),
            Reflected().Members.Keys.Order(StringComparer.Ordinal)
        );

        Assert.Equal(Lanes.Length * 16, Marshal.SizeOf<UiShape>());
    }

    /// <summary>
    ///     The bytes a shape actually serialises to, read back the way the shader indexes them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The offsets are one claim and what is <i>at</i> them is another.</b> Two lanes could be
    ///     the right size at the right place and still be filled in the wrong order by the
    ///     constructor — swapping <c>midColour</c> and <c>stops</c> keeps every assertion above green
    ///     and paints a gradient whose middle colour is four stop positions. So this writes a shape
    ///     whose every float is distinguishable and checks the bytes, which is the thing
    ///     <c>MemoryMarshal</c> will hand the GPU.
    /// </remarks>
    [Fact]
    public void The_bytes_carry_what_the_constructor_was_given() {
        var shape = new UiShape(
            new Vector2(10f, 20f),
            3f,
            CornerRadii.Circular(1f, 2f, 3f, 4f),
            GradientShape.Conic,
            GradientSpace.Oklab,
            new Vector2(0.5f, -0.25f),
            new Color4(0.11f, 0.12f, 0.13f, 0.14f),
            new Color4(0.21f, 0.22f, 0.23f, 0.24f),
            hasVia: true,
            new GradientStops(0.1f, 0.4f, 0.9f),
            blur: 7f
        );

        var floats = MemoryMarshal.Cast<UiShape, float>(MemoryMarshal.CreateReadOnlySpan(ref shape, 1));
        var members = Reflected().Members;

        // The four radii are stored across two lanes rather than in the pair order they were given —
        // clockwise from the top left, horizontal in one and vertical in the other — so a record that
        // wrote them as pairs would round-trip through `CornerRadii` and draw the wrong corners.
        AssertLane(floats, members["size"], [10f, 20f, 3f, (float) GradientShape.Conic]);
        AssertLane(floats, members["radiiX"], [1f, 2f, 3f, 4f]);
        AssertLane(floats, members["radiiY"], [1f, 2f, 3f, 4f]);
        AssertLane(floats, members["axis"], [0.5f, -0.25f, 7f, (float) GradientSpace.Oklab]);
        AssertLane(floats, members["endColour"], [0.11f, 0.12f, 0.13f, 0.14f]);
        AssertLane(floats, members["midColour"], [0.21f, 0.22f, 0.23f, 0.24f]);
        AssertLane(floats, members["stops"], [0.1f, 0.4f, 0.9f, 1f]);
    }

    /// <summary>The two lanes whose zero the growth relied on meaning what it used to mean.</summary>
    /// <remarks>
    ///     ⚠ This is the assertion that says the forty-three committed screenshots were allowed not to
    ///     move. A gradient built the way every caller predating <see cref="GradientShape" /> builds
    ///     one has to serialise to a <c>size.w</c> of exactly one and an <c>axis.w</c> of exactly zero,
    ///     because that is the record the old shader drew from.
    /// </remarks>
    [Fact]
    public void A_gradient_built_the_old_way_still_serialises_the_old_way() {
        var shape = new UiShape(
            new Vector2(4f, 4f),
            0f,
            default,
            new Color4(1f, 1f, 1f, 1f),
            new Vector2(0f, 1f)
        );

        Assert.Equal(1f, shape.Size.W);
        Assert.Equal(0f, shape.Axis.W);

        // And with no axis at all it is a flat fill, which is the same zero the flag had.
        var flat = new UiShape(new Vector2(4f, 4f), 0f, default, default, Vector2.Zero);

        Assert.Equal(0f, flat.Size.W);
    }

    static void AssertLane(ReadOnlySpan<float> floats, Member member, float[] expected) {
        var lane = floats.Slice(member.Offset / sizeof(float), 4);

        Assert.Equal(expected, lane.ToArray());
    }

    static int OffsetOf(string property) {
        var field = typeof(UiShape)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                candidate.Name == property || candidate.Name == $"<{property}>k__BackingField"
            );

        Assert.True(field is not null, $"UiShape has no field behind `{property}`.");

        return (int) Marshal.OffsetOf<UiShape>(field!.Name);
    }

    /// <summary>What the committed reflection says the <c>shapes</c> buffer's element looks like.</summary>
    static (int Size, IReadOnlyDictionary<string, Member> Members) Reflected() {
        using var document = JsonDocument.Parse(File.ReadAllText(ReflectionPath()));

        var shapes = document.RootElement
            .GetProperty("Sets")
            .EnumerateArray()
            .SelectMany(set => set.GetProperty("Bindings").EnumerateArray())
            .Single(binding => binding.GetProperty("Name").GetString() == "shapes");

        var members = new Dictionary<string, Member>(StringComparer.Ordinal);

        foreach (var member in shapes.GetProperty("Members").EnumerateArray()) {
            var name = member.GetProperty("Name").GetString()!;

            // The buffer's own entry is the whole struct under the binding's name; the lanes are
            // `shapes.<field>`. Skipping by shape rather than by name keeps this working if the
            // binding is ever renamed.
            if (!name.StartsWith("shapes.", StringComparison.Ordinal)) {
                continue;
            }

            members[name["shapes.".Length..]] = new Member(
                member.GetProperty("Offset").GetInt32(),
                member.GetProperty("Size").GetInt32()
            );
        }

        return (shapes.GetProperty("Size").GetInt32(), members);
    }

    /// <summary>The editor's shader directory, found the way the golden suite finds it.</summary>
    static string ReflectionPath() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            var candidate = Path.Combine(
                directory.FullName, "Editor", "Vixen.Editor.Host", "Shaders", "UiBox.reflect.json"
            );

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Editor/Vixen.Editor.Host/Shaders/UiBox.reflect.json was not found above "
            + $"'{AppContext.BaseDirectory}'."
        );
    }

    record struct Member(int Offset, int Size);
}
