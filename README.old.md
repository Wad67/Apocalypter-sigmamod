# SigmaMod

> **⚠ Very fucking early.** This is a 0.2 that has barely been driven in game. Expect things to break,
> saves to get weird, templates to load wrong and the log to scream at you. Back up your saves and
> expect to lose some hair.

Cram your own 3D shit into Apocalypter without installing the bloated fucking Unity editor. Build a
car, a part or some random crap in Blender and it shows up in game as a real, working thing: it
spawns in the world, saves and loads, turns up in Apocaspawner, and other mods can bolt their own
bullshit onto it.

Ships with a 1981 Mitsubishi Sigma and its Astron 2000 engine, because that's what this whole mess
was built for.

## The example shit

- **1981 Mitsubishi Sigma** (`sigma_chassis`): right-hand drive, rear-wheel drive, 1115 kg, the real
  KM132 five-speed ratios, real track and wheelbase. The diff is welded, because why the fuck not. It
  turns up wherever the game spawns Outriders.
- **Astron 2000 4G52 2.0L I4** (`astron_4g52`): the 64 kW tune with its 3.91 final drive and 650 rpm
  idle. Uses the vanilla 2.3L's model for now. Sold where the 2.3L is.
- **Engine Sim users:** `SigmaMod/enginesim_assets/astron_4g52.engines.cfg` makes the Astron sound like an
  Astron. Engine Sim 1.4 and later reads it from there by itself; nothing to paste.
- **Test rig** (`sigma_chassis_test`, `sigma_assets/sigma_test`): a from-scratch rig, every bit of it built
  in its `.json` with no donor. Engine and radiator mounts, fuel tank you can siphon, ignition key, hand
  throttle, clutch, gear and steering levers, handbrake, two flywheels on the gearbox, a belt clutch
  driving a sprung rear axle, wheel, dashboard, radio and headlight hinges, light switch, interior light,
  tow hitch, load lock volumes and toolsets. It's an ugly frame, not a car; it's there to crib from.
  Spawn it with Apocaspawner. The belt, couplings and flywheel drag are Engine Sim's (1.3.0 or later):
  without it the rig still spawns and the engine runs, but nothing drives the wheels, and the log
  names the bits it left out.

Pull them apart in Blender to see how a template is put together.

## What the fuck is a template?

**Your own edited copy of something in the game.**

Export a game object (say the Outrider), drag it into Blender and butcher it: new body, new
suspension mesh, wheels moved, crap bolted on. Give it its own name and dump it in a `sigma_assets`
folder. When the game loads, SigmaMod copies the Outrider, rams all your changes onto the copy, and
registers it as a **brand new object** with its own name and save slot.

The original is never touched. Swap the suspension mesh on your car and only your car gets it; every
vanilla Outrider keeps its own. Your car is a separate car gutted out of an Outrider. The object it
was copied from is the **donor**.

Why a donor? Because all the game logic lives in it: fuel, part mounts, damage, doors, pedals,
input, saving. It's a mountain of PlayMaker FSMs, and the copy keeps all of it working without anyone
rebuilding a single fucking state machine. Rather build every last bit yourself? See
[From scratch](#from-scratch).

## Install

Unzip into `Apocalypter/BepInEx/plugins`. You get `BepInEx/plugins/SigmaMod/` with the DLL, the
example Sigma and Astron in `sigma_assets/`, and the modding tools in `tools/`. Players can ignore
the tools.

## Where templates go

SigmaMod rummages through every mod folder in `BepInEx/plugins/`, its own included:

- A `sigma_assets` folder in a mod folder gets loaded. `BepInEx/plugins/MyMod/sigma_assets/my_truck.glb`. Done.
- Or drop a `sigma.json` in the mod folder to point at your shit:
  ```json
  { "name": "My Mod", "assets": ["models", "parts/engines"] }
  ```
  Paths are relative to the mod folder, one or a list. `name` is what Apocaspawner calls your mod
  (default: the folder name). With a `sigma.json`, only what it lists gets loaded.

Every `.glb` in there (subfolders too) loads, with its `.json` next to it. Shipping templates needs
zero code.

`sigma_id`s are global across every mod. Prefix them (`mymod_truck`, not `truck`), or the second
one gets binned and the log bitches about it.

## Settings

`BepInEx/config/com.burner.apocalypter.sigmamod.cfg`, or the Apocasetter Mods menu.

| Setting | Default | |
|---|---|---|
| `Tools.ExportAll` | off | A button. Flip it on, every game object gets exported, it flips itself off. Takes minutes and the game locks up solid until it's done. It hasn't crashed. Leave it alone. |
| `Tools.ExportPlaces` | off | Another button, separate from that one. In a loaded game: every place the terrain generator puts down (camps, wrecks, caves, buildings, and the rocks and dry shrubs while it's at it) goes to `BepInEx/SigmaMod-export-places/` as `.glb` + `.fsm.json`. Each root says which outputs place it (`poi_in`), and `outputs.txt` lists them all. Run `tools/fill.sh` on them for the real meshes. |
| `Tools.ExportMaterials` | off | Button three. In a loaded game: every game material and every terrain layer as a flat swatch in `BepInEx/SigmaMod-export-materials/materials.glb`, named after it, `materials.txt` listing the lot. Append the materials in Blender, slap them on your own meshes, and the game uses its own material back. |
| `Tools.HotReload` | off | For modding. Save the `.glb` or `.json` and within a second the template is rebuilt and every spawned copy swapped in place, fitted parts moved over. No save/load dance through Easy Save's molasses. A broken save or a `.json` typo keeps the old version and says why in the log. The car you're sitting in swaps when you get out. Code changes still need a restart. |
| `Debug.VerboseLog` | off | Log spam, plus FSM, vehicle and terrain graph dumps in `BepInEx/`. |

## Making one

1. **Export.** Load a save, hit *Export All*, wait for `export: done` in the log.
   `BepInEx/SigmaMod-export/` now has a `.glb` per game object, plus `<name>-live.glb` for the car
   nearest you with whatever's bolted to it.
2. **Fill the meshes.** Most game meshes can't be read at runtime, so they come out as boxes. Rip the
   real ones out of the game files (Python 3), from `BepInEx/plugins/SigmaMod`:
   ```
   python3 -m venv tools/.venv && tools/.venv/bin/pip install UnityPy   # once
   tools/fill.sh ../../SigmaMod-export/*.glb
   ```
   Windows, per file: `tools\.venv\Scripts\python tools\fill_meshes.py ..\..\..\Apocalypter_Data\data.unity3d <file.glb>`
3. **Blender.** Import the plain prefab (`Outrider.glb`), not a `-live` one; that's full of junk like
   open doors. Run `tools/blender_organize.py` to hide the hundreds of fucking colliders.
4. **Butcher it.** Change meshes, move empties (wheel hinges, seats, mounts), add stuff. **Leave the
   custom properties on game objects alone**; they're how SigmaMod knows what's what.
5. **Name it.** Root object gets custom property `sigma_id` (e.g. `my_truck`), optionally
   `sigma_name` for players. A car that drives on its own tuning also gets `sigma_type = chassis`.
6. **Export.** File > Export > glTF 2.0: *glTF Binary*, *Include > Custom Properties* **on** (forget
   this and nothing loads), *+Y Up* on, *Visible Objects* off. Save into a `sigma_assets` folder.
   Then `python3 tools/strip_textures.py <file.glb>`: Blender stuffs every game texture into the file,
   and SigmaMod uses the game's own materials anyway, so that's a hundred-odd MB of dead weight. Your
   own materials keep theirs. All game materials? *Material > Images: None* in the export does the same.
7. **Tune** (optional): a `.json` with the same name next to it. See [Tuning](#tuning).
8. **Load a save** (or turn on *HotReload* and just save the file). Log says `custom prefab: 'my_truck' ... from donor 'Outrider'`. Spawn it from
   Apocaspawner or go hunt for it. Changed the file? Reload the save.

## What happens to your edits

- Game objects take your position, rotation, scale, mesh and materials.
- New objects you made get added.
- Colliders: edited ones update, deleted ones switch off, new ones (`sigma = collider` plus `shape`)
  get added.
- Game objects you deleted stop drawing but keep their logic. Ripping the logic out breaks shit.
- Fitted parts and cargo from a `-live` export get skipped; they're separate objects.
- One template, one donor. Mix two exports and it's refused; the log names the culprits.
- Stuff the FSMs place with hard-coded numbers (pedal travel, hinge positions per suspension type,
  hitch height) moves with your objects, so pedals and lift kits still work. Hard-coded rotations
  (handbrake, gear lever) don't; the log names them.
- Game materials are used as-is. Delete `es3global` off one in Blender to use your own colours and
  textures.
- Want a game material on your own mesh? `Tools.ExportMaterials`, append from `materials.glb`, assign.
  Terrain (sand, cracked sand, cliff, soil...) comes as `terrain <layer>` materials: no Easy Save id, so
  they carry `terrainLayer` instead and SigmaMod rebuilds them from the layer's textures. Their swatch is
  one tile big, so UV your mesh so one UV square is that many metres and it matches the ground.
- Faces are one-sided in game, whatever Blender's *Backface Culling* claims. Give thin panels some
  thickness (Solidify, applied).
- Driver sits at `DriveTrigger/sitPos`. The seated player (`parts/PlayerModel_Sit`) is skinned: move
  `PlayerModel_Sit` itself and the bones follow.

**Root properties:**

| Property | |
|---|---|
| `sigma_id` | Required. No id, not loaded, so a file can hold reference junk. Change it later and saved copies are orphaned. |
| `sigma_name` | Display name. Defaults to the id. |
| `sigma_type` | `chassis`: see [Chassis](#chassis). |
| `sigma_spawn` | `false` keeps it out of world spawning. A number is how likely it is next to each other thing in the same spawn list: `0.25` turns up a quarter as often, `2` twice. |
| `sigma_spawn_like` | Game prefab ids (`es3prefab` from an export, comma-separated). It joins the spawn lists holding one of them *instead of* its donor's: a from-scratch part with a bumper's id is found where bumpers are, and a toolbox variant with a repair box's id isn't in your starting kit. |

`sigma_id` on any other object names it inside the template, for the `.json` and for code.

**Variants:** a root with no children and no mesh is the donor untouched, under its own name and
tuning. That's how the Astron is done. Make one without Blender (from `BepInEx/plugins/SigmaMod`,
needs the .NET 10 runtime):
```
dotnet tools/GltfTest/GltfTest.dll variant <mod>/sigma_assets/<id>.glb <es3prefab> <id> "<name>"
```
`<es3prefab>` is the `es3prefab` property on an exported root.

## Chassis

`sigma_type = chassis` bins the donor's driving setup (mass, axles, steering, brakes, diffs,
gearbox) and rebuilds it from your wheels, so it doesn't drive like a reskinned Outrider. Fuel,
mounts, damage and input still come from the donor.

Set at least `Rigidbody.mass` and `"drive"` (`"rear"`, `"front"`, `"all"`) in the `.json`. Skip
`drive` and you're stuck with all-wheel drive. Axles count front to back by wheel position; with three
or more, `"rear"` is just the second one, so say `"drive": 1` and mean it. NWH only puts differentials
on the first two.

`BepInEx/SigmaMod-chassis-<sigma_id>.txt` dumps every driving value: donor, default, final. The top
section is the shit you haven't set yet.

Wheel hinges: a fitted wheel sits on the hinge's origin, and the hinge is the wheel centre at full
bump. Keep hinge scale at 1 or every wheel gets squashed.

## Spawning and Apocaspawner

Your thing joins every spawn list its donor is in (starting cars, camp spawners, merchants), same
odds. The log lists which.

With Apocaspawner installed, every template sits in its catalog under its `sigma_name`, sorted as
vehicle, trailer or part type, at the donor's price, tagged with your mod's name. Spawn it like
anything else; it saves and loads like vanilla. The catalog refreshes when templates load.

## Tuning

`<name>.json` next to `<name>.glb`, keyed by `sigma_id`:

```json
{
  "my_truck": {
    "Rigidbody": { "mass": 1300 },
    "VehicleController": { "powertrain.transmission.gears": [-3.5, 0, 3.2, 2.1, 1.4, 1.0, 0.8] },
    "nodes": { "hitch": { "Transform": { "localPosition": [0, -0.15, -2.2] } } },
    "fsm": { "CheckTag": { "finalGearRatio": 3.91 } },
    "drive": "rear"
  }
}
```

- `"<Component>": { "<field.path>": value }`: any component on the object. Numbers index lists.
- `nodes`: objects by Blender object name (or `sigma_id`).
- `fsm`: PlayMaker values, for every FSM of that name on the object or below:
  - `"<variable>"`, e.g. `"finalGearRatio": 3.91`
  - `"set:<property.path>"`, every SetProperty writing it, e.g. `"set:powertrain.engine.idleRPM": 650`
  - `"<State>/<Action>[n].<field>"`, e.g. `"condition100/SetFloatValue.floatValue": 54.3`

Find the names with *VerboseLog* on, in `BepInEx/SigmaMod-fsm-dump.txt`. Engine shit lives in the
engine part's `CheckTag` (final drive, idle, rev limit) and `Condition` (power) FSMs. Gear ratios are
on the chassis. Diffs: `powertrain.differentials.<n>.DifferentialType` (`Open`, `Locked`,
`LimitedSlip`) and `stiffness`. The Sigma and Astron `.json` files are working examples.

## For mod authors

```csharp
[BepInDependency("com.burner.apocalypter.sigmamod")]
```

Tag any object in Blender `sigma_behaviour = tow_hook` (comma-separate several), then:

```csharp
CustomPrefabs.RegisterBehaviour("tow_hook", (obj, props, info) => obj.AddComponent<TowHook>());
```

Register in `Awake`. It runs on every copy, however it showed up: world spawn, Apocaspawner, a
loaded save, `Spawn`. Components you add aren't saved, so saves never depend on your mod.

| `SigmaMod.CustomPrefabs` | |
|---|---|
| `Instantiated` | Fires for every new copy. |
| `All`, `Find(id)` | Loaded templates (`Id`, `Name`, `PrefabId`, `Prefab`, `Donor`, `IsVehicle`). Empty in the main menu. |
| `TryGet(obj, out info)` | Which template an object belongs to. |
| `TryGetExtras(obj, out props)` | An object's custom properties from Blender. |
| `FindNode(copy, id)`, `IdOf(obj)` | Objects by `sigma_id`, and back. |
| `Spawn(info, position, rotation)` | Spawn a saved copy. |

## From scratch

No donor, no game ids anywhere in the file: SigmaMod builds exactly what's in Blender and nothing
else. Every Blender object (empty or mesh) becomes an object, meshes get their mesh. Then the `.json`
bolts the guts on, addressing objects by **Blender name**. `body -> doohickey` in Blender, then:

```json
{
  "my_truck": {
    "layer": 8, "tag": "Vehicle",
    "add": ["Rigidbody", "VehicleController"],
    "Rigidbody": { "mass": 1300 },
    "nodes": {
      "doohickey": {
        "layer": 13, "tag": "Hinge",
        "add": ["BoxCollider"],
        "BoxCollider": { "isTrigger": true, "size": [0.6, 0.5, 0.8] },
        "playmaker": {
          "getParent": { "variables": { "gameObject": { "vehicle": "root" } }, "states": { "idle": {} } }
        }
      }
    }
  }
}
```

Logic-only objects (a sound source, a trigger) don't have to be in Blender: a `nodes` entry with
`"parent": "<object or root>"` makes an empty one. Put it somewhere with `"at": "<object>"` (that
object's position) plus an optional `"position"` offset and `"rotation"`. Numbers in the `.json` are
Unity space: **X is Blender's -X**, Y is Blender's Z, Z is Blender's -Y. Use `"at"` and skip the
maths. `"align": "<object>"` turns it to match that object (rotation only, not its fucked-up scale).
`"parent"` on an object that *is* in Blender moves it under another, staying put in the world.

Per object, in this order: `layer`, `tag`, `add` (component types), `collider`, `playmaker` (new
FSMs), `<Component>` values, `fsm` tweaks.

**Colliders** come from the object's own mesh: `"collider": "convex"` (or `"box"`, `"sphere"`,
`"mesh"`, or `{ "shape": "box", "trigger": true, "render": true }`). The mesh stops drawing unless
`render` is on. A moving car can only collide with **convex** shapes; physics wraps everything else
in its convex hull, filling dents, tunnels and wheel arches. So build a concave thing out of several
convex lumps: one object, many loose parts. Each loose part becomes its own collider, and the log
says when a part's hull is noticeably fatter than the part. That's why the game's own cars are a
pile of little boxes. You don't need hundreds, just one lump per convex bit. `"mesh"` keeps it
concave but only works on things that don't move.

Hinges hate scale. A fitted part sits under the hinge and inherits whatever stretched-ass scale your
marker mesh has. So make the hinge an unscaled empty and borrow the marker for its trigger:
`"Dashboard": { "parent": "root", "at": "Dash Marker", "align": "Dash Marker", "collider": { "shape":
"box", "trigger": true, "from": "Dash Marker" } }`. `"from"` builds the box (or sphere) around the
other object's mesh and hides that mesh.

**FSMs** are written out in full:

```json
"playmaker": { "Speed": {
  "variables": { "float": { "speed": 0 } },
  "states": { "getSpeed": {
    "actions": [ { "GetSpeed": { "gameObject": "owner", "storeResult": "{speed}", "everyFrame": true } } ],
    "transitions": {} } } } }
```

- `variables` by type: `float`, `int`, `bool`, `string`, `vector2`, `vector3`, `quaternion`,
  `color`, `rect`, `gameObject`, `object`, `material`, `texture`.
- `states` in order; the first is the start unless `"start"` says otherwise. `"global": {EVENT: state}`
  for global transitions, `"globalEvents": [...]` for events other FSMs send in.
- Same FSM on several objects? Put it once in a top-level `"fsms": { "name": {...} }` in the file and
  write `{ "use": "name", "variables": { "string": { "AcceptID": "radiator" } } }`; what you write is
  merged over it.
- Actions are `{ "<ActionType>": { fields } }`, PlayMaker's or the game's own. Skipped fields keep the
  action's defaults. `"_enabled": false` switches one off.
- FSMs are built after every object has its components, so an action can point at a component on any object,
  whatever order the nodes are in.
- `"{name}"` binds a variable (local, else global like `Hand`), `"{}"` is none, `"owner"` is the FSM's
  own object. Anything else is a constant.
- Nested stuff (`FsmProperty`, event targets) takes an object of its fields:
  `"targetProperty": { "TargetObject": "{vc}", "TargetTypeName": "NWH.VehiclePhysics2.VehicleController", "PropertyName": "powertrain.engine.OutputRPM", "FloatParameter": "{rpm}" }`
- Some values need a type nothing else gives them. An `object` variable that `GetComponent` stores
  into: `{ "type": "UnityEngine.MeshFilter", "value": null }`. An enum handed to `SetProperty`:
  `"EnumParameter": { "type": "UnityEngine.ConfigurableJointMotion", "value": "Locked" }`.

**References**, anywhere a value is an object:

| Value | |
|---|---|
| `"doohickey"` | Your object by Blender name (or `sigma_id`). `"root"` is the root, `"/body/doohickey"` a path. |
| `"doohickey:BoxCollider"` | A component on it. |
| `"asset:AudioClip/wrench1"` | A loaded game asset by type and name. `"asset:<id>"` takes an `es3global` id from an export. |
| `"file:sounds/idle.wav"` | A file next to your `.glb`: `.wav` (PCM or float) or `.png`/`.jpg`. |

**Shorthand**, because the long form is a fucking novel. It all expands to the long form when the file
loads, the long form still works, and expanding the long form changes nothing. Want proof? *VerboseLog* writes
the expansion to `BepInEx/SigmaMod-expanded/`, or offline:
`dotnet tools/GltfTest/GltfTest.dll expand <file.json>` (warns and exits 1 if anything's off). It knows the game's
types and every installed mod's, so `SetProperties` on another mod's component checks out too.

- Libraries at the top of the file: `"fsms"`, `"states"` and `"actions"` (a list), each `{ "<name>": ... }`.
- `"use": "<name>"` pulls one in wherever that thing goes: an FSM, a state, or an entry in an `"actions"` list
  (spliced in). `"with": { "roll": "Locked" }` fills `$roll` in it; a library entry can set defaults with
  `"params"`. `"$roll"` on its own keeps the value's type, inside text it's just text. Anything written next
  to `"use"` on an FSM or state is merged over it.
- `SetProperties` / `GetProperties`: one action, many properties, and it works out the parameter and enum types:
  `{ "SetProperties": { "on": "{ConfigurableJoint}", "xMotion": "Locked", "anchor": "{pos}" } }`.
  `"on"` is a typed object variable, a gameObject, or `"thing:Component"`; add `"type"` for a global variable.

`BurnersUtilities/sigma_assets/trailer_hitches.json` is a working example: 338 lines that expand to a
2400-line monster.

**Cribbing:** *Export All* writes `<name>.fsm.json` next to every `.glb`: every FSM of that game object
in this exact format. Read how vanilla does shit, copy what you want, rename the references.

**What the game wants** on a car (FSM names other systems look up, tags, layers, hinge rules, fuel
tank): [docs/vehicle-contract.md](docs/vehicle-contract.md). Short version: engines find the car
through the hinge's `getParent.vehicle`, then the root's `getFuel`, `getAudio`, `GetRSound`,
`RpmGear` and `Speed`.

Added components and FSMs get stable save ids. Templates with no donor don't join world spawning;
use Apocaspawner or `Spawn`.

## Places (POIs)

`sigma_type = poi` and a `poi` block: the terrain generator puts it in the world, in some of the spots it
already puts camps, wrecks, caves and buildings. Not extra spots: the map isn't any more cluttered, the
camp that would have been there just isn't. Same spots every load, every save. A spot that becomes yours
already has the flat pad and the road the game gives that kind of spot.

```json
{
  "my_outpost": {
    "poi": { "in": "15169460551123206150", "share": 0.15 }
  },
  "my_airfield": {
    "poi": { "in": "14795495755795660805", "share": 0.3,
             "footprint": { "length": 1200, "width": 45, "shoulder": 40,
                            "max_grade": 0.015, "max_cut": 4, "headings": 24 } }
  }
}
```

- `in`: which of the game's outputs to steal spots from, by graph node id (a **string**: the numbers
  are too big for JSON). Several: a list.
- `share`: how many of that output's spots, 0 to 1. Default: as one more kind of thing in it.
- `footprint`: for shit that needs proper ground, like a runway. A spot is only taken if the
  footprint (length along the model's +Z, or X with `"along": "x"`, width across) fits the ground already there: a straight
  grade no steeper than `max_grade`, no point more than `max_cut` metres off it, all of it inside one
  tile. The ground is cut to the model's origin, or to `"ground"`: a height from the origin, or an object whose top is the
ground (a slab the place stands on); `"raise"` sets the model that many metres above it. The model is tilted to the grade and set on it. Origin not in the middle of it (the gate's at one end)? `"center": [x, z]` says where its middle is, in the
  model's own space. Easier: `"bounds": "<object>"`, a plane or box in the model marking the site out; its size,
  long axis and middle become the footprint (anything you give yourself wins), and it isn't drawn. Tried at
  `headings` headings, the one needing the least digging wins and the model is turned
  to it. Then the ground under the whole footprint is graded to that grade, flat across, and eased back into
  the land over the `shoulder`. No fit: the camp stays. Mountains don't get runways.

The outputs (from `BepInEx/SigmaMod-mapmagic-graph.txt`, written with `Debug.VerboseLog`):

| `in` | What the game puts there | Pad | Roads |
|---|---|---|---|
| `14795495755795660805` | Camps 1, 2, 3, 5, 8, 9, 11, 16 | 30 m | yes |
| `15169460551123206150` | Camps 4, 6, 10, 12, Buildings 1, 7 | 30 m | yes |
| `15543463829357723661` | Camps 13, 15, Buildings 2, 3, 5 | 80 m | yes |
| `15844372674089320462` | Camps 7, 14, Buildings 4, 6 | 80 m | yes |
| `1575289000812347441` | Camp 16, Wrecks 1, 4, 6, 7, 8, Caves 2, 3 | 50 m | no |
| `17530240658818727944` | Wrecks 2, 3, 5 | 25 m | no |
| `14432369946094731313` | Caves 5, 6, 7 | 70 m | no |
| `16289588021920006162` | Caves 1, 4 | 70 m | no |

Roads lead to the model's origin. Put the gate there, not the middle of the runway.

Want to see what you're replacing, or crib a camp? `Tools.ExportPlaces` dumps every one of them to `.glb`, each
tagged with the `in` ids that place it.

**The game's working parts** (loot, enemies, map icon, codex entry): they're the game's own prefabs, so
point at them instead of building them:

```json
"nodes": {
  "Loot spots": { "instance": { "fsm": "ItemSpawner", "where": { "minItemNum": 21 }, "as": "loot" } },
  "loot": { "fsm": { "ItemSpawner": { "minItemNum": 6, "maxItemNum": 10 } } }
}
```

`instance` puts a copy of the game prefab that has that FSM (and those FSM values, where there's more
than one) right before the node, at its spot. The game's spawner takes the object right after it as its
list of spawn points, so "Loot spots" is an empty with empties under it, and that's where the loot lands.
`as` names the copy so you can tune it further down. More than one candidate: the log lists them with
their values so you can pick. The parts in a camp: `ItemSpawner` (loot), the enemy spawner, `codexTrigger`
(codex), the map icon. Read a camp in the FSM dump for the rest.

## When it fucks up

| Log says | Why |
|---|---|
| `nothing loaded (no root object with sigma_id ...)` | No `sigma_id` on the root, or you forgot *Custom Properties* in the export. |
| `no game prefab found to build on (...)` | Objects from different exports mixed together. |
| `no object named '...'` | The `.json` names an object that isn't in the Blender file. Names are case-sensitive. |
| `no FsmStateAction type named '...'` | Typo in an action name. Check `*.fsm.json` from an export. |
| Your FSM does nothing, no warning | PlayMaker's own errors go to Unity's `Player.log` (in the game's AppData/LocalLow folder), not BepInEx's, unless `WriteUnityLog = true` in `BepInEx.cfg`. |
| `no variable '...' (local or global)` | `"{name}"` points at a variable the FSM doesn't define. |
| `chassis has no WheelControllers under it` | Add `WheelController` to your wheel hinges. |
| `chassis mass is the default 1 kg` | Set `Rigidbody.mass`. |
| `no "drive" in the .json` | Set `"drive"`. |
| `nothing matched` | A `.json` FSM key names something that FSM doesn't have. |

## Build

```
dotnet build -c Release
```

Builds against the game's own assemblies and deploys to `BepInEx/plugins/SigmaMod/`. Set `GameDir`
if the repo isn't in the game folder. `dotnet build -c Release tools/GltfTest` builds the variant
tool.

## License

[WTFPL](LICENSE). Do whatever the fuck you want with it.
