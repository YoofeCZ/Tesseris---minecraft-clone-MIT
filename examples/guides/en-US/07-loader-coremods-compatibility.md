# 7. Loader phases, CoreMods, compatibility, and security

The standalone launcher discovers unpacked manifests and `.tmod` archives, validates trust and version ranges,
resolves dependencies, and creates one collectible load context per ordinary managed mod. Private dependencies
stay isolated; platform contract assemblies are shared deliberately.

## Entrypoint phases

- `PreLaunch`: runs in dependency order before Game is loaded.
- `CoreMod`: registers IL transformations before Game is loaded.
- `Runtime`: shared game logic.
- `Client`: client-only logic.
- `DedicatedServer`: server-only logic.

## CoreMods

Implement `ICoreMod` from `Tesseris.Loader.Abstractions` and register a `ModMethodPatchDescriptor`. Targets and
patch methods use exact assembly, full type name, method, generic arity, static/instance flag, return type, and
parameter types. Supported kinds are `Prefix`, `Postfix`, and `Replace`.

Patch order follows the resolved load plan, priority, stable patch ID, and explicit before/after edges. Cycles,
invalid signatures, and conflicting replacements fail closed. The transformer writes a content-addressed cache
copy and never edits the installed Game DLL.

CoreMods require both `trustedCode: true` and `coreMod: true`. They are native-equivalent trusted code, are not
sandboxed, and stay loaded until process exit.

## Custom loaders

An external loader implements `IModLoader` and is installed under `modloaders/<loader>` with a small manifest
containing `assembly`, `entryType`, and `trustedCode: true`. Standard resolved packages give configured custom
loaders first chance in deterministic order; otherwise the standalone runtime loader handles them.

## Save compatibility

World metadata records the selected preset/dimension and generator fingerprint. `tesseris.mods.lock.json`
records API version, mod IDs/versions, and package hashes. A changed world generator or incompatible modpack is
rejected before chunk scheduling to avoid mixing old and new terrain.

## Security

Content-only packages are data. Managed mods, CoreMods, native libraries, and external loaders run with the
same OS permissions as the game. Assembly isolation prevents dependency collisions; it is not a security
sandbox. Install executable mods only from authors you trust.

[Next: API map](api-map.md)
