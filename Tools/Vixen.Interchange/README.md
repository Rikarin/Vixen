# Vixen.Interchange

Reading and writing the files other tools make: meshes through Assimp, and images through
StbImageSharp and Vixen's own DDS and KTX2 readers. It knows nothing about a project, an asset or a
renderer. `IModelReadOptions` is what a reader needs to be told, and the editor's
`ModelImportSettings` implements it; a host with no settings of its own uses `ModelReadOptions`.

Nothing has to be installed to read a model. `Silk.NET.Assimp` brings the native, and `AssimpLoader`
finds it through `Vixen.Platform.Native`: in `runtimes/<rid>/native/` for a build, beside the executable
for a publish, and only then on the machine, under the binding's own soname. That is `libassimp.so.5` on
Linux. The package also carries a 6, which the binding's ABI does not match and nothing opens, so
anything that lays out its own files can leave it behind. Beyond that the library needs `glibc` 2.33,
`libstdc++` from GCC 11 and `libgcc_s`; zlib is inside it.

`CubeLut` stayed in `Vixen.Editor.Assets`, because it still uses `Vixen.Rendering`.
