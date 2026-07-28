; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VXNET1001 | Vixen.Net | Error | A replicated component has a field of a type that cannot be put on the wire.
VXNET1002 | Vixen.Net | Error | [Quantize] is on a field that is not a float.
VXNET1003 | Vixen.Net | Error | [Quantize] declares a range or a width that cannot be encoded with.
VXNET1004 | Vixen.Net | Warning | A replicated component has no fields to send.
VXNET2001 | Vixen.Net | Error | A remote call has an argument of a type that cannot be sent.
VXNET2002 | Vixen.Net | Error | A type declaring remote calls is not partial.
VXNET2003 | Vixen.Net | Error | A type declaring remote calls does not implement IRpcObject.
VXNET2004 | Vixen.Net | Error | A remote call returns something other than void.
VXNET2005 | Vixen.Net | Error | A handler is marked as both a ServerRpc and a ClientRpc.
VXNET2006 | Vixen.Net | Error | A type declaring remote calls is nested, generic, or not a class.
VXNET2007 | Vixen.Net | Error | [Quantize] is on an argument that is not a float.
