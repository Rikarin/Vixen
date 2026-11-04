# Vixen.Interchange

Reading and writing the files other tools make: meshes through Assimp, and images through
StbImageSharp and Vixen's own DDS and KTX2 readers. It knows nothing about a project, an asset or a
renderer. `IModelReadOptions` is what a reader needs to be told, and the editor's
`ModelImportSettings` implements it; a host with no settings of its own uses `ModelReadOptions`.

`CubeLut` stayed in `Vixen.Editor.Assets`, because it still uses `Vixen.Rendering`.
