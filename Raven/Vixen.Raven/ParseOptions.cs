namespace Vixen.Raven;

public sealed class ParseOptions : IEquatable<ParseOptions> {
    public bool Equals(ParseOptions? other) => throw new NotImplementedException();

    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj) || (obj is ParseOptions other && Equals(other));

    public override int GetHashCode() => throw new NotImplementedException();
}
