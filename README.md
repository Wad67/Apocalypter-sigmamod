# SigmaMod 2

> **⚠ Early.** 2.0 rebuilt how assets are made. It works in the author's own game with the author's own mods;
> expect rough edges. Back up your saves.

Cram your own 3D shit into Apocalypter without the Unity editor. Build a car, a machine, a part or a whole place in
Blender, describe it in a short Lua script, and it shows up in game as a real, working thing: it spawns in the world,
saves and loads, turns up in Apocaspawner, and other mods can bolt their own stuff onto it.

What 2.0 changes:

- **Lua instead of JSON.** An asset is a `.glb` and a `.lua` next to it. The script builds the asset out of
  **parts** (`parts.mount`, `parts.lever`, `parts.fuel_tank`, ...) instead of hundreds of lines of copied PlayMaker
  FSMs. The test stand went from 197 KB of JSON to a 6 KB script. Scripts can also run code on every spawned copy.
- **Parts in C#.** The things a player handles (levers, switches, the ignition key, the gear shifter, wheel hubs, the
  fuel tank and its filler, readouts) are C# components, written clean from how they should work, not copied FSMs.
  What the game and other mods talk to (an FSM's name and variables: the fuel level, a part's condition, a mount's
  attach handshake, the save keys) stays exactly as the game has it.
- **Live code.** C# reloads while the game runs: `dotnet build`, and the change is in your game in a second, with
  every live object keeping its state. Lua scripts reload on save.
- **Places.** An asset can be put into the game's own camps and buildings, swap their objects, or stand in for the
  game's decorative props.
- **A console.** With Apoca-95, the backquote key opens an MS-DOS Prompt that runs Lua against the running game.

`.json` assets still load exactly as before. [README.old.md](README.old.md) is the 0.x manual: the export workflow,
what happens to your edits, chassis, tuning keys, FSMs in JSON and POIs are all still true, because a Lua script
builds the same document a `.json` did.

## The examples

- **1981 Mitsubishi Sigma** (`sigma_chassis`): built from scratch, out of parts: its own body, interior, dashboard,
  ignition key, pedals, handbrake, gear lever, lights and slots for the game's parts, the KM132 five-speed ratios, a
  welded diff. Turns up wherever the game spawns Outriders. Its script, `sigma_assets/apocasigma.lua`, is also the kit
  the Blender add-on builds cars from: read it to see what a car is made of.
- **Astron 2000 4G52** (`astron_4g52`): the game's 2.3 L four, retuned. Sold where the 2.3 L is.
- **Test stand** (`sigma_chassis_test`): built from scratch with no donor, out of parts: engine and radiator mounts,
  fuel tank with a siphon, ignition key, throttle, clutch, belt clutch and steering levers, gear shifter, handbrake, two
  flywheels on the gearbox belted to a sprung rear axle, three wheel hubs, dashboard, radio and headlight slots,
  headlights, a cab light, a tow hitch and toolkits. Read `sigma_assets/sigma_test.lua` first: it is the manual's best
  example. The belt and couplings are Engine Sim's; without it the stand still spawns and runs.

Other mods' assets are good reading too: Engine Sim's generator set (`generator.lua`: places, a C# readout, a
belt off the crank), Apoc-Air's gyrocopter (`apocagyro.lua`, with its own Lua library `apocair/flight.lua`), Burner's
Utilities' tow hitches (`trailer_hitches.lua` and `burners/hitch.lua`).

## Install

Unzip into `Apocalypter/BepInEx/plugins`. You get `BepInEx/plugins/SigmaMod/` with the DLL, MoonSharp (the Lua
interpreter), `lua/` (the parts library), `live/` (the C# parts), the examples in `sigma_assets/` and the modding tools
in `tools/`.

## The Blender add-on

`sigmamod_blender-x.y.z.zip`, from the release page: Blender 4.2 or later, *Edit > Preferences > Get Extensions*,
the arrow at the top right, *Install from Disk*. The 3D view's sidebar (N) gets a **SigmaMod** tab with a guide that
walks through making an asset, one step at a time, each ticked off when it's done:

1. **Set up.** Finds the game in Steam's folders and installs the helper tools (coacd, UnityPy) into a Python of its
   own, once.
2. **Start an asset.** Import one of the game's objects from Export All (its meshes filled from the game's files, the
   colliders and hidden parts tidied into hidden collections), start from an empty, or copy a game object under a new
   name.
3. **Name it.** The id, the name players see, and your mod's folder in `BepInEx/plugins` (not SigmaMod's own: an
   update replaces that).
4. **Build it.** For a car of your own, a checklist of what a car needs to work (engine bay, wheels and suspension,
   fuel tank, dashboard and ignition, driver's seat and controls) and what's nice to have (exhaust, gear lever,
   headlights, seats and body panels, interior light, tow hitch, lock volumes, toolsets). Each is made of objects:
   an engine bay is an engine mount and a radiator mount, a sound spot and a bay mesh if you want one; headlights are
   as many slots as the car has; the ignition has a mesh for each state of the key. Yours are found by name as you
   work, or picked (the eyedropper), or found in a group you point a part at; a group picked for something there can
   be any number of takes everything in it that fits. What you haven't got can be the Sigma's, placed in proportion
   to your body for you to move into place. *Add to car* writes the part's lines into your script for what was
   picked: a line about something left out goes, or just its option. The checklist is the Sigma's own script
   (`apocasigma.lua`), read from the installed SigmaMod: each `-- ##` section is one part, `[required]` what a car
   needs, and its `-- - name [type]: what it is` lines the objects (`?` optional, `*` any number); a line marked
   `-- optional: a, b` can lose those options. The types (`spot`, `marker`, `mesh`, `clickable`, `collider`,
   `volume`, `solid`, `hull`) are explained in the script's own header, and the add-on shows that explanation on every
   object: what to make it from, whether it's seen, clicked or bumped into. A section the script already has without
   its `-- ##` block, written by hand, counts as in the car and is left alone.
   Seats, doors, hoods, trunk lids, bumpers, roof racks and radios take as many slots as the car has: a slot is an
   empty that takes any part of its kind, so a van has five doors and a hearse two lids.
   **Parts that go with the car** (doors, hood, trunk lid, seats, bumpers): select your mesh and press the mesh button
   on a slot. The part works as the game's do (it opens, latches and saves), lent its workings by one of the game's
   parts of that kind, which you never see: your mesh is the part, where you modelled it, with colliders made from it
   (the game part's click and fit boxes are kept). The import button fits one of the game's parts as it is instead.
   Each part is a top object of its own in the same `.glb` and a template of its own in the same script
   (`-- ### Part:` blocks): one file can hold any number of templates, each naming its root with `model`.
   **Preview** shows the car as the game will: the game's parts on your slots and mounts (*Randomize* picks others,
   as a player might fit them), the game's wheels on your hubs where the springs hold them with the car standing
   still, the ground under them with the ground clearance, and every door, hood and lid swung open as far as its own
   FSM swings it. None of it is exported. A door's, hood's or lid's slot must not be turned: the game turns it back
   straight each time the part shuts.
   **Tuning**: mass, driven wheels, steering lock, brake torque, each axle's spring travel, force and damping, the
   gears and the differential (open, locked or limited slip; a centre one for all-wheel drive), written into the
   script as a `-- ## Tuning` block before the car's template line. The preview's wheels move as the springs would.
   The final drive belongs to the engine fitted (each of the game's engines sets its own).
5. **Colliders.** `colliders.py`'s convex pieces from the selected mesh, made as Blender objects, so they are kept in
   the `.blend` and exported with everything else. Made again, they replace the old ones.
6. **Script.** A starting script for the kind of asset, opened in Blender's text editor; *Add a part* writes
   `parts.wheel`, `parts.mount`, `parts.slot` and the rest for the selected objects, their names filled in.
7. **Put it in the game.** Checks first: names in the script that no object has, game materials that lost their id,
   scaled hinges, objects in switched-off collections, huge textures, an unsaved script. Then the export, with
   the settings SigmaMod needs and the game's textures stripped, into the game. It can switch on Hot reload, and shows
   what the game's log says about the asset.

Above the guide sits Clunky, who looks at what you're doing every couple of seconds and tells you what's wrong, one
thing at a time, with the button that fixes it: game ids left on objects (your car is about to be a clone of the
Outrider), objects from two game objects at once, materials inherited from the game (your edits to them don't reach
it), colliders older than the mesh they were made from, objects outside the asset, a car lying sideways or forty
metres long, names in the script no object has, missing parts; and the scene's own traps: objects hidden in Blender
(the game shows them: hiding is for you, the export takes everything), the Sigma's objects in the model without
their section in the script (shown, doing nothing), pieces left from an interrupted import, one object picked for two
jobs, parts still wearing the game's mesh or colliders. *Shut up, Clunky* silences one hint; he sulks.

*Add a part* lists the parts library as `parts.lua`'s own comments describe it, and writes each the way SigmaMod's
shipped assets call it, the selected objects' names filled in. Long jobs run in the background (Esc cancels);
everything they print is in the *SigmaMod log* text.

## Making an asset

The Blender side is as before (README.old: *Making one*): export, butcher, export as glTF Binary, strip the textures,
or let the add-on do it. Nothing needs tagging in Blender: the script says it all. Then write `<name>.lua` next to
`<name>.glb`:

```lua
-- my_cart.lua: a two-seat cart, everything named as in my_cart.glb
local parts = require "sigma.parts"

local g = parts.chassis { mass = 400, drive = "rear", speed = true }
parts.colliders(g, "Body Collider", "Floor Collider")
parts.mount(g, "Engine Mount", "engine")
parts.engine_sound(g, { at = "Engine Mount" })
parts.fuel_tank(g, { tank = "Tank", filler = "Tank Cap", litres = 10, capacity = 20 })
parts.ignition(g, { switch = "Key Switch", fuel = "Tank", engine = "Engine Mount" })
parts.wheel(g, "Wheel FL", { at = "FL Hub Marker" })   -- and the other three

g.name = "Cart"
template "my_cart" (g)
```

The template's root is the `.glb`'s only root object, or the one named as the template, or `model = "<name>"`. What
the old custom properties said, the template says (and it wins over any still in the model):

| | |
|---|---|
| `name` | What the game calls it (`sigma_name`). |
| `type` | `"chassis"` (`parts.chassis` sets it), `"poi"` (any template with `poi`). |
| `spawn`, `spawn_like` | Spawn weight; game prefab ids whose spawn lists it joins (`sigma_spawn`, `sigma_spawn_like`). |
| `behaviour` | C# behaviours by name, on the root; `nodes = { X = { behaviour = ... } }` on an object. |
| `nodes = { X = { spawns = "<id>" } }` | In a POI: the empty `X` is a spot where that template spawns (`sigma_place`). |
| `starter = { ... }` | A starting car's kit (below). |

**A starting car's kit.** The starter camp gives each of the game's starting cars its own kit next to it (the Poloska
a 594cc engine and a motorcycle radiator, the Junker a 1.2L and a small radiator, the Crossbreed a 594cc and two
motorcycle wheels). A car template in that camp's list gets the kit of the car it spawns like (its donor, or its first
`spawn_like`), or its own:

```lua
g.starter = { "594cc I2 23HP 39Nm Gasoline", "radiator_motorcycle" }   -- game items by name or id, or templates
```

Each item goes on the camp's kit spots in turn and gets the game's starting fuel, condition and oil.

Load a save (or, with `Tools.HotReload`, just save the script) and it's there. Every name is a Blender object name.
Anything no part covers can be written straight into the table: `g.nodes["Name"] = { ... }` is an object,
`g.playmaker["Name"] = { ... }` an FSM on the root, any `"<Component>" = { field = value }` sets a component, all in
the same terms as the old `.json` (README.old: *Tuning*, *From scratch*).

Game materials lose the game's id when a material is appended or copied in Blender, and then their textures go into
the file (a car's worth is over 100 MB). `tools/strip_textures.py <file.glb> --ids-from <other.glb>` gives them back
the id of the same-named material in another file (an Export All file, or an earlier export) before stripping.

A new object can also be a copy of an object of a game prefab, with everything under it: `copy = "<prefab>/<path>"`
in its `nodes` entry, beside `parent` (and `at`, `align`, `position`, `rotation` as for an empty one). The seated
player model, armature and all: `g.nodes["Driver Model"] = { parent = "DriveTrigger", copy =
"Outrider/parts/PlayerModel_Sit", at = "sitPos" }`.

### The script

| | |
|---|---|
| `template "<sigma_id>" (table)` | The asset: the same document as the `.json`'s entry for that id. |
| `fsms { name = {...} }`, `states { }`, `actions { }` | The FSM libraries a `.json` kept at its top (`"use"` them in FSMs). |
| `behaviour "<sigma_id>" { spawn = f, update = f }` | Code on every copy (below). |
| `require "lib.name"` | A Lua file from `SigmaMod/lua/` or the asset's own folder (`lib/name.lua`). |
| `null` | The `.json`'s null: there, but empty (an FSM variable declared with no value). A Lua `nil` isn't there at all. |
| `clr.*`, `vec`, `log`, `print` | C# from Lua (below). |

A list is a table with keys 1..n, an object any other table; an empty `{}` is an object. Saving the script rebuilds
the asset, and so does saving a library it `require`s (only those: a library it doesn't use changing is no change to
it).

### Code on every copy

```lua
behaviour "sigma_chassis_test" {
  spawn = function(stand, state)            -- once per copy: spawned, bought, or loaded from a save
    log(stand.name .. " is running Lua")
    state.ticks = 0
  end,
  update = function(stand, state, dt)       -- every frame
    state.ticks = state.ticks + dt
  end,
}
```

`stand` is the copy's GameObject (anything Unity has on it works), `state` a table of that copy's own. An error stops
that copy's behaviour and is logged once.

### C# from Lua

| | |
|---|---|
| `clr.type("Name")` | A type's statics (`clr.type("UnityEngine.Time").timeScale`). Live code's newest version first. |
| `clr.new("Name", ...)` | A new object. |
| `clr.objects("Name")` | Every live object of that type, a list from 1. |
| `clr.find("path")` | A GameObject by name or path. |
| `clr.component(go, "Name")` | A component on it. |
| `vec(x, y, z)` | A Vector3. |

Objects passed to Lua are usable as they are: fields, properties and methods by name.

## Colliders from the mesh

A body that collides as it looks needs convex colliders, and a car body is anything but convex. `tools/colliders.sh`
makes them from the object's own mesh and writes them into the `.glb`:

```
python3 -m venv tools/.venv && tools/.venv/bin/pip install coacd numpy     # once
tools/colliders.sh my_car.glb "Body" [tolerance] [--into "Body Colliders"]
```

The mesh is cut into convex pieces where it is concave (approximate convex decomposition, CoACD) until no piece is
further than `tolerance` from it, relative to the object's size. On a car body the default 0.05 gives about seventy
pieces; 0.1 about thirty, coarse enough that pieces bridge the window and door openings; 0.03 about 130, nearly
exact. A mesh that mirrors itself across X = 0, with an edge loop down its middle, is cut one half at a time and the
pieces mirrored, so both sides match. A flat mesh (a plane, a floor) is given 2 cm of thickness first. Each piece is a child of the object (or of the `--into` object, an empty that holds them) marked as a convex
mesh collider, the way exports carry the game's colliders: SigmaMod builds them as colliders, and in Blender they
are objects to look at, move or delete. Run it again and the pieces it made are replaced; colliders you made
yourself stay. Pieces have at most 128 corners, under Unity's limit for a convex collider.

## The parts library

`require "sigma.parts"`. Each part takes the template table `g` (from `parts.chassis`) and the names of your objects.
The options are in the library's own comments (`SigmaMod/lua/sigma/parts.lua`); in short:

| Part | What it makes | Options |
|---|---|---|
| `chassis(o)` | The root: a vehicle the game knows, with the lookups parts use. Returns `g`. | `mass`, `static`, `drive` (`"front"`, `"rear"`, `"all"`, `"none"`, axle number), `controller` (NWH values), `add`, `speed`, `flip_over`, `dashboard`, `parked_brakes` (false: wheels not clamped while nobody drives it) |
| `colliders(g, ...)` | Solid colliders from the objects' meshes. | |
| `numbered(base, n)` | `"Body"`, `"Body.001"` ... `"Body.00n"`, for `colliders`. | |
| `lock_volumes(g, ...)` | Volumes that carry loose items with the vehicle. | |
| `mount(g, name, accepts, o)` | Where a game part fits (`"engine"`, `"radiator"`, ...). | `at` (a marker), `lookup`, `collider` |
| `slot(g, name, accepts, o)` | Where a part clips in (dashboard `"gauge"`, `"radio"`, `"headlight"`, `"seat"`). | `use`, `at`, `align`, `shape`, `layer`, `marker`, `box` or `sphere` (a trigger of that size, for an empty hinge with no mesh) |
| `ignition(g, o)` | A key: click on/off, hold to crank. | `switch`, `fuel`, `engine`, `key`, `off` / `on` / `start` (lamps), `dashboard` |
| `fuel_tank(g, o)` | A tank cans pour into, burning fuel, with a filler that siphons. | `tank`, `filler`, `litres`, `capacity`, `fuel`, `hose`, `hose_at`, `hose_position`, `siphon_hose`, `render`, `saved` |
| `engine_sound(g, o)` | The engine's sound where it sits. | `at` |
| `lever(g, o)` | A stepped lever writing a property. | `label`, `mount`, `handle`, `collider`, `target`, `property`, `more` / `less`, `step`, `min`, `angle`, `pivot` |
| `toggle_lever(g, o)` | A two-position lever (a handbrake). | `label`, `collider`, `target`, `property`, `angle`, `mesh`, `material`, `pivot_position` |
| `gear_lever(g, o)` | A gear shifter and its fitted lever's lean. | `collider`, `joint`, `mount`, `gate` |
| `wheel(g, name, o)` | A hub game wheels fit on. | `at`, `hub`, `rim`, `rim_rotation`, `radius`, `width`, `mass`, `grip`, `spring`, `stiffness`, `damper`, `extra` |
| `free_wheel(g, name, o)` | A wheel of the vehicle's own. | `mesh`, `spin`, `position`, `radius`, `width`, `mass`, `spring`, `stiffness`, `damper`, `rolling`, `extra` |
| `flywheel(g, name, o)` | A flywheel or pulley (Engine Sim's drag and rim friction). | `mesh`, `spin`, `position`, `radius`, `width`, `mass` |
| `coupling(g, name, o)` | Two wheels turned together (Engine Sim). | `a`, `b`, `ratio`, `capacity`, `engagement` |
| `belt(g, name, o)` | A belt drawn round two pulleys (Engine Sim). | `a`, `b`, `radius_a`, `radius_b`, `width`, `thickness`, `sag`, `pitch`, `coupling` or `drive` |
| `light(g, o)` | A lamp and its switch. | `lamp`, `at`, `position`, `collider`, `light`, `label` |
| `headlights(g, o)` | The headlight switch. | `collider`, `on`, `off` |
| `tow_hitch(g, o)` | A hitch trailers hook onto. | `hitch`, `collider`, `reach` |
| `toolset(g, name, o)` | A toolkit kept on board. | `at`, `mesh`, `material`, `id`, `label`, `sphere` or `box` |
| `driver_seat(g, o)` | Get in and drive. | `enter`, `exit`, `input`, `sit`, `sit_position`, `exit_position` |
| `car_input(g, o)` | The driver's controls, the game's own cars' INPUT FSMs: pedals, steering, shifting, H-shifter, mouse steering, cruise control, horn, with the game's driving settings. Live while someone sits in the seat (`driver_seat` with `input = "DriveTrigger"`). | `handbrake` (a `toggle_lever`'s collider), `headlights` (a `headlights` collider), `horn_at` |
| `pedal(g, name, o)` | A pedal that moves with its input, as the game's do. | `input` (`"Throttle"`, `"Brakes"`, `"Clutch"`), `travel` (`{x, y, z}` from rest to pressed) |
| `pose(g, bones)` | Bones posed by hand (a seated player model). | `{ bone, parent, position, rotation }` per bone |
| `repairable(g, name, o)` | A part the repair box works on, its condition saved. | `suffix`, `condition` |
| `saved(g, name, suffix, fsm, var)` | An FSM float kept with the save. | |
| `tooltip(g, name, o)` | A readout of components' properties on the HUD. | `source`, `name`, `line2`, `line3`, `use`, `sources`, `label` |
| `item(o)` | A game item made into another: its ID and name. The whole template. | `id`, `name` |
| `attachable(g, o)` | A loose item that attaches to vehicles as the game's metal plates do: Use against a vehicle fixes it there, the game's tools take it off. With several colliders, built as the game builds a car door, so it picks up whole. | `mass`, `id`, `colliders` (objects whose meshes become its colliders), `collider_groups` (objects already carrying colliders, from an export), `grip` (`center`, `radius`) |

A mod can ship its own library: a `.lua` in its `sigma_assets` folder that isn't an asset's own script is a library
its own assets can `require` (Apoc-Air's `apocair/flight.lua`). Another mod's libraries aren't found: only SigmaMod's
`lua/` and the asset's own mod folder are searched.

## The C# parts

The parts library puts these on your objects (`SigmaMod.Parts`, in `SigmaMod/live/SigmaParts.dll`). They can be used
directly too, as any component (`add = { "SigmaMod.Parts.Lever" }`, `["SigmaMod.Parts.Lever"] = { ... }`).

| Component | | Fields |
|---|---|---|
| `Lever` | Stepped with the mouse, writes `property` (a dotted path such as `input.Throttle`) on `target` every frame, turns `pivot`. | `label`, `target`, `property`, `position`, `min`, `step`, `angle`, `pivot`, `more`, `less` |
| `ToggleLever` | Pulled and released with a click, with sounds; `Pulled` works it from code or an FSM. | `label`, `target`, `property`, `on`, `pivot`, `angle`, `pulled`, `released`, `use` |
| `Switch` | On and off: objects shown, the game's events sent to the vehicle; `On` works it from code or an FSM. | `label`, `on`, `whenOn`, `whenOff`, `eventOn`, `eventOff`, `clip`, `state` |
| `KeySwitch` | The ignition key. Exposes `KeyOn` and `Cranking`; set `external` and it leaves the engine to you. | `key`, `off` / `on` / `start`, `dashboard`, `fuel`, `engineMount`, `toggle`, `stop`, `StarterClip` |
| `GearShifter` | Shifts the gearbox, leans the fitted lever. | `target`, `lever`, `gate` |
| `WheelHub` | Draws the fitted wheel on the rim, a bare hub without. | `rim`, `bareMesh`, `bareMaterial`, `radius`, `mass`, `grip` |
| `FuelTank` | Burns fuel from the tank's `LiquidAmount` at the engine's `FuelConsumption`. | |
| `Filler` | The tank's readout; F opens it to siphon into a can. | `tank`, `hose`, `hoseMesh`, `cap`, `filling`, `rate` |
| `Readout` | HUD lines from components' properties. | `label`, `sources`, `properties` |
| `SeatedModel` | Shows a seated player model while someone sits in `seat` and the camera isn't at their head: seen in third person, out of the way in first. | `model`, `seat`, `head` |

The handled ones share `Interactable`: what's looked at is found the way the game picks things (vehicle and item
bodies are seen through), the HUD's four lines are the game's, and clicks on a part are its own (other mods' hands
leave them alone).

## Live code

C# in a plugin's `live/` folder (`BepInEx/plugins/<Mod>/live/*.dll`) or in `BepInEx/live/` is loaded at start. With
`Dev.LiveCode` on, a DLL built again into that folder is loaded again while the game runs:

- every live component of the old build gets the new build's type, with its fields carried over;
- references other code holds to them (a component's field, an FSM's object variable) are pointed at the new ones;
- static fields carry over, the old build's Harmony patches come off and the new one's go on;
- `[SigmaMod.Live.OnUnload]` methods of the old build run before, `[SigmaMod.Live.OnLoad]` of the new one after;
- type names (in a script's `add`, `clr.type`) mean the newest build.

So: point the project's output at the folder (the parts project, `parts/SigmaParts.csproj`, is one to copy), `dotnet
build`, and watch it change in game. Rules for live code:

- Set up in `OnEnable`, not `Awake` (Awake runs once with default values before the old state is copied in).
- Undo what you register in `OnDisable` or `[OnUnload]`; the log warns if old components outlive a reload.
- A new default value doesn't reach live objects (their old values carry over); changed code does.
- Other plugins mustn't name live types: they reach them through their own interfaces, by name, or by reflection.
- Mono can't unload an old build: each reload stays in memory. It's a dev tool; leave `Dev.LiveCode` off to play.

## The console

With Apoca-95, the backquote key opens an MS-DOS Prompt: warnings and errors as they're logged (`log all` for
everything), and a prompt that runs Lua. An expression prints its value:

```
C:\APOCALYPTER>g = clr.objects("DcGenerator")[1]
C:\APOCALYPTER>g.LoadText
39.2 A  941 W
C:\APOCALYPTER>for _, c in ipairs(clr.objects("WheelCoupling")) do print(c.name, c.engagement) end
```

`help`, `cls`, `reset` (a fresh Lua state), `log all` / `warn` / `off`, `exit`; Up/Down for history. While it's open
the game's keyboard and mouse read as nothing. Without Apoca-95, `SigmaMod.LuaConsole.Run(code)` and its `Output`
event are the same console for any other UI.

## Places

A template can put itself into the world's places:

```lua
g.places = {
  Starter_Area = { at = { 0.5, 4.5, 0 }, turn = 0,                 -- Blender's x, y, z in the place's export
    swap = { ["lights_1/lights/tin_can_light_hanging"] = "Camp_13/lights_1/lights/lamp_1" } },
}
g.replaces = { "9163650183033119127" }   -- the game's decorative generator mesh: stood in for, wherever it is
```

- `places`: keyed by the game's place prefab (`Camp_13`, `Building_4`: the names `Tools.ExportPlaces` gives) or a
  place built into the map (`Starter_Area`). A spot spawns one copy once the game is playing and you're within 300 m,
  unless a saved copy is already there. It settles onto what's under it.
- `swap`: an object in the place switched off and a copy of another place's object put where it was.
- `replaces`: the game's own props by their mesh's Easy Save id: switched off (with their sound and collider)
  wherever they are, and a copy spawned in their place.
- In a POI: `nodes = { ["Gyro spawn"] = { spawns = "gyro_mad_max" } }` makes that empty a spot.

### The game's working parts in a POI

Copy a camp or building out of `Tools.ExportPlaces` into your POI and it works as the game's does. What had an FSM in
the game (the loot, enemy, critter and car spawners, the map icon, the codex trigger, the lights) is the game's own
object in your POI, put where you put its copy; its spawn points (the empty that followed it in the game) go with it
whatever order Blender sorts them into. What has spawned is kept with the save as for the game's camps.

The game's places run every spawner they have. To have only one of some, by chance:

```lua
one_of = { "EnemySpawn_Wild_V1.007", "CritterSpawn" }          -- Blender names; or { A = 3, B = 1 } weighted
```

Each POI picks by where it is in the world, so the same one always picks the same.

New places of your own (`poi = { in = ..., share = ... }`, footprints) are as before (README.old:
*Places*).

## Hot reload

`Tools.HotReload`: saving an asset's `.glb`, `.lua` (or a library it requires) or `.json` rebuilds that asset and
swaps every spawned copy in place, fitted parts moved over. Changing a POI re-places it, which regenerates the
terrain: the game is frozen until the ground is back, so nothing standing on it falls through. A file that fails to
load is reported once, then tried again when it changes.

## Fallen through the ground

Anything that falls through the map keeps falling, and after some minutes it is far enough down to lock the game.
Every two seconds each body is checked against the ground under it: one more than 100 m below and still falling is put
back on the ground right above, at rest, and the log says what it was (`fall watch:`). Something resting underground,
in a cave, isn't falling and stays.

## Settings

`BepInEx/config/com.burner.apocalypter.sigmamod.cfg`, or the Apocasetter Mods menu.

| Setting | Default | |
|---|---|---|
| `Tools.ExportAll` | off | A button: every game object exported to `BepInEx/SigmaMod-export/`. Locks the game up for minutes. |
| `Tools.ExportPlaces` | off | A button: every place the terrain generator puts down, to `BepInEx/SigmaMod-export-places/`. |
| `Tools.ExportMaterials` | off | A button: every game material and terrain layer as a swatch, to `BepInEx/SigmaMod-export-materials/`. |
| `Tools.HotReload` | off | Rebuild assets when their files change, and swap spawned copies. For modding. |
| `Dev.LiveCode` | off | Reload live DLLs when they're rebuilt. For modding. |
| `Debug.VerboseLog` | off | Log spam, plus FSM, vehicle and terrain graph dumps in `BepInEx/`; bodies 50 m under the ground reported; a frame trace (`SigmaMod-frames.txt`) for ten minutes after a hot reload, to place a freeze. |
| `Debug.StarterCar` | empty | The starting car of a new game: a template's id (`sigma_chassis`) or a game car's name (`Junker`). Empty is the game's random pick. A name that isn't on the starter list is logged with the ones that are. |

## For mod authors

```csharp
[BepInDependency("com.burner.apocalypter.sigmamod")]
```

| | |
|---|---|
| `CustomPrefabs.RegisterBehaviour(name, (obj, props, info) => ...)` | Code for objects given `behaviour = name` in the script (`sigma_behaviour` in Blender), on every copy. |
| `CustomPrefabs.Instantiated` | Fires for every new copy. |
| `CustomPrefabs.All`, `Find(id)`, `TryGet(obj, out info)` | Loaded templates, and which one an object belongs to. |
| `CustomPrefabs.Spawn(info, position, rotation)` | Spawn a saved copy. |
| `LuaConsole.Run(code)`, `LuaConsole.Output` | The console's Lua. |
| `Lua.LibraryDir` | Where the shared Lua libraries are. |
| `SigmaMod.Live.OnLoad`, `OnUnload` | Hooks for live code. |

Parts belong in C# when they're mechanics (live code, `SigmaMod.Parts` as the model) and in Lua when they're
assembly (a library function returning or filling a template table).

## When it fucks up

The old table (README.old) still holds. New ones:

| Log says | Why |
|---|---|
| `<name>.lua: ...:line: ...` | A Lua error in the script or a library it requires. The previous version stays. |
| `template "..." takes a table` | `template "id" (g)`: the table is missing. |
| `nodes.X: "at": no object 'Y'` | Y isn't in the Blender file (names are case-sensitive). |
| `live code: X: not loaded` / `types failed to load` | The DLL was built against other versions of the game's or other plugins' DLLs. |
| `components of an old generation still alive` | Live code that registered something and didn't undo it in `OnDisable` / `[OnUnload]`. |
| `places: no place 'X'` | Not a place prefab in this world's terrain graph, nor a scene object. |
| A lever or switch ignores clicks | Something solid that isn't a vehicle or item body is between you and it. |
| `module '...' not found` | A `require` of another mod's library: only SigmaMod's `lua/` and the asset's own mod folder are searched. |
| Grabbing an item takes one of its colliders, not the item | Its colliders are other objects, and none is on the item itself: `parts.attachable` with `colliders` or `collider_groups` builds it so the item picks up whole. |
| An attachable gives no Use prompt against a vehicle (fine with Apocapocket installed) | Made with SigmaMod before 2.3.0: its `Attach` FSM switched itself off after spawning. Apocapocket happened to switch it back on. Fixed in `parts.attachable`. |

## Build

```
dotnet build -c Release                  # SigmaMod.dll, the Lua library and MoonSharp into BepInEx/plugins/SigmaMod
dotnet build -c Release parts            # SigmaParts.dll into BepInEx/plugins/SigmaMod/live
```

Against the game's own assemblies; set `GameDir` if the repo isn't in the game folder. `python3 blender/build.py` makes
the Blender add-on's zip in `dist/`, the tools bundled. `dotnet build -c Release
tools/GltfTest` builds the offline glTF tool.

## Credits and license

[WTFPL](LICENSE). Do whatever you want with it.

Lua is [MoonSharp](https://www.moonsharp.org/) 2.0, (c) 2014-2016 Marco Mastropaolo, under the BSD 3-clause license: see
`MoonSharp.LICENSE.txt` next to its DLL.
