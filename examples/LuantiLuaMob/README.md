# Luanti-style Lua mob

Copy this directory to `mods/LuantiLuaMob`. Tesseris loads `init.lua` folders after its built-in
entity definitions, so this prototype replaces the wolf callbacks while retaining the C# wolf
model, collision and default AI.

Supported first slice: `core`/`minetest.register_entity`, lifecycle callbacks, logging and live
`ObjectRef` position, velocity, acceleration, yaw, HP, removal, properties and Lua-entity access.
