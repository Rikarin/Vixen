// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Shaders.Generators;

/// <summary>
///     Turns Raven's <c>.reflect.json</c> files into typed C# keys and constant-buffer writers.
/// </summary>
/// <remarks>
///     <para>
///         Add a shader's reflection to a project as an <c>AdditionalFiles</c> item and its bindings
///         appear in <c>Vixen.Shaders.Generated</c>. The reflection is produced by
///         <c>raven compile --emit-reflection</c>, which the content build runs anyway — so the
///         generator adds no step, only a consumer for a file that already exists.
///     </para>
///     <para>
///         <strong>Why an analyzer rather than a build task.</strong> The generated code has to be
///         visible to the code that uses it, in the same compilation, with rename and go-to-definition
///         working in the editor before anything is built. A task writing <c>.cs</c> into
///         <c>obj/</c> gets there eventually and gets there wrong for the first build after a shader
///         changes.
///     </para>
/// </remarks>
[Generator]
public class ShaderBindingsGenerator : IIncrementalGenerator {
    internal const string Suffix = ".reflect.json";

    static readonly DiagnosticDescriptor Unreadable = new(
        "VXSH0001",
        "Shader reflection could not be read",
        "'{0}' was included as shader reflection but could not be read",
        "ShaderBindings",
        DiagnosticSeverity.Warning,
        true
    );

    static readonly DiagnosticDescriptor Malformed = new(
        "VXSH0002",
        "Shader reflection is malformed",
        "'{0}' is not usable shader reflection: {1}",
        "ShaderBindings",
        DiagnosticSeverity.Error,
        true
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var files = context.AdditionalTextsProvider
            .Where(static text => text.Path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase));

        context.RegisterSourceOutput(files, static (production, text) => Generate(production, text));
    }

    static void Generate(SourceProductionContext context, AdditionalText file) {
        if (file.GetText(context.CancellationToken)?.ToString() is not { } content) {
            context.ReportDiagnostic(Diagnostic.Create(Unreadable, Location.None, file.Path));
            return;
        }

        var shaderName = ShaderNameOf(file.Path);

        try {
            var reflection = ReflectionReader.Read(content);
            var source = BindingsEmitter.Emit(shaderName, reflection, Path.GetFileName(file.Path));
            context.AddSource($"{shaderName}.Bindings.g.cs", SourceText.From(source, Encoding.UTF8));
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            // Reported rather than thrown: an analyzer that throws takes the whole build down with a
            // stack trace naming Roslyn, and the file at fault is the one thing the author needs.
            context.ReportDiagnostic(Diagnostic.Create(Malformed, Location.None, file.Path, exception.Message));
        }
    }

    /// <summary>The shader's name, which is the file name with the suffix removed.</summary>
    /// <remarks>
    ///     Raven names the file after the shader, so this needs no field in the document — and a
    ///     renamed file producing a renamed class is the behaviour a build expects.
    /// </remarks>
    internal static string ShaderNameOf(string path) {
        var name = Path.GetFileName(path);
        return name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
            ? name.Substring(0, name.Length - Suffix.Length)
            : Path.GetFileNameWithoutExtension(path);
    }
}
