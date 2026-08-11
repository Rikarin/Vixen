// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Rendering;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Samples.PbrShowcase;

/// <summary>The grid itself: twenty-five materials whose two numbers are their coordinates.</summary>
/// <remarks>
///     <para>
///         <b>An <see cref="IMaterialSource" /> the project implements, rather than twenty-five
///         <c>.vxmat</c> files.</b> The interface is the seam the extraction already asks through —
///         sample 13's materials come from assets through the same one — and this grid is
///         arithmetic: metallic is the row over four, roughness is the column over four. A file per
///         cell would be the arithmetic transcribed by hand, which is the class of mistake the
///         standard frame exists to retire.
///     </para>
///     <para>
///         ⚠ <b>The permutations here and the document's knobs are one contract.</b> `gi: Ambient`
///         splits the shading pass's output across four targets, so <c>SplitOutputs</c> must be on
///         or target 0 is a frame missing its ambient with nothing downstream to put it back;
///         `shadows: Cascades` at `quality: High` fills four cascades, so <c>CascadeCount</c> must
///         say four or a fragment selects a slot nobody wrote; and the two compose slots below are
///         what let the document's clipmap and punctual-shadow nodes fill bindings the shader
///         actually declares. Every one of these is stated beside the knob it answers.
///     </para>
/// </remarks>
sealed class GridMaterials : IMaterialSource {
    readonly Dictionary<AssetReference, Material> cells;
    readonly AssetReference[,] grid;

    GridMaterials(
        Dictionary<AssetReference, Material> cells,
        AssetReference[,] grid,
        AssetReference floor,
        Material fallback
    ) {
        this.cells = cells;
        this.grid = grid;
        Floor = floor;
        Fallback = fallback;
    }

    /// <summary>The floor's material: a rough dielectric, mid-grey, so every shadow reads on it.</summary>
    public AssetReference Floor { get; }

    /// <summary>What a renderable with a broken reference draws in — never, in a healthy run.</summary>
    public Material Fallback { get; }

    /// <summary>The reference the sphere at a grid position names.</summary>
    /// <param name="row">The metallic axis, 0 to 4.</param>
    /// <param name="column">The roughness axis, 0 to 4.</param>
    /// <returns>The reference.</returns>
    public AssetReference Cell(int row, int column) => grid[row, column];

    /// <inheritdoc />
    public bool TryGet(AssetReference reference, out Material material) => cells.TryGetValue(reference, out material!);

    /// <summary>Compiles the grid, or returns null with the reason logged.</summary>
    /// <param name="rows">How many metallic steps.</param>
    /// <param name="columns">How many roughness steps.</param>
    /// <param name="log">Where a refusal goes.</param>
    /// <returns>The palette, or null if the material would not compile.</returns>
    public static GridMaterials? Compile(int rows, int columns, ILogger log) {
        // Which shader fills each forward compose slot is the project's decision rather than the
        // material's — and the one line answers a node the document's knobs emit: the clipmap node
        // (`gi: Ambient`) publishes its field under ForwardPlus.GlobalDistanceField, and a material
        // that composed the neutral filler instead would leave it publishing into bindings no
        // shader declares.
        //
        // ⚠ The punctual-shadow slot is deliberately NOT bound, and the reason is the render
        // graph's producer rule: composing it makes every Main draw *read* PunctualShadowAtlas,
        // and this scene has no spot or point light, so the `shadows:` knob's Lamps node never
        // writes the atlas — a read with no producer, refused by name. A scene that gains its
        // first lamp binds MaterialCompiler.ForwardPunctualShadowSlot to PunctualShadowShader
        // here, and the atlas the node already renders starts being sampled.
        var slots = new Dictionary<string, string>(StringComparer.Ordinal) {
            [MaterialCompiler.ForwardDistanceFieldSlot] = "GlobalDistanceField"
        };

        // The project's facts about the frame, true of all twenty-six materials at once. See the
        // class remarks for why each number is the other half of a document knob.
        var permutations = new ParameterCollection();

        permutations.Set(ForwardPlusKeys.SplitOutputs, true);
        permutations.Set(ForwardPlusKeys.UseShadows, true);
        permutations.Set(ForwardPlusKeys.CascadeCount, 4);

        // On, because ShowcaseFrame bakes the sky both feed from: the cube's prefiltered chain is
        // the specular ambient — the thing that makes a rough metal and a smooth one different on
        // their unlit sides — and the probe array falls back to the same cube.
        permutations.Set(ForwardPlusKeys.UseImageBasedLighting, true);
        permutations.Set(ForwardPlusKeys.UseReflectionProbe, true);

        // Off, each for a stated reason: no probe field exists at `gi: Ambient`, and the frame's
        // one ambient-occlusion march is the document's !DistanceFieldAo node — marching the same
        // clipmap from inside the material as well would be the room's occlusion applied squared,
        // sample 13's exact lesson.
        permutations.Set(ForwardPlusKeys.UseIrradianceField, false);
        permutations.Set(ForwardPlusKeys.UseDistanceFieldOcclusion, false);

        var grid = new AssetReference[rows, columns];
        var cells = new Dictionary<AssetReference, Material>();

        for (var row = 0; row < rows; row++) {
            for (var column = 0; column < columns; column++) {
                var material = Bake(
                    new MetalRoughnessFeature {
                        // The base colour, one value for the whole grid. Varying it as well would
                        // confound the two axes the grid exists to separate.
                        BaseColor = new(0.86f, 0.82f, 0.74f),
                        Metalness = row / (float)(rows - 1),
                        Roughness = column / (float)(columns - 1)
                    },
                    slots,
                    permutations,
                    log
                );

                if (material is null) {
                    return null;
                }

                var reference = new AssetReference(AssetId.New());

                grid[row, column] = reference;
                cells[reference] = material;
            }
        }

        var floor = Bake(
            new MetalRoughnessFeature { BaseColor = new(0.45f, 0.46f, 0.48f), Metalness = 0f, Roughness = 0.85f },
            slots,
            permutations,
            log
        );

        var fallback = Bake(
            new MetalRoughnessFeature { BaseColor = new(0.62f, 0.63f, 0.66f), Metalness = 0f, Roughness = 0.7f },
            slots,
            permutations,
            log
        );

        if (floor is null || fallback is null) {
            return null;
        }

        var floorReference = new AssetReference(AssetId.New());
        cells[floorReference] = floor;

        return new(cells, grid, floorReference, fallback);
    }

    /// <summary>An exact signed-distance field for a sphere — one subtraction per texel.</summary>
    /// <param name="radius">The sphere's radius.</param>
    /// <returns>The field, margin included, for the occlusion march to walk.</returns>
    /// <remarks>
    ///     The margin is the whole point: a march starts <em>outside</em> a surface and walks toward
    ///     it, so what it needs is the positive distances around the sphere, not the zero at it.
    /// </remarks>
    public static MeshDistanceField SphereField(float radius) =>
        Field(new Vector3(radius), at => at.Length() - radius);

    /// <summary>An exact field for an axis-aligned slab — the floor, thickness and all.</summary>
    /// <param name="half">The slab's half-extents.</param>
    /// <returns>The field.</returns>
    public static MeshDistanceField SlabField(Vector3 half) => Field(
        half,
        at => {
            var outside = Vector3.Abs(at) - half;

            var beyond = new Vector3(
                MathF.Max(outside.X, 0f),
                MathF.Max(outside.Y, 0f),
                MathF.Max(outside.Z, 0f)
            );

            return beyond.Length() + MathF.Min(MathF.Max(outside.X, MathF.Max(outside.Y, outside.Z)), 0f);
        }
    );

    static MeshDistanceField Field(Vector3 half, Func<Vector3, float> distance) {
        const float Margin = 1.5f;
        const float Cell = 0.25f;

        var bounds = new BoundingBox(-half - new Vector3(Margin), half + new Vector3(Margin));
        var size = bounds.Maximum - bounds.Minimum;

        var resolution = new Int3(
            Math.Clamp((int)MathF.Ceiling(size.X / Cell) + 1, 5, 48),
            Math.Clamp((int)MathF.Ceiling(size.Y / Cell) + 1, 5, 48),
            Math.Clamp((int)MathF.Ceiling(size.Z / Cell) + 1, 5, 48)
        );

        var distances = new float[resolution.X * resolution.Y * resolution.Z];

        var step = new Vector3(
            size.X / (resolution.X - 1),
            size.Y / (resolution.Y - 1),
            size.Z / (resolution.Z - 1)
        );

        var index = 0;

        for (var z = 0; z < resolution.Z; z++) {
            for (var y = 0; y < resolution.Y; y++) {
                for (var x = 0; x < resolution.X; x++) {
                    distances[index++] = distance(bounds.Minimum + new Vector3(x * step.X, y * step.Y, z * step.Z));
                }
            }
        }

        return new(bounds, resolution, distances);
    }

    static Material? Bake(
        MetalRoughnessFeature surface,
        Dictionary<string, string> slots,
        ParameterCollection permutations,
        ILogger log
    ) {
        var compilation = MaterialCompiler.Compile(
            new() { ShaderName = "ForwardPlus", Features = [surface] },
            slots
        );

        if (compilation.Failed || compilation.Material is not { } material) {
            SampleLog.NoMaterial(
                log,
                string.Join("; ", compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
            );

            return null;
        }

        material.Parameters.Apply(permutations);
        return material;
    }
}
