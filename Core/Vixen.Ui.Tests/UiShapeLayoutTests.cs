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
///         <see cref="UiShape" />, <c>Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn</c>,
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
///     <para>
///         ⚠ <b>What this does <i>not</i> cover, stated because it was learned the expensive way.</b>
///         Six files have to agree about this layout and this test sees two of them. It says nothing
///         about how a host <i>sizes a buffer</i> around the record — <c>UiRenderer</c> spelled the
///         stride <c>80</c> in three places and every assertion here stayed green — and nothing about
///         the one hand-maintained GLSL copy of the box shader left, under
///         <c>Vixen.Graphics.Golden.Tests</c>. Both were caught by that suite on a real device
///         instead.
///         Passing this file is necessary and is not sufficient.
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
        ("Stops", "stops"),
        ("Paint", "paint"),
        ("Area", "area"),
        ("Inset", "inset")
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

    /// <summary>The one hand-written GLSL copy declares the same record, lane for lane and in order.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The copy this file's own remark says it does not cover, and it is the copy the
    ///         defect shipped in.</b> When the record grew 80 → 112 the host wrote 112-byte records
    ///         into a buffer sized for 80 and each shader indexed at the old stride, so every box
    ///         after the first read the previous record's tail — plausible rounded rectangles with
    ///         the wrong radii. What caught it was <c>Vixen.Graphics.Golden.Tests</c> on a real
    ///         device, which is the most expensive instrument in the tree and the one that does not
    ///         run on most machines.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is invisible to a search for the type, because it calls the struct
    ///         <c>Shape</c>.</b> That is why a census by grep kept reporting the GLSL copies as
    ///         missing, and it is why this is a parse rather than a name lookup.
    ///     </para>
    ///     <para>
    ///         <b>What this is not.</b> It does not compile the file — <c>TestShaders.cs</c> records
    ///         the decision not to require <c>glslc</c> on every CI leg, and compiling would prove
    ///         only that the text is legal GLSL, not that it agrees with this record. Nor does it
    ///         compare the GLSL with the Raven the shipping application draws through: those are two
    ///         implementations of one specification in two languages and only a picture rendered
    ///         through each can compare them, which regenerates every reference image in that suite
    ///         and belongs on its own (#286). What it pins is the one claim a text comparison can
    ///         make exactly — the <i>record</i> the two sides index — and it is the claim that broke.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Ordered, not a set.</b> An <c>std430</c> lane's offset is its position, so two
    ///         lanes swapped is the same struct to any comparison by name and a different byte at
    ///         every read. And the type of each lane is asserted rather than assumed from the count:
    ///         a <c>float</c> where a <c>vec4</c> belongs is the specific mistake the packing rules
    ///         make invisible.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_one_hand_written_GLSL_copy_declares_the_same_record() {
        var path = GoldenBoxShaderPath();
        var declared = GlslLanes(File.ReadAllText(path), "Shape");

        // Ordered and whole: a missing lane, an extra one, a rename or a swap is all one failure
        // here, and an empty parse — a renamed struct, a moved file, a rewritten declaration — fails
        // as loudly as any of them rather than agreeing with itself.
        Assert.Equal(Lanes.Select(lane => lane.Field), declared);
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
            blur: 7f,
            paintCentre: new Vector2(31f, 32f),
            paintExtent: new Vector2(33f, 34f),
            areaCentre: new Vector2(41f, 42f),
            areaHalf: new Vector2(43f, -44f)
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

        // ⚠ The negative is not a typo and is the one lane on this record whose sign carries meaning:
        // `area.w` below zero is `background-repeat: no-repeat` down the vertical axis. A constructor
        // that took the absolute value on the way in would tile a layer that asked not to be, and
        // every other assertion in this file would stay green.
        AssertLane(floats, members["paint"], [31f, 32f, 33f, 34f]);
        AssertLane(floats, members["area"], [41f, 42f, 43f, -44f]);
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

        // ⚠ And the two lanes added after it are zero, which is what the second growth relied on:
        // `paint.zw` of zero is "the ramp is the box" and `area.zw` of zero is "the tile is the box,
        // do not tile and do not clip". A record that defaulted either to the box's own half size
        // would say the same thing in a way the shader's fast-path guard could not recognise, and
        // every gradient in the interface would take the tiling branch to arrive where it started.
        Assert.Equal(Vector4.Zero, shape.Paint);
        Assert.Equal(Vector4.Zero, shape.Area);
        Assert.Equal(Vector4.Zero, flat.Paint);
        Assert.Equal(Vector4.Zero, flat.Area);
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

    /// <summary>The interface's shader directory, found the way the golden suite finds it.</summary>
    /// <remarks>
    ///     ⚠ This read the editor's own copy under <c>Editor/Vixen.Editor.Host/Shaders</c> until that
    ///     copy was deleted. There is one <c>Ui.rvn</c> now, and it is the one every application
    ///     including the editor draws with.
    /// </remarks>
    static string ReflectionPath() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            var candidate = Path.Combine(
                directory.FullName, "Platform", "Vixen.Ui.Desktop", "Shaders", "UiBox.reflect.json"
            );

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Platform/Vixen.Ui.Desktop/Shaders/UiBox.reflect.json was not found above "
            + $"'{AppContext.BaseDirectory}'."
        );
    }

    /// <summary>The <c>vec4</c> lanes a GLSL struct declares, in declaration order.</summary>
    /// <param name="source">The shader text.</param>
    /// <param name="name">The struct's name in that file, which is not the C# type's.</param>
    /// <returns>The lane names, in order.</returns>
    /// <remarks>
    ///     ⚠ A lane that is not a <c>vec4</c> throws rather than being skipped, for the reason the
    ///     size assertion above exists: a skipped lane is a shorter list, and a shorter list read as
    ///     "this side has fewer lanes" is a true failure reported as the wrong one.
    /// </remarks>
    static List<string> GlslLanes(string source, string name) {
        var lanes = new List<string>();
        var at = source.IndexOf($"struct {name} {{", StringComparison.Ordinal);

        if (at < 0) {
            return lanes;
        }

        var body = source[(source.IndexOf('{', at) + 1)..];
        body = body[..body.IndexOf('}', StringComparison.Ordinal)];

        foreach (var line in body.Split('\n')) {
            // Everything after `//` is prose, and every lane in this struct carries some.
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            var declaration = (comment < 0 ? line : line[..comment]).Trim().TrimEnd(';').Trim();

            if (declaration.Length == 0) {
                continue;
            }

            var parts = declaration.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(2, parts.Length);
            Assert.Equal("vec4", parts[0]);

            lanes.Add(parts[1]);
        }

        return lanes;
    }

    /// <summary>The golden suite's hand-written copy of the box shader.</summary>
    static string GoldenBoxShaderPath() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            var candidate = Path.Combine(
                directory.FullName, "Platform", "Vixen.Graphics.Golden.Tests", "Shaders", "ui-box.frag"
            );

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Platform/Vixen.Graphics.Golden.Tests/Shaders/ui-box.frag was not found above "
            + $"'{AppContext.BaseDirectory}'. It is the last hand-maintained copy of this record, and "
            + "a test that cannot find it must say so rather than pass."
        );
    }

    record struct Member(int Offset, int Size);
}
