// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Interchange;

/// <summary>What reading a model needs to be told, apart from the file.</summary>
/// <remarks>
///     An interface rather than a settings record, because the editor's import settings carry many
///     more fields about distance fields and meshlets, and this assembly reads six.
/// </remarks>
public interface IModelReadOptions {
    /// <summary>Scales every position, for a source authored in other units.</summary>
    float Scale { get; }

    /// <summary>Whether to make normals the source does not have.</summary>
    bool GenerateNormals { get; }

    /// <summary>Whether to make tangents the source does not have.</summary>
    bool GenerateTangents { get; }

    /// <summary>Whether to read animation clips.</summary>
    bool ImportAnimations { get; }

    /// <summary>Whether to read blend shapes.</summary>
    bool ImportBlendShapes { get; }

    /// <summary>Whether to keep the first vertex colour set, which only a bake's id map reads.</summary>
    bool ImportVertexColours { get; }

    /// <summary>The smallest displacement a blend shape keeps for a vertex.</summary>
    float BlendShapeThreshold { get; }
}
