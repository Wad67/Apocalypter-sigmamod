-- The 1981 Mitsubishi Sigma: its own body, interior and parts, built from scratch. Its own gearbox (a four-speed's
-- ratios), a locked rear differential, rear-wheel drive.
--
-- It is also the kit the Blender add-on builds cars from. Each "-- ##" section is one thing a car can have ([required]:
-- what a car needs to work at all), described by the comment under it. Its "-- - " lines are the objects it is made
-- of, by their names here: "name [type]: what it is. Where it goes and what it does", "?" after the name if the
-- section works without it, "*" if there can be any number (they are a list in the code). The add-on finds yours by
-- name or lets you pick them, brings the Sigma's for the rest, and writes the section's lines into your script with
-- your objects' names; a line about an object you left out goes, unless the line says "-- optional: <option>, ...":
-- then just that option goes.
--
-- The types, what each kind of object is in the game:
-- [spot]: an empty (Add > Empty > Plain Axes): a place and a direction. Nothing you see or touch in game.
-- [marker]: an empty or a small mesh: where a part bolts on. Hold the part up to it in game and it fits there.
-- [mesh]: a mesh you see in game. Nothing bumps into it and nothing clicks it.
-- [clickable]: a mesh you see in game and click: the box round it is what takes the click. Nothing bumps into it.
-- [collider]: a mesh used only for the box round it: invisible in game, it's what you click, walk into or put things in. A plain cube, scaled to size, is all it needs to be.
-- [volume]: a mesh whose box is a space: the fuel tank's is what a can pours into. You see it in game.
-- [solid]: a mesh you see in game and bump into. It needs colliders made from it (Make colliders, in its row).
-- [hull]: a mesh you see in game and bump into: the game wraps its shape in one convex collider by itself.
-- [state]: a mesh the game shows for one state and hides for the others (the key off, on, starting): hide it in Blender if you like, the game decides.
-- [hidden]: a mesh the game keeps hidden until something shows it (a lift kit, fitted). The add-on keeps it in a hidden collection, "Hidden in game", so it doesn't clutter the car in Blender either.
local parts = require "sigma.parts"

local g = parts.chassis {
  mass = 1115, drive = "rear", speed = true, flip_over = true,
  controller = {
    ["powertrain.transmission.gears"] = { -3.369, 0, 3.369, 2.035, 1.36, 1.0, 0.856 },   -- reverse, neutral, 1st - 5th
    ["powertrain.differentials.0.DifferentialType"] = "Locked",
    ["powertrain.differentials.0.stiffness"] = 1,
  },
}
g.model = "sigma-chassis"
g.name = "1981 Mitsubishi Sigma"
-- where the Outrider spawns, and where the Poloska does: the starter camp among them
g.spawn_like = { "1335052897901121881", "6132430150351803222" }
-- which comes with its own engine, an AKMS and two cigars to start with
g.starter = { "astron_4g52", "akms", "cigar", "cigar" }
g.nodes["Body Colliders"] = { layer = 8 }

-- ## Engine bay [required]
-- Where the engine and the radiator bolt in, and where the engine is heard from. No engine mount, no engine.
-- - hinge_engine [marker]: Engine mount. Where the engine bolts in: put it where the engine's middle should be, turned the way the engine should face. Without it, no engine fits
-- - hinge_radiator [marker]: Radiator mount. Where the radiator bolts in: usually in front of the engine, facing forward
-- - EngineSound? [spot]: Engine sound. Where the engine's noise comes from: put it on the engine. Without it, the car's middle
-- - engine_bay? [mesh]: Engine bay. A mesh of the bay itself, if your body hasn't got one. The horn sounds from it
parts.mount(g, "hinge_engine", "engine")
parts.mount(g, "hinge_radiator", "radiator")
parts.engine_sound(g, { at = "EngineSound" })   -- optional: at

-- ## Exhaust
-- Where an exhaust part bolts on.
-- - hinge_exhaust [marker]: Exhaust mount. Where an exhaust part bolts on: under the car, behind the engine
parts.mount(g, "hinge_exhaust", "exhaust", { lookup = false })

-- ## Wheels and suspension [required]
-- Four hubs the game's wheels fit on, the stock suspension under them, and the slot lift kits clip into. The lifted
-- suspension is hidden until a lift kit is fitted.
-- - wheel_hub [marker]: Front left hub. The centre of the front left wheel with the suspension pushed all the way up (the wheel can only drop from there). The game's wheels are drawn on it
-- - wheel_hub.001 [marker]: Front right hub. The centre of the front right wheel, suspension all the way up
-- - wheel_hub.002 [marker]: Rear left hub. The centre of the rear left wheel, suspension all the way up
-- - wheel_hub.003 [marker]: Rear right hub. The centre of the rear right wheel, suspension all the way up
-- - suspension front? [mesh]: Front suspension. The stock front suspension's mesh, between the front wheels
-- - suspension rear? [mesh]: Rear suspension. The stock rear suspension's mesh, between the rear wheels
-- - drive shaft? [mesh]: Drive shaft. Its mesh, from the gearbox to the driven axle
-- - hinge_suspension? [spot]: Lift kit slot. Where a lift kit clips in: under the car, between the axles
-- - suspension_lifted? [hidden]: Lifted suspension. The suspension as it looks with a lift kit fitted; hidden until one is
-- - suspension rear lifted? [hidden]: Lifted rear suspension. Hidden until a lift kit is fitted
-- - spring? [hidden]: Lifted springs. Hidden until a lift kit is fitted
-- - suspension_front_lifted? [hidden]: Lifted front suspension. Hidden until a lift kit is fitted
-- - undershield? [hidden]: Undershield. Hidden until a lift kit is fitted
parts.wheel(g, "Wheel FL", { at = "wheel_hub" })
parts.wheel(g, "Wheel FR", { at = "wheel_hub.001", rim_rotation = { 0, 180, 0 } })
parts.wheel(g, "Wheel RL", { at = "wheel_hub.002" })
parts.wheel(g, "Wheel RR", { at = "wheel_hub.003", rim_rotation = { 0, 180, 0 } })
parts.slot(g, "hinge_suspension", "suspension", { box = { 0.64, 0.2, 0.19 } })
for _, name in ipairs({ "suspension_lifted", "suspension rear lifted", "spring", "suspension_front_lifted", "undershield" }) do
  g.nodes[name] = { MeshRenderer = { enabled = false } }
end

-- ## Fuel tank [required]
-- A tank cans pour into and the engine burns from, with a filler cap that siphons. An engine without fuel is a
-- paperweight.
-- - fuel_tank [volume]: Tank. The tank's mesh: the box round it is where a fuel can pours in. Put it where the tank is
-- - fuel_cap [clickable]: Filler cap. Where you fill up: look at it to see the fuel level; F opens it to siphon fuel out. On the body's side, over the tank
parts.fuel_tank(g, { tank = "fuel_tank", filler = "fuel_cap", litres = 20, capacity = 50, render = true })

-- ## Dashboard and ignition [required]
-- The key that starts the engine and what shows its state, the headlight switch and its two positions, and the slot
-- the game's gauges clip into.
-- - dashboard [solid]: Dashboard. The dashboard's mesh: seen, solid (make its colliders), and where the game's gauges clip in
-- - Ignition Collider [collider]: Key click box. An invisible box round the ignition key: clicking inside it turns the key, holding it starts the engine. Without it, the car can't be started
-- - Ignition Off? [state]: Key, off. The key as it looks with the ignition off (shown then, hidden otherwise)
-- - Ignition On? [state]: Key, on. The key turned to on (shown while the ignition is on)
-- - Ignition and Starter On? [state]: Key, starting. The key turned all the way (shown while the starter turns)
-- - Headlight Collider? [collider]: Headlight switch click box. An invisible box round the headlight switch: clicking inside it switches the headlights
-- - Headlights ON? [state]: Switch, on. The switch as it looks with the headlights on
-- - Headlights OFF? [state]: Switch, off. The switch as it looks with them off
g.playmaker.getDashboard.variables.gameObject.Dashboard = "Dashboard"
parts.slot(g, "Dashboard", "gauge", { use = "dashboard_attach", at = "dashboard", align = true })
g.nodes.Dashboard.collider.render = true
g.nodes.dashboard = { layer = 8 }
parts.ignition(g, { switch = "Ignition Collider", fuel = "fuel_tank", engine = "hinge_engine", dashboard = "Dashboard",
                    off = "Ignition Off", on = "Ignition On", start = "Ignition and Starter On" })   -- optional: off, on, start
parts.headlights(g, { collider = "Headlight Collider", on = "Headlights ON", off = "Headlights OFF" })   -- optional: on, off

-- ## Driver's seat and controls [required]
-- Getting in and out, the driver seen from outside, the pedals, the steering wheel's slot, the handbrake, and the
-- game's own driving controls on the keyboard.
-- - Enter Collider [collider]: Get-in box. An invisible box at the driver's door: look at it and press Use to get in
-- - Exit Collider [collider]: Get-out box. An invisible box beside the driver's door, on the ground: you're put there when you get out
-- - DriveTrigger [spot]: Driver. An empty at the driver's seat; Head and Get-out spot go under it (parent them to it)
-- - sitPos [spot]: Head. Where the driver's eyes are, looking forward: the first-person camera sits here. Under Driver
-- - exitPos [spot]: Get-out spot. Where you stand after getting out, facing away from the car. Under Driver
-- - hinge_steeringwheel? [spot]: Steering wheel slot. The steering wheel's centre, facing the driver: a steering wheel clips in here and turns as you steer
-- - steering_rod? [mesh]: Steering column. Its mesh, from the dashboard to the steering wheel
-- - handbrake? [clickable]: Handbrake lever. Click to pull it, click again to let it off; it tilts about its own origin. Its box is what's clicked
-- - gas? [mesh]: Gas pedal. Moves down as you press the throttle
-- - brake? [mesh]: Brake pedal. Moves down as you brake
-- - clutch? [mesh]: Clutch pedal. Moves down as you press the clutch
parts.driver_seat(g, { enter = "Enter Collider", exit = "Exit Collider", input = "DriveTrigger", sit = "sitPos" })
g.nodes.DriveTrigger.at = nil
g.nodes.sitPos = { parent = "DriveTrigger" }
g.nodes.exitPos = { parent = "DriveTrigger" }
g.nodes["Driver Model"] = { parent = "DriveTrigger", copy = "Outrider/parts/PlayerModel_Sit", at = "sitPos", position = { -0.011, -0.4775, -0.0293 } }
parts.add(g.nodes.DriveTrigger, "SigmaMod.Parts.SeatedModel")
g.nodes.DriveTrigger["SigmaMod.Parts.SeatedModel"] = { model = "Driver Model", seat = "sitPos" }
local VC, VCT = "root:VehicleController", "NWH.VehiclePhysics2.VehicleController"
parts.toggle_lever(g, { label = "Handbrake", collider = "handbrake", target = VC, type = VCT, property = "input.Handbrake" })
g.nodes.handbrake.collider.render = true
g.nodes.handbrake.parent = "Handbrake Pivot"
parts.car_input(g, { handbrake = "handbrake", headlights = "Headlight Collider", horn_at = "engine_bay" })   -- optional: handbrake, headlights, horn_at
parts.pedal(g, "gas", { input = "Throttle", travel = { 0, 0, 0.093 } })
parts.pedal(g, "brake", { input = "Brakes", travel = { 0, 0, 0.104 } })
parts.pedal(g, "clutch", { input = "Clutch", travel = { 0, 0, 0.102 } })
parts.slot(g, "hinge_steeringwheel", "steeringwheel", { sphere = 0.05 })
g.VehicleController["steering.steeringWheel"] = "hinge_steeringwheel"
g.VehicleController["steering.steeringWheelTurnRatio"] = -15
g.VehicleController["steering.maximumSteerAngle"] = 30

-- ## Gear lever
-- A shifter a gear lever part clips onto, leaning into each gear. The keys shift without it.
-- - gearlever_bottom [clickable]: Gear lever boot. The boot round the lever's foot: seen, and clicked to shift
-- - hinge_gearlever_Joint [spot]: Lever pivot. An empty where the lever leans from, at the boot
-- - hinge_gearlever [spot]: Lever slot. An empty under the pivot, where a gear lever part clips in
parts.gear_lever(g, { collider = "gearlever_bottom", joint = "hinge_gearlever_Joint", mount = "hinge_gearlever" })
g.nodes.gearlever_bottom.collider.render = true                       -- the boot is drawn, not only clicked
g.nodes.hinge_gearlever.collider = nil                                -- an empty: the game's 7 cm sphere
g.nodes.hinge_gearlever.add = { "SphereCollider" }
g.nodes.hinge_gearlever.SphereCollider = { isTrigger = true, radius = 0.07 }

-- ## Headlights
-- Slots the game's headlights clip into, as many as the car has. The switch is on the dashboard.
-- - hinge_headlight* [spot]: Headlight slot. An empty where a headlight clips in, facing forward. One per headlight
-- - headlight_holder_small? [mesh]: Headlight holder. A holder mesh round a headlight
-- - headlight_holder_small (1)? [mesh]: Headlight holder. The other one
for _, hinge in ipairs({ "hinge_headlight", "hinge_headlight.001" }) do parts.slot(g, hinge, "headlight", { sphere = 0.04 }) end

-- ## Seats, doors and body panels
-- Slots for the game's seats, doors, hoods, trunk lids, bumpers, roof racks and radios, as many of each as your car
-- has: a van can have five doors, a hearse two trunk lids. Each slot is an empty where a part of that kind clips in,
-- and any part of that kind fits any of its slots. Leave out what your car hasn't got.
-- - hinge_seat_driver?* [spot]: Seat slot. An empty where a seat clips in. One per seat: the driver's, the passenger's, a rear bench
-- - hinge_door_L?* [spot]: Door slot. An empty where a door hangs, at its front edge: the door swings out about it. One per door
-- - hinge_hood?* [spot]: Hood slot. An empty where a hood clips on. One per hood
-- - hinge_trunk?* [spot]: Trunk lid slot. An empty where a trunk lid clips on. One per trunk lid
-- - hinge_bumper_front?* [spot]: Bumper slot. An empty where a bumper clips on. One per bumper: front, rear
-- - hinge_roofrack?* [spot]: Roof rack slot. An empty on the roof where a roof rack clips on. One per rack
-- - hinge_radio?* [spot]: Radio slot. An empty in the dashboard where a radio clips in. One per radio
-- - hinge_windshield? [spot]: Windshield slot. Where the windshield clips in
for _, hinge in ipairs({ "hinge_seat_driver", "hinge_seat_passenger", "hinge_seat_rear" }) do parts.slot(g, hinge, "seat", { box = true }) end
for _, hinge in ipairs({ "hinge_door_L", "hinge_door_R" }) do parts.slot(g, hinge, "door", { box = true }) end
for _, hinge in ipairs({ "hinge_hood" }) do parts.slot(g, hinge, "hood", { box = true }) end
for _, hinge in ipairs({ "hinge_trunk" }) do parts.slot(g, hinge, "trunk", { box = true }) end
for _, hinge in ipairs({ "hinge_bumper_front", "hinge_bumper_rear" }) do parts.slot(g, hinge, "bumper", { box = true }) end
for _, hinge in ipairs({ "hinge_roofrack" }) do parts.slot(g, hinge, "roofrack", { box = true }) end
for _, hinge in ipairs({ "hinge_radio" }) do parts.slot(g, hinge, "radio", { box = true }) end
parts.slot(g, "hinge_windshield", "windshield", { sphere = 0.05 })

-- ## Interior light
-- A cab lamp, switched on and off by clicking it.
-- - interior_car_light [clickable]: Cab lamp. Its mesh, on the ceiling: click it to switch the light
parts.light(g, { lamp = "Interior Lamp", at = "interior_car_light", position = { 0, -0.06, 0 }, collider = "interior_car_light" })
g.nodes.interior_car_light.collider.render = true

-- ## Tow hitch
-- Trailers hook on here.
-- - Tow Hitch [hull]: Hitch. The hitch's mesh, at the back, low: seen and solid
-- - Hitch Collider [collider]: Hitch click box. An invisible box at the hitch: clicking it hitches and unhitches a trailer
parts.tow_hitch(g, { hitch = "Tow Hitch", collider = "Hitch Collider" })

-- ## Lock volumes
-- Loose things inside these boxes ride along instead of sliding out through the floor at speed.
-- - Physlock Cab* [collider]: Lock volume. An invisible box filling a space things ride in (the cab, the boot): loose things inside move with the car. One per space
for _, volume in ipairs({ "Physlock Cab", "Physlock RearCab", "Physlock Boot" }) do parts.lock_volumes(g, volume) end

-- ## Toolsets
-- The game's toolbox and part adjuster, kept on board.
-- - toolset_1? [clickable]: Toolbox. Where the toolbox sits: look at it and press F to use it
-- - toolset_2? [clickable]: Part adjuster. Where the part adjuster sits: look at it and press F to use it
parts.toolset(g, "toolset_1", { at = "toolset_1", label = "Toolset" })
parts.toolset(g, "toolset_2", { at = "toolset_2", id = "PartAdjusterTools", label = "Part Adjuster Toolset",
                               box = { center = { -0.001, -0.011, -0.006 }, size = { 0.027, 0.24, 0.303 } } })

template "sigma_chassis" (g)
