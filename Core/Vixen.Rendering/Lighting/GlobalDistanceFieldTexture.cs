// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.DistanceFields;
using Vixen.Shaders;

namespace Vixen.Rendering.Lighting;

/// <summary>The GPU's copy of a <see cref="GlobalDistanceField" />: one volume texture per level.</summary>
/// <remarks>
///     <para>
///         <b>The clipmap itself references no device, and this is why.</b> Everything about what a
///         field <i>says</i> — the bake, the composite, the snapping, the tracer — is arithmetic with
///         closed-form answers, and it lives in an assembly that could not open a device if it wanted
///         to. What is left over is this: allocate, stage, copy, and name. Splitting it that way is
///         what lets the hard half be checked against a sphere's own equation instead of against a
///         screenshot.
///     </para>
///     <para>
///         <b>A texture per level rather than one atlas.</b> A three-dimensional texture cannot be an
///         array layer, so the levels cannot be slices of one resource — they are separate textures
///         bound together, exactly as <see cref="ReflectionProbe" />'s cubes are. An atlas packing
///         them along Z would work and would cost a manual filter at every level boundary, because
///         hardware trilinear does not know where one level stops.
///     </para>
///     <para>
///         <b>Sampled with linear filtering and clamped at the edges.</b> Linear because the whole
///         point of a sampled field is that the hardware interpolates it for free; clamped because a
///         sample walking off a level must read that level's edge rather than wrap to the far side of
///         the world, which is geometry on the wrong side of the camera.
///     </para>
///     <para>
///         <b>Stored as <see cref="PixelFormat.R32Float" />, deliberately for now.</b> Half would
///         hold a clipmap level's range to well inside a cell, and a narrow band of bytes is what
///         Unreal ships — but this is the format that matches the CPU field exactly, so a readback
///         can be compared against it without a quantisation step in between to argue about. The
///         memory is not the reason to hurry: four levels of 32³ is half a megabyte.
///     </para>
/// </remarks>
public sealed class GlobalDistanceFieldTexture : IDisposable {
    /// <summary>The shader's name for the array of level volumes.</summary>
    const string LevelsName = "distanceFieldLevels";

    /// <summary>The shader's name for how they are sampled — one sampler for all of them.</summary>
    const string SamplerName = "distanceFieldSampler";

    readonly TextureHandle[] textures;

    BufferHandle staging;
    SamplerHandle sampler;
    IGraphicsDevice? device;
    bool disposed;

    /// <summary>The clipmap this mirrors.</summary>
    public GlobalDistanceField Field { get; }

    /// <summary>Whether the textures exist yet.</summary>
    public bool IsCreated { get; private set; }

    /// <summary>How many times the whole clipmap has been copied up.</summary>
    /// <remarks>
    ///     Every level, every call, for now — which is what makes this worth counting rather than
    ///     assuming. A camera that moved one cell dirties one slab per axis of one level and nothing
    ///     else, and the snapping that makes that true is already done on the other side; until the
    ///     scroll is written, this number is the cost.
    /// </remarks>
    public int Uploads { get; private set; }

    /// <summary>How the volumes are sampled.</summary>
    public SamplerHandle Sampler => sampler;

    /// <summary>Mirrors one clipmap.</summary>
    /// <param name="field">The clipmap.</param>
    /// <exception cref="ArgumentNullException">There is no clipmap.</exception>
    public GlobalDistanceFieldTexture(GlobalDistanceField field) {
        ArgumentNullException.ThrowIfNull(field);

        Field = field;
        textures = new TextureHandle[field.LevelCount];
    }

    /// <summary>One level's volume texture.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>The texture, or an invalid handle before the first upload.</returns>
    public TextureHandle Level(int level) => textures[level];

    /// <summary>Copies every level up to the device, creating the textures if they are not there.</summary>
    /// <param name="graphics">The device.</param>
    /// <param name="commands">The list to record the copies into.</param>
    /// <exception cref="ArgumentNullException">There is no device or no command list.</exception>
    /// <exception cref="InvalidOperationException">The clipmap has never been composited.</exception>
    /// <remarks>
    ///     <b>One staging buffer for the whole clipmap, written once and copied per level.</b> A
    ///     buffer per level would be a driver call per level for bytes a single write already has,
    ///     and reusing one small buffer across levels would overwrite a region a copy has been
    ///     recorded against but has not yet run.
    /// </remarks>
    public void Upload(IGraphicsDevice graphics, ICommandList commands) {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!Field.HasContent) {
            throw new InvalidOperationException(
                "The clipmap has nothing in it. Composite it with Update before uploading, or every "
                + "level is a volume of zeroes — which reads as a surface everywhere."
            );
        }

        Create(graphics);

        var resolution = Field.Resolution;
        var perLevel = (long)resolution * resolution * resolution * sizeof(float);

        for (var level = 0; level < textures.Length; level++) {
            graphics.Write(
                staging,
                level * perLevel,
                MemoryMarshal.AsBytes(Field.LevelData(level))
            );

            commands.CopyBufferToTexture(
                staging,
                level * perLevel,
                new TextureRegion(textures[level]),
                new Int3(resolution)
            );
        }

        Uploads++;
    }

    /// <summary>Writes the names a shader reads the clipmap through.</summary>
    /// <param name="parameters">Where the values go — the frame's, since the clipmap follows the camera.</param>
    /// <param name="shaderName">The pass whose keys to write.</param>
    /// <exception cref="ArgumentNullException">There are no parameters.</exception>
    /// <exception cref="ArgumentException">The shader name is empty.</exception>
    /// <remarks>
    ///     <para>
    ///         Each level contributes where it starts, how big a cell is, and how far it is willing
    ///         to claim anything is. A shader finds itself by testing the levels in order and taking
    ///         the first that contains it — the finest — which is three subtractions rather than a
    ///         search.
    ///     </para>
    ///     <para>
    ///         The reciprocal of the cell is written rather than the cell, because a shader turns a
    ///         world position into a texture coordinate by multiplying, and a divide per level per
    ///         step is a divide nobody needs.
    ///     </para>
    /// </remarks>
    public void Apply(ParameterCollection parameters, string shaderName = "ForwardPlus") {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(shaderName);

        parameters.Set(ParameterKeys.New<float>($"{shaderName}.distanceFieldLevelCount"), Field.LevelCount);

        for (var level = 0; level < Field.LevelCount; level++) {
            var index = level.ToString(CultureInfo.InvariantCulture);
            var slot = $"{shaderName}.distanceFieldVolumes[{index}]";
            var bounds = Field.BoundsOf(level);
            var cell = Field.CellSizeOf(level);

            parameters.Set(ParameterKeys.New<Vector3>($"{slot}.minimum"), bounds.Minimum);
            parameters.Set(ParameterKeys.New<Vector3>($"{slot}.maximum"), bounds.Maximum);
            parameters.Set(ParameterKeys.New<float>($"{slot}.inverseCellSize"), 1f / cell);
            parameters.Set(ParameterKeys.New<float>($"{slot}.maxDistance"), Field.MaxDistanceOf(level));
        }
    }

    /// <summary>The shader's name for one level's volume texture.</summary>
    /// <param name="level">Which level.</param>
    /// <param name="shaderName">The pass whose keys to name.</param>
    /// <returns>The binding's name.</returns>
    public static string LevelBinding(int level, string shaderName = "ForwardPlus") =>
        $"{shaderName}.{LevelsName}[{level.ToString(CultureInfo.InvariantCulture)}]";

    /// <summary>The shader's name for the sampler all the levels share.</summary>
    /// <param name="shaderName">The pass whose keys to name.</param>
    /// <returns>The binding's name.</returns>
    public static string SamplerBinding(string shaderName = "ForwardPlus") => $"{shaderName}.{SamplerName}";

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (device is null) {
            return;
        }

        foreach (var texture in textures) {
            if (texture.IsValid) {
                device.Destroy(texture);
            }
        }

        if (staging.IsValid) {
            device.Destroy(staging);
        }

        if (sampler.IsValid) {
            device.Destroy(sampler);
        }

        IsCreated = false;
    }

    /// <summary>Allocates the textures, the staging buffer and the sampler, once.</summary>
    /// <param name="graphics">The device.</param>
    void Create(IGraphicsDevice graphics) {
        if (IsCreated) {
            return;
        }

        device = graphics;

        var resolution = Field.Resolution;

        for (var level = 0; level < textures.Length; level++) {
            textures[level] = graphics.CreateTexture(
                new TextureDescription(
                    PixelFormat.R32Float,
                    resolution,
                    resolution,
                    TextureUsage.Sampled | TextureUsage.CopyDestination,
                    Depth: resolution,
                    Dimension: TextureDimension.Texture3D,
                    Name: $"GlobalDistanceField.Level{level.ToString(CultureInfo.InvariantCulture)}"
                )
            );
        }

        staging = graphics.CreateBuffer(
            new BufferDescription(
                (long)resolution * resolution * resolution * sizeof(float) * textures.Length,
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "GlobalDistanceField.Staging"
            )
        );

        // Clamped, not repeated: a sample that walks off a level must read that level's own edge.
        // Wrapping puts geometry from behind the camera in front of it.
        sampler = graphics.CreateSampler(SamplerDescription.LinearClamp);

        IsCreated = true;
    }
}
