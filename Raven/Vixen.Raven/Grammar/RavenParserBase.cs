using Antlr4.Runtime;

namespace Vixen.Raven.Grammar;

public abstract class RavenParserBase(ITokenStream input) : Parser(input);
