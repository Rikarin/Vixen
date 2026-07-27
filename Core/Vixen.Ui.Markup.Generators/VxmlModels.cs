// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Markup.Generators;

/// <summary>What the build told the generator about the project it is running in.</summary>
/// <param name="ProjectDirectory">
///     What paths are relative to, for the hint name and the namespace. Empty when MSBuild did not
///     say — a design-time build of a loose file, most often.
/// </param>
/// <param name="RootNamespace">The project's root namespace, or empty for the global one.</param>
internal readonly record struct ProjectOptions(string ProjectDirectory, string RootNamespace);

/// <summary>One <c>.vxml</c> file, reduced to the strings that decide what comes out of it.</summary>
/// <param name="FullPath">Where the file is, which is what <c>#line</c> points at.</param>
/// <param name="HintName">What the generated file is called inside the compilation.</param>
/// <param name="Namespace">Where the component's class goes, or empty for the global namespace.</param>
/// <param name="Text">The file's contents.</param>
/// <remarks>
///     ⚠ <b>This is the cache key of the whole generator, so it holds nothing but values.</b> An
///     <c>AdditionalText</c> is an object the host may hand out afresh on every compilation, so a
///     pipeline that carries one past the first step compares references and re-parses every
///     keystroke in an unrelated file. Reading the text early is what makes the expensive step —
///     lex, parse, bind, emit — depend on the file's contents and nothing else.
/// </remarks>
internal readonly record struct VxmlSource(string FullPath, string HintName, string Namespace, string Text);

/// <summary>A VXML diagnostic flattened into values a Roslyn one can be rebuilt from.</summary>
/// <param name="Id">The <c>VXML1002</c>-style code.</param>
/// <param name="Title">The descriptor's short title.</param>
/// <param name="Category">The descriptor's category.</param>
/// <param name="Message">The message, already formatted.</param>
/// <param name="Severity">How bad it is, in Roslyn's scale.</param>
/// <param name="FilePath">The <c>.vxml</c> it is about.</param>
/// <param name="Start">The character offset it starts at.</param>
/// <param name="Length">How many characters it covers.</param>
/// <param name="StartLine">Zero-based start line.</param>
/// <param name="StartCharacter">Zero-based start column.</param>
/// <param name="EndLine">Zero-based end line.</param>
/// <param name="EndCharacter">Zero-based end column.</param>
/// <remarks>
///     Flattened rather than carried, for the same reason as <see cref="VxmlSource" />: a
///     <c>Vixen.Core.Syntax</c> diagnostic holds a <c>Location</c> holding the whole
///     <c>SourceText</c>, which is a reference and would both defeat the cache and keep every
///     parsed file alive for as long as the generator is loaded.
/// </remarks>
internal readonly record struct ReportedDiagnostic(
    string Id,
    string Title,
    string Category,
    string Message,
    Microsoft.CodeAnalysis.DiagnosticSeverity Severity,
    string FilePath,
    int Start,
    int Length,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter
);

/// <summary>What one <c>.vxml</c> compiled to.</summary>
/// <param name="HintName">The generated file's name.</param>
/// <param name="Source">The C#, or null when the file did not compile.</param>
/// <param name="Diagnostics">Everything to report, errors and warnings alike.</param>
internal readonly record struct VxmlOutput(
    string HintName,
    string? Source,
    EquatableArray<ReportedDiagnostic> Diagnostics
);
