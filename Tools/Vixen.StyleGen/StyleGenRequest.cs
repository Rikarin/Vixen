// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.StyleGen;

/// <summary>What one run of the build step was asked to do.</summary>
/// <remarks>
///     A record rather than a bag of <c>string[]</c> handling inside <see cref="StyleGenRunner" />,
///     because the argument parsing and the work are the two halves worth testing apart: an option
///     nobody spelled the same way in the <c>.targets</c> and in the tool is the failure mode of every
///     build step, and it is only findable if the parse has a value to compare against.
/// </remarks>
internal sealed record StyleGenRequest {
    /// <summary>The <c>vixen.ui.yaml</c>, or <c>null</c> for the built-in defaults.</summary>
    internal string? Tokens { get; init; }

    /// <summary>Every file whose text is searched for class names.</summary>
    /// <remarks>
    ///     ⚠ <b>C# as well as markup, and that is the point of scanning rather than parsing.</b> Most
    ///     of the editor's chrome is built in code with <c>AddClass("flex")</c>, and a build step that
    ///     only looked at <c>.vxml</c> would emit nothing for it. <c>CandidateScanner</c> is
    ///     deliberately over-inclusive precisely so that it can be pointed at any text at all — a
    ///     false positive is one unused rule, and a false negative is a style silently missing at run
    ///     time.
    /// </remarks>
    internal IReadOnlyList<string> Scan { get; init; } = [];

    /// <summary>The hand-written sheets, in order, emitted before the utilities.</summary>
    /// <remarks>
    ///     ⚠ <b>Before, and unlayered, which is what makes a component rule beat a utility.</b> The
    ///     generated sheet is entirely inside <c>@layer utilities</c> and a layer loses to an
    ///     unlayered rule whatever the order — so emitting the base sheet <em>second</em> would make
    ///     it win for the wrong reason and hide a regression in the layering. <c>@apply</c> in these
    ///     files is expanded here, which is the one place it can be: it has to happen before the CSS
    ///     parser sees the text.
    /// </remarks>
    internal IReadOnlyList<string> Base { get; init; } = [];

    /// <summary>Class names to emit whether or not anything was seen to use them.</summary>
    internal IReadOnlyList<string> Safelist { get; init; } = [];

    /// <summary>Where to write the stylesheet, or <c>null</c> not to.</summary>
    internal string? Output { get; init; }

    /// <summary>Where to write the C# accessor, or <c>null</c> not to.</summary>
    internal string? Accessor { get; init; }

    /// <summary>The namespace the accessor is declared in.</summary>
    internal string Namespace { get; init; } = "Vixen.Ui.Generated";

    /// <summary>The accessor's type name.</summary>
    internal string Class { get; init; } = "VixenUtilityStyles";

    /// <summary>Whether the accessor is <c>public</c> rather than <c>internal</c>.</summary>
    internal bool Public { get; init; }

    /// <summary>Where to write the candidates the generator did not recognise, or <c>null</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Written to a file rather than warned about, and the asymmetry is deliberate.</b> The
    ///     scanner takes every run of characters that could be a class name, so most of what lands
    ///     here is prose out of a comment — a build that warned about each would be a build nobody
    ///     reads the output of. But a <i>misspelt</i> utility lands here too, and a misspelt utility
    ///     is a style that silently does nothing, which no compiler and no binder can see. So the
    ///     list is always written and a project's own test suite is what asks the narrower question
    ///     about it.
    /// </remarks>
    internal string? Report { get; init; }
}
