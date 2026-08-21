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
