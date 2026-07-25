using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
/// Golden-file parser tests. Each fixture <c>Fixtures/&lt;name&gt;.rvn</c> is parsed,
/// dumped with <see cref="SyntaxDumper"/>, and compared against the committed
/// <c>Fixtures/&lt;name&gt;.tree</c> snapshot.
///
/// To (re)generate snapshots after an intentional change, run the suite with
/// the environment variable <c>UPDATE_GOLDEN=1</c>; the <c>.tree</c> files are
/// rewritten from the current output. Review the diff before committing.
/// </summary>
public class GoldenSyntaxTests {
    [Theory]
    [InlineData("package_imports")]
    public void Matches_golden(string name) {
        var rvnPath = FixturePath(name + ".rvn");
        var goldenPath = FixturePath(name + ".tree");

        var text = File.ReadAllText(rvnPath);
        var tree = SyntaxTree.ParseText(text);
        var actual = Normalize(SyntaxDumper.Dump(tree.GetRoot()));

        if (ShouldUpdate || !File.Exists(goldenPath)) {
            File.WriteAllText(goldenPath, actual);
            Assert.Fail($"Golden '{name}.tree' was (re)generated. Review the diff and re-run.");
        }

        var expected = Normalize(File.ReadAllText(goldenPath));

        if (expected != actual) {
            // Leave the mismatching output next to the golden for easy diffing.
            File.WriteAllText(goldenPath + ".actual", actual);
        }

        Assert.Equal(expected, actual);
    }

    static bool ShouldUpdate =>
        Environment.GetEnvironmentVariable("UPDATE_GOLDEN") is "1" or "true";

    static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    // bin/Debug/net10.0 -> Tests project root -> Fixtures
    static string FixturePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", file);
}
