# Patches against upstream CUE4Parse

The CLI repo depends on a sibling clone of
[CUE4Parse](https://github.com/FabianFG/CUE4Parse). Most of the SCUM work lives
in this repository. One small local change was made in the upstream tree and is
kept here as a patch.

## `upstream-cue4parse-landscape-debug.patch`

Adds a single diagnostic line to `CUE4Parse-Conversion/Meshes/MeshConverter.cs`
when converting landscape meshes. It prints the image extent, component count
and current managed memory to stderr. It does not change the output.

Apply from the root of the CUE4Parse clone:

```sh
git apply /path/to/cue4parse-public/patches/upstream-cue4parse-landscape-debug.patch
```

The patch was generated against CUE4Parse commit `b3636fc6`. If the context has
moved, apply it manually. The line is optional.

No keys, game files or extracted assets belong in this folder.
