// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Authoring;

namespace Vixen.Texturing.Layers;

/// <summary>What a layer command needs from whatever holds the open stack.</summary>
/// <remarks>The editor's document implements this, and so can a host with no editor.</remarks>
public interface ILayerStackDocument : IAuthoredDocument {
    /// <summary>Where the stack lives, relative to whatever the host calls its root.</summary>
    string AssetPath { get; }

    /// <summary>The stack as it stands. Commands replace it.</summary>
    LayerStackAsset Document { get; set; }

    /// <summary>The texture set being painted.</summary>
    string PaintSet { get; }
}
