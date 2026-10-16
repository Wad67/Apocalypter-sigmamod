-- The Sigma test stand: a three-wheeled engine stand, built from scratch. The engine drives two heavy flywheels through
-- the gearbox; a belt with its own clutch lever takes the flywheels' power to the rear axle. Every control is a lever
-- on the stand: throttle, clutch, belt clutch, steering, gears, handbrake; plus a key, headlights, a cab light, a tow
-- hitch and two toolkits. Each line names an object in sigma_test.glb.
local parts = require "sigma.parts"

local g = parts.chassis {
  mass = 1115, drive = 1, speed = true, flip_over = true, dashboard = "Dashboard", parked_brakes = false,
  controller = {
    enginePosition = { -0.062, 0.731, 1.62 }, transmissionPosition = { -0.062, 0.731, 1.62 },
    ["powertrain.clutch.slipTorque"] = 100000,
    ["powertrain.clutch.controlType"] = "UserInput",
    ["powertrain.transmission.gears"] = { -2.216, 0, 3.274, 2.093, 1.439, 1.0, 0.817, 0.651 },
    ["powertrain.differentials.0.DifferentialType"] = "Locked",
    ["powertrain.differentials.0.stiffness"] = 1,
    ["powertrain.wheelGroups.1.steerCoefficient"] = 0,
    ["powertrain.wheelGroups.2.steerCoefficient"] = 0,
  },
}

-- the body: solid colliders, and volumes that carry loose items with the stand
parts.colliders(g, "Floor Collider", "Firewall Collider", parts.numbered("Body Collider", 17))
parts.lock_volumes(g, parts.numbered("Physlock Volume", 2))

-- the engine and what it needs
parts.mount(g, "Engine Mount", "engine")
parts.mount(g, "Radiator Mount", "radiator")
parts.engine_sound(g, { at = "Engine Mount" })
parts.fuel_tank(g, { tank = "Access Collider", filler = "Fuel Inlet", hose = "Fuel Hose End", saved = false })
parts.ignition(g, { switch = "Ignition Collider", fuel = "Access Collider", engine = "Engine Mount",
                    off = "Ignition Off", on = "Ignition On", start = "Ignition and Starter On" })
parts.slot(g, "Dashboard", "gauge", { use = "dashboard_attach", at = "Dashboard Collider", align = true })

-- the drive: two flywheels on the gearbox, belted to the rear axle
parts.flywheel(g, "Flywheel Axle", { mesh = "Wheel (Heavy Flywheel)", spin = "Flywheel Spin", position = { 0.0295, 0, 0 }, radius = 0.5586, width = 0.126, mass = 970 })
parts.flywheel(g, "Flywheel Axle 2", { mesh = "Wheel (Heavy Flywheel).001", spin = "Flywheel Spin 2", position = { -0.059, 0, 0 }, radius = 0.56, width = 0.126, mass = 975 })
parts.coupling(g, "Belt Drive", { a = "Flywheel Axle", b = "Wheel RL", ratio = 2.639, capacity = 3000, engagement = 0 })
parts.belt(g, "Belt Drive", { coupling = "Belt Drive", a = "Flywheel Axle", b = "Wheel (Heavy Flywheel).002", radius_a = 0.5586, radius_b = 0.2117, width = 0.063, sag = 0.25 })
parts.coupling(g, "Rear Axle", { a = "Wheel RL", b = "Wheel RR" })

-- the wheels: game wheels fit on the three hubs
parts.wheel(g, "Wheel RL", { at = "Rear Left Wheel Mount", hub = "RL Hub", rim = "RL Rim" })
parts.wheel(g, "Wheel RR", { at = "Rear Right Wheel Mount", hub = "RR Hub", rim = "RR Rim", rim_rotation = { 0, 180, 0 } })
parts.wheel(g, "Wheel Front", { at = "Steering Hinge", hub = "Front Hub", rim = "Front Rim", stiffness = 16000, damper = 7000 })
g.nodes["Wheel (Heavy Flywheel).002"] = { parent = "RL Hub" }   -- the belt's pulley turns with the rear left wheel

-- the controls
local VC, VCT = "root:VehicleController", "NWH.VehiclePhysics2.VehicleController"
parts.lever(g, { label = "Throttle", mount = "Throttle Mount", handle = "Lever", collider = "Throttle Collider",
                 target = VC, type = VCT, property = "input.Throttle" })
parts.lever(g, { label = "Clutch", mount = "Clutch Mount", handle = "Clutch Lever", collider = "Clutch Collider",
                 target = VC, type = VCT, property = "input.Clutch", more = "Pull", less = "Release" })
parts.lever(g, { label = "Belt Clutch", mount = "Belt Clutch Mount", handle = "Belt Clutch", collider = "Belt Clutch Collider",
                 target = "Belt Drive:EngineSim.WheelCoupling", type = "EngineSim.WheelCoupling", property = "engagement",
                 more = "Tighten", less = "Slacken" })
parts.lever(g, { label = "Steering", mount = "Steering Mount", handle = "Lever.001", collider = "Steering Collider",
                 target = VC, type = VCT, property = "input.Steering", more = "Left", less = "Right", step = -0.1, min = -1 })
parts.toggle_lever(g, { label = "Handbrake", collider = "Handbrake", pivot_position = { 0, 0.107, 0 },
                        mesh = "asset:Mesh/handbrake.005", material = "asset:Material/gear_lever_1",
                        target = VC, type = VCT, property = "input.Handbrake" })
parts.gear_lever(g, { collider = "Gear Collider", joint = "Gear Lever Joint", mount = "Gear Lever Mount" })

-- lights, radio, hitch, tools
parts.light(g, { lamp = "InteriorLight Lamp", at = "InteriorLight", position = { 0, -0.06, 0 }, collider = "InteriorLight Collider" })
parts.headlights(g, { collider = "Headlight Collider", on = "Headlights ON", off = "Headlights OFF" })
parts.slot(g, "Headlight Hinge", "headlight", { at = "Headlight Install Position", shape = "sphere" })
parts.slot(g, "Cassette Player Install Hinge", "radio")
parts.tow_hitch(g, { hitch = "Tow Hitch", collider = "Hitch Interact Collider" })
parts.toolset(g, "Part Adjuster Toolset", { at = "Part Adjuster Location", mesh = "asset:Mesh/toolset", material = "asset:Material/tools_1",
                                            id = "PartAdjusterTools", label = "Part Adjuster Toolset",
                                            box = { center = { 0.002, -0.011, -0.006 }, size = { 0.03, 0.24, 0.303 } } })
parts.toolset(g, "Toolset", { at = "Toolset Location", mesh = "asset:Mesh/toolset.002", material = "asset:Material/tools_1" })

template "sigma_chassis_test" (g)

-- what the stand does while it exists, in Lua: it says so once, then counts every five seconds (state is each stand's own)
behaviour "sigma_chassis_test" {
  spawn = function(stand, state)
    log("sigma_test: " .. stand.name .. " is running Lua")
    state.ticks, state.wait = 0, 5
  end,
  update = function(stand, state, dt)
    state.wait = state.wait - dt
    if state.wait <= 0 then state.wait, state.ticks = 5, state.ticks + 1 end
  end,
}
