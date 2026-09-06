# Frozen save fixtures

These fixtures were captured from the green 986-test baseline on 2026-08-10, before the
entity/component, item-state and multi-dimension refactors.

- `chunk-chk2-alternating.base64.gz`: a gzip-compressed base64 CHK2 payload with a two-entry
  `tesseris:air` / `tesseris:stone` palette and alternating one-bit indices. The decompressed
  payload is 4,136 bytes. It is intentionally constructed independently of `ChunkSerializer.Encode`.
- `furnaces-frn2.base64`: one FRN2 furnace at `(3, 4, -5)` with fuel, two populated outputs,
  progress, durability damage and burn state. It is loaded directly by `Furnaces.Load`.
- `world-v1.json`: legacy world metadata version 1 with only name and seed.
- `moddata-v1.json`: current per-mod format version 1 with both world and position blobs.

The base64 wrapper keeps binary provenance reviewable in git. Tests decode these checked-in bytes;
they do not regenerate the fixtures with the current writers.
