// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Doc 19 § L2's filler B, rendering its own cubes on a device.
/// </summary>
/// <remarks>
///     <para>
///         <b>The half that was owed.</b> <c>CapturedIrradianceFillerTests</c> checks the projection
///         against arithmetic with nothing rendered; this renders. Until it did, doc 19 § 7's promise
///         to WebGL2 — the same lighting model at a different update rate — was a claim about a type
///         that had no way to produce its input.
///     </para>
///     <para>
///         <b>One emissive quad in a generic direction, and that is the whole design of the test.</b>
///         A uniform environment pins down the constant coefficient and nothing else, so it cannot
///         tell a correct capture from one whose faces are permuted, mirrored within a face, or
///         transposed — every one of those integrates to the same constant. A single bright quad off
///         to one side makes the three linear coefficients into a <i>direction</i>, and a direction
///         disagrees with all of them at once.
///     </para>
///     <para>
///         Nothing here uses the material system. <c>line.vert</c> takes a world position and a
///         view-projection push constant, which is exactly what a cube face needs, and the fragment
///         stage writes the vertex colour — so what a probe sees is decided by where the quads are
///         rather than by a shading model that would have to be set up correctly first.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class IrradianceCaptureDeviceTests {
    /// <summary>How bright the quad is.</summary>
    /// <remarks>
    ///     Above one, which an 8-bit target could not carry. That is deliberate: the capture's colour
    ///     target is <c>Rgba32Float</c> precisely so a bake is not clamped, and a value of two comes
    ///     back as one through a target that is not.
    /// </remarks>
    const float Emissive = 2f;

    /// <summary>How far the quad is from the probe, and how big it is.</summary>
    const float Distance = 4f;

    const float Half = 1.5f;

    /// <summary>Where the light comes from, as seen from the origin.</summary>
    /// <remarks>
    ///     All three components nonzero and all three different, so a swap of any two axes moves the
    ///     answer — a direction like <c>+X</c> would survive four of the six face permutations.
    /// </remarks>
    static Vector3 Toward => Vector3.Normalize(new(0.6f, 0.3f, -0.74f));

    /// <summary>
    ///     A probe with one bright quad beside it comes back lit from that direction.
    /// </summary>
    [Fact]
    public void TheCaptureSeesTheQuadWhereTheQuadIs() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var probe = Capture(owned, Vector3.Zero, out var capture, out var source);

        Assert.Equal(1, source);

        // The linear band as a vector: L1m1 is Y, L10 is Z, L11 is X — see IrradianceProbe.Irradiance.
        var linear = new Vector3(probe.Radiance.L11.X, probe.Radiance.L1m1.X, probe.Radiance.L10.X);

        Assert.True(linear.Length() > 1e-3f, $"the capture had no direction in it at all: {linear}");

        var lit = Vector3.Normalize(linear);

        // ⚠ The assertion the whole fixture exists for. A permuted face order, a mirrored face, or a
        // transposed readback all leave the constant band untouched and point this somewhere else.
        Assert.True(
            Vector3.Dot(lit, Toward) > 0.9f,
            $"the light came from {lit} and the quad is at {Toward}"
        );

        // And a surface facing the quad receives more than one facing away, which is what a linear
        // band means before it is a vector.
        var facing = probe.Irradiance(Toward).X;
        var away = probe.Irradiance(-Toward).X;

        Assert.True(facing > away * 2f, $"facing the light gave {facing} and facing away gave {away}");

        // Nothing else was drawn, so every other direction is the sky — which is black here, and a
        // probe in the open.
        Assert.Equal(1f, capture.Validity, 0.01f);
    }

    /// <summary>
    ///     <b>And the radiance is not clamped, because a bake's target is floating point.</b>
    /// </summary>
    /// <remarks>
    ///     The quad is brighter than one and covers a known solid angle, so what the constant band
    ///     holds is an integral this test can write down: radiance times the fraction of the sphere
    ///     the quad covers. An 8-bit target would answer half of it, which is a number close enough
    ///     to right to be mistaken for the quadrature.
    /// </remarks>
    [Fact]
    public void TheCaptureCarriesRadianceAboveOne() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var probe = Capture(owned, Vector3.Zero, out var capture, out _);

        // The average radiance over the sphere, which is the constant coefficient read back through
        // its own basis function. A quad of solid angle Ω at radiance L averages LΩ/4π.
        var average = probe.Radiance.L00.X * 0.282095f;
        var solid = SolidAngle();

        Assert.Equal(Emissive * solid / (4f * MathF.PI), average, 0.01f);

        // ⚠ And it is above what a byte could have carried. Without this the test passes for a
        // capture whose every texel was clamped to one, because the quad is small enough that the
        // average lands under one either way.
        Assert.True(Peak(capture) > 1.5f, $"the brightest texel came back at {Peak(capture)}");
    }

    /// <summary>
    ///     <b>And a probe walled in on every side is invalid.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The number that keeps a buried probe from lighting the room it is buried under. Six
    ///         quads at a hand's width, which is nearer than <c>MinimumDistance</c> in every
    ///         direction, so the capture is entirely geometry at no distance — a probe inside a wall.
    ///     </para>
    ///     <para>
    ///         Asserted against the same quads moved out to room scale, because "validity is zero
    ///         when I make it zero" says nothing on its own: the same six quads far away are a room,
    ///         and a room is a place a probe belongs.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AProbeInsideGeometryIsInvalidAndOneInARoomIsNot() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        Capture(owned, Vector3.Zero, out var buried, out _, Box(0.05f));

        Assert.True(buried.Validity < 0.05f, $"a probe inside a wall came back {buried.Validity} valid");

        Capture(owned, Vector3.Zero, out var room, out _, Box(3f));

        Assert.True(room.Validity > 0.95f, $"a probe in a room came back only {room.Validity} valid");
    }

    /// <summary>
    ///     <b>And the sun is shadowed by what stands between the probe and it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The second of the three numbers a capture carries, and the one with nowhere else to
    ///         come from: the radiance cube says what arrives, and says nothing about whether the
    ///         directional light does — a shadowed probe and an unlit one look identical in the
    ///         coefficients.
    ///     </para>
    ///     <para>
    ///         The same quad, and the sun moved rather than the geometry. Both directions in one test
    ///         because either alone is satisfied by a constant: a tap that always answers "shadowed"
    ///         passes the first half, and one that always answers "lit" passes the second.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheSunIsShadowedByWhatStandsInFrontOfIt() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        Capture(owned, Vector3.Zero, out var behind, out _, sun: Toward);
        Capture(owned, Vector3.Zero, out var clear, out _, sun: -Toward);

        Assert.Equal(0f, behind.SunShadow, 0.01f);
        Assert.Equal(1f, clear.SunShadow, 0.01f);
    }

    /// <summary>
    ///     <b>And a whole field, baked by rendering, is the field the tracer would have traced.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The end of doc 19 § L2's second path, and the assertion doc 19 § 7 rests on: a target
    ///         with no compute fills the same bricks to the same numbers, at build time instead of per
    ///         frame. Sixty-four probes, each a submit and a stall — which is what a bake is.
    ///     </para>
    ///     <para>
    ///         An empty world under a sky of radiance <i>L</i>, because that is the one environment
    ///         with a closed form for every probe at once: a uniform environment lights every surface
    ///         with exactly <i>L</i> whichever way it faces, so the whole field should read <i>L</i>
    ///         and any probe that does not is a probe the walk addressed wrongly.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AFieldBakedByRenderingHoldsTheSkyItWasBakedUnder() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        const float Radiance = 0.75f;

        var field = new IrradianceField(new BoundingBox(new(-2f), new(2f)), new(1));

        field.AllocateAll();

        using var source = Source(owned, [], new(Radiance, Radiance, Radiance, 1f), Vector3.UnitY);
        var filler = new CapturedIrradianceFiller(source);

        Assert.Equal(field.BrickCount, filler.Fill(field));
        Assert.Equal(0, filler.Skipped);
        Assert.Equal(64 * field.BrickCount, source.Captured);

        // The pair the filler's own remarks insist on, in this order: a border is a copy, so copying
        // before the original is repaired copies the hole.
        field.Dilate();
        field.SyncBorders();

        foreach (var brick in field.Bricks) {
            for (var z = 0; z < IrradianceBrickPool.BrickResolution; z++) {
                for (var y = 0; y < IrradianceBrickPool.BrickResolution; y++) {
                    for (var x = 0; x < IrradianceBrickPool.BrickResolution; x++) {
                        var probe = field.GetProbe(brick, x, y, z);

                        Assert.Equal(Radiance, probe.Irradiance(Vector3.UnitY).X, 0.02f);
                        Assert.Equal(1f, probe.Validity, 0.01f);
                    }
                }
            }
        }
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>Captures one probe and projects it, with a scene of world-space triangles.</summary>
    static IrradianceProbe Capture(
        Fixture fixture,
        Vector3 position,
        out IrradianceCapture capture,
        out int captured,
        Vertex[]? scene = null,
        Vector3? sun = null
    ) {
        using var source = Source(fixture, scene ?? Quad(Toward * Distance, Emissive), default, sun ?? Vector3.UnitY);

        VulkanDiagnostics.Reset();

        Assert.True(source.TryCapture(position, out capture));

        captured = source.Captured;

        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "The capture produced validation errors, so what it read back is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        return new CapturedIrradianceFiller(source).Project(capture, IrradianceProbe.Empty);
    }

    /// <summary>A capture source over a scene of world-space triangles.</summary>
    static RenderedIrradianceCaptures Source(Fixture fixture, Vertex[] triangles, Color4 sky, Vector3 sun) {
        var device = fixture.Device;

        var pipeline = fixture.Pipeline(
            fixture.Shader("line.vert.spv", ShaderStage.Vertex),
            fixture.Shader("line.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,

            // Reversed, like everything else the engine draws with — see DepthStencilAttachment.
            new DepthStencilState { DepthTest = true, DepthWrite = true, DepthCompare = CompareFunction.Greater },
            [new VertexBufferLayout(Vertex.Stride, [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X4, 12)])],
            pushConstantBytes: 64,

            // ⚠ Two-sided, and this is the line that decides whether a room exists. A probe standing
            // in one sees its inside faces, which are back faces — culled, the room vanishes and
            // every probe in the level reports an open sky. See IrradianceCubeCapture's remarks.
            rasterizer: RasterizerState.TwoSided,
            targets: [new ColourTargetState(PixelFormat.Rgba32Float, BlendState.Opaque)]
        );

        var buffer = triangles.Length > 0 ? fixture.Buffer<Vertex>(triangles, BufferUsage.Vertex) : default;

        var cube = new IrradianceCubeCapture(device) {
            Size = 32,
            Range = 100f,
            MinimumDistance = 0.5f,
            Sky = sky,
            SunDirection = sun
        };

        return new(
            device,
            cube,
            (commands, _, viewProjection) => {
                if (triangles.Length == 0) {
                    return;
                }

                commands.BindPipeline(pipeline);
                commands.PushConstants(ShaderStage.Vertex, 0, MemoryMarshal.AsBytes([viewProjection]));
                commands.BindVertexBuffer(0, buffer);
                commands.Draw(triangles.Length, 1);
            }
        );
    }

    /// <summary>The brightest texel of a capture, which says whether anything clamped.</summary>
    static float Peak(in IrradianceCapture capture) {
        var peak = 0f;

        foreach (var texel in capture.Radiance.Pixels) {
            peak = MathF.Max(peak, texel.X);
        }

        return peak;
    }

    /// <summary>The solid angle the quad covers from the origin.</summary>
    /// <remarks>
    ///     The same closed form <see cref="CubeMapping.SolidAngle" /> uses, which applies here because
    ///     the quad is square, centred on its own normal from the probe, and perpendicular to it —
    ///     the arrangement <c>Quad</c> builds.
    /// </remarks>
    static float SolidAngle() => CubeMapping.SolidAngle(-Half / Distance, -Half / Distance, 2f * Half / Distance);

    /// <summary>Two triangles facing the origin, at a distance, with a colour.</summary>
    static Vertex[] Quad(Vector3 centre, float radiance) {
        var normal = Vector3.Normalize(centre);
        var away = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(away, normal)) * Half;
        var up = Vector3.Normalize(Vector3.Cross(normal, right)) * Half;

        var a = centre - right - up;
        var b = centre + right - up;
        var c = centre - right + up;
        var d = centre + right + up;

        return [
            new(a, radiance), new(b, radiance), new(c, radiance),
            new(c, radiance), new(b, radiance), new(d, radiance)
        ];
    }

    /// <summary>Six quads around the origin at a distance — a wall or a room, depending.</summary>
    static Vertex[] Box(float distance) {
        var vertices = new List<Vertex>();

        foreach (var axis in new[] {
            Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ
        }) {
            // Wider than the distance, so the six overlap at the corners and leave no gap for a
            // direction to escape through — a leak here would show up as validity nobody intended.
            vertices.AddRange(Sized(axis * distance, distance * 3f, 0.25f));
        }

        return [.. vertices];
    }

    /// <summary>A quad of a given half-extent, facing the origin.</summary>
    static Vertex[] Sized(Vector3 centre, float half, float radiance) {
        var normal = Vector3.Normalize(centre);
        var away = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(away, normal)) * half;
        var up = Vector3.Normalize(Vector3.Cross(normal, right)) * half;

        var a = centre - right - up;
        var b = centre + right - up;
        var c = centre - right + up;
        var d = centre + right + up;

        return [
            new(a, radiance), new(b, radiance), new(c, radiance),
            new(c, radiance), new(b, radiance), new(d, radiance)
        ];
    }

    /// <summary>What <c>line.vert</c> reads: a world position and a colour.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex(Vector3 position, float radiance) {
        public const int Stride = 28;

        public Vector3 Position = position;
        public Vector4 Colour = new(radiance, radiance, radiance, 1f);
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");

        return false;
    }
}
