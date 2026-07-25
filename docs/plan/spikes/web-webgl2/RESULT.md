# Spike: Silk.NET.OpenGLES on .NET 10 `browser-wasm` — ✅ **PASSED**

Run on macOS arm64, .NET SDK 10.0.302, `wasm-tools` + `wasm-experimental` workload 10.0.110,
Emscripten 3.1.56 (pinned by the workload), Silk.NET 2.23.0, Chromium.

This retires risk **R1**, which the plan had ranked *likelihood high · impact high* and scheduled a
one-week timebox for. It took an afternoon and the answer is yes.

## What was proven

A rendered triangle, driven entirely by `Silk.NET.OpenGLES` calling into the browser's WebGL2 context
from managed C# compiled to WebAssembly. Step-by-step verification from the running page:

```
step1 :enter
step2 :DllImport GetProcAddress('glClear')=0x4F8      ← emscripten proc-address resolver works
step3 :InitAttrs ok
step4 :CreateContext=0x1                             ← real WebGL2 context
step5 :MakeCurrent=0
step6 :STATIC DllImport glClearColor ok
step7 :Silk GL object constructed
step8 :SILK gl.ClearColor ok                         ← Silk.NET dynamic fn-ptr dispatch works
step9 :SILK version=OpenGL ES 3.0 (WebGL 2.0 (OpenGL ES 3.0 Chromium))
step10:begin triangle
step11:CreateShader=2
step12:ShaderSource(string) ok                       ← string marshalling
step13:CompileShader ok
step14:GetShader(out int)=1                          ← out-param / int* signature
step15:GetShaderInfoLog len=0
step16:fs compiled=1
step17:LinkProgram=1
step18:VAO=5
step19:VBO=6
step20:BufferData(void*) ok                          ← raw pointer + fixed
step21:VertexAttribPointer ok
step22:DrawArrays ok, glGetError=Points               ← "Points" is GLEnum 0 == GL_NO_ERROR
ALL OK
```

Every P/Invoke shape the RHI will need works: string in, `out int`, `void*` buffer upload, struct by
`ref`, and — critically — Silk.NET's runtime-resolved function-pointer dispatch.

## The bridge, in full

This is the entire platform-specific surface. ~40 lines.

```csharp
[DllImport("*", EntryPoint = "emscripten_GetProcAddress")]
static extern nint GetProcAddress([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

[DllImport("*", EntryPoint = "emscripten_webgl_init_context_attributes")]
static extern void InitAttrs(ref Attrs a);

[DllImport("*", EntryPoint = "emscripten_webgl_create_context")]
static extern nint CreateContext([MarshalAs(UnmanagedType.LPUTF8Str)] string target, ref Attrs a);

[DllImport("*", EntryPoint = "emscripten_webgl_make_context_current")]
static extern int MakeCurrent(nint ctx);

// EmscriptenWebGLContextAttributes — 14 × int32 in Emscripten 3.1.56
[StructLayout(LayoutKind.Sequential)]
struct Attrs { public int A,D,S,Aa,Pm,Pd,Pp,Fi,Maj,Min,Ee,Es,Pc,Ro; }

// wiring
var attrs = new Attrs();
InitAttrs(ref attrs);
attrs.Maj = 2; attrs.Min = 0; attrs.D = 1;         // WebGL2 + depth
var ctx = CreateContext("#canvas", ref attrs);
MakeCurrent(ctx);
var gl = new GL(new LamdaNativeContext(GetProcAddress));   // ← Silk.NET.Core
```

`DllImport("*")` means "resolve from the statically linked main module", which is how the .NET WASM
SDK binds Emscripten's own exports. The generated `obj/.../pinvoke-table.h` confirms it binds the real
symbol rather than a stub:

```c
void * emscripten_GetProcAddress (void *);

static PinvokeImport _2A__imports [] = {
    {"emscripten_GetProcAddress", emscripten_GetProcAddress}, // wasmtest
    ...
};
```

`LamdaNativeContext` (note the upstream typo) is the load-bearing Silk.NET type: it adapts any
`Func<string, nint>` into the `INativeContext` every Silk.NET binding resolves entry points through.
Because of it, **no Silk.NET fork or patch is needed** — the browser is just another proc-address
source.

## Required project configuration

```xml
<Project Sdk="Microsoft.NET.Sdk.WebAssembly">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>          <!-- NOT net10.0-browser -->
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <WasmBuildNative>true</WasmBuildNative>             <!-- required: relink with emcc -->
    <InvariantGlobalization>true</InvariantGlobalization>
    <PublishTrimmed>true</PublishTrimmed>
    <EmccExtraLDFlags>-lGL -sMAX_WEBGL_VERSION=2 -sMIN_WEBGL_VERSION=2</EmccExtraLDFlags>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Silk.NET.Core" Version="2.23.0" />
    <PackageReference Include="Silk.NET.OpenGLES" Version="2.23.0" />
  </ItemGroup>
</Project>
```

## ⚠ The trap: silent WebGL1 downgrade

Omitting `-sMAX_WEBGL_VERSION=2` does **not** produce an error. Verified by building the identical
code without the flag:

```
step4 :CreateContext=0x1                                        ← succeeds!
step9 :SILK version=OpenGL ES 2.0 (WebGL 1.0 (OpenGL ES 2.0 Chromium))   ← silently WebGL1
step14:GetShader(out int)=0                                     ← #version 300 es fails to compile
EX ArgumentOutOfRangeException: ArgumentOutOfRange_IndexLength   ← from GetShaderInfoLog
```

Three compounding problems: the context request for `majorVersion = 2` is silently satisfied with a
WebGL1 context; the ES 3.00 shader then fails to compile; and Silk.NET's `GetShaderInfoLog` throws
`ArgumentOutOfRangeException` instead of returning the compile error, hiding the actual cause.

**Mitigations for the real backend:**
1. Assert `glGetString(GL_VERSION)` contains `WebGL 2` immediately after context creation and fail
   with an explicit, actionable message naming the missing emcc flag.
2. Wrap `GetShaderInfoLog`/`GetProgramInfoLog` — query the length first and return `""` on `<= 0`
   rather than letting Silk.NET throw.
3. Put the emcc flags in `Vixen.Platform.Web`'s `.targets` so consumers cannot omit them.

## Payload size — an order of magnitude better than the plan assumed

Clean `dotnet publish -c Release`, measuring only Brotli assets (what a browser actually downloads):

| Configuration | Brotli | Uncompressed |
|---|---|---|
| Silk.NET + WebGL2 triangle, default | **1.99 MB** | 7.09 MB |
| + `InvariantGlobalization` + `PublishTrimmed` | **0.93 MB** | — |

Breakdown of the 1.99 MB build:

| Asset | Brotli |
|---|---|
| `dotnet.native.wasm` (the Mono runtime) | 911 KB |
| `System.Private.CoreLib.wasm` | 344 KB |
| `icudt_CJK` / `icudt_no_CJK` / `icudt_EFIGS` | 243 + 217 + 140 KB |
| `Silk.NET.OpenGLES.wasm` | **25 KB** (from ~2 MB unpublished) |
| `Silk.NET.Core.wasm` | 6 KB |

Two things stand out. **ICU is ~600 KB of the default payload and is fully removable** — the plan
already specifies `InvariantGlobalization`, which is worth more here than anywhere else. And **the
trimmer reduces Silk.NET.OpenGLES to 25 KB**, which means the enormous generated bindings cost
essentially nothing for the subset actually called.

The verified floor is therefore **~930 KB Brotli** for a working WebGL2 app, and the engine's own code
adds to that from a sub-1-MB baseline rather than a multi-megabyte one. The plan's earlier "tens of
megabytes" figure was wrong.

## Runtime facts confirmed

Installed workload packs, which settle the Mono question definitively:

```
Microsoft.NETCore.App.Runtime.Mono.browser-wasm              ← the runtime. Mono, by name.
Microsoft.NETCore.App.Runtime.Mono.multithread.browser-wasm
Microsoft.NETCore.App.Runtime.AOT.osx-arm64.Cross.browser-wasm  ← AOT cross-compiler…
Microsoft.NET.Runtime.MonoAOTCompiler.Task                      ← …which is *Mono* AOT
Microsoft.NET.Runtime.MonoTargets.Sdk
Microsoft.NET.Runtime.Emscripten.3.1.56.{Sdk,Node,Python,Cache}.osx-arm64
```

- `Microsoft.DotNet.ILCompiler.LLVM` (NativeAOT for wasm) is **not on nuget.org at all** — it exists
  only on the `dotnet-experimental` Azure feed, and every version there is prerelease
  (newest `10.0.0-rc.1.26357.1`). No ILCompiler pack is installed by `wasm-tools`.
- "AOT on WASM" therefore means **Mono AOT** (`RunAOTCompilation=true`), not NativeAOT. These are
  different things and the distinction matters when reasoning about what the trimmer and the
  source-generator-only discipline buy us.
- `Silk.NET.Windowing` 2.23.0 has TFM groups for `netcoreapp3.1`, `netstandard2.0/2.1`, `net5.0`,
  `net6.0`, `net7.0-android33.0`, `net7.0-ios16.1`, `net7.0-maccatalyst16.1` — **no browser group**, and
  no Silk.NET package mentions browser/wasm/emscripten anywhere. Windowing, surface, and input on the
  web are ours to write, as the plan already assumed.

## Reproducing

```bash
# a throwaway SDK, so the system install is untouched
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ./dotnet-local --no-path
export DOTNET_ROOT=$PWD/dotnet-local && export PATH=$DOTNET_ROOT:$PATH
dotnet workload install wasm-tools wasm-experimental

dotnet new wasmbrowser -o webgl2spike
# copy Program.cs, wasmtest.csproj, wwwroot/index.html, wwwroot/main.js from this folder
dotnet publish -c Release
cd bin/Release/net10.0/publish/wwwroot && python3 -m http.server 8099
```

Note `dotnet workload install` writes into the SDK directory. On a stock macOS install that is
root-owned (`/usr/local/share/dotnet`) and needs `sudo`; a user-local SDK as above avoids that.
