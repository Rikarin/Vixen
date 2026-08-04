// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ai.Diagnostics;

/// <summary>What the AI overlay draws, how far, and in what colours.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>default</c> is a quiet style, not the usual one</b>, and it is the trap
///         <c>ConstraintGizmoStyle</c> paid for first: a struct's property initialisers do not run for
///         <c>default</c>, so a zeroed style has no categories, no range and no size. That is a real
///         style somebody would want — the overlay off — which is the only reason it is allowed to be
///         the zero one. <see cref="Default" /> is the usual one and it is <c>new()</c>.
///     </para>
///     <para>
///         ⚠ <b><see cref="Range" /> is what stops the overlay being useless in a crowd.</b> Forty
///         agents each labelled with their active path is a screen of text; drawing only what is near
///         the viewpoint is the difference between a tool and a screenshot of one. Zero means
///         everything, which is what a headless test wants.
///     </para>
/// </remarks>
public readonly record struct AiOverlayStyle {
    /// <summary>How far from the viewpoint an agent is still drawn, in metres.</summary>
    public const float DefaultRange = 40f;

    /// <summary>How tall a line of text is, in world units.</summary>
    public const float DefaultTextSize = 0.18f;

    /// <summary>How many agents are drawn at most, however near they are.</summary>
    public const int DefaultMaximumAgents = 16;

    /// <summary>The usual style: where and what, plus the sense shapes, out to forty metres.</summary>
    public static AiOverlayStyle Default => new();

    /// <summary>Everything, out to any distance — what a test uses, and what a close-up wants.</summary>
    public static AiOverlayStyle Everything => new() { Categories = AiDebugCategory.All, Range = 0f };

    /// <summary>Which categories are drawn.</summary>
    public AiDebugCategory Categories { get; init; } = AiDebugCategory.Default;

    /// <summary>How far from the viewpoint to draw, in metres. Zero is everywhere.</summary>
    public float Range { get; init; } = DefaultRange;

    /// <summary>How many agents to draw at most. Zero means the usual.</summary>
    public int MaximumAgents { get; init; } = DefaultMaximumAgents;

    /// <summary>How tall a line of text is, in world units. Zero means the usual.</summary>
    public float TextSize { get; init; } = DefaultTextSize;

    /// <summary>How high above an agent its readout starts, in metres.</summary>
    public float Headroom { get; init; } = 2.2f;

    /// <summary>How many rows of one section to draw before it is cut short.</summary>
    public int RowsPerSection { get; init; } = 8;

    /// <summary>The colour of an agent that is running normally.</summary>
    public Color4 Running { get; init; } = new(0.4f, 0.8f, 1f, 1f);

    /// <summary>The colour of one that succeeded.</summary>
    public Color4 Succeeded { get; init; } = new(0.4f, 1f, 0.5f, 1f);

    /// <summary>The colour of one that failed.</summary>
    public Color4 Failed { get; init; } = new(1f, 0.45f, 0.35f, 1f);

    /// <summary>The colour of the live row of a list — the active node, the chosen action.</summary>
    public Color4 Live { get; init; } = new(1f, 0.9f, 0.35f, 1f);

    /// <summary>The colour of everything that is merely being reported.</summary>
    public Color4 Quiet { get; init; } = new(0.65f, 0.65f, 0.7f, 1f);

    /// <summary>The colour of the selected agent, and of anything <see cref="AiDiagnosis" /> flagged.</summary>
    public Color4 Attention { get; init; } = new(1f, 0.35f, 0.8f, 1f);

    /// <summary>Creates the usual style.</summary>
    public AiOverlayStyle() {
    }

    /// <summary>How tall a line of text actually is, with a zeroed style answering the usual size.</summary>
    public float Text => TextSize > 0f ? TextSize : DefaultTextSize;

    /// <summary>How many agents are actually drawn, ditto.</summary>
    public int Agents => MaximumAgents > 0 ? MaximumAgents : DefaultMaximumAgents;

    /// <summary>Whether a category is on.</summary>
    /// <param name="category">The category.</param>
    /// <returns>Whether it is drawn.</returns>
    public bool Shows(AiDebugCategory category) => (Categories & category) != 0;

    /// <summary>What colour a status reads as.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The colour.</returns>
    public Color4 ColourOf(ActionStatus status) => status switch {
        ActionStatus.Succeeded => Succeeded,
        ActionStatus.Failed => Failed,
        _ => Running
    };
}
