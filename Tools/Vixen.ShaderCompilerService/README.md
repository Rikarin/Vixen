# Vixen.ShaderCompilerService

Compiles shader variants for a device that has no compiler.

A phone or a console has no Raven and no shader sources. Without this, every change to a `.rvn` means
a content build and a redeploy — a loop long enough that people stop making the change. With it, the
device asks over TCP, this machine compiles, and the device caches what comes back.

Stride's `EffectCompilerServer` pattern, and docs/plan/06 says it is worth building early. It is.

```bash
vixen-shader-compiler Raven/Library --cache .shadercache --any
```

| | |
|---|---|
| `<path>…` | directories to search for `.rvn` files, or the files themselves |
| `--target, -t` | default backend: `spirv` or `glsl` |
| `--reference, -r` | a compiled `.rvnlib` to bind against; repeatable |
| `--cache, -c` | keep compiled variants here, so a restart costs nothing |
| `--port, -p` | what to listen on (default 9930) |
| `--any` | bind every interface rather than loopback, for a device on the same network |

The device side is `RemoteEffectSource` in `Vixen.Shaders`:

```csharp
var remote = new RemoteEffectSource("192.168.1.20", 9930);
var cache = new EffectDiskCache(Path.Combine(writable, "shaders"), "spirv", remote);

effects.AddProvider(new EffectSourceProvider(cache, new EffectLoader(device)));
```

## Two caches, for two different things

The one on the server saves the *compilation*: five devices asking for one variant is one
compilation, and restarting this process costs nothing because the entries are still on disk. The one
on the device saves the *round trip*: the second run of the game does not ask at all.

## The target is asked for, not assumed

A phone wants GLSL ES while the laptop serving it builds SPIR-V for itself all day. A server that
cannot produce what was asked for says so, rather than sending back modules the device will fail to
create.

## It is a development tool

No TLS, no authentication, no access control: anything that reaches the port gets whatever this can
compile. Keep it behind a firewall. It binds loopback unless told otherwise for that reason.

A shader that does not compile sends its diagnostics back rather than closing the connection — the
service outlives every device that connects to it, and the person editing the shader is the one who
needs to read them.
