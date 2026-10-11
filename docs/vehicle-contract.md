# What the game expects on a vehicle

The game's own C# barely touches vehicles. Everything that talks to a car (engines, radiators,
gauges, fuel cans, the player, spawners) does it through PlayMaker: it grabs an object, then reads or
writes a variable in an FSM **by name** on it. Build a car from scratch and these names are the
contract. Get one wrong and that system silently does fuck all.

Pulled from the FSM dump of every vehicle and every non-vehicle FSM that reaches into one (game
version as of 2026-10). `*.fsm.json` from *Export All* has the full vanilla versions to crib from.

## Root object

Tag `Vehicle`, layer `8`. Needs `Rigidbody` and `VehicleController` (NWH). The VehicleController
needs `stateSettings` = `asset:StateSettings/DefaultStateSettings` (also `SemiTruckStateSettings`,
`TrailerStateSettings`); without one NWH logs "StateSettings are null" and the engine never runs.

A VehicleController added from scratch has NWH's defaults, which assume a standalone car. The game's
cars set these, and a scratch car must too:

| Field | NWH default | Game | Otherwise |
|---|---|---|---|
| `input.autoSetInput` | true | false | the car reads the keyboard whether anyone is in it; the game feeds input from the `DriveTrigger/INPUT` FSMs while driving |
| `powertrain.engine.autoStartOnThrottle` | true | false | throttle starts the engine without the ignition |
| `powertrain.engine.ignition` | true | false | spawns with the ignition on |
| `powertrain.engine.forcedInduction.useForcedInduction` | true | false | a turbo nobody fitted |
| `brakes.brakeWhileIdle` | true | false | brakes on whenever there's no throttle |
| `input.swapInputInReverse` | true | false | throttle and brake swap in reverse |

The rest (sound mix, smoke, steering) is taste; `BepInEx/SigmaMod-chassis-<id>.txt` of a donor-based chassis lists the game's values.

Engine sound: an object (vanilla `RSound`) with `NWH2_RES2`, and a child named exactly `EngineSound`
with `RealisticEngineSound`; the root's `GetRSound.RSound` points at it. The fitted engine puts its
clips on it. Engine Sim finds cars by scanning for `RealisticEngineSound`, so without it Engine Sim
never sees the car or takes over its ignition (`Start` FSM with an `off3` state). Engine Sim also
reads the engine hinge's `getEngine` state named `noEngine`.

| FSM | Variable | Type | Who reads it | What it has to hold |
|---|---|---|---|---|
| `RpmGear` | `rpm`, `gear` | float, int | engine `OnOff`, gear lever; Apocaspawner uses the FSM's existence to call it a vehicle | engine rpm and current gear (GetProperty `powertrain.engine.OutputRPM`, `powertrain.transmission.Gear`) |
| `Speed` | `speed` | float | engine `AirCooling` | speed (GetSpeed on owner) |
| `getFuel` | `Fuel` | GameObject | engine `CheckTag` | the fuel tank object (see below) |
| `getAudio` | `AudioThrottle` | GameObject | engine `CheckTag` (sets its clip) | object with the engine AudioSource |
| `GetRSound` | `RSound` | GameObject | engine `CheckTag` | sound object; it must have a child named `EngineSound` with `SkrilStudio.RealisticEngineSound` |
| `getEngineHinge` | `hinge_engine` | GameObject | radiators, oil temp gauge | the engine hinge |
| `getRadiatorHinge` | `hinge_radiator` | GameObject | water temp gauge | the radiator hinge |
| `TireWear` | (enabled) | | wheels enable it when fitted | tyre wear logic |
| `CrashDamage`, `getFuel`, `TrailerAttached` | | | Apocaspawner: any of these makes it a vehicle in its catalog | |

The lookup FSMs (`getFuel`, `GetRSound`, `getEngineHinge`, ...) only exist to fill one GameObject
variable. Vanilla finds the child by name at runtime; from JSON just preset the variable:

```json
"getFuel": { "variables": { "gameObject": { "Fuel": "FuelTank" } }, "states": { "idle": {} } }
```

## Hinges (where parts bolt on)

A hinge is an object with a **trigger collider** and these FSMs. Vanilla: layer `2`, engine,
radiator and exhaust hinges layer `13` tag `Hinge`.

| FSM | Variable | Who reads it | |
|---|---|---|---|
| `vehPart_Attach` | `ID`, `Item`, `Hinge` | itself | waits for a loose part (tag `vehPartRemoved`) in the trigger, held in the player's `Hand`, whose `ID` FSM's `ID` matches; on Use it strips the part's Rigidbody, tags it `vehPart`, layer 8, parents it to the hinge. Full vanilla version in any export. |
| `getParent` | `vehicle` (GameObject) | every fitted engine, radiator, wheel | the car root. Engines go part -> parent hinge -> `getParent.vehicle` -> root FSMs above. |
| `getEngine` | `engine` | radiators, oil gauge (engine hinge only) | the fitted engine |
| `getRadiator` | `radiator` | water gauge (radiator hinge only) | the fitted radiator |

Part IDs (`compareTo` in `vehPart_Attach`, matched against the part's `ID` FSM): `engine`,
`radiator`, `exhaust`, `wheel`, `suspension`, `brakes`, `gauge` (dashboard), `gearlever`,
`steeringwheel`, `seat`, `door`, `hood`, `trunk`, `bumper`, `headlight`, `windshield`, `roofrack`,
`radio`. The engine also does `GetChild "transmission"` on itself.

Wheel hinges also carry the NWH `WheelController`, plus `checkWheel`, `Suspension` (sets hinge height
and travel per suspension type) and `disableMeshCollider`.

## Looking at things

The player's camera FSM `ItemNameLook` raycasts 2 m on layers **0, 8, 9, 13, 21** only and sends
`OverItem` / `OffItem` to the GameObject whose collider it hit. Anything with hover text needs a
collider on one of those layers and an FSM with global transitions on `OverItem`/`OffItem` writing
`{UI_ItemName}`, `{UI_ItemName_2}`, `{UI_ItemName_3}` (global UI text objects). Layer 2 is invisible
to it.

Cursor icons: send `Cursor_Point`, `Cursor_Wrench`, `Cursor_Adjust` or `Cursor_Wrench_2` to the
GameObject `{UI_Canvas}` (its `MouseIcon` FSM has global transitions for them). After fitting a part,
send `not_Hold` to `{PlayerCamera}` FSM `GrabItem` so the hand lets go.

## Fuel tank

The object `getFuel.Fuel` points at. Vanilla: layer `2`, tag `Gasoline`, trigger collider.

| FSM | Variable | Who | |
|---|---|---|---|
| `LiquidAmount` | `Liquid`, `LiquidCapacity` (float), `FuelModule` (Object) | engine `OnOff`, cans, pumps, barrels, spawners | fuel in litres and capacity |
| `FuelConsumption` | `FuelConsumption` (float) | engine `CheckTag` writes it | burn rate |
| `UseCan`, `Open`, `Closed`, `CheckLiquidLevel` | | fuel cans, syphon | refuelling |

Siphoning (vanilla `Fuel/Syphon` and `Fuel/FuelTrigger`): a layer 0 trigger the player looks at
shows the level and `Syphon Fuel: F` in `{UI_ItemUse}`; the player's Use sends it `UseItem`. Opening
disables the tank's pour-in collider, enables the hose-end capsule trigger (layer 2, off until then)
and enables the tank's `UseCan` FSM, which moves 1 L/s from the tank into a can whose tag matches
while the can is in the hose-end trigger. Closing reverses it.

## Driving

`DriveTrigger` (layer 7): Rigidbody, ConfigurableJoint, colliders and FSMs `Drive`, `Camera`,
`ExitSpeed`; children `sitPos`, `exitPos`, `3rdCamera` (FSMs `MouseLook`, `Zoom`; the player's
camera enables `Zoom` by name) and `INPUT` (the `INPUT_*` and `Bool_*` FSMs feeding NWH input).
Pedals (`Pedal` FSM), `Handbrake`, gear lever `gearChange` (reads root `RpmGear.gear` through
two GetParents) are cosmetic followers of the input.

## Saving

Spawned copies go in the `ArrayList_Cars` list (SigmaMod's `Spawn` and Apocaspawner do that). A
`saveItemVar` FSM on the root is how vanilla saves its own variables; without one, only what Easy
Save saves by itself survives (transform, components it has types for).
