using Vixen.Raven;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>The README's language example has to actually compile.</summary>
public class ReadmeExampleTests {
    [Fact]
    public void The_readme_language_example_compiles_cleanly() {
        var readme = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "README.md"));

        var start = readme.IndexOf("## Language Example", StringComparison.Ordinal);
        var open = readme.IndexOf("```typescript", start, StringComparison.Ordinal) + "```typescript\n".Length;
        var close = readme.IndexOf("```", open, StringComparison.Ordinal);
        var source = readme[open..close];

        var tree = SyntaxTree.ParseText(source, path: "README.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Readme", tree);
        Assert.Empty(compilation.GetDiagnostics());
    }
}
