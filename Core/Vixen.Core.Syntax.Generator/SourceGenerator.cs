// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Vixen.Core.Syntax.Generator.Model;

namespace Vixen.Core.Syntax.Generator;

[Generator]
public class SourceGenerator : IIncrementalGenerator {
    /// <summary>The MSBuild property a project sets to say it is building a language.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Opt-in rather than on, and the reason is that this generator ships in a
    ///         package.</b> <c>Vixen.Core.Syntax</c>'s own description advertises the third-party
    ///         case — <i>"Shared by Raven, VXML and VCSS — each supplies its own Syntax.xml"</i> —
    ///         and a <c>Syntax.xml</c> with no generator produces nothing at all, so the generator
    ///         has to travel in the package. But <c>VXS0001</c> fired on every compilation that had
    ///         no <c>Syntax.xml</c>, which is every consumer that is not writing a language, and
    ///         under <c>TreatWarningsAsErrors</c> that is a build break rather than a warning. It
    ///         was the reason the package could not carry it (#1188).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An opt-in and not a disabled-by-default severity.</b> Turning the rule off by
    ///         default would remove the guard from the three in-tree consumers as well, which is a
    ///         worse trade: the diagnostic exists so that a project that imported the generator and
    ///         forgot its <c>AdditionalFiles</c> line hears about it, and a project that has said
    ///         it is building a language is exactly the one that can be told.
    ///     </para>
    ///     <para>
    ///         Surfaced through <c>CompilerVisibleProperty</c>, which the package's
    ///         <c>buildTransitive</c> props declares so that a consumer outside this repository has
    ///         only the property itself to set.
    ///     </para>
    /// </remarks>
    const string RequiredProperty = "build_property.vixensyntaxxmlrequired";

    static readonly DiagnosticDescriptor MissingSyntaxXml = new(
        "VXS0001",
        "Syntax.xml is missing",
        "The Syntax.xml file was not included in the project, so we are not generating source",
        "SyntaxGenerator",
        DiagnosticSeverity.Warning,
        true,
        "Reported only where VixenSyntaxXmlRequired is true. A compilation that merely references "
        + "Vixen.Core.Syntax is not building a language and has no Syntax.xml to forget."
    );

    static readonly DiagnosticDescriptor UnableToReadSyntaxXml = new(
        "VXS0002",
        "Syntax.xml could not be read",
        "The Syntax.xml file could not even be read. Does it exist?.",
        "SyntaxGenerator",
        DiagnosticSeverity.Error,
        true
    );

    static readonly DiagnosticDescriptor SyntaxXmlError = new(
        "VXS0003",
        "Syntax.xml has a syntax error",
        "{0}",
        "SyntaxGenerator",
        DiagnosticSeverity.Error,
        true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var syntaxXmlFiles = context.AdditionalTextsProvider
            .Where(at => Path.GetFileName(at.Path) == "Syntax.xml")
            .Collect();

        // ⚠ The key is `build_property.` followed by the MSBuild property name AS AUTHORED — the
        // generated editorconfig really does read `build_property.VixenSyntaxXmlRequired = true` —
        // and this looks it up lower-cased, which works because AnalyzerConfigOptions compares keys
        // ordinal-ignore-case. Verified rather than assumed: the A/B that proved the opt-in was a
        // compilation whose only difference was the property, and it reported VXS0001.
        var required = context.AnalyzerConfigOptionsProvider.Select(
            static (provider, _) =>
                provider.GlobalOptions.TryGetValue(RequiredProperty, out var value)
                && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        );

        context.RegisterSourceOutput(
            syntaxXmlFiles.Combine(required),
            static (context, input) => {
                var (syntaxXmlFiles, required) = input;
                var file = syntaxXmlFiles.SingleOrDefault();

                if (file == null) {
                    if (required) {
                        context.ReportDiagnostic(Diagnostic.Create(MissingSyntaxXml, null));
                    }

                    return;
                }

                var inputText = file.GetText();
                if (inputText == null) {
                    context.ReportDiagnostic(Diagnostic.Create(UnableToReadSyntaxXml, null));
                    return;
                }

                Tree tree;
                try {
                    var reader = XmlReader.Create(
                        new SourceTextReader(inputText),
                        new() { DtdProcessing = DtdProcessing.Prohibit }
                    );
                    var serializer = new XmlSerializer(typeof(Tree));
                    tree = (Tree)serializer.Deserialize(reader);
                } catch (InvalidOperationException ex) when (ex.InnerException is XmlException xmlException) {
                    var line = inputText.Lines[xmlException.LineNumber - 1]; // LineNumber is one-based.
                    var offset = xmlException.LinePosition - 1; // LinePosition is one-based
                    var position = line.Start + offset;
                    var span = new TextSpan(position, 0);
                    var lineSpan = inputText.Lines.GetLinePositionSpan(span);

                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            SyntaxXmlError,
                            Location.Create(file.Path, span, lineSpan),
                            xmlException.Message
                        )
                    );

                    return;
                }

                DoGeneration(tree, context, context.CancellationToken);
            }
        );
    }

    static void DoGeneration(
        Tree tree,
        SourceProductionContext context,
        CancellationToken cancellationToken
    ) {
        TreeFlattening.FlattenChildren(tree);

        AddResult(writer => SourceWriter.WriteMain(writer, tree, cancellationToken), "Syntax.xml.Main.Generated.cs");
        AddResult(
            writer => SourceWriter.WriteSyntax(writer, tree, cancellationToken),
            "Syntax.xml.Syntax.Generated.cs"
        );
        AddResult(
            writer => SourceWriter.WriteInternal(writer, tree, cancellationToken),
            "Syntax.xml.Internal.Generated.cs"
        );

        void AddResult(Action<TextWriter> writeFunction, string hintName) {
            // Write out the contents to a StringBuilder to avoid creating a single large string
            // in memory
            var stringBuilder = new StringBuilder();
            using (var textWriter = new StringWriter(stringBuilder)) {
                writeFunction(textWriter);
            }

            // And create a SourceText from the StringBuilder, once again avoiding allocating a single massive string
            using var stringBuilderReader = new StringBuilderReader(stringBuilder);
            var sourceText = SourceText.From(stringBuilderReader, stringBuilder.Length, Encoding.UTF8);
            context.AddSource(hintName, sourceText);
        }
    }
}
