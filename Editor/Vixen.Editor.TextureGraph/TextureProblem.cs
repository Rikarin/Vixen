// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.TextureGraph;

/// <summary>How much a problem found in a plan matters.</summary>
/// <remarks>
///     ⚠ <b>There were two states before <a href="https://github.com/Rikarin/Vixen/issues/692">#692</a>
///     — "fine" and "refused" — and the case that made a third necessary is a clipped radius.</b> A
///     plan whose blur is wider than the kernel's loop still bakes, and the picture it bakes is a
///     different material from the one the same graph produces at the resolution it was authored at.
///     Refusing it would refuse a bake an artist may well want; saying nothing is
///     <see href="https://github.com/Rikarin/Vixen/issues/678">#678</see>, which cost a measured
///     26/255 and no message anywhere.
/// </remarks>
public enum TextureProblemSeverity : byte {
    /// <summary>The plan bakes, and the picture is not the one the graph describes.</summary>
    Warning = 0,

    /// <summary>The plan does not bake. <see cref="TexturePlanEvaluator.Evaluate" /> throws.</summary>
    Error = 1
}

/// <summary>One thing wrong with a plan, and how much it matters.</summary>
/// <param name="Severity">Whether the plan is refused or merely reported on.</param>
/// <param name="Message">What to put in front of whoever chose the number.</param>
/// <remarks>
///     <b>A sentence rather than a code</b>, for the reason <see cref="TexturePlan.Validate" /> was
///     already a list of sentences: the reader is an artist looking at a resolution field, and every
///     message here names the op, the number authored and the number it resolved to at this bake.
/// </remarks>
public readonly record struct TextureProblem(TextureProblemSeverity Severity, string Message) {
    /// <summary>A problem that stops the bake.</summary>
    /// <param name="message">What is wrong.</param>
    /// <returns>The problem.</returns>
    public static TextureProblem Refusal(string message) => new(TextureProblemSeverity.Error, message);

    /// <summary>A problem the bake reports and then bakes anyway.</summary>
    /// <param name="message">What is wrong.</param>
    /// <returns>The problem.</returns>
    public static TextureProblem Caution(string message) => new(TextureProblemSeverity.Warning, message);

    /// <inheritdoc />
    public override string ToString() => Message;
}
