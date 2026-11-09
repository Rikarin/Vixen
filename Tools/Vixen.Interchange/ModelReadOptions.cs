// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Interchange;

/// <summary>The defaults, for a host with no import settings of its own.</summary>
public sealed record ModelReadOptions : IModelReadOptions {
    /// <inheritdoc />
    public float Scale { get; init; } = 1f;

    /// <inheritdoc />
    public bool GenerateNormals { get; init; } = true;

    /// <inheritdoc />
    public bool GenerateTangents { get; init; } = true;

    /// <inheritdoc />
    public bool ImportAnimations { get; init; } = true;

    /// <inheritdoc />
    public bool ImportBlendShapes { get; init; } = true;

    /// <inheritdoc />
    public bool ImportVertexColours { get; init; }

    /// <inheritdoc />
    public bool ImportAllTexCoords { get; init; }

    /// <inheritdoc />
    public float BlendShapeThreshold { get; init; } = 1e-4f;
}
