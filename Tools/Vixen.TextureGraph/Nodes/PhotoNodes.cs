// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>What a <c>Photo/Flatten Light</c> divides out.</summary>
enum TextureLightFlatten {
    /// <summary>The light's brightness: one gain per texel, so every texel keeps its hue.</summary>
    Luminance,

    /// <summary>The light per channel, which evens out a colour cast that drifts across the photo as well.</summary>
    Colour
}

/// <summary>Builds the chains the photo and anti-repetition nodes share.</summary>
static class PhotoChains {
    /// <summary>The 1×1 mean of an image, through a <c>MeanReduce</c> ladder.</summary>
    /// <returns>The ladder's last rung, which holds the mean.</returns>
    public static int Mean(TextureEmitter emitter, int source) {
        var size = emitter.SizeOf(source);
        var level = emitter.LevelOf(source);
        var levels = TextureAdjust.ReductionLevels(size.X, size.Y);
        var scratch = ImmutableArray.CreateBuilder<int>(levels.Length);

        foreach (var rung in levels) {
            scratch.Add(emitter.Scratch(emitter.FormatOf(TextureChannels.Colour), level + rung));
        }

        var ladder = scratch.ToImmutable();

        emitter.Dispatch(TextureAdjust.Reduction(source, ladder, size.X, size.Y, TextureColourOperators.MeanReduce));

        return ladder[^1];
    }

    /// <summary>One <c>PatchScatter</c> over a node's <c>Input</c>, with the numbers both its nodes share.</summary>
    public static void Patches(TextureEmitter emitter, float margin) {
        var source = emitter.Read("Input", TextureChannels.Colour);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0) {
            return;
        }

        var mean = Mean(emitter, source);

        emitter.Dispatch(
            new TextureOp {
                Kernel = TexturePhotoKernels.PatchScatter,
                Output = target,
                Inputs = [source, mean],

                // A patch reads its source wherever its hash put it, and the mean is 1×1.
                ReadsOtherExtents = true,
                OtherExtentInputs = [mean],
                DependsOnEveryTexel = true,
                // ⚠ Each number held to its port's bounds here, so a number past a bound draws what the bound draws:
                // the kernel's own clamps are looser, to keep its loop bounded rather than the slider honest.
                Parameters = [
                    new("cells", Math.Clamp(emitter.Integer("Patches"), 1, 32)),
                    new("size", Math.Clamp(emitter.Number("Patch Size"), 0.5f, 3f)),
                    new("jitter", Math.Clamp(emitter.Number(nameof(BreakUpTilingNode.Randomness)), 0f, 1f) * 0.5f),
                    new("turn", Math.Clamp(emitter.Number(nameof(BreakUpTilingNode.Rotation)), 0f, 3.14159f)),
                    new("margin", margin),
                    new("softness", Math.Clamp(emitter.Number(nameof(BreakUpTilingNode.Softness)), 0f, 1f)),
                    new("contrast", Math.Clamp(emitter.Number("Keep Contrast"), 0f, 1f)),
                    new("variation", Math.Max(emitter.Integer(nameof(BreakUpTilingNode.Seed)), 0))
                ]
            }
        );
    }
}

/// <summary>A photo's uneven lighting divided out — Patina #178's light flatten and colour equaliser.</summary>
/// <remarks>
///     <para>
///         <b>Three steps and a gain.</b> The photo is shrunk with a box filter and blurred until only its light is
///         left, a <c>MeanReduce</c> ladder measures that light's average, and every texel is scaled by the average over
///         the light where it stands. Shrinking first is what lets a blur as wide as a tenth of the photo cost what a
///         blur of a few texels does.
///     </para>
///     <para>
///         ⚠ <b>Not <c>Utility/Equalize</c>, which subtracts a blur.</b> A shadow darkens a surface by a fraction of its
///         colour, so a high pass leaves a dark corner's texture at a lower contrast than the middle's. Dividing keeps
///         the contrast and evens the brightness, which is what a scan that has to tile needs before anything else.
///     </para>
/// </remarks>
[Node(
    "Photo/Flatten Light",
    Preview = true,
    Summary =
        "Divides a photo's uneven lighting out so it can become a texture: delighting, and with Colour, colour equalisation."
)]
sealed partial class FlattenLightNode : TextureNode {
    /// <summary>The photo.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>Whether the light is divided out by brightness alone or per channel.</summary>
    [Setting(
        AcceptedFrom = typeof(TextureLightFlatten),
        Summary = "Luminance evens the brightness and keeps every hue; Colour evens colour casts as well."
    )]
    public string Mode = "Luminance";

    /// <summary>The size of the lighting changes to remove, as a fraction of the image's width.</summary>
    [Input(
        Name = "Light Size",
        Minimum = 0.01f,
        Maximum = 1f,
        Summary =
            "How broad the lighting changes to remove are, as a fraction of the width. Detail smaller than this is kept."
    )]
    public Scalar LightSize = 0.15f;

    /// <summary>How much of the light is divided out.</summary>
    [Input(Minimum = 0f, Maximum = 1f, Summary = "How much of the light is divided out: 0 none, 1 all of it.")]
    public Scalar Strength = 1f;

    /// <summary>The photo, evenly lit.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var mode = TextureSettings.Enum(emitter, nameof(Mode), TextureLightFlatten.Luminance);
        var source = emitter.Read("Input", TextureChannels.Colour);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0) {
            return;
        }

        var size = emitter.SizeOf(source);
        var level = emitter.LevelOf(source);

        // A gaussian whose two sigmas span the light size, at the base resolution — and the shrink that brings it
        // inside BlurHq's ceiling, which a light a tenth of a 4K photo across would otherwise pass sixteenfold.
        var sigma = Math.Clamp(emitter.Number("Light Size"), 0.01f, 1f) * size.X * 0.5f;
        var shrink = 0;

        while (sigma / (1 << shrink) > 16f && Math.Min(size.X, size.Y) >> (shrink + 1) >= 8) {
            shrink++;
        }

        var format = emitter.FormatOf(TextureChannels.Colour);
        var small = emitter.Scratch(format, level + shrink);
        var across = emitter.Scratch(format, level + shrink);
        var light = emitter.Scratch(format, level + shrink);

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourKernels.Resample,
                Output = small,
                Inputs = [source],
                ReadsOtherExtents = true,
                Parameters = [new("filter", (float)TextureFilter.Box)]
            }
        );

        // ⚠ The sigma is in texels at the base and the evaluator scales it to the small image, so it is the same
        // light at every bake size; it is capped at BlurHq's own ceiling there.
        var blur = Math.Min(sigma / (1 << shrink), 64f / 3f) * (1 << shrink);

        emitter.Dispatch(TextureFilters.BlurHqOp(across, small, blur));
        emitter.Dispatch(TextureFilters.BlurHqOp(light, across, blur, true));

        var mean = PhotoChains.Mean(emitter, light);

        emitter.Dispatch(
            new TextureOp {
                Kernel = TexturePhotoKernels.FlattenLight,
                Output = target,
                Inputs = [source, light, mean],
                ReadsOtherExtents = true,
                OtherExtentInputs = [light, mean],
                DependsOnEveryTexel = true,
                Parameters = [
                    new("strength", Math.Clamp(emitter.Number(nameof(Strength)), 0f, 1f)),
                    new("colour", mode == TextureLightFlatten.Colour ? 1f : 0f),
                    new("maxGain", 8f)
                ]
            }
        );
    }
}

/// <summary>A photo made to tile by rebuilding it from round patches of its own middle — Patina #178.</summary>
/// <remarks>
///     <para>
///         <b>Make It Tile's patch mode.</b> <c>Utility/Make It Tile</c> moves the seams into the middle and blends a
///         cross over them, which keeps the photo and shows a cross. This throws the photo's layout away instead: a grid
///         of patches, each a round piece of the photo from inside its edges, turned and blended with its neighbours,
///         and the grid tiles because its patches are a function of the folded cell.
///     </para>
///     <para>
///         ⚠ <b>It suits a photo of something without a layout</b> — gravel, bark, soil, plaster. A brick wall's
///         courses do not survive being cut into patches, and Make It Tile is the answer there.
///     </para>
/// </remarks>
[Node(
    "Photo/Tile from Patches",
    Preview = true,
    Summary = "Makes a photo tile by rebuilding it from round patches of itself, with no seams and no mirrored edges."
)]
sealed partial class TileFromPatchesNode : TextureNode {
    /// <summary>The photo.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How many patches across the image, and down it.</summary>
    [Input(Minimum = 1f, Maximum = 32f, Summary = "How many patches across the image, and down it. More is smaller.")]
    public Int Patches = 4;

    /// <summary>A patch's diameter, in grid cells.</summary>
    [Input(
        Name = "Patch Size",
        Minimum = 0.5f,
        Maximum = 3f,
        Summary = "A patch's diameter, in grid cells. Above 1, patches overlap and blend."
    )]
    public Scalar PatchSize = 2.4f;

    /// <summary>How far a patch wanders from its place in the grid.</summary>
    [Input(Minimum = 0f, Maximum = 1f, Summary = "How far each patch wanders from its place in the grid.")]
    public Scalar Randomness = 0.6f;

    /// <summary>The largest random turn of a patch, in radians either way.</summary>
    [Input(
        Minimum = 0f,
        Maximum = 3.14159f,
        Summary =
            "The largest random turn of a patch, in radians either way. Keep it at 0 for a photo with a direction to it."
    )]
    public Scalar Rotation = 0f;

    /// <summary>How soft a patch's edge is.</summary>
    [Input(Minimum = 0f, Maximum = 1f, Summary = "How soft the patches' edges are: 0 hard, a mosaic; 1 broad blends.")]
    public Scalar Softness = 0.6f;

    /// <summary>Whether overlapping patches keep their contrast.</summary>
    [Input(
        Name = "Keep Contrast",
        Minimum = 0f,
        Maximum = 1f,
        Summary = "1 keeps the photo's contrast where patches overlap; 0 averages them, which softens the overlaps."
    )]
    public Scalar KeepContrast = 1f;

    /// <summary>Another draw of the same patches.</summary>
    [Input(Minimum = 0f, SoftMaximum = 100f, Summary = "Another arrangement of the same patches.")]
    public Int Seed = 0;

    /// <summary>The photo, tiling.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        // Every patch's source stays inside the photo's edges by its own radius, so no patch reads across an edge
        // the photo does not tile at.
        var patches = Math.Clamp(emitter.Integer(nameof(Patches)), 1, 32);
        var reach = Math.Clamp(emitter.Number("Patch Size"), 0.5f, 3f) * 0.5f / patches;

        PhotoChains.Patches(emitter, Math.Min(reach, 0.5f));
    }
}

/// <summary>A tiling texture rebuilt from random, turned patches of itself, so it does not repeat — Patina #218.</summary>
/// <remarks>
///     <b>Texture bombing with a blend that keeps contrast.</b> A texture that tiles shows its tile across a large
///     surface; this covers the image with a grid of patches each taken from a random place in the texture and turned,
///     blended by Heitz and Neyret's variance-preserving mix so their overlaps do not go soft. Its patches read across
///     the source's edges freely, since the source tiles, and the result tiles in turn.
/// </remarks>
[Node(
    "Space/Break Up Tiling",
    Preview = true,
    Summary =
        "Hides the repetition of a tiling texture by rebuilding it from randomly placed and turned patches of itself."
)]
sealed partial class BreakUpTilingNode : TextureNode {
    /// <summary>The tiling texture.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How many patches across the image, and down it.</summary>
    [Input(Minimum = 1f, Maximum = 32f, Summary = "How many patches across the image, and down it. More is smaller.")]
    public Int Patches = 4;

    /// <summary>A patch's diameter, in grid cells.</summary>
    [Input(
        Name = "Patch Size",
        Minimum = 0.5f,
        Maximum = 3f,
        Summary = "A patch's diameter, in grid cells. Above 1, patches overlap and blend."
    )]
    public Scalar PatchSize = 2.4f;

    /// <summary>How far a patch wanders from its place in the grid.</summary>
    [Input(Minimum = 0f, Maximum = 1f, Summary = "How far each patch wanders from its place in the grid.")]
    public Scalar Randomness = 0.6f;

    /// <summary>The largest random turn of a patch, in radians either way.</summary>
    [Input(
        Minimum = 0f,
        Maximum = 3.14159f,
        Summary = "The largest random turn of a patch, in radians either way. 0 for a texture with a direction to it."
    )]
    public Scalar Rotation = 3.14159f;

    /// <summary>How soft a patch's edge is.</summary>
    [Input(Minimum = 0f, Maximum = 1f, Summary = "How soft the patches' edges are: 0 hard, a mosaic; 1 broad blends.")]
    public Scalar Softness = 0.6f;

    /// <summary>Whether overlapping patches keep their contrast.</summary>
    [Input(
        Name = "Keep Contrast",
        Minimum = 0f,
        Maximum = 1f,
        Summary = "1 keeps the texture's contrast where patches overlap; 0 averages them, which softens the overlaps."
    )]
    public Scalar KeepContrast = 1f;

    /// <summary>Another draw of the same patches.</summary>
    [Input(Minimum = 0f, SoftMaximum = 100f, Summary = "Another arrangement of the same patches.")]
    public Int Seed = 0;

    /// <summary>The texture, without its repetition.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        PhotoChains.Patches(emitter, 0f);
    }
}

/// <summary>A normal map and an albedo from four photos lit from four sides — Patina #178's photometric normal.</summary>
/// <remarks>
///     <para>
///         <b>Photometric stereo, in closed form.</b> Four photos of one surface, the camera still, a light a quarter turn
///         round between each at one height: the differences give the slope and the sum the facing, so each texel's
///         normal and albedo come out of one division. <c>PhotometricNormal.rvn</c> writes the arithmetic.
///     </para>
///     <para>
///         ⚠ <b>The surface is taken to be matte and unshadowed.</b> A highlight reads as a slope towards its light and
///         a cast shadow as one away from it; a polariser and high lights keep both small. The albedo is the delighted
///         colour photometric scanning is for.
///     </para>
/// </remarks>
[Node(
    "Photo/Normal from Lighting",
    Preview = true,
    Summary =
        "A normal map and a delighted albedo from four photos of a surface lit from the left, right, top and bottom."
)]
sealed partial class NormalFromLightingNode : TextureNode {
    /// <summary>The photo lit from the left.</summary>
    [Input(Name = "Lit from Left")]
    public Image Left;

    /// <summary>The photo lit from the right.</summary>
    [Input(Name = "Lit from Right")]
    public Image Right;

    /// <summary>The photo lit from the top of the picture.</summary>
    [Input(Name = "Lit from Top")]
    public Image Top;

    /// <summary>The photo lit from the bottom of the picture.</summary>
    [Input(Name = "Lit from Bottom")]
    public Image Bottom;

    /// <summary>How high the lights stood above the surface, in radians.</summary>
    [Input(
        Name = "Light Height",
        Minimum = 0.05f,
        Maximum = 1.52f,
        Summary = "How high the lights stood above the surface, in radians: small is grazing, 1.57 overhead."
    )]
    public Scalar LightHeight = 0.7853982f;

    /// <summary>The normal map.</summary>
    [Output(Name = "Normal")]
    public Image Normal;

    /// <summary>The colour of the surface with the lighting divided out.</summary>
    [Output(Name = "Albedo")]
    public Image Albedo;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        int[] photos = [
            emitter.Read("Lit from Left", TextureChannels.Colour),
            emitter.Read("Lit from Right", TextureChannels.Colour),
            emitter.Read("Lit from Top", TextureChannels.Colour),
            emitter.Read("Lit from Bottom", TextureChannels.Colour)
        ];
        var normal = emitter.Write("Normal", TextureChannels.Colour);
        var albedo = emitter.Write("Albedo", TextureChannels.Colour);

        if (photos.Any(static photo => photo < 0)) {
            return;
        }

        foreach (var (output, which) in (ReadOnlySpan<(int, float)>)[(normal, 0f), (albedo, 1f)]) {
            emitter.Dispatch(
                new TextureOp {
                    Kernel = TexturePhotoKernels.PhotometricNormal,
                    Output = output,
                    Inputs = [..photos],
                    ReadsOtherExtents = true,
                    Parameters = [new("elevation", emitter.Number("Light Height")), new("albedo", which)]
                }
            );
        }
    }
}

/// <summary>Steep slopes of a height map crumbling and settling — Patina #218's terrain erosion.</summary>
/// <remarks>
///     <para>
///         <b>Thermal erosion: one kernel, stepped.</b> Each step moves the part of every drop steeper than the talus
///         downhill, as a pairwise exchange that conserves material exactly; the node emits one dispatch per step, each
///         into an image of its own, since a plan writes an image once.
///     </para>
///     <para>
///         ⚠ <b>The talus is a slope across the image and resolution-independent; the step count is not.</b> Material
///         moves at most a texel a step, so a 4K bake needs four times the steps of a 1K one to slide as far — the
///         same trade <c>Analysis/Flood Fill</c>'s iterations make. Hydraulic erosion, which carries material along
///         water's paths, is not here.
///     </para>
/// </remarks>
[Node(
    "Surface/Thermal Erosion",
    Preview = true,
    Summary =
        "Lets the steep slopes of a height map crumble and settle, as scree does: terrain, rock faces and worn stone."
)]
sealed partial class ThermalErosionNode : TextureNode {
    /// <summary>The height map.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The steepest slope that stands, as a height change across the image's width.</summary>
    [Input(
        Minimum = 0f,
        SoftMaximum = 16f,
        Summary =
            "The steepest slope that stands, as the height it would climb across the image's width. Lower settles flatter."
    )]
    public Scalar Talus = 2f;

    /// <summary>How fast steep slopes give way.</summary>
    [Input(Minimum = 0f, Maximum = 1f, Summary = "How much of a slope's excess slides in one step.")]
    public Scalar Strength = 0.8f;

    /// <summary>How many steps run.</summary>
    [Input(
        Minimum = 1f,
        Maximum = 256f,
        Summary = "How many steps run. Material moves at most a texel a step, so a larger image wants more."
    )]
    public Int Iterations = 32;

    /// <summary>The eroded height.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.ReadGrey("Input");
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (source < 0) {
            return;
        }

        var steps = Math.Clamp(emitter.Integer(nameof(Iterations)), 1, 256);
        var rate = Math.Clamp(emitter.Number(nameof(Strength)), 0f, 1f) * 0.125f;
        var talus = emitter.Number(nameof(Talus));
        var from = source;

        for (var step = 0; step < steps; step++) {
            var into = step == steps - 1 ? target : emitter.Scratch(emitter.FormatOf(TextureChannels.Grey));

            emitter.Dispatch(
                new TextureOp {
                    Kernel = TexturePhotoKernels.ThermalErosion,
                    Output = into,
                    Inputs = [from],
                    Parameters = [new("talus", talus), new("rate", rate)]
                }
            );

            from = into;
        }
    }
}

/// <summary>Rain running down a height map, carving channels and laying silt — Patina #218's hydraulic erosion.</summary>
/// <remarks>
///     <para>
///         <b>One kernel, stepped, over a state of ground, water and sediment.</b> Each step rains, runs the water
///         down the slope of its surface to the eight neighbours, carries sediment with it, dissolves ground where the
///         water could carry more and lays it down where it carries too much, then dries some water. The state lives
///         in a 32-bit image of its own, whatever the graph's depth, because a step moves a few thousandths of a
///         texel's height and half floats round that away.
///     </para>
///     <para>
///         ⚠ <b>Ground and sediment are conserved exactly; water is not.</b> The flows are a gather of a pairwise
///         exchange, as <c>Surface/Thermal Erosion</c>'s are, and <c>Out</c> lays down whatever the water still
///         carries where it is, so the eroded height has the input's total to rounding.
///     </para>
///     <para>
///         ⚠ <b>The step count is not resolution-independent</b>: water moves at most a texel a step, so a 4K bake
///         needs four times the steps of a 1K one to run as far. The capacity is measured across the image's width,
///         so the same number of texels travelled carves the same depth at any size.
///     </para>
///     <para>
///         ⚠ <b>It wears and silts; it does not yet cut long dendritic channels.</b> Water spreads from where it falls
///         and moves a texel a step, so within 256 steps it gathers little from far uphill. Raising Capacity, Erosion
///         and Deposition together toward their ends brings back a fine maze at the scale of a texel, which the
///         kernel's limits damp but do not remove.
///     </para>
/// </remarks>
[Node(
    "Surface/Hydraulic Erosion",
    Preview = true,
    Summary = "Lets rain run down a height map, carving channels and gullies and laying silt in the valleys."
)]
sealed partial class HydraulicErosionNode : TextureNode {
    /// <summary>The height map.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The water each step adds to every texel.</summary>
    [Input(Minimum = 0f, SoftMaximum = 0.1f, Summary = "The water each step adds to every texel, as a height.")]
    public Scalar Rain = 0.01f;

    /// <summary>The share of the water that dries each step.</summary>
    [Input(
        Minimum = 0f,
        Maximum = 1f,
        Summary = "The share of the water that dries each step. Higher keeps the channels short."
    )]
    public Scalar Evaporation = 0.05f;

    /// <summary>How much sediment moving water can carry.</summary>
    [Input(
        Minimum = 0f,
        SoftMaximum = 0.5f,
        Summary = "How much sediment moving water can carry. Higher carves deeper."
    )]
    public Scalar Capacity = 0.05f;

    /// <summary>How fast the ground dissolves.</summary>
    [Input(Minimum = 0f, Maximum = 1f, Summary = "How much of the water's spare capacity it dissolves each step.")]
    public Scalar Erosion = 0.3f;

    /// <summary>How fast sediment settles.</summary>
    [Input(Minimum = 0f, Maximum = 1f, Summary = "How much of the sediment past capacity settles each step.")]
    public Scalar Deposition = 0.3f;

    /// <summary>How many steps run.</summary>
    [Input(
        Minimum = 1f,
        Maximum = 256f,
        Summary = "How many steps run. Water moves at most a texel a step, so a larger image wants more."
    )]
    public Int Iterations = 64;

    /// <summary>The eroded height.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <summary>Where water stands at the end, as a mask for wet or silted ground.</summary>
    [Output(Name = "Water")]
    public Image Water;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.ReadGrey("Input");
        var height = emitter.Write("Out", TextureChannels.Grey);
        var water = emitter.Write("Water", TextureChannels.Grey);

        if (source < 0) {
            return;
        }

        var steps = Math.Clamp(emitter.Integer(nameof(Iterations)), 1, 256);
        var from = source;

        for (var step = 0; step < steps - 1; step++) {
            var into = emitter.Scratch(TextureFormat.Rgba32Float);

            emitter.Dispatch(Step(emitter, into, from, step == 0, 0));
            from = into;
        }

        emitter.Dispatch(Step(emitter, height, from, steps == 1, 1));
        emitter.Dispatch(Step(emitter, water, from, steps == 1, 2));
    }

    static TextureOp Step(TextureEmitter emitter, int into, int from, bool first, int read) =>
        new() {
            Kernel = TexturePhotoKernels.HydraulicErosion,
            Output = into,
            Inputs = [from],
            Parameters = [
                new("first", first ? 1f : 0f),
                new("read", read),
                new("rain", emitter.Number(nameof(Rain))),
                new("evaporation", Math.Clamp(emitter.Number(nameof(Evaporation)), 0f, 1f)),
                new("capacity", emitter.Number(nameof(Capacity))),
                new("erosion", emitter.Number(nameof(Erosion))),
                new("deposition", emitter.Number(nameof(Deposition)))
            ]
        };
}

/// <summary>One cell of a grid of pictures, stretched to fill the image — Patina #178's atlas split.</summary>
[DoesNotTile(
    "A cell of an atlas tiles only if the picture in it was made to, and the atlas around it does not decide that."
)]
[Node(
    "Space/Atlas Cell",
    Preview = true,
    Summary =
        "One cell of a grid of pictures, such as a sprite sheet or an atlas of scans, stretched to fill the image."
)]
sealed partial class AtlasCellNode : TextureNode {
    /// <summary>The atlas.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How many cells across.</summary>
    [Input(Minimum = 1f, Maximum = 64f, Summary = "How many cells across the atlas.")]
    public Int Columns = 2;

    /// <summary>How many cells down.</summary>
    [Input(Minimum = 1f, Maximum = 64f, Summary = "How many cells down the atlas.")]
    public Int Rows = 2;

    /// <summary>Which cell, counted along each row from the top left.</summary>
    [Input(Minimum = 0f, SoftMaximum = 63f, Summary = "Which cell, counted from 0 along each row from the top left.")]
    public Int Cell = 0;

    /// <summary>The cell.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var columns = Math.Clamp(emitter.Integer(nameof(Columns)), 1, 64);
        var rows = Math.Clamp(emitter.Integer(nameof(Rows)), 1, 64);
        var cell = Math.Max(emitter.Integer(nameof(Cell)), 0) % (columns * rows);

        Smoothing.Filter(
            emitter,
            (target, source) => new() {
                Kernel = TextureColourKernels.Crop,
                Output = target,
                Inputs = [source],
                ReadsOtherExtents = true,
                Parameters = [
                    new("rectX", cell % columns / (float)columns),
                    new("rectY", cell / columns / (float)rows),
                    new("rectW", 1f / columns),
                    new("rectH", 1f / rows),
                    new("filter", (float)TextureFilter.Bilinear),
                    new("actual", 0f)
                ]
            }
        );
    }
}
