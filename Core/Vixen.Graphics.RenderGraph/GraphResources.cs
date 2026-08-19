// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.RenderGraph;

/// <summary>A texture the graph will provide, named before it exists.</summary>
/// <remarks>
///     An index into one graph's resource table, not a GPU object. It is meaningless to another
///     graph and meaningless after <see cref="RenderGraph.Reset" />, which is what the generation
///     catches — a handle kept across frames is a bug that would otherwise address whatever resource
///     took the slot.
/// </remarks>
/// <param name="Index">Which resource, from 1. Zero is no resource.</param>
/// <param name="Generation">Which build of the graph it belongs to.</param>
public readonly record struct GraphTexture(int Index, int Generation) {
    /// <summary>No texture.</summary>
    public static GraphTexture None => default;

    /// <summary>Whether this names anything.</summary>
    public bool IsValid => Index > 0;
}

/// <summary>A buffer the graph will provide, named before it exists.</summary>
/// <param name="Index">Which resource, from 1. Zero is no resource.</param>
/// <param name="Generation">Which build of the graph it belongs to.</param>
public readonly record struct GraphBuffer(int Index, int Generation) {
    /// <summary>No buffer.</summary>
    public static GraphBuffer None => default;

    /// <summary>Whether this names anything.</summary>
    public bool IsValid => Index > 0;
}

/// <summary>Which queue a pass's work belongs on.</summary>
public enum PassKind : byte {
    /// <summary>Draws. The default, and the only kind that may have attachments.</summary>
    Graphics = 0,

    /// <summary>Dispatches.</summary>
    Compute = 1,

    /// <summary>Copies.</summary>
    Transfer = 2
}

/// <summary>How a pass touches a resource.</summary>
/// <param name="Texture">The texture, when it is one.</param>
/// <param name="Buffer">The buffer, when it is one.</param>
/// <param name="State">What the pass needs it to be in.</param>
/// <param name="IsWrite">Whether the pass writes it, which is what culling and ordering turn on.</param>
readonly record struct ResourceUse(
    GraphTexture Texture,
    GraphBuffer Buffer,
    ResourceState State,
    bool IsWrite
) {
    public bool IsTexture => Texture.IsValid;
}

/// <summary>One attachment a pass renders into.</summary>
/// <param name="Texture">What it renders into.</param>
/// <param name="Load">What happens to it at the start of the pass.</param>
/// <param name="Store">
///     What happens at the end, or <see langword="null" /> to let the graph decide — which is the
///     point of declaring attachments here rather than building a
///     <see cref="RenderPassDescription" /> by hand.
/// </param>
/// <param name="ClearColour">What to clear to.</param>
/// <param name="ClearDepth">What to clear depth to. Zero is <em>far</em> under reversed-Z.</param>
/// <param name="ClearStencil">What to clear stencil to.</param>
/// <param name="IsDepth">Whether it is the depth-stencil attachment.</param>
/// <param name="ReadOnly">Whether a depth attachment is only tested, not written.</param>
/// <param name="Resolve">
///     Where a multisampled attachment is resolved to at the end of the pass, or
///     <see cref="GraphTexture.None" /> for an attachment that is not resolved.
/// </param>
readonly record struct GraphAttachment(
    GraphTexture Texture,
    LoadAction Load,
    StoreAction? Store,
    Color4 ClearColour,
    float ClearDepth,
    byte ClearStencil,
    bool IsDepth,
    bool ReadOnly,
    GraphTexture Resolve = default
);

/// <summary>What the graph knows about one virtual resource.</summary>
sealed class GraphResource {
    public required int Index { get; init; }

    public required string Name { get; init; }

    public required bool IsTexture { get; init; }

    /// <summary>Whether it came from outside the graph and outlives it.</summary>
    public required bool IsImported { get; init; }

    public TextureDescription TextureDescription { get; init; }

    public BufferDescription BufferDescription { get; init; }

    /// <summary>The imported resource, when there is one.</summary>
    public TextureHandle ImportedTexture { get; init; }

    public BufferHandle ImportedBuffer { get; init; }

    /// <summary>The imported view, when the importer had one to give.</summary>
    public TextureViewHandle ImportedView { get; init; }

    /// <summary>What state an imported resource is in when the graph receives it.</summary>
    public ResourceState EntryState { get; init; }

    /// <summary>
    ///     What state an imported resource must be left in.
    /// </summary>
    /// <remarks>
    ///     The whole reason importing takes two states. A swapchain image handed back in
    ///     <c>ColourTarget</c> rather than <c>Present</c> is a validation error at present time, a
    ///     long way from the graph that caused it.
    /// </remarks>
    public ResourceState ExitState { get; init; }

    // ── Filled in by Compile ────────────────────────────────────────────────────────────────

    /// <summary>The first surviving pass that touches it, or <c>-1</c>.</summary>
    public int FirstUse { get; set; } = -1;

    /// <summary>The last surviving pass that touches it, or <c>-1</c>.</summary>
    public int LastUse { get; set; } = -1;

    /// <summary>How many surviving passes read it.</summary>
    public int ReadCount { get; set; }

    /// <summary>Whether any surviving pass writes it.</summary>
    public bool IsWritten { get; set; }

    /// <summary>What it is being used as, as execution walks the passes.</summary>
    public ResourceState CurrentState { get; set; }

    /// <summary>The physical texture it was given, for a realised transient or an import.</summary>
    public TextureHandle Texture { get; set; }

    public TextureViewHandle View { get; set; }

    public BufferHandle Buffer { get; set; }

    /// <summary>Which pool entry it borrowed, so it can be given back at its last use.</summary>
    public int PoolSlot { get; set; } = -1;
}
