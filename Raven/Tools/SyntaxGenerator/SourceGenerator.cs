using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SyntaxGenerator;
using SyntaxGenerator.Model;
using System.Collections.Immutable;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

[Generator]
public class SourceGenerator : IIncrementalGenerator {
    static readonly DiagnosticDescriptor MissingSyntaxXml = new(
        "RVN0001",
        "Syntax.xml is missing",
        "The Syntax.xml file was not included in the project, so we are not generating source",
        "SyntaxGenerator",
        DiagnosticSeverity.Warning,
        true
    );

    static readonly DiagnosticDescriptor UnableToReadSyntaxXml = new(
        "RVN0002",
        "Syntax.xml could not be read",
        "The Syntax.xml file could not even be read. Does it exist?.",
        "SyntaxGenerator",
        DiagnosticSeverity.Error,
        true
    );

    static readonly DiagnosticDescriptor SyntaxXmlError = new(
        "RVN0002",
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


        context.RegisterSourceOutput(
            syntaxXmlFiles,
            static (context, syntaxXmlFiles) => {
                var input = syntaxXmlFiles.SingleOrDefault();

                if (input == null) {
                    context.ReportDiagnostic(Diagnostic.Create(MissingSyntaxXml, null));
                    return;
                }

                var inputText = input.GetText();
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
                            Location.Create(input.Path, span, lineSpan),
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
        AddResult(writer => SourceWriter.WriteSyntax(writer, tree, cancellationToken), "Syntax.xml.Syntax.Generated.cs");
        AddResult(writer => SourceWriter.WriteInternal(writer, tree, cancellationToken), "Syntax.xml.Internal.Generated.cs");

        void AddResult(Action<TextWriter> writeFunction, string hintName) {
            // Write out the contents to a StringBuilder to avoid creating a single large string
            // in memory
            var stringBuilder = new StringBuilder();
            using (var textWriter = new StringWriter(stringBuilder)) {
                writeFunction(textWriter);
            }

            // And create a SourceText from the StringBuilder, once again avoiding allocating a single massive string
            var sourceText = SourceText.From(
                new StringBuilderReader(stringBuilder),
                stringBuilder.Length,
                Encoding.UTF8
            );
            context.AddSource(hintName, sourceText);
        }
    }
}
