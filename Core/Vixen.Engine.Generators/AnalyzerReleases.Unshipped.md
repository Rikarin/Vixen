; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VXS0401 | Vixen.Engine | Warning | A component is generic, so a compiled scene cannot name it
VXS0402 | Vixen.Engine | Warning | A described behaviour has no parameterless constructor, so a scene cannot restore it
VXS0403 | Vixen.Engine | Warning | A behaviour is generic, so a scene cannot name it
VXS0404 | Vixen.Engine | Warning | A [GameSystem] type does not implement ISystem, so nothing could add it to a frame
VXS0405 | Vixen.Engine | Warning | A [GameSystem] type does not have exactly one public constructor, so what it needs is ambiguous
VXS0406 | Vixen.Engine | Warning | A [GameSystem] type is abstract or generic, so there is no one system to add
VXS0407 | Vixen.Engine | Warning | An [InferAccess] type does not implement ISystem, so nothing would read the declaration
VXS0408 | Vixen.Engine | Warning | An [InferAccess] type is not a partial top-level non-generic class, so the declaration has nowhere to go
VXS0409 | Vixen.Engine | Warning | An [InferAccess] type already implements IDeclaredAccess, so the inferred declaration is dropped
VXS0410 | Vixen.Engine | Warning | An [InferAccess] type also carries [Reads] or [Writes], so the attributes win and nothing is inferred
VXS0411 | Vixen.Engine | Warning | An [InferAccess] type's body yielded no component access, so it stays undeclared and conflicts with everything
VXS0412 | Vixen.Engine | Error | A Behavior holds an Entity, which is a slot in a running process and does not survive being written down
VXS0413 | Vixen.Engine | Error | A Behavior holds a copy of a component the world is already the authority on
VXS0414 | Vixen.Engine | Warning | A structural change made inside a query body, a chunk walk or a struct visitor
