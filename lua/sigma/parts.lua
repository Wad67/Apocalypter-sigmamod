-- SigmaMod parts: what a vehicle, a machine or a place is made of, one function each.
--
-- An asset's script starts from parts.chassis and adds parts to it by the names its objects have in Blender:
--
--   local parts = require "sigma.parts"
--   local g = parts.chassis { mass = 250, drive = "rear" }
--   parts.mount(g, "Engine Mount", "engine")
--   parts.lever(g, { mount = "Throttle Mount", handle = "Lever", collider = "Throttle Collider", label = "Throttle",
--                    target = "root:VehicleController", type = "NWH.VehiclePhysics2.VehicleController", property = "input.Throttle" })
--   template "my_vehicle" (g)
--
-- What a part writes is what the .json format takes (objects' settings, components, PlayMaker FSMs), with the game's
-- own conventions in it (taken from working assets: the Sigma test stand, the gyrocopter, the generator set), so the
-- game, the repair box, saves and other mods see what they expect. Anything a part doesn't cover can be written into
-- g by hand: g.nodes["Name"] = { ... } is an object, g.playmaker["Name"] = { ... } an FSM on the root.
local parts = {}

local function merge(into, from)
  for k, v in pairs(from or {}) do into[k] = v end
  return into
end
local function node(g, name)
  g.nodes[name] = g.nodes[name] or {}
  return g.nodes[name]
end
local function fsm(spec, name, body)
  spec.playmaker = spec.playmaker or {}
  spec.playmaker[name] = body
end
local function add(spec, ...)
  spec.add = spec.add or {}
  for _, c in ipairs({ ... }) do table.insert(spec.add, c) end
end
parts.merge, parts.node, parts.fsm, parts.add = merge, node, fsm, add
local meta = setmetatable({}, { __mode = "k" })   -- what parts remember about a template, by its table
local WC = "NWH.WheelController3D.WheelController"

-- The root: a vehicle the game knows (layer, tag, body, NWH's controller) with the lookups every part may use.
--   mass (kg), static (true: kinematic, it doesn't move), drive ("front", "rear", "all", "none" or an axle number),
--   controller (NWH VehicleController values over these defaults), add (more components on the root),
--   speed (true: the Speed FSM a driver's seat reads), flip_over (true or its settings: NWH's flip-over module),
--   dashboard (the dashboard object, for the ignition's gauges), parked_brakes (false: the wheels aren't clamped while
--   nobody drives it: for a machine with its own handbrake, or none)
function parts.chassis(o)
  o = o or {}
  local g = {
    type = "chassis",
    layer = 8, tag = "Vehicle",
    add = { "Rigidbody", "VehicleController" },
    Rigidbody = { mass = o.mass or 500 },
    drive = o.drive or "rear",
    VehicleController = merge({
        stateSettings = "asset:StateSettings/DefaultStateSettings",
        ["powertrain.transmission.transmissionType"] = "Manual",
        ["input.autoSetInput"] = false,
        ["input.swapInputInReverse"] = false,
        ["powertrain.engine.autoStartOnThrottle"] = false,
        ["powertrain.engine.ignition"] = false,
        ["powertrain.engine.forcedInduction.useForcedInduction"] = false,
        ["brakes.brakeWhileIdle"] = false,
        ["soundManager.spatialBlend"] = 1,
        ["soundManager.interiorAttenuation"] = 0,
        ["soundManager.engineRunningComponent.baseVolume"] = 0.25,
        ["soundManager.engineRunningComponent.volumeRange"] = 0.2,
        ["soundManager.engineRunningComponent.pitchRange"] = 1.6,
        ["soundManager.engineRunningComponent.smoothing"] = 0.1,
        ["soundManager.engineRunningComponent.maxDistortion"] = 0.5,
        ["soundManager.engineStartStopComponent.baseVolume"] = 0.4,
        ["soundManager.gearChangeComponent.baseVolume"] = 0,
        ["soundManager.crashComponent.baseVolume"] = 0,
        ["soundManager.transmissionWhineComponent.baseVolume"] = 0.08,
        ["soundManager.transmissionWhineComponent.maxSpeed"] = 100,
        ["soundManager.turboWhistleComponent.baseVolume"] = 0.05,
        ["soundManager.turboFlutterComponent.baseVolume"] = 0.2,
        ["soundManager.suspensionBumpComponent.baseVolume"] = 0.5,
        ["soundManager.exhaustPopComponent.popChance"] = 0.3,
      }, o.controller),
    playmaker = {
      RpmGear = {
          variables = { float = { rpm = 0 }, int = { gear = 0 }, object = { VehicleController = "root:VehicleController" } },
          states = {
            RpmGear = {
              actions = {
                {
                  GetProperty = {
                    everyFrame = true,
                    targetProperty = {
                      TargetObject = "{VehicleController}",
                      TargetTypeName = "NWH.VehiclePhysics2.VehicleController",
                      PropertyName = "powertrain.engine.OutputRPM",
                      FloatParameter = "{rpm}",
                    },
                  },
                },
                {
                  GetProperty = {
                    everyFrame = true,
                    targetProperty = {
                      TargetObject = "{VehicleController}",
                      TargetTypeName = "NWH.VehiclePhysics2.VehicleController",
                      PropertyName = "powertrain.transmission.Gear",
                      IntParameter = "{gear}",
                    },
                  },
                },
              },
            },
          },
        },
      getAudio = {
          variables = { gameObject = { AudioThrottle = null } },
          states = {
            wait = { actions = { { NextFrameEvent = { sendEvent = "next" } } }, transitions = { next = "getAudio" } },
            getAudio = {
              actions = {
                {
                  GetChild = {
                    gameObject = "owner",
                    childName = "EngineAudioSources",
                    withTag = "Untagged",
                    storeResult = "{AudioThrottle}",
                  },
                },
              },
            },
          },
        },
      getDashboard = { variables = { gameObject = { Dashboard = o.dashboard or null } }, states = { idle = {} } },
    },
    nodes = {},
  }
  if o.static then g.Rigidbody.isKinematic = true end
  -- NWH clamps every wheel with its brakes' full torque while its brakes are switched off (the game's parking brake for
  -- a car nobody drives); a machine nobody sits in (a stand, a generator) has its own handbrake, or none
  if o.parked_brakes == false then g.VehicleController["brakes.brakeWhileDisabled"] = false end
  if o.speed then g.playmaker.Speed = {
      variables = { float = { speed = 0 } },
      states = {
        getSpeed = { actions = { { GetSpeed = { gameObject = "owner", storeResult = "{speed}", everyFrame = true } } } },
      },
    } end
  if o.clutch_capacity ~= false then g.playmaker.ClutchCapacity = {
      variables = { object = { VehicleController = "root:VehicleController" } },
      states = {
        hold = {
          actions = {
            {
              SetProperty = {
                everyFrame = true,
                targetProperty = {
                  TargetObject = "{VehicleController}",
                  TargetTypeName = "NWH.VehiclePhysics2.VehicleController",
                  PropertyName = "powertrain.clutch.slipTorque",
                  setProperty = true,
                  FloatParameter = 100000,
                },
              },
            },
          },
        },
      },
    } end
  if o.flip_over then
    add(g, "NWH.VehiclePhysics2.Modules.FlipOver.FlipOverModuleWrapper")
    g["NWH.VehiclePhysics2.Modules.FlipOver.FlipOverModuleWrapper"] = merge({
      ["module.flipOverType"] = "Gradual",
      ["module.flipOverActivation"] = "Automatic",
      ["module.allowedAngle"] = 70,
      ["module.instantFlipOverVerticalOffset"] = 1,
      ["module.maxDetectionSpeed"] = 1,
      ["module.timeout"] = 1,
      ["module.flipOverDuration"] = 3,
    }, type(o.flip_over) == "table" and o.flip_over or nil)
  end
  for _, c in ipairs(o.add or {}) do table.insert(g.add, c) end
  meta[g] = { dashboard = o.dashboard }
  fsms {
    hinge_attach = {
      variables = { string = { ID = "", AcceptID = "" }, gameObject = { Item = null, Hinge = null } },
      states = {
        partAttached = {
          actions = { { GameObjectHasChildren = { gameObject = "owner", falseEvent = "next", everyFrame = true } } },
          transitions = { next = "partMissing" },
        },
        partMissing = {
          actions = {
            { GameObjectHasChildren = { gameObject = "owner", trueEvent = "back", everyFrame = true } },
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Point" } },
            {
              TriggerEvent = {
                gameObject = "owner",
                trigger = "OnTriggerEnter",
                collideTag = "vehPartRemoved",
                sendEvent = "next",
                storeCollider = "{Item}",
              },
            },
          },
          transitions = { back = "partAttached", next = "checkChild" },
        },
        checkChild = {
          actions = {
            {
              GameObjectIsChildOf = { gameObject = "{Item}", isChildOf = "{Hand}", trueEvent = "next", falseEvent = "back" },
            },
          },
          transitions = { next = "checkID", back = "partMissing" },
        },
        checkID = {
          actions = {
            { GetFsmString = { gameObject = "{Item}", fsmName = "ID", variableName = "ID", storeValue = "{ID}" } },
            {
              StringCompare = { stringVariable = "{ID}", compareTo = "{AcceptID}", equalEvent = "next", notEqualEvent = "back" },
            },
          },
          transitions = { next = "Over", back = "partMissing" },
        },
        Over = {
          actions = {
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Wrench", everyFrame = true } },
            {
              TriggerEvent = {
                gameObject = "owner",
                trigger = "OnTriggerExit",
                collideTag = "vehPartRemoved",
                sendEvent = "back",
                storeCollider = "{Item}",
              },
            },
            { GetButtonDown = { buttonName = "Use", sendEvent = "next" } },
          },
          transitions = { back = "partMissing", next = "Attach" },
        },
        Attach = {
          actions = {
            { PlaySound = { gameObject = "owner", clip = "asset:AudioClip/wrench1", volume = 1 } },
            { GetOwner = { storeGameObject = "{Hinge}" } },
            { SendEvent = { eventTarget = "{PlayerCamera}/GrabItem", sendEvent = "not_Hold" } },
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Point" } },
            { DestroyComponent = { gameObject = "{Item}", component = "UnityEngine.Rigidbody" } },
            { SetTag = { gameObject = "{Item}", tag = "vehPart" } },
            { SetLayer = { gameObject = "{Item}", layer = 8 } },
            {
              SetParent = {
                gameObject = "{Item}",
                parent = "{Hinge}",
                worldPositionStays = true,
                resetLocalPosition = true,
                resetLocalRotation = true,
              },
            },
            { GameObjectHasChildren = { gameObject = "owner", trueEvent = "back", everyFrame = true } },
          },
          transitions = { back = "partAttached" },
        },
      },
    },
    dashboard_attach = {
      variables = { string = { ID = "", AcceptID = "" }, gameObject = { Item = null, Hinge = null } },
      states = {
        partMissing = {
          actions = {
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Point" } },
            {
              TriggerEvent = {
                gameObject = "owner",
                trigger = "OnTriggerEnter",
                collideTag = "vehPartRemoved",
                sendEvent = "next",
                storeCollider = "{Item}",
              },
            },
          },
          transitions = { next = "checkChild" },
        },
        checkChild = {
          actions = {
            {
              GameObjectIsChildOf = { gameObject = "{Item}", isChildOf = "{Hand}", trueEvent = "next", falseEvent = "back" },
            },
          },
          transitions = { next = "checkID", back = "partMissing" },
        },
        checkID = {
          actions = {
            { GetFsmString = { gameObject = "{Item}", fsmName = "ID", variableName = "ID", storeValue = "{ID}" } },
            {
              StringCompare = { stringVariable = "{ID}", compareTo = "{AcceptID}", equalEvent = "next", notEqualEvent = "back" },
            },
          },
          transitions = { next = "Over", back = "partMissing" },
        },
        Over = {
          actions = {
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Wrench", everyFrame = true } },
            {
              TriggerEvent = {
                gameObject = "owner",
                trigger = "OnTriggerExit",
                collideTag = "vehPartRemoved",
                sendEvent = "back",
                storeCollider = "{Item}",
              },
            },
            { GetButtonDown = { buttonName = "Use", sendEvent = "next" } },
          },
          transitions = { back = "partMissing", next = "Attach" },
        },
        Attach = {
          actions = {
            { PlaySound = { gameObject = "owner", clip = "asset:AudioClip/wrench1", volume = 1 } },
            { GetOwner = { storeGameObject = "{Hinge}" } },
            { SendEvent = { eventTarget = "{PlayerCamera}/GrabItem", sendEvent = "not_Hold" } },
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Point" } },
            { DestroyComponent = { gameObject = "{Item}", component = "UnityEngine.Rigidbody" } },
            { SetTag = { gameObject = "{Item}", tag = "vehPart" } },
            { SetLayer = { gameObject = "{Item}", layer = 8 } },
            {
              SetParent = {
                gameObject = "{Item}",
                parent = "{Hinge}",
                worldPositionStays = true,
                resetLocalPosition = false,
                resetLocalRotation = false,
              },
            },
            { NextFrameEvent = { sendEvent = "back" } },
          },
          transitions = { back = "partMissing" },
        },
      },
      start = "partMissing",
    },
    wheel_attach = {
      variables = { string = { ID = "", AcceptID = "" }, gameObject = { Item = null, Hinge = null, Fitted = null } },
      states = {
        partMissing = {
          actions = {
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Point" } },
            {
              TriggerEvent = {
                gameObject = "owner",
                trigger = "OnTriggerEnter",
                collideTag = "vehPartRemoved",
                sendEvent = "next",
                storeCollider = "{Item}",
              },
            },
          },
          transitions = { next = "checkChild" },
        },
        checkChild = {
          actions = {
            {
              GameObjectIsChildOf = { gameObject = "{Item}", isChildOf = "{Hand}", trueEvent = "next", falseEvent = "back" },
            },
          },
          transitions = { next = "checkID", back = "partMissing" },
        },
        checkID = {
          actions = {
            { GetFsmString = { gameObject = "{Item}", fsmName = "ID", variableName = "ID", storeValue = "{ID}" } },
            {
              StringCompare = { stringVariable = "{ID}", compareTo = "{AcceptID}", equalEvent = "next", notEqualEvent = "back" },
            },
          },
          transitions = { next = "Over", back = "partMissing" },
        },
        Over = {
          actions = {
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Wrench", everyFrame = true } },
            {
              TriggerEvent = {
                gameObject = "owner",
                trigger = "OnTriggerExit",
                collideTag = "vehPartRemoved",
                sendEvent = "back",
                storeCollider = "{Item}",
              },
            },
            { GetButtonDown = { buttonName = "Use", sendEvent = "next" } },
          },
          transitions = { back = "partMissing", next = "Attach" },
        },
        Attach = {
          actions = {
            { PlaySound = { gameObject = "owner", clip = "asset:AudioClip/wrench1", volume = 1 } },
            { GetOwner = { storeGameObject = "{Hinge}" } },
            { SendEvent = { eventTarget = "{PlayerCamera}/GrabItem", sendEvent = "not_Hold" } },
            { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Point" } },
            { DestroyComponent = { gameObject = "{Item}", component = "UnityEngine.Rigidbody" } },
            { SetTag = { gameObject = "{Item}", tag = "vehPart" } },
            { SetLayer = { gameObject = "{Item}", layer = 8 } },
            {
              SetParent = {
                gameObject = "{Item}",
                parent = "{Hinge}",
                worldPositionStays = true,
                resetLocalPosition = true,
                resetLocalRotation = true,
              },
            },
            { NextFrameEvent = { sendEvent = "back" } },
          },
          transitions = { back = "check" },
        },
        check = {
          actions = {
            { GetChild = { gameObject = "owner", childName = "", withTag = "vehPart", storeResult = "{Fitted}" } },
            { GameObjectIsNull = { gameObject = "{Fitted}", isNull = "none", isNotNull = "fitted" } },
          },
          transitions = { none = "partMissing", fitted = "partAttached" },
        },
        partAttached = { actions = { { Wait = { time = 0.25 } } }, transitions = { FINISHED = "check" } },
      },
      start = "check",
    },
    marker = {
      states = {
        empty = {
          actions = {
            { SetVisibility = { gameObject = "$marker", visible = true, resetOnExit = false } },
            { GameObjectHasChildren = { gameObject = "owner", trueEvent = "next", everyFrame = true } },
          },
          transitions = { next = "fitted" },
        },
        fitted = {
          actions = {
            { SetVisibility = { gameObject = "$marker", visible = false, resetOnExit = false } },
            { GameObjectHasChildren = { gameObject = "owner", falseEvent = "back", everyFrame = true } },
          },
          transitions = { back = "empty" },
        },
      },
    },
  }
  return g
end

-- Plain colliders from the objects' own meshes: parts.colliders(g, "Floor", "Body.001", ...) (convex, solid)
function parts.colliders(g, ...)
  for _, name in ipairs({ ... }) do node(g, name).collider = "convex" end
end
-- A run of Blender's numbered copies: parts.numbered("Body Collider", 17) is "Body Collider", ".001" ... ".017"
function parts.numbered(base, count)
  local list = { base }
  for i = 1, count do table.insert(list, string.format("%s.%03d", base, i)) end
  return table.unpack(list)
end
-- Physics-lock volumes (layer 18: loose items inside ride along with the vehicle)
function parts.lock_volumes(g, ...)
  for _, name in ipairs({ ... }) do merge(node(g, name), { layer = 18, collider = { shape = "convex", trigger = true } }) end
end

-- A mount fitted parts go on (an engine, a radiator, a gear lever): name, accepts (the part's ID: "engine", ...).
--   at (a marker object to make the mount at: its pose and collider, shown while the mount is empty),
--   lookup (false: no get<Kind>Hinge on the root; engine and radiator mounts need it), collider (false: the model's own)
function parts.mount(g, name, accepts, o)
  o = o or {}
  local Kind = accepts:sub(1, 1):upper() .. accepts:sub(2)
  local spec = merge(node(g, name), { layer = 13, tag = "Hinge" })
  if o.at then
    merge(spec, { parent = "root", at = o.at, align = o.at })
    if o.collider ~= false then spec.collider = { shape = "box", trigger = true, from = o.at } end
  elseif o.collider ~= false then spec.collider = { shape = "box", trigger = true, render = true } end
  fsm(spec, "vehPart_Attach", { use = "hinge_attach", variables = { string = { AcceptID = accepts } } })
  fsm(spec, "getParent", { variables = { gameObject = { vehicle = "root" } }, states = { idle = {} } })
  if o.at then
    fsm(spec, "get" .. Kind, {
      variables = { gameObject = { [accepts] = null } },
      states = {
        ["no" .. Kind] = {
          actions = {
            { SetVisibility = { gameObject = o.at, visible = true, resetOnExit = false } },
            { SetGameObject = { variable = "{" .. accepts .. "}", gameObject = null } },
            { GameObjectHasChildren = { gameObject = "owner", trueEvent = "next", everyFrame = true } },
          },
          transitions = { next = "has" .. Kind },
        },
        ["has" .. Kind] = {
          actions = {
            { SetVisibility = { gameObject = o.at, visible = false, resetOnExit = false } },
            {
              GetChild = { gameObject = "owner", childName = "", withTag = "vehPart", storeResult = "{" .. accepts .. "}" },
            },
            { GameObjectHasChildren = { gameObject = "owner", falseEvent = "back", everyFrame = true } },
          },
          transitions = { back = "no" .. Kind },
        },
      },
    })
  else
    fsm(spec, "get" .. Kind, {
      variables = { gameObject = { [accepts] = null } },
      states = {
        ["no" .. Kind] = {
          actions = {
            { SetVisibility = { gameObject = "owner", visible = true } },
            { SetGameObject = { variable = "{" .. accepts .. "}", gameObject = null } },
            { GameObjectHasChildren = { gameObject = "owner", trueEvent = "next", everyFrame = true } },
          },
          transitions = { next = "has" .. Kind },
        },
        ["has" .. Kind] = {
          actions = {
            { SetVisibility = { gameObject = "owner", visible = false } },
            {
              GetChild = { gameObject = "owner", childName = "", withTag = "vehPart", storeResult = "{" .. accepts .. "}" },
            },
            { GameObjectHasChildren = { gameObject = "owner", falseEvent = "back", everyFrame = true } },
          },
          transitions = { back = "no" .. Kind },
        },
      },
    })
  end
  if o.lookup ~= false then
    fsm(g, "get" .. Kind .. "Hinge", { variables = { gameObject = { ["hinge_" .. accepts] = name } }, states = { idle = {} } })
  end
end

-- A slot a part clips into (a dashboard, a radio, a headlight, a seat): name, accepts.
--   use ("hinge_attach", "dashboard_attach" or "wheel_attach": how the part is held), at (a marker: made there),
--   align (turned as the marker too), shape ("box" or "sphere"), layer (default 2),
--   marker (true: the marker shows while the slot is empty, the seat's way)
function parts.slot(g, name, accepts, o)
  o = o or {}
  local spec = node(g, name)
  if o.at then merge(spec, { parent = "root", at = o.at }) if o.align then spec.align = o.at end end
  spec.layer = o.layer or 2
  spec.collider = { shape = o.shape or "box", trigger = true, from = o.at }
  fsm(spec, "vehPart_Attach", { use = o.use or "hinge_attach", variables = { string = { AcceptID = accepts } } })
  if o.marker then
    fsm(spec, "getParent", { variables = { gameObject = { vehicle = "root" } }, states = { idle = {} } })
    fsm(spec, "marker", { use = "marker", with = { marker = o.at } })
  end
end

-- The ignition: a key (SigmaMod.Parts.KeySwitch): a click turns it on and off, held it cranks; it starts and stops the
-- engine (fitted, with fuel), lights its lamps, and tells the dashboard. Engine Sim's simulated drivetrain works it too.
--   switch (what is clicked), fuel (the tank), engine (the engine mount), key (what turns: default an empty
--   "Ignition Key" made at `off`; the switch itself works too), off / on / start (lamp objects: shown as the key is),
--   dashboard (default the chassis's)
function parts.ignition(g, o)
  local key = o.key or "Ignition Key"
  if key ~= o.switch and not g.nodes[key] then g.nodes[key] = { parent = "root", at = o.key_at or o.off } end
  local spec = merge(node(g, o.switch), { layer = 0, collider = { shape = "box", trigger = true } })
  add(spec, "AudioSource", "SigmaMod.Parts.KeySwitch")
  spec.AudioSource = { playOnAwake = false, spatialBlend = 1 }
  spec["SigmaMod.Parts.KeySwitch"] = { key = key, off = o.off, on = o.on, start = o.start, fuel = o.fuel, engineMount = o.engine,
    dashboard = o.dashboard or (meta[g] and meta[g].dashboard),
    toggle = "asset:AudioClip/electricity_on1", stop = "asset:AudioClip/EngineStop_2", StarterClip = "asset:AudioClip/poloska_621_v2_start" }
end

-- A fuel tank (the volume a can pours into; SigmaMod.Parts.FuelTank burns its fuel) with its filler cap (SigmaMod.Parts.Filler:
-- the level shown, F opens it to siphon the tank into a can held to the hose end).
--   tank, filler (object names), litres (in it new), capacity, fuel ("Gasoline" or "Diesel"),
--   hose (the hose end's name, made at the filler: hose_at, hose_position), siphon_hose (a hose mesh shown while siphoning),
--   render (false: the tank's mesh is only its volume), saved (false: the level isn't kept with the save)
function parts.fuel_tank(g, o)
  local hose = o.hose or (o.tank .. " Hose End")
  local spec = merge(node(g, o.tank), { layer = 2, tag = o.fuel or "Gasoline", collider = { shape = "box", trigger = true, render = o.render } })
  -- what the game and other mods see: the level (a can pours in by itself) and the rate the fitted engine writes
  fsm(spec, "LiquidAmount", { variables = { float = { Liquid = o.litres or 30, LiquidCapacity = o.capacity or 60 }, object = { FuelModule = null } },
                              states = { idle = {} } })
  fsm(spec, "FuelConsumption", { variables = { float = { FuelConsumption = 0 } }, states = { idle = {} } })
  add(spec, "SigmaMod.Parts.FuelTank")
  if o.saved ~= false then parts.saved(g, o.tank, "_fuel", "LiquidAmount", "Liquid") end
  local cap = merge(node(g, o.filler), { layer = 0, collider = { shape = "box", trigger = true, render = o.filler_render ~= false or nil } })
  add(cap, "AudioSource", "SigmaMod.Parts.Filler")
  cap.AudioSource = { playOnAwake = false, spatialBlend = 1 }
  cap["SigmaMod.Parts.Filler"] = { tank = o.tank, hose = hose, hoseMesh = o.siphon_hose,
    cap = "asset:AudioClip/gastank_cap1", filling = "asset:AudioClip/liquid_filling2" }
  g.nodes[hose] = { parent = "root", layer = 2, add = { "CapsuleCollider" }, CapsuleCollider = { isTrigger = true, radius = 0.1, height = 0.5, enabled = false },
                    at = o.hose_at or o.filler, position = o.hose_position or { -0.5, 0, 0 } }
  fsm(g, "getFuel", { variables = { gameObject = { Fuel = o.tank } }, states = { idle = {} } })
end

-- A lever on a pivot, set in tenths with the mouse, that writes a property every frame (SigmaMod.Parts.Lever).
--   mount (the object it turns on), handle (the lever's mesh), collider (what is clicked), label (shown),
--   target ("<object>:<Component>" or "root:<Component>"), property (a dotted path on it, set 0 - 1, or min - 1),
--   more / less (what the left and right buttons do), step (per click, default 0.1; negative: the left button lowers
--   it), min (default 0; -1 for a steering lever), angle (degrees at full travel, default -40),
--   pivot (the empty it turns about: default "<label> Pivot", made at the handle)
function parts.lever(g, o)
  local pivot = o.pivot or (o.label .. " Pivot")
  g.nodes[pivot] = { parent = o.mount, at = o.handle }
  merge(node(g, o.handle), { parent = pivot })
  local spec = merge(node(g, o.collider), { parent = pivot, layer = 0, collider = { shape = "box", trigger = true } })
  add(spec, "SigmaMod.Parts.Lever")
  spec["SigmaMod.Parts.Lever"] = { label = o.label, target = o.target, property = o.property, min = o.min or 0, step = o.step or 0.1,
                                   angle = o.angle or -40, pivot = pivot, more = o.more or "More", less = o.less or "Less" }
end

-- A lever with two places, on and off (a handbrake): a click pulls or releases it, with the game's sounds
-- (SigmaMod.Parts.ToggleLever). collider (what is clicked, and where the pivot is made: pivot_position from it), label,
--   target, property (1 pulled, 0 released), angle (degrees pulled, default -30), mesh / material (the lever, made on the
--   pivot: "asset:Mesh/..."), use (the hint), pivot (default "<label> Pivot"), lever (default "<label> Lever")
function parts.toggle_lever(g, o)
  local pivot = o.pivot or (o.label .. " Pivot")
  g.nodes[pivot] = { parent = "root", at = o.collider, position = o.pivot_position }
  if o.mesh then
    g.nodes[o.lever or (o.label .. " Lever")] = { parent = pivot, add = { "MeshFilter", "MeshRenderer" },
      MeshFilter = { sharedMesh = o.mesh }, MeshRenderer = { sharedMaterial = o.material } }
  end
  local spec = merge(node(g, o.collider), { layer = 0, collider = { shape = "box", trigger = true } })
  add(spec, "SigmaMod.Parts.ToggleLever")
  spec["SigmaMod.Parts.ToggleLever"] = { label = o.label, target = o.target, property = o.property, pivot = pivot, angle = o.angle or -30,
    pulled = o.pulled or "asset:AudioClip/handbrake1", released = o.released or "asset:AudioClip/handbrake_release1",
    use = o.use or "Pull / release: Left mouse button" }
end

-- A wheel hub a game wheel is fitted to: the WheelController at the hub's marker, the fitted wheel drawn on the hub
-- (its mesh and material onto rim), a bare hub drawn when none is fitted (SigmaMod.Parts.WheelHub; bare_mesh, bare_material).
--   at (the marker), hub (default "<name> Hub"), rim (default "<name> Rim"), rim_rotation (degrees: a right-hand rim),
--   radius, width, mass, grip (a bare hub's: 0.1, 0.2, 10, 0.1), spring (m, 0.2), stiffness (N, 12000), damper (6000),
--   extra (more WheelController values)
function parts.wheel(g, name, o)
  local hub, rim = o.hub or (name .. " Hub"), o.rim or (name .. " Rim")
  local r, m, grip = o.radius or 0.1, o.mass or 10, o.grip or 0.1
  local spec = merge(node(g, name), { parent = "root", at = o.at, layer = 2, collider = { shape = "box", trigger = true, from = o.at }, add = { WC } })
  spec[WC] = merge({ ["wheel.radius"] = r, ["wheel.width"] = o.width or 0.2, ["wheel.mass"] = m, ["forwardFriction.grip"] = grip,
    ["spring.maxLength"] = o.spring or 0.2, ["spring.maxForce"] = o.stiffness or 12000, ["damper.bumpRate"] = o.damper or 6000,
    ["damper.reboundRate"] = o.damper or 6000, layerMask = { 0, 14 }, meshColliderLayer = 2, ["wheel.visual"] = hub }, o.extra)
  fsm(spec, "vehPart_Attach", { use = "wheel_attach", variables = { string = { AcceptID = "wheel" } } })
  fsm(spec, "getParent", { variables = { gameObject = { vehicle = "root" } }, states = { idle = {} } })
  add(spec, "SigmaMod.Parts.WheelHub")
  spec["SigmaMod.Parts.WheelHub"] = { rim = rim, bareMesh = o.bare_mesh or "asset:Mesh/wheel_hub", bareMaterial = o.bare_material or "asset:Material/rusted_black_metal",
                                      radius = r, mass = m, grip = grip }
  g.nodes[hub] = { parent = name }
  g.nodes[rim] = { parent = hub, add = { "MeshFilter", "MeshRenderer" }, rotation = o.rim_rotation }
end

-- A wheel of the vehicle's own (not a part): the WheelController at its mesh, which turns on an empty made there.
--   mesh (the wheel's object), spin (the empty, default "<name> Spin"), position (the axle's offset from the mesh),
--   radius, width, mass, spring, stiffness, damper, rolling (rolling resistance, N m), extra (more values)
function parts.free_wheel(g, name, o)
  local spin = o.spin or (name .. " Spin")
  local spec = merge(node(g, name), { parent = "root", at = o.mesh, position = o.position, add = { WC } })
  spec[WC] = merge({ ["wheel.radius"] = o.radius, ["wheel.width"] = o.width, ["wheel.mass"] = o.mass, ["spring.maxLength"] = o.spring,
    ["spring.maxForce"] = o.stiffness, ["damper.bumpRate"] = o.damper, ["damper.reboundRate"] = o.damper, layerMask = { 0, 14 },
    meshColliderLayer = 2, ["wheel.visual"] = spin, rollingResistanceTorque = o.rolling }, o.extra)
  g.nodes[spin] = { parent = name, at = o.mesh }
  merge(node(g, o.mesh), { parent = spin })
end

-- A flywheel (or a pulley): a wheel nothing touches, its mesh turning on an empty, with Engine Sim's air drag and rim
-- friction (it rubs on what it lands against). mesh, spin (default "<name> Spin"), position, radius, width, mass
function parts.flywheel(g, name, o)
  local spin = o.spin or (name .. " Spin")
  local spec = merge(node(g, name), { parent = "root", at = o.mesh, position = o.position })
  add(spec, WC, "EngineSim.Windage", "EngineSim.RimFriction")
  spec.WheelController = { ["wheel.visual"] = spin, ["wheel.radius"] = o.radius, ["wheel.width"] = o.width, ["wheel.mass"] = o.mass,
                           ["spring.maxLength"] = 0, layerMask = { 0, 14 }, meshColliderLayer = 2 }
  g.nodes[spin] = { parent = name }
  merge(node(g, o.mesh), { parent = spin })
end

-- Two wheels turned together (Engine Sim's WheelCoupling): a, b (wheel objects), ratio (b's turns per a's),
-- capacity (the most torque, N m at b; 0: rigid), engagement (0 - 1)
function parts.coupling(g, name, o)
  local spec = merge(node(g, name), { parent = "root" })
  add(spec, "EngineSim.WheelCoupling")
  spec["EngineSim.WheelCoupling"] = { a = o.a .. ":" .. WC, b = o.b .. ":" .. WC, ratio = o.ratio or 1, capacity = o.capacity or 0, engagement = o.engagement or 1 }
end

-- A belt drawn round two pulleys (Engine Sim's Belt) on a coupling made with parts.coupling (or an IBeltDrive):
--   coupling (its object), a, b (the pulleys' centres), radius_a, radius_b, width, thickness, sag, pitch, drive (an
--   IBeltDrive component instead: "<object>:<Type>")
function parts.belt(g, name, o)
  local spec = merge(node(g, name), { parent = "root" })
  add(spec, "EngineSim.Belt")
  spec["EngineSim.Belt"] = { a = o.a, b = o.b, radiusA = o.radius_a, radiusB = o.radius_b, width = o.width or 0.05, thickness = o.thickness or 0.008,
    sag = o.sag or 0.06, coupling = o.coupling and (o.coupling .. ":EngineSim.WheelCoupling") or nil, crankBelt = o.drive, pitch = o.pitch or 0.02 }
end

-- A gear lever (SigmaMod.Parts.GearShifter): the clickable shifts up and down, and the lever's joint leans the fitted
-- lever to each gear. collider (what is clicked), joint (an empty made at the mount), mount (where a game gear lever is
-- fitted: a mount of its own, on the joint), gate (the lean per gear: reverse, neutral, 1st ...)
function parts.gear_lever(g, o)
  local spec = merge(node(g, o.collider), { parent = "root", layer = 0, collider = { shape = "box", trigger = true } })
  add(spec, "SigmaMod.Parts.GearShifter")
  spec["SigmaMod.Parts.GearShifter"] = { label = o.label or "Gear", target = "root:VehicleController", lever = o.joint,
    -- the lever's lean in each gear, degrees: reverse, neutral, 1st ... 5th
    gate = o.gate or { { -15, 4, -15 }, { 0, 0, 0 }, { 15, 4, 15 }, { -15, -4, 15 }, { 15, 0, 0 }, { -15, 0, 0 }, { 15, -4, -15 } } }
  merge(node(g, o.joint), { parent = "root", at = o.mount })
  parts.mount(g, o.mount, "gearlever", { lookup = false })
  node(g, o.mount).parent = o.joint
end

-- A light and its switch (click: on and off; SigmaMod.Parts.Switch). lamp (the light: made at `at`, position from it),
-- collider (the switch), light (Unity Light values), label (shown, default "Light")
function parts.light(g, o)
  g.nodes[o.lamp] = { parent = o.at, at = o.at, position = o.position, add = { "Light" },
    Light = merge({ type = "Point", range = 2.5, intensity = 1.2, color = { 1, 0.86, 0.62, 1 }, shadows = "None" }, o.light) }
  local spec = merge(node(g, o.collider), { layer = 0, collider = { shape = "box", trigger = true } })
  add(spec, "SigmaMod.Parts.Switch")
  spec["SigmaMod.Parts.Switch"] = { label = o.label or "Light", whenOn = { o.lamp }, clip = "asset:AudioClip/switch1", state = false }
end

-- Headlights: a switch that sends the game's HeadlightsON / OFF to the vehicle and shows one of two objects (the switch
-- up or down; SigmaMod.Parts.Switch). collider (the switch), on, off (the switch's two objects)
function parts.headlights(g, o)
  local spec = merge(node(g, o.collider), { layer = 0, collider = { shape = "box", trigger = true } })
  add(spec, "SigmaMod.Parts.Switch")
  spec["SigmaMod.Parts.Switch"] = { label = o.label or "Headlights", whenOn = { o.on }, whenOff = { o.off },
    eventOn = "HeadlightsON", eventOff = "HeadlightsOFF", clip = "asset:AudioClip/switch1" }
end

-- A tow hitch trailers hook onto (the game's TrailerAttach and TrailerLoad on the root), with a clickable to hitch and
-- unhitch. hitch (the hitch's object: its own collider), collider (the clickable), reach (m: the trailer's ray, 0.511)
function parts.tow_hitch(g, o)
  node(g, o.hitch).collider = { shape = "convex", render = true }
  g.nodes.hitch = { parent = "root", at = o.hitch }
  g.nodes.hitch_raycast = { parent = "root", at = o.hitch, position = { 0, 0, o.reach or 0.511 } }
  g.playmaker.TrailerAttach = {
    variables = {
      float = { speed = 0 },
      bool = { trailerAttached = false, trailerInRange_bool = false },
      vector3 = { attacher_pos = { 0, 0, 0 }, hitch_pos = { 0, 0, 0 } },
      gameObject = { attacher = null, Trailer = null },
      object = {
        ConfigurableJoint = { type = "UnityEngine.ConfigurableJoint", value = null },
        Rigidbody = { type = "UnityEngine.Rigidbody", value = null },
      },
    },
    start = "trailerOff",
    states = {
      trailerOff = {
        actions = {
          { UiTextSetText = { gameObject = "{UI_AttachTrailer}", text = "" } },
          { BoolTest = { boolVariable = "{trailerAttached}", isTrue = "next2", everyFrame = true } },
          {
            SphereCast2 = {
              fromGameObject = "hitch_raycast",
              radius = 0.2,
              direction = { 0, 0, -1 },
              space = "Self",
              maxDistance = 0.2,
              storeDidHit = "{trailerInRange_bool}",
              storeHitObject = "{Trailer}",
              repeatInterval = 2,
              layerMask = { 20 },
            },
          },
          { BoolTest = { boolVariable = "{trailerInRange_bool}", isTrue = "next", everyFrame = true } },
        },
        transitions = { next = "canAttach", next2 = "trailerOn" },
      },
      canAttach = {
        actions = {
          {
            SphereCast2 = {
              fromGameObject = "hitch_raycast",
              radius = 0.2,
              direction = { 0, 0, -1 },
              space = "Self",
              maxDistance = 0.2,
              storeDidHit = "{trailerInRange_bool}",
              storeHitObject = "{Trailer}",
              repeatInterval = 2,
              layerMask = { 20 },
            },
          },
          { BoolTest = { boolVariable = "{trailerInRange_bool}", isFalse = "back", everyFrame = true } },
        },
        transitions = { back = "trailerOff", hitchUse = "clear" },
      },
      attach = {
        actions = {
          { GetChild = { gameObject = "{Trailer}", childName = "attacher", storeResult = "{attacher}" } },
          { GetPosition = { gameObject = "hitch", vector = "{hitch_pos}", space = "Self" } },
          { GetPosition = { gameObject = "{attacher}", vector = "{attacher_pos}", space = "Self" } },
          {
            AddComponent = {
              gameObject = "{Trailer}",
              component = "UnityEngine.ConfigurableJoint",
              storeComponent = "{ConfigurableJoint}",
            },
          },
          { GetComponent = { gameObject = "owner", storeComponent = "{Rigidbody}" } },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "connectedBody",
                setProperty = true,
                ObjectParameter = "{Rigidbody}",
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "anchor",
                setProperty = true,
                Vector3Parameter = "{attacher_pos}",
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "autoConfigureConnectedAnchor",
                setProperty = true,
                BoolParameter = false,
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "connectedAnchor",
                setProperty = true,
                Vector3Parameter = "{hitch_pos}",
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "xMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Locked" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "yMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Locked" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "zMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Locked" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "angularXMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Free" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "angularYMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Free" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "angularZMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Free" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "projectionDistance",
                setProperty = true,
                FloatParameter = 0.1,
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "projectionAngle",
                setProperty = true,
                FloatParameter = 180,
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "enableCollision",
                setProperty = true,
                BoolParameter = true,
              },
            },
          },
          { SetBoolValue = { boolVariable = "{trailerAttached}", boolValue = true } },
          { NextFrameEvent = { sendEvent = "next" } },
        },
        transitions = { next = "trailerOn" },
      },
      trailerOn = {
        actions = {
          { GetSpeed = { gameObject = "owner", storeResult = "{speed}", everyFrame = true } },
          {
            FloatCompare = { float1 = "{speed}", float2 = 3, tolerance = 0, greaterThan = "next2", everyFrame = true },
          },
        },
        transitions = { next2 = "moving", hitchUse = "detach" },
      },
      detach = {
        actions = { { SetBoolValue = { boolVariable = "{trailerAttached}", boolValue = false } } },
        transitions = { FINISHED = "unjoint" },
      },
      moving = {
        actions = {
          { GetSpeed = { gameObject = "owner", storeResult = "{speed}", everyFrame = true } },
          {
            FloatCompare = { float1 = "{speed}", float2 = 3, tolerance = 0, lessThan = "back", everyFrame = true },
          },
        },
        transitions = { back = "trailerOn" },
      },
      clear = {
        actions = {
          {
            HasComponent = {
              gameObject = "{Trailer}",
              component = "UnityEngine.ConfigurableJoint",
              trueEvent = "remove",
              falseEvent = "done",
            },
          },
        },
        transitions = { remove = "clearRemove", done = "attach" },
      },
      clearRemove = {
        actions = {
          { DestroyComponent = { gameObject = "{Trailer}", component = "UnityEngine.ConfigurableJoint" } },
          { NextFrameEvent = { sendEvent = "next" } },
        },
        transitions = { next = "clear" },
      },
      unjoint = {
        actions = {
          {
            HasComponent = {
              gameObject = "{Trailer}",
              component = "UnityEngine.ConfigurableJoint",
              trueEvent = "remove",
              falseEvent = "done",
            },
          },
        },
        transitions = { remove = "unjointRemove", done = "trailerOff" },
      },
      unjointRemove = {
        actions = {
          { DestroyComponent = { gameObject = "{Trailer}", component = "UnityEngine.ConfigurableJoint" } },
          { NextFrameEvent = { sendEvent = "next" } },
        },
        transitions = { next = "unjoint" },
      },
    },
  }
  g.playmaker.TrailerLoad = {
    variables = {
      float = {},
      bool = { trailerInRange_bool = false },
      vector3 = { attacher_pos = { 0, 0, 0 }, hitch_pos = { 0, 0, 0 } },
      gameObject = { attacher = null, Trailer = null },
      object = {
        ConfigurableJoint = { type = "UnityEngine.ConfigurableJoint", value = null },
        Rigidbody = { type = "UnityEngine.Rigidbody", value = null },
      },
    },
    start = "loadAttach",
    states = {
      loadAttach = {
        actions = {
          {
            SphereCast2 = {
              fromGameObject = "hitch_raycast",
              radius = 0.2,
              direction = { 0, 0, -1 },
              space = "Self",
              maxDistance = 0.2,
              storeDidHit = "{trailerInRange_bool}",
              storeHitObject = "{Trailer}",
              repeatInterval = 1,
              layerMask = { 20 },
            },
          },
          { BoolTest = { boolVariable = "{trailerInRange_bool}", isTrue = "next", everyFrame = true } },
          { Wait = { time = 0.5, finishEvent = "back" } },
        },
        transitions = { next = "clear", back = "trailerOff" },
      },
      attach = {
        actions = {
          { GetChild = { gameObject = "{Trailer}", childName = "attacher", storeResult = "{attacher}" } },
          { GetPosition = { gameObject = "hitch", vector = "{hitch_pos}", space = "Self" } },
          { GetPosition = { gameObject = "{attacher}", vector = "{attacher_pos}", space = "Self" } },
          {
            AddComponent = {
              gameObject = "{Trailer}",
              component = "UnityEngine.ConfigurableJoint",
              storeComponent = "{ConfigurableJoint}",
            },
          },
          { GetComponent = { gameObject = "owner", storeComponent = "{Rigidbody}" } },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "connectedBody",
                setProperty = true,
                ObjectParameter = "{Rigidbody}",
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "anchor",
                setProperty = true,
                Vector3Parameter = "{attacher_pos}",
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "autoConfigureConnectedAnchor",
                setProperty = true,
                BoolParameter = false,
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "connectedAnchor",
                setProperty = true,
                Vector3Parameter = "{hitch_pos}",
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "xMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Locked" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "yMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Locked" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "zMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Locked" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "angularXMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Free" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "angularYMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Free" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "angularZMotion",
                setProperty = true,
                EnumParameter = { type = "UnityEngine.ConfigurableJointMotion", value = "Free" },
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "projectionDistance",
                setProperty = true,
                FloatParameter = 0.1,
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "projectionAngle",
                setProperty = true,
                FloatParameter = 180,
              },
            },
          },
          {
            SetProperty = {
              targetProperty = {
                TargetObject = "{ConfigurableJoint}",
                TargetTypeName = "UnityEngine.ConfigurableJoint",
                PropertyName = "enableCollision",
                setProperty = true,
                BoolParameter = true,
              },
            },
          },
          { NextFrameEvent = { sendEvent = "next" } },
        },
        transitions = { next = "trailerOn" },
      },
      trailerOn = {
        actions = {
          {
            SetFsmGameObject = { gameObject = "owner", fsmName = "TrailerAttach", variableName = "Trailer", setValue = "{Trailer}" },
          },
          {
            SetFsmBool = { gameObject = "owner", fsmName = "TrailerAttach", variableName = "trailerAttached", setValue = true },
          },
        },
      },
      trailerOff = {},
      clear = {
        actions = {
          {
            HasComponent = {
              gameObject = "{Trailer}",
              component = "UnityEngine.ConfigurableJoint",
              trueEvent = "remove",
              falseEvent = "done",
            },
          },
        },
        transitions = { remove = "clearRemove", done = "attach" },
      },
      clearRemove = {
        actions = {
          { DestroyComponent = { gameObject = "{Trailer}", component = "UnityEngine.ConfigurableJoint" } },
          { NextFrameEvent = { sendEvent = "next" } },
        },
        transitions = { next = "clear" },
      },
    },
  }
  local spec = merge(node(g, o.collider), { layer = 0, collider = { shape = "box", trigger = true } })
  fsm(spec, "HitchUse", {
    variables = { bool = { attached = false, inRange = false } },
    globalEvents = { "OverItem", "OffItem" },
    global = { OverItem = "over", OffItem = "off" },
    start = "off",
    states = {
      off = {
        actions = {
          { UiTextSetText = { gameObject = "{UI_ItemName}", text = "" } },
          { UiTextSetText = { gameObject = "{UI_ItemName_2}", text = "" } },
          { UiTextSetText = { gameObject = "{UI_ItemName_3}", text = "" } },
          { UiTextSetText = { gameObject = "{UI_ItemUse}", text = "" } },
        },
      },
      over = {
        actions = {
          { UiTextSetText = { gameObject = "{UI_ItemName}", text = "Tow Hitch" } },
          {
            GetFsmBool = {
              gameObject = "root",
              fsmName = "TrailerAttach",
              variableName = "trailerAttached",
              storeValue = "{attached}",
            },
          },
          { BoolTest = { boolVariable = "{attached}", isTrue = "attached", isFalse = "notAttached" } },
        },
        transitions = { attached = "attached", notAttached = "checkRange" },
      },
      checkRange = {
        actions = {
          {
            GetFsmBool = {
              gameObject = "root",
              fsmName = "TrailerAttach",
              variableName = "trailerInRange_bool",
              storeValue = "{inRange}",
            },
          },
          { BoolTest = { boolVariable = "{inRange}", isTrue = "inRange", isFalse = "none" } },
        },
        transitions = { inRange = "inRange", none = "none" },
      },
      attached = {
        actions = {
          { UiTextSetText = { gameObject = "{UI_ItemName_2}", text = "Trailer hitched" } },
          { UiTextSetText = { gameObject = "{UI_ItemUse}", text = "Unhitch: Left mouse button" } },
          { GetMouseButtonDown = { button = "Left", sendEvent = "use", inUpdateOnly = true } },
          { Wait = { time = 0.25 } },
        },
        transitions = { use = "use", FINISHED = "over" },
      },
      inRange = {
        actions = {
          { UiTextSetText = { gameObject = "{UI_ItemName_2}", text = "Trailer in reach" } },
          { UiTextSetText = { gameObject = "{UI_ItemUse}", text = "Hitch: Left mouse button" } },
          { GetMouseButtonDown = { button = "Left", sendEvent = "use", inUpdateOnly = true } },
          { Wait = { time = 0.25 } },
        },
        transitions = { use = "use", FINISHED = "over" },
      },
      none = {
        actions = {
          { UiTextSetText = { gameObject = "{UI_ItemName_2}", text = "No trailer in reach" } },
          { UiTextSetText = { gameObject = "{UI_ItemUse}", text = "" } },
          { Wait = { time = 0.25 } },
        },
        transitions = { FINISHED = "over" },
      },
      use = {
        actions = {
          { SendEvent = { eventTarget = "root/TrailerAttach", sendEvent = "hitchUse" } },
          { Wait = { time = 0.1 } },
        },
        transitions = { FINISHED = "over" },
      },
    },
  })
end

-- A tool kept on the vehicle (the game's toolbox: F uses it): made at a marker. at, mesh, material ("asset:..."),
-- id (the game's item ID, default "Toolbox"), label (shown, default "Toolset"),
-- sphere (radius: a round trigger) or box ({ center, size })
function parts.toolset(g, name, o)
  local spec = merge(node(g, name), { parent = "root", at = o.at, align = o.at, layer = 0, add = { "MeshFilter", "MeshRenderer" },
    MeshFilter = { sharedMesh = o.mesh }, MeshRenderer = { sharedMaterial = o.material } })
  if o.box then
    add(spec, "BoxCollider"); spec.BoxCollider = { isTrigger = true, center = o.box.center, size = o.box.size }
  else
    add(spec, "SphereCollider"); spec.SphereCollider = { isTrigger = true, center = { 0, 0, 0 }, radius = o.sphere or 0.05 }
  end
  fsm(spec, "ID", { variables = { string = { ID = o.id or "Toolbox" } }, states = { ID = {} } })
  fsm(spec, "ItemName", {
    globalEvents = { "OverItem", "OffItem" },
    global = { OverItem = "over", OffItem = "off" },
    start = "off",
    states = {
      off = {
        actions = {
          { UiTextSetText = { gameObject = "{UI_ItemName}", text = "" } },
          { UiTextSetText = { gameObject = "{UI_ItemUse}", text = "" } },
        },
      },
      over = {
        actions = {
          { UiTextSetText = { gameObject = "{UI_ItemName}", text = (o.label or "Toolset") } },
          { UiTextSetText = { gameObject = "{UI_ItemUse}", text = "Use: F" } },
        },
      },
    },
  })
end

-- A driver's seat: get in at `enter` (the game's Drive FSM: the player sits at sitPos, the camera drives), get out at
-- `exit`; the input object hears DriveOn and DriveOff. enter, exit (colliders), input (the object whose FSMs take the
-- controls), sit (where the head goes: an object, and sit_position from it), exit_position (from `exit`)
function parts.driver_seat(g, o)
  merge(node(g, o.enter), { layer = 7, collider = { shape = "box", trigger = true } })
  fsm(g.nodes[o.enter], "Drive", {
    variables = {
      float = { speed = 0 },
      vector3 = { exitPosVector = { 0, 0, 0 } },
      object = { FloatingOrigin = { type = "NWH.Common.FloatingOrigin.FloatingOrigin", value = null } },
    },
    globalEvents = { "Activate", "Deactivate" },
    global = { Activate = "enter", Deactivate = "exit" },
    start = "outCar",
    states = {
      outCar = {},
      enter = {
        actions = {
          { UiTextSetText = { gameObject = "{UI_ItemName}", text = "" } },
          {
            SetParent = {
              gameObject = "{Player}",
              parent = "sitPos",
              worldPositionStays = false,
              resetLocalPosition = true,
              resetLocalRotation = true,
            },
          },
          { SendEvent = { eventTarget = "{Player}/InCar", sendEvent = "InCar" } },
          {
            EnableCollider = {
              gameObject = "owner",
              colliderSelect = "Box",
              enable = false,
              inclChildren = false,
              allCollider = false,
            },
          },
          {
            EnableCollider = {
              gameObject = o.exit,
              colliderSelect = "Box",
              enable = true,
              inclChildren = false,
              allCollider = false,
            },
          },
          { SendEvent = { eventTarget = o.input, sendEvent = "DriveOn" } },
          {
            AddComponent = {
              gameObject = "root",
              component = "NWH.Common.FloatingOrigin.FloatingOrigin",
              storeComponent = "{FloatingOrigin}",
            },
          },
          { SetProperties = { on = "{FloatingOrigin}", distanceThreshold = 1000 } },
        },
        transitions = { FINISHED = "inCar" },
      },
      inCar = {
        actions = {
          {
            GetFsmFloat = {
              gameObject = "root",
              fsmName = "Speed",
              variableName = "speed",
              storeValue = "{speed}",
              everyFrame = true,
            },
          },
          {
            SetFsmFloat = {
              gameObject = "{PlayerCamera}",
              fsmName = "DriveUse",
              variableName = "speed",
              setValue = "{speed}",
              everyFrame = true,
            },
          },
        },
      },
      exit = {
        actions = {
          { DestroyComponent = { gameObject = "root", component = "NWH.Common.FloatingOrigin.FloatingOrigin" } },
          { UiTextSetText = { gameObject = "{UI_ItemName}", text = "" } },
          {
            SetParent = {
              gameObject = "{Player}",
              parent = null,
              worldPositionStays = false,
              resetLocalPosition = false,
              resetLocalRotation = false,
            },
          },
          { GetPosition = { gameObject = "exitPos", vector = "{exitPosVector}", space = "World" } },
          { SetPosition = { gameObject = "{Player}", vector = "{exitPosVector}", space = "World" } },
          { SendEvent = { eventTarget = "{Player}/InCar", sendEvent = "OnFoot" } },
          { SendEvent = { eventTarget = o.input, sendEvent = "DriveOff" } },
          {
            EnableCollider = {
              gameObject = "owner",
              colliderSelect = "Box",
              enable = true,
              inclChildren = false,
              allCollider = false,
            },
          },
          {
            EnableCollider = {
              gameObject = o.exit,
              colliderSelect = "Box",
              enable = false,
              inclChildren = false,
              allCollider = false,
            },
          },
        },
        transitions = { FINISHED = "outCar" },
      },
    },
  })
  merge(node(g, o.exit), { layer = 7, collider = { shape = "box", trigger = true }, BoxCollider = { enabled = false } })
  fsm(g.nodes[o.exit], "Drive", {
    globalEvents = { "Deactivate" },
    global = { Deactivate = "forward" },
    start = "idle",
    states = {
      idle = {},
      forward = {
        actions = { { SendEvent = { eventTarget = o.enter .. "/Drive", sendEvent = "Deactivate" } } },
        transitions = { FINISHED = "idle" },
      },
    },
  })
  g.nodes.DriveTrigger = {
    parent = "root",
    at = o.enter,
    layer = 7,
    add = { "Rigidbody", "ConfigurableJoint" },
    Rigidbody = { mass = 1 },
    ConfigurableJoint = {
      connectedBody = "root:Rigidbody",
      xMotion = "Locked",
      yMotion = "Locked",
      zMotion = "Locked",
      angularXMotion = "Locked",
      angularYMotion = "Locked",
      angularZMotion = "Locked",
      enableCollision = false,
    },
  }
  g.nodes.sitPos = { parent = "DriveTrigger", at = o.sit, position = o.sit_position }
  g.nodes.exitPos = { parent = "DriveTrigger", at = o.exit, position = o.exit_position }
end

-- Bones posed by hand (a seated player model): pose(g, { { "bone", "parent", { x, y, z }, { qx, qy, qz, qw } }, ... })
function parts.pose(g, bones)
  for _, b in ipairs(bones) do g.nodes[b[1]] = { parent = b[2], position = b[3], rotation = b[4] } end
end

-- An FSM float kept with the save, under the root's name and a suffix (the root is saved as the game saves a car)
function parts.saved(g, name, suffix, fsmName, var)
  local spec = node(g, name)
  fsm(spec, "saveItemVar" .. (spec.playmaker and spec.playmaker.saveItemVar and ("_" .. var) or ""), {
    variables = { float = { [var] = 0 }, string = { name = "" }, gameObject = { root = null } },
    globalEvents = { "SaveGame", "LoadVar" }, start = "off", global = { SaveGame = "save", LoadVar = "load" },
    states = {
      off = {},
      save = { actions = {
        { GetRoot = { gameObject = "owner", storeRoot = "{root}" } }, { GetName = { gameObject = "{root}", storeName = "{name}" } },
        { StringAppend = { stringVariable = "{name}", appendString = suffix } },
        { GetFsmFloat = { gameObject = "owner", fsmName = fsmName, variableName = var, storeValue = "{" .. var .. "}" } },
        { SaveAll = { key = "{name}", saveFsmVariables = true, saveGlobalVariables = false, path = "SaveFile.es3" } },
      }, transitions = { FINISHED = "off" } },
      load = { actions = {
        { GetRoot = { gameObject = "owner", storeRoot = "{root}" } }, { GetName = { gameObject = "{root}", storeName = "{name}" } },
        { StringAppend = { stringVariable = "{name}", appendString = suffix } },
        { LoadAll = { key = "{name}", loadFsmVariables = true, loadGlobalVariables = false, path = "SaveFile.es3" } },
        { SetFsmFloat = { gameObject = "owner", fsmName = fsmName, variableName = var, setValue = "{" .. var .. "}" } },
      }, transitions = { FINISHED = "off" } },
    },
  })
end

-- Repairable the game's way (a vehicle part with a Condition the repair box adds to), its condition kept with the save.
--   suffix (the save key's, default "_" .. name), condition (new, default 100)
function parts.repairable(g, name, o)
  o = o or {}
  local spec = merge(node(g, name), { layer = 8, tag = "vehPart" })
  fsm(spec, "Condition", { variables = { float = { Condition = o.condition or 100 } }, states = { idle = {} } })
  fsm(spec, "Repair", {
    variables = { float = { Condition = 100 } },
    globalEvents = { "Repair" },
    start = "idle",
    global = { Repair = "repair" },
    states = {
      idle = {},
      repair = {
        actions = {
          {
            GetFsmFloat = {
              gameObject = "owner",
              fsmName = "Condition",
              variableName = "Condition",
              storeValue = "{Condition}",
              everyFrame = false,
            },
          },
          { FloatAdd = { floatVariable = "{Condition}", add = 25, everyFrame = false, perSecond = false } },
          { FloatClamp = { floatVariable = "{Condition}", minValue = 0, maxValue = 100, everyFrame = false } },
          {
            SetFsmFloat = {
              gameObject = "owner",
              fsmName = "Condition",
              variableName = "Condition",
              setValue = "{Condition}",
              everyFrame = false,
            },
          },
        },
        transitions = { FINISHED = "idle" },
      },
    },
  })
  parts.saved(g, name, o.suffix or ("_" .. name), "Condition", "Condition")
end

-- A hover readout: the game's four HUD lines from properties of components, every frame while looked at
-- (SigmaMod.Parts.Readout). source ("<object>:<Component>"), and the property (a dotted path) for each line: name, line2,
-- line3, use (lines left out stay empty; name falls back to label); sources = { [line] = "<object>:<Component>" } for a
-- line from another component
function parts.tooltip(g, name, o)
  local spec = node(g, name)
  local srcs, props = {}, {}
  for i, key in ipairs({ "name", "line2", "line3", "use" }) do
    srcs[i] = o[key] and ((o.sources and o.sources[key]) or o.source) or null
    props[i] = o[key] or ""
  end
  add(spec, "SigmaMod.Parts.Readout")
  spec["SigmaMod.Parts.Readout"] = { label = o.label or name, sources = srcs, properties = props }
end

-- The engine's sound source where the engine sits (the game's RealisticEngineSound, driven by NWH): at (the mount)
function parts.engine_sound(g, o)
  g.nodes.RSound = { parent = "root", add = { "NWH2_RES2" }, at = o.at }
  g.nodes.EngineSound = {
    parent = "RSound",
    add = { "RealisticEngineSound" },
    RealisticEngineSound = { enableReverseGear = false, dynamicAudioMixer = "Off" },
  }
  g.playmaker.GetRSound = { variables = { gameObject = { RSound = "RSound" } }, states = { idle = {} } }
end

-- A game item made into another (a template with a donor item): its ID (what the game and mods know it by) and the name
-- it shows. parts.item { id = "RopeTools", name = "Rope Kit" } is the whole template.
function parts.item(o)
  local f = {}
  if o.id then f.ID = { ID = o.id } end
  if o.name then f.ItemName = { ItemName = o.name } end
  return { fsm = f }
end

-- A loose item that attaches to vehicles the way the game's attachables do (its metal plates): held against a
-- vehicle's part, Use fixes it there (a fitted part: parented to that part, no body of its own, moving with the
-- vehicle); the game's tools take it off again, and it falls loose. Its name on screen while looked at.
--   mass (kg, default 10), id (the game's item ID, default "attachable")
--   colliders (Blender objects whose meshes become its colliders: convex, not drawn), collider_groups (objects that
--   already carry its colliders, e.g. an exported vehicle's "colliders"): with either, it is built as the game builds
--   an item of many colliders (a car door): those are switched off while it is held, and the item itself has a trigger
--   box round its mesh, which the game's grab finds before anything inside it, and a small solid sphere, the collider
--   the game makes a trigger while it is held: grip = { center = { x, y, z }, radius } (default its origin, 0.1).
--   Without either, its own mesh is its collider (convex).
function parts.attachable(g, o)
  o = o or {}
  g.nodes = g.nodes or {}
  merge(g, { layer = 9, tag = "vehPartRemoved" })   -- a loose part
  add(g, "Rigidbody")
  g.Rigidbody = merge(g.Rigidbody or {}, { mass = o.mass or 10, collisionDetectionMode = "Continuous" })

  local held = {}   -- what is switched off while it is held
  for _, c in ipairs(o.colliders or {}) do node(g, c).collider = "convex"; table.insert(held, c) end
  for _, c in ipairs(o.collider_groups or {}) do table.insert(held, c) end
  if #held > 0 then
    local grip = o.grip or {}
    add(g, "SphereCollider")
    g.SphereCollider = { center = grip.center or { 0, 0, 0 }, radius = grip.radius or 0.1 }
    g.collider = { shape = "box", trigger = true, render = true }
    local function switch(on)
      local actions = {}
      for _, c in ipairs(held) do table.insert(actions, { ActivateGameObject = { gameObject = c, activate = on } }) end
      return actions
    end
    -- the game's grab sends these
    fsm(g, "Colliders", {
      globalEvents = { "CollidersActivate", "CollidersDisable" },
      global = { CollidersActivate = "on", CollidersDisable = "off" },
      states = { on = { actions = switch(true) }, off = { actions = switch(false) } },
    })
  else
    g.collider = { shape = "convex", render = true }
  end

  fsm(g, "ID", { variables = { string = { ID = o.id or "attachable" } }, states = { ID = {} } })
  fsm(g, "ItemName", {
    variables = { string = { ItemName = "" } },
    globalEvents = { "OverItem", "OffItem" },
    global = { OverItem = "over", OffItem = "off" },
    start = "off",
    states = {
      off = { actions = { { UiTextSetText = { gameObject = "{UI_ItemName}", text = "" } } } },
      over = { actions = { { UiTextSetText = { gameObject = "{UI_ItemName}", text = "{ItemName}" } } } },
    },
  })

  -- held against a vehicle's part (layer 8), Use fixes it there
  fsm(g, "Attach", {
    variables = { gameObject = { CarPart = null } },
    states = {
      partMissing = {
        actions = {
          { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Point" } },
          { TriggerEventByLayer = { trigger = "OnTriggerEnter", layer = 8, sendEvent = "next", storeCollider = "{CarPart}" } },
        },
        transitions = { next = "checkChild" },
      },
      checkChild = {
        actions = {
          { GameObjectIsChildOf = { gameObject = "owner", isChildOf = "{Hand}", trueEvent = "next", falseEvent = "back" } },
        },
        transitions = { next = "Over", back = "partMissing" },
      },
      Over = {
        actions = {
          { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Wrench", everyFrame = true } },
          { TriggerEventByLayer = { trigger = "OnTriggerExit", layer = 8, sendEvent = "back", storeCollider = "{CarPart}" } },
          { GetButtonDown = { buttonName = "Use", sendEvent = "next" } },
        },
        transitions = { back = "partMissing", next = "Attach" },
      },
      Attach = {
        actions = {
          { PlaySound = { gameObject = "owner", clip = "asset:AudioClip/wrench1", volume = 1 } },
          { DestroyComponent = { gameObject = "owner", component = "UnityEngine.Rigidbody" } },
          { SendEvent = { eventTarget = "{PlayerCamera}/GrabItem", sendEvent = "not_Hold" } },
          { SendEvent = { eventTarget = "{UI_Canvas}", sendEvent = "Cursor_Point" } },
          { SetParent = { gameObject = "owner", parent = "{CarPart}", worldPositionStays = true } },
          { SetLayer = { gameObject = "owner", layer = 8 } },
          { SetTag = { gameObject = "owner", tag = "vehPart" } },
          { NextFrameEvent = { sendEvent = "back" } },
        },
        transitions = { back = "partMissing" },
      },
    },
  })

  -- the game's tools take it off with de_Attach: a loose part again
  fsm(g, "de_Attach", {
    globalEvents = { "de_Attach" },
    global = { de_Attach = "de Attach" },
    states = {
      idle = {},
      ["de Attach"] = {
        actions = {
          { SetLayer = { gameObject = "owner", layer = 9 } },
          { SetTag = { gameObject = "owner", tag = "vehPartRemoved" } },
        },
        transitions = { FINISHED = "idle" },
      },
    },
  })

  -- fitted or loose, by its tag: taken off, it leaves the vehicle and gets its body back (its own mass); fitted, it
  -- has none, and can't be fixed again
  fsm(g, "CheckTag", {
    variables = {
      float = { mass = 0 },
      gameObject = { owner = null },
      object = { Rigidbody = { type = "UnityEngine.Rigidbody", value = null } },
    },
    states = {
      start = {
        actions = {
          { GetMass = { gameObject = "owner", storeResult = "{mass}" } },
          { GetOwner = { storeGameObject = "{owner}" } },
          { NextFrameEvent = { sendEvent = "next" } },
        },
        transitions = { next = "vehPartRemoved" },
      },
      vehPartRemoved = {
        actions = {
          { GameObjectCompareTag = { gameObject = "{owner}", tag = "vehPart", trueEvent = "bolted" } },
          { SetParent = { gameObject = "owner", parent = null, worldPositionStays = true } },
          { EnableFSM = { gameObject = "owner", fsmName = "Attach", enable = true } },
          { HasRigidBody = { gameObject = "owner", found = "{}", foundEvent = "loose" } },
          { AddComponent = { gameObject = "owner", component = "UnityEngine.Rigidbody", storeComponent = "{Rigidbody}" } },
          { SetMass = { gameObject = "owner", mass = "{mass}" } },
          { SetProperties = { on = "{Rigidbody}", collisionDetectionMode = "Continuous" } },
        },
        transitions = { bolted = "vehPart", loose = "loose", FINISHED = "loose" },
      },
      loose = {
        actions = {
          { GameObjectCompareTag = { gameObject = "{owner}", tag = "vehPart", trueEvent = "next", everyFrame = true } },
        },
        transitions = { next = "vehPart" },
      },
      vehPart = {
        actions = {
          { DestroyComponent = { gameObject = "owner", component = "UnityEngine.Rigidbody" } },
          { EnableFSM = { gameObject = "owner", fsmName = "Attach", enable = false } },
          { GameObjectCompareTag = { gameObject = "{owner}", tag = "vehPart", falseEvent = "next", everyFrame = true } },
        },
        transitions = { next = "vehPartRemoved" },
      },
    },
  })
  return g
end

return parts
