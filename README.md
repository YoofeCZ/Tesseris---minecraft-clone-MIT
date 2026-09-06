# Voxelity (Tesseris)

Minecraft? No
## Demo

[![Náhled videa](https://img.youtube.com/vi/3CbC3Ek9UDw/hqdefault.jpg)](https://www.youtube.com/watch?v=3CbC3Ek9UDw)

Written from scratch in C# on a custom renderer (Vulkan), no game engine.

---

## Requirements

| | |
|---|---|
| Runtime | .NET 8 (also builds and runs on SDK 10) |
| Graphics | Vulkan 1.3 |
| OS | Windows, Linux, macOS (via MoltenVK) |
| Build | `glslangValidator` — shaders compile to SPIR-V |

### macOS

```bash
brew install molten-vk vulkan-loader glslang
```

The loader is found automatically. Validation layers are recommended, otherwise Vulkan
fails silently:

```bash
brew install vulkan-validationlayers
```

### Audio

Uses the native **SoLoud** library (zlib license, miniaudio backend). The binary isn't
in the repo and has to be built — without it, the game runs fine, just silently.

**Windows** (needs Visual Studio with C++ tools):

```bash
git clone --depth 1 https://github.com/jarikomppa/soloud.git
```

In an `x64 Native Tools Command Prompt`, inside `soloud`:

```bash
cl /nologo /O2 /MT /EHsc /W0 /MP /D_CRT_SECURE_NO_WARNINGS /DWITH_MINIAUDIO /Iinclude /Isrc/backend/miniaudio src/core/*.cpp src/filter/*.cpp src/audiosource/*/*.cpp src/audiosource/*/*.c src/backend/miniaudio/soloud_miniaudio.cpp src/c_api/soloud_c.cpp /LD /Fe:soloud_x64.dll /link /DEF:src/c_api/soloud.def ole32.lib user32.lib
```

Copy the resulting `soloud_x64.dll` into `native/`.

**macOS / Linux** — the library must be named `libsoloud_x64.dylib` / `libsoloud_x64.so`:

```bash
c++ -O2 -fPIC -shared -DWITH_MINIAUDIO -Iinclude -Isrc/backend/miniaudio \
    $(find src/core src/filter src/audiosource -name '*.cpp' -o -name '*.c') \
    src/backend/miniaudio/soloud_miniaudio.cpp src/c_api/soloud_c.cpp \
    -framework CoreFoundation -framework CoreAudio -framework AudioToolbox \
    -o libsoloud_x64.dylib
```

Same on Linux without `-framework`, using `-lpthread -lm -ldl` instead.

## Running

```bash
dotnet run --project src/Game -c Release
```

On first launch you'll get a world picker or the option to create a new world.

## Controls

**First person** — physical work and exploration:

| Key | Action |
|---|---|
| W A S D | walk |
| Space / Shift | jump / sprint |
| Mouse | look |
| Left / right click | mine / place block |
| 1–6 / scroll | select material |
| F3 | overlay |
| Esc | close panel / release or recapture mouse |

**Tab** — commander mode (top-down view, colony management). Scroll moves the visible
floor, **Q**/**E** rotate the view. The commander has no hands — they only give orders:

| Key | Tool |
|---|---|
| 1 | Mark an area to dig |
| 2 | Place a belt |
| 3 | Place a crusher |
| 4 | Place an inserter |
| 5 | Demolish |

## Status and modding

The core loop works end to end: mark an area, colonists dig and carry the ore, a machine
processes it, and it automates itself once both belts and power are connected. Colonists
have physical bodies (collision, avoidance, jumping), their own pathfinding, and survive
saving and loading.

Detailed status, what's done and what's still missing, is tracked in
[docs/STAV.md](docs/STAV.md) (in Czech).

The game has its own modding API (`Tesseris.ModApi`) — content-only and C# mods, worldgen
hooks, namespaced blocks/items. Guide in [docs/MODDING.md](docs/MODDING.md), examples in
[examples](examples/README.md).

---

## License

**The original code (engine, renderer, colony simulation, world generation) is MIT** —
see [LICENSE](LICENSE). Do whatever you want with it, just keep the copyright notice.

A handful of specific files and assets are derived from other projects and keep **their own
license** regardless of the line above — that's how derivative work licensing works, MIT at
the top doesn't override it:

| What | Source | License |
|---|---|---|
| `LuantiLight.cs`, `FaceShading.cs`, `luanti_light.glsl`, sky colours in `DayCycle.cs` | port of [Luanti](https://github.com/luanti-org/luanti)'s lighting model — algorithms and constants, not literal source text | **LGPL-2.1-or-later** |
| `assets/models/character.b3d`, `assets/textures/character.png` | Luanti's own "Minetest Sam" character (model by MirceaKitsune, texture by Jordach), exact copy | **CC BY-SA 3.0** |
| `mobs_sheep_classic.b3d` / `.png` | classic Mobs Animal / Carbone Mobs model and texture (Pavel_S) | WTFPL |
| Animals: bat, bear, cat, fox, horse, opossum, owl, pig, songbird, turkey, reindeer, wolf | [Animalia for Luanti](https://codeberg.org/ElCeejo) (ElCeejo) | MIT |
| MoonSharp (Lua interpreter) | MoonSharp (Marco Mastropaolo) | BSD-3-Clause |
| FXAA in `color_grade.frag` | adaptation of Luanti's FXAA / WebGL-Minecraft (Armin Ronacher) | BSD-style |

**Test-only content that must not ship commercially:** files containing `_test_nc`
(`assets/models/mobs_sheep_test_nc.b3d` and the sheep textures) and everything in
`assets/mobs/mobs-redo-test-only.json` mix MIT/CC0/WTFPL/CC BY-SA, including explicitly
**non-commercial** CC BY-SA-NC textures (Summer Field Texture Pack by LithiumSound). They're
there for development and visual testing only — remove them before selling or publicly
releasing the game.

Full attribution and exact upstream commit hashes (if anyone needs to verify in detail) are
in the project history; this table is the short version for normal use.
