; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VXNET1001 | Vixen.Net | Error | A replicated component has a field of a type that cannot be put on the wire.
VXNET1002 | Vixen.Net | Error | [Quantize] is on a field that is not a float.
VXNET1003 | Vixen.Net | Error | [Quantize] declares a range or a width that cannot be encoded with.
VXNET1004 | Vixen.Net | Warning | A replicated component has no fields to send.
