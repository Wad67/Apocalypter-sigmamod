# SigmaMod

> **⚠ Very fucking early.** This is a 0.1 that has barely been driven in game. Expect things to break,
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
- **Engine Sim users:** paste `SigmaMod/examples/enginesim/astron_4g52.cfg` onto the end of
  `BepInEx/config/EngineSim.engines.cfg` and the Astron sounds like an Astron.

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
rebuilding a single fucking state machine. That's the plan to change, see [Coming](#coming-no-donor-needed).

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
| `Debug.VerboseLog` | off | Log spam, plus FSM and vehicle dumps in `BepInEx/`. |

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
7. **Tune** (optional): a `.json` with the same name next to it. See [Tuning](#tuning).
8. **Load a save.** Log says `custom prefab: 'my_truck' ... from donor 'Outrider'`. Spawn it from
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
| `sigma_spawn` | `false` keeps it out of world spawning. |

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
`drive` and you're stuck with all-wheel drive.

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
- `nodes`: objects by their `sigma_id`.
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

## Coming: no donor needed

Not built yet. The plan:

- Tag any Blender object `sigma_from = Outrider/hinge_wheel_FL` (or a game object's `es3local` id)
  and it gets that game object's logic: FSMs, colliders, audio, scripts. Objects copied out of an
  export already carry their id.
- No donor, so you can mix: an Outrider dashboard on Duke wheel hinges on a body built from scratch.
- FSMs pointing at other game objects get re-pointed at whatever you built from those objects.
  Anything left dangling gets logged, and the `.json` can wire it up by hand
  (`"refs": { "switch_ignition": "my_ignition" }`).
- A chassis root gets a fresh Rigidbody, VehicleController and save identity, plus whatever root
  logic you tag onto it.
- The donor way stays as the lazy option.

## When it fucks up

| Log says | Why |
|---|---|
| `nothing loaded (no root object with sigma_id ...)` | No `sigma_id` on the root, or you forgot *Custom Properties* in the export. |
| `no game prefab found to build on (...)` | Objects from different exports mixed together, or nothing from the game in there at all. |
| `chassis mass is the default 1 kg` | Set `Rigidbody.mass`. |
| `no "drive" in the .json` | Set `"drive"`. |
| `nothing matched` | A `.json` FSM key names something that FSM doesn't have. |

## Build

```
dotnet build -c Release
```

Builds against the game's own assemblies and deploys to `BepInEx/plugins/SigmaMod/`. Set `GameDir`
if the repo isn't in the game folder. `dotnet build -c Release tools/GltfTest` builds the variant
tool. `apocasigma.glb` is too fat for GitHub (159 MB); it's only in the release zip.

## License

[WTFPL](LICENSE). Do whatever the fuck you want with it.
