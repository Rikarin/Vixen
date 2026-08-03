// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The editor's gizmo-handle path classifies the outside of a shape as front-facing.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the question the front-face inversion was invented to answer, pinned on the
///         device this time.</b> The mapping in <c>VulkanEnums.ToVulkan(FrontFace)</c> was once
///         inverted on the claim that the editor's gizmo heads shaded as though lit from inside —
///         an argument the <c>cull-back</c>/<c>cull-front</c> references refuted for the engine at
///         large, but nothing refuted for the editor's own path, because no automated test drew a
///         gizmo head on a real device. This one does, so the next "the gizmo looks inside-out" has
///         a fixture to argue with instead of the enum.
///     </para>
///     <para>
///         The path is the editor's exactly: the <em>committed</em> <c>Mesh.vert.spv</c> and
///         <c>Mesh.frag.spv</c> the editor embeds (loaded from the repo, the same way
///         <see cref="RavenEffects" /> reads <c>Raven/Library</c>); attribute locations off
///         <c>Mesh.reflect.json</c> rather than written down; <c>MeshRenderer</c>'s two-sided
///         rasterizer and premultiplied blend; and geometry through the same maths as
///         <c>GizmoGeometry</c> — a <see cref="MeshPrimitives.Cube" /> in an arm-aligned frame with
///         normals through the inverse transpose.
///     </para>
///     <para>
///         The discriminator is binary by construction. The light travels straight from the camera,
///         so the face looking at it has a Lambert of one — full colour — while a face whose normal
///         the shader flipped has a Lambert of zero and comes back at the ambient term alone.
///         <c>Mesh.rvn</c> flips the normal exactly when <c>SV_IsFrontFace</c> is false, so a bright
///         pixel is the rasteriser saying "outside is front" and a dim one is it saying the opposite.
///         Nothing in between is possible for a face square to the camera.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class EditorGizmoFacingDeviceTests {
    const float Ambient = 0.35f;

    static readonly Color4 Colour = new(0.8f, 0.3f, 0.1f, 1f);

    [Fact]
    public void TheOutsideOfAGizmoHeadIsFrontFacing() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var (vertexSpv, fragmentSpv, locations) = EditorMeshShaders();
        var vertex = device.CreateShader(ShaderStage.Vertex, vertexSpv, "editor mesh vertex");
        var fragment = device.CreateShader(ShaderStage.Fragment, fragmentSpv, "editor mesh fragment");

        // The layout MeshRenderer declares: one 80-byte range read by both stages — the vertex
        // stage takes the matrix, the fragment stage the light.
        var layout = device.CreatePipelineLayout(
            new([], [new(ShaderStage.Vertex | ShaderStage.Fragment, 0, 80)], "editor mesh layout")
        );

        var pipeline = device.CreateGraphicsPipeline(
            new(
                vertex,
                fragment,
                layout,
                [new(PixelFormat.Rgba8UNorm, BlendState.PremultipliedAlpha)],
                [
                    new(
                        Marshal.SizeOf<MeshVertex>(),
                        [
                            new(locations.Position, VertexFormat.Float32X3, 0),
                            new(locations.Normal, VertexFormat.Float32X3, 12),
                            new(locations.Colour, VertexFormat.Float32X4, 24)
                        ]
                    )
                ],
                PrimitiveTopology.TriangleList,
                Rasterizer: RasterizerState.TwoSided,
                DepthStencil: DepthStencilState.Disabled,
                Name: "editor mesh facing"
            )
        );

        try {
            var image = Render(owned, pipeline);

            // The corner is the clear, so the pass ran and the head does not cover everything.
            var corner = Pixel(image, 2, 2);

            Assert.True(corner.X < 0.1f, $"the pass did not clear: {corner}");

            // The face square to the camera. Lit, its red channel is the colour's own 0.8; flipped,
            // it is the ambient 0.35 of it — 0.28 — and nothing between is reachable for a face at
            // Lambert one. The threshold sits above the flipped answer with room for an 8-bit target.
            var centre = Pixel(image, image.Width / 2, image.Height / 2);

            Assert.True(
                centre.X > 0.6f,
                $"the camera-facing face of a gizmo head came back at ambient, so the rasteriser "
                + $"classified the outside as back-facing and Mesh.rvn flipped its normal: {centre}"
            );
        } finally {
            device.Destroy(pipeline);
            device.Destroy(layout);
            device.Destroy(vertex);
            device.Destroy(fragment);
        }
    }

    // --- The frame ----------------------------------------------------------

    static Bitmap Render(Fixture fixture, PipelineHandle pipeline) {
        // The X arm's frame, exactly as GizmoGeometry.Frame builds it: `across × direction`, with
        // the arm along +X and the shape's local Y stretched down it. Its determinant is positive,
        // so the cube's winding survives the placement.
        var head = Frame(Vector3.UnitX, 0.8f, 0.8f, Vector3.Zero);

        var vertices = new List<MeshVertex>();
        var triangles = new List<uint>();

        Append(vertices, triangles, MeshPrimitives.Cube(), head, Colour);

        var vertexBuffer = fixture.Buffer<MeshVertex>(CollectionsMarshal.AsSpan(vertices), BufferUsage.Vertex);
        var indexBuffer = fixture.Buffer<uint>(CollectionsMarshal.AsSpan(triangles), BufferUsage.Index);
        var count = triangles.Count;

        // The engine's own camera maths, the chain EditorCamera wraps: a view from +Z at the
        // origin-centred head, and the light travelling with the line of sight — so the face that
        // looks at the camera is the face that looks at the light.
        var view = Matrix4x4.LookAt(new Vector3(0f, 0f, 3f), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.PerspectiveFieldOfView(MathUtil.DegreesToRadians(60f), 1f, 0.1f, 100f);
        var viewProjection = view * projection;

        var colour = fixture.ColourTarget("editor-gizmo-facing");

        fixture.Graph.AddPass(
            "gizmo head",
            pass => {
                pass.ColourAttachment(colour, LoadAction.Clear, new Color4(0.03f, 0.03f, 0.05f, 1f));
                pass.SideEffect();

                pass.Execute(context => {
                    var commands = context.CommandList;

                    Span<Matrix4x4> matrix = [viewProjection];
                    Span<Vector4> light = [new(0f, 0f, -1f, Ambient)];

                    commands.BindPipeline(pipeline);
                    commands.PushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 0, MemoryMarshal.AsBytes(matrix));
                    commands.PushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 64, MemoryMarshal.AsBytes(light));
                    commands.BindVertexBuffer(0, vertexBuffer);
                    commands.BindIndexBuffer(indexBuffer, IndexFormat.UInt32);
                    commands.DrawIndexed(count);
                });
            }
        );

        return fixture.Render(colour);
    }

    // --- The editor's own artifacts -----------------------------------------

    /// <summary>The committed editor mesh modules, and where its reflection says each input lives.</summary>
    static (byte[] Vertex, byte[] Fragment, (uint Position, uint Normal, uint Colour) Locations) EditorMeshShaders() {
        var shaders = EditorShaderDirectory();

        var reflection = JsonDocument.Parse(File.ReadAllText(Path.Combine(shaders, "Mesh.reflect.json")));
        var inputs = reflection.RootElement.GetProperty("VertexInputs");
        var byName = new Dictionary<string, uint>(StringComparer.Ordinal);

        foreach (var input in inputs.EnumerateArray()) {
            byName[input.GetProperty("Name").GetString()!] = input.GetProperty("Location").GetUInt32();
        }

        return (
            File.ReadAllBytes(Path.Combine(shaders, "Mesh.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaders, "Mesh.frag.spv")),
            (byName["position"], byName["normal"], byName["vertexColour"])
        );
    }

    /// <summary>The editor's shader directory, found the way <see cref="RavenEffects.Library" /> is.</summary>
    /// <remarks>
    ///     ⚠ <b>The host's, not the application's.</b> Doc 36 § P3 split the executable out and the
    ///     committed SPIR-V went with it — the application is a library now and has no shaders beside
    ///     it. This still named <c>Vixen.Editor.App</c> and threw with the old path in the message,
    ///     which is a device test that cannot find the thing it is about.
    /// </remarks>
    static string EditorShaderDirectory() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Editor", "Vixen.Editor.Host", "Shaders");

            if (Directory.Exists(candidate)) {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Editor/Vixen.Editor.Host/Shaders was not found above '{AppContext.BaseDirectory}'."
        );
    }

    // --- GizmoGeometry's maths, verbatim ------------------------------------
    //
    // Copied rather than referenced: the golden suite does not depend on editor assemblies, and the
    // point is to pin the *maths* the gizmo uses — a change to Frame's chirality over there without
    // this fixture noticing is exactly the class of drift the copy makes visible, because this test
    // keeps passing while the editor's gizmo goes dark, or fails while it stays right.

    static Matrix4x4 Frame(Vector3 direction, float width, float length, Vector3 centre) {
        var across = Perpendicular(direction);

        var side = across * width;
        var forward = Vector3.Cross(across, direction) * width;
        var up = direction * length;

        return new(
            side.X, side.Y, side.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            centre.X, centre.Y, centre.Z, 1f
        );
    }

    static Vector3 Perpendicular(Vector3 direction) =>
        Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY));

    static void Append(
        List<MeshVertex> vertices,
        List<uint> triangles,
        MeshData mesh,
        in Matrix4x4 transform,
        Color4 colour
    ) {
        var first = (uint) vertices.Count;
        var normals = Matrix4x4.Invert(transform, out var inverse) ? Matrix4x4.Transpose(inverse) : transform;

        for (var index = 0; index < mesh.Positions.Length; index++) {
            var normal = Vector3.Normalize(Matrix4x4.TransformDirection(mesh.Normals[index], normals));
            vertices.Add(new(Matrix4x4.TransformPosition(mesh.Positions[index], transform), normal, colour));
        }

        foreach (var index in mesh.Indices) {
            triangles.Add(first + (uint) index);
        }
    }

    /// <summary>One pixel, as channels in 0..1.</summary>
    static Vector3 Pixel(in Bitmap image, int x, int y) {
        var offset = image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1));

        return new(image.Pixels[offset] / 255f, image.Pixels[offset + 1] / 255f, image.Pixels[offset + 2] / 255f);
    }

    /// <summary>Passes when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }
    }
}
