-- Built-in Tesseris entities use the same prototype/callback shape as Luanti entities.
-- C# remains responsible for collision, rendering and the default state machine.

local function persistent_mob(model, hp)
    return {
        initial_properties = {
            hp_max = hp,
            physical = true,
            collide_with_objects = true,
            tesseris_model = model,
        },

        on_activate = function(self, staticdata, dtime_s)
            self.age = tonumber(staticdata) or 0
        end,

        on_step = function(self, dtime, moveresult)
            self.age = (self.age or 0) + dtime
        end,

        on_punch = function(self, puncher, time_from_last_punch, tool_capabilities, direction, damage)
            self.last_damage = damage
        end,

        get_staticdata = function(self)
            return string.format("%.3f", self.age or 0)
        end,
    }
end

core.register_entity("tesseris:sheep", persistent_mob("animalia_sheep", 15))
core.register_entity("tesseris:deer", persistent_mob("animalia_reindeer", 15))
core.register_entity("tesseris:wolf", persistent_mob("animalia_wolf", 20))
