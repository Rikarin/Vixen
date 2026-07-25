using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Text;
using Xunit;

namespace Vixen.Core.Syntax.Tests;

/// <summary>
///     The shared diagnostics model. One implementation serves Raven, VXML and VCSS, so
///     the editor's error list, the engine log and the on-screen overlay agree.
/// </summary>
public class DiagnosticsTests {
    static readonly DiagnosticDescriptor Unexpected =
        new("TOY0001", "Unexpected word", "Unexpected word '{0}'", "Syntax", DiagnosticSeverity.Error);

    static readonly DiagnosticDescriptor Advice =
        new("TOY0002", "Could be shorter", "This could be shorter", "Style", DiagnosticSeverity.Warning);

    static Location At(string text, TextSpan span) => Location.Create("toy.txt", span, SourceText.From(text));

    [Fact]
    public void A_message_is_the_template_filled_with_the_arguments() {
        var diagnostic = Diagnostic.Create(Unexpected, Location.None, "banana");

        Assert.Equal("Unexpected word 'banana'", diagnostic.GetMessage());
        Assert.Equal("TOY0001", diagnostic.Id);
        Assert.True(diagnostic.IsError);
    }

    [Fact]
    public void A_template_with_no_arguments_is_used_verbatim() =>
        Assert.Equal("This could be shorter", Diagnostic.Create(Advice, Location.None).GetMessage());

    [Fact]
    public void Severity_comes_from_the_descriptor() {
        Assert.False(Diagnostic.Create(Advice, Location.None).IsError);
        Assert.Equal(DiagnosticSeverity.Warning, Diagnostic.Create(Advice, Location.None).Severity);
    }

    [Fact]
    public void A_null_location_becomes_None() =>
        Assert.True(Diagnostic.Create(Unexpected, null!, "x").Location.IsNone);

    [Fact]
    public void ToString_reports_file_position_severity_id_and_message() {
        var diagnostic = Diagnostic.Create(Unexpected, At("one\ntwo", TextSpan.FromBounds(4, 7)), "two");

        // One-based line and column, as compilers print them.
        Assert.Equal("toy.txt(2,1): error TOY0001: Unexpected word 'two'", diagnostic.ToString());
    }

    [Fact]
    public void An_unpositioned_diagnostic_omits_the_location() =>
        Assert.Equal(
            "error TOY0001: Unexpected word 'x'",
            Diagnostic.Create(Unexpected, Location.None, "x").ToString()
        );

    [Fact]
    public void A_bag_starts_empty_and_tracks_errors_separately_from_warnings() {
        var bag = new DiagnosticBag();
        Assert.True(bag.IsEmpty);
        Assert.False(bag.HasErrors);

        bag.Add(Advice, Location.None);
        Assert.False(bag.IsEmpty);
        Assert.False(bag.HasErrors);

        bag.Add(Unexpected, Location.None, "x");
        Assert.True(bag.HasErrors);
    }

    [Fact]
    public void A_bag_preserves_insertion_order() {
        var bag = new DiagnosticBag();
        bag.Add(Unexpected, Location.None, "first");
        bag.AddRange([Diagnostic.Create(Advice, Location.None)]);

        Assert.Equal(["TOY0001", "TOY0002"], bag.ToArray().Select(d => d.Id));
    }

    [Fact]
    public void A_snapshot_does_not_change_when_the_bag_does() {
        var bag = new DiagnosticBag();
        bag.Add(Advice, Location.None);

        var snapshot = bag.ToArray();
        bag.Add(Unexpected, Location.None, "x");

        Assert.Single(snapshot);
        Assert.Equal(2, bag.ToArray().Length);
    }

    [Fact]
    public void An_unbacked_location_reports_a_zero_line_span() {
        var location = Location.None;

        Assert.Equal(default, location.GetLineSpan());
        Assert.Equal("<none>", location.ToString());
    }
}
