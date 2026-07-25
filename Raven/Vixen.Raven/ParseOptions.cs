
namespace Vixen.Raven;

/// <summary>
///     Options controlling how source text is turned into a syntax tree.
/// </summary>
/// <remarks>
///     There are no options yet, so every instance is equivalent to every other and
///     equality is constant. <b>When the first option is added, both members below
///     must start reading it</b> — otherwise anything caching on a
///     <see cref="ParseOptions" /> key will silently reuse a tree parsed under
///     different settings.
/// </remarks>
public sealed class ParseOptions : IEquatable<ParseOptions> {
    public static ParseOptions Default { get; } = new();

    public bool Equals(ParseOptions? other) => other is not null;

    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj) || (obj is ParseOptions other && Equals(other));

    public override int GetHashCode() => typeof(ParseOptions).GetHashCode();
}
