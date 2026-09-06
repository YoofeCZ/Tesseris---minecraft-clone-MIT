-- Drop this directory into mods/ to replace the built-in wolf prototype.
-- It uses only the first Luanti-compatible API slice implemented by Tesseris.
local wolf = {
    initial_properties = { hp_max = 16, physical = true, tesseris_model = "wolf" },

    on_activate = function(self, staticdata, dtime_s)
        self.lived = tonumber(staticdata) or 0
    end,

    on_step = function(self, dtime, moveresult)
        self.lived = self.lived + dtime
        -- ObjectRef is live. For example, a mod may call:
        -- self.object:set_velocity({ x = 0, y = 0, z = 2 })
    end,

    on_punch = function(self, puncher, elapsed, toolcaps, direction, damage)
        core.log("action", "scripted wolf took " .. tostring(damage) .. " damage")
    end,

    on_death = function(self, killer)
        core.log("action", "scripted wolf died after " .. string.format("%.1f", self.lived) .. " seconds")
    end,

    get_staticdata = function(self)
        return tostring(self.lived)
    end,
}

core.register_entity("tesseris:wolf", wolf)
