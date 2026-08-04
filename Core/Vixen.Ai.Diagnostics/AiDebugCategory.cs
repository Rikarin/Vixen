// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Ai.Diagnostics;

/// <summary>Which parts of the AI debugger are drawn.</summary>
/// <remarks>
///     <para>
///         <b>Unreal's numbered categories, and the numbering is the feature.</b> Its gameplay
///         debugger is one key that opens one overlay and then digits that turn parts of it on and
///         off, and it is the most-used AI feature in that engine because the alternative — a menu of
///         checkboxes somewhere else — is a thing you stop doing while chasing a bug.
///     </para>
///     <para>
///         ⚠ <b>Flags rather than a mode.</b> "The plan and the senses, but not the blackboard" is the
///         ordinary request; an enum would make every combination a member and the useful ones the
///         ones nobody thought of.
///     </para>
/// </remarks>
[Flags]
public enum AiDebugCategory {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Where each agent is, what it is running, and how that is getting on. Category 1.</summary>
    Agent = 1 << 0,

    /// <summary>
    ///     What it is doing in detail: the active path, the scored candidates, the plan. Category 2.
    /// </summary>
    Doing = 1 << 1,

    /// <summary>
    ///     Why: a decorator's last answer, a consideration's factor, an unmet condition. Category 3.
    /// </summary>
    Why = 1 << 2,

    /// <summary>Its data: the blackboard, live, and a GOAP agent's world keys. Category 4.</summary>
    Data = 1 << 3,

    /// <summary>Its senses: the perceived list, and a line to each thing it can sense. Category 5.</summary>
    Senses = 1 << 4,

    /// <summary>The sight cones and hearing radii themselves, in the world. Category 6.</summary>
    Shapes = 1 << 5,

    /// <summary>What <see cref="AiDiagnosis" /> makes of the recorded log. Category 7.</summary>
    Findings = 1 << 6,

    /// <summary>Everything.</summary>
    All = Agent | Doing | Why | Data | Senses | Shapes | Findings,

    /// <summary>What is on when somebody first presses the key: where and what, and the shapes.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <see cref="All" />.</b> Every category at once over a dozen agents is a screen of
    ///     overlapping text, which reads as the tool being broken; the default is the two questions
    ///     somebody has before they know which agent they care about.
    /// </remarks>
    Default = Agent | Shapes
}

/// <summary>Turning categories on and off by their number, which is what a key press does.</summary>
public static class AiDebugCategories {
    static readonly AiDebugCategory[] Numbered = [
        AiDebugCategory.Agent,
        AiDebugCategory.Doing,
        AiDebugCategory.Why,
        AiDebugCategory.Data,
        AiDebugCategory.Senses,
        AiDebugCategory.Shapes,
        AiDebugCategory.Findings
    ];

    /// <summary>How many numbered categories there are.</summary>
    public static int Count => Numbered.Length;

    /// <summary>The category a digit names.</summary>
    /// <param name="number">The digit, from one.</param>
    /// <returns>The category, or <see cref="AiDebugCategory.None" /> when there is no such digit.</returns>
    public static AiDebugCategory Of(int number) =>
        number >= 1 && number <= Numbered.Length ? Numbered[number - 1] : AiDebugCategory.None;

    /// <summary>What a category is called, lower case.</summary>
    /// <param name="category">The category.</param>
    /// <returns>Its name.</returns>
    public static string NameOf(AiDebugCategory category) => category switch {
        AiDebugCategory.Agent => "agent",
        AiDebugCategory.Doing => "doing",
        AiDebugCategory.Why => "why",
        AiDebugCategory.Data => "data",
        AiDebugCategory.Senses => "senses",
        AiDebugCategory.Shapes => "shapes",
        AiDebugCategory.Findings => "findings",
        _ => "none"
    };

    /// <summary>Flips one category of a set.</summary>
    /// <param name="categories">The set.</param>
    /// <param name="category">Which one.</param>
    /// <returns>The set with it flipped.</returns>
    public static AiDebugCategory Toggle(AiDebugCategory categories, AiDebugCategory category) =>
        (categories & category) != 0 ? categories & ~category : categories | category;

    /// <summary>Which section of a snapshot a category shows, if it shows one.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The category that draws it.</returns>
    public static AiDebugCategory For(AiDebugSection section) => section switch {
        AiDebugSection.Doing => AiDebugCategory.Doing,
        AiDebugSection.Why => AiDebugCategory.Why,
        AiDebugSection.Data => AiDebugCategory.Data,
        _ => AiDebugCategory.Senses
    };
}
