-- metal_plate.lua: a metal plate that attaches to vehicles, made from scratch. metal_plate.glb: the plate's mesh
-- ("Plate"), with its colliders (Collider A, Collider B, Collider C) parented to it. Nothing is tagged in Blender.
local parts = require "sigma.parts"

local plate = {
  -- the object's properties (sigma_* in Blender before)
  model = "Plate",                 -- the .glb's root object this template is (sigma_id)
  name = "Metal Plate",            -- what the game calls it (sigma_name)
  spawn = 0.5,                     -- half as likely as each other thing in its spawn lists (sigma_spawn)
  spawn_like = {                   -- the spawn lists it joins: those of the game's metal plates (sigma_spawn_like)
    "7710917957508905884",         --   metal_plate_1
    "6529531430021914791",         --   metal_plate_2
  },
}

-- attachable the way the game's plates are; each collider object's mesh becomes a collider
parts.attachable(plate, { mass = 10, colliders = { "Collider A", "Collider B", "Collider C" } })

template "metal_plate" (plate)
