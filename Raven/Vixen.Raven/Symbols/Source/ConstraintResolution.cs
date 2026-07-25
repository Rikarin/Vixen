using Vixen.Raven.Binding;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Symbols.Source;

/// <summary>Applies <c>where T : Base, Other</c> clauses to type parameters.</summary>
public static class ConstraintResolution {
    public static void Apply(
        IReadOnlyList<TypeParameterSymbol> typeParameters,
        SyntaxList<TypeParameterConstraintClauseSyntax> clauses,
        Binder binder
    ) {
        foreach (var clause in clauses) {
            var name = clause.Name.Identifier.ValueText;
            var parameter = typeParameters.FirstOrDefault(p => p.Name == name);
            if (parameter is null) {
                continue;
            }

            List<TypeSymbol> constraints = [];
            foreach (var constraint in clause.Constraints) {
                // `default` carries no type; only type constraints contribute.
                if (constraint is TypeConstraintSyntax typeConstraint) {
                    var type = binder.BindType(typeConstraint.Type);
                    if (!type.IsErrorType) {
                        constraints.Add(type);
                    }
                }
            }

            parameter.SetConstraintTypes(constraints.ToArray());
        }
    }
}
