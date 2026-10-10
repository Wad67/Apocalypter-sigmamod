# Mitsubishi Sigma GH 2.0 (Australia, 1981): reference data

Collected for the `sigma_chassis` template and an Astron 4G52 engine part. Each value lists its
sources and a confidence level:

- **high**: two or more independent sources agree.
- **medium**: one primary listing, or agreement that follows from physics.
- **low**: a forum post or a single unverified figure.
- **open**: nothing found; any number used for it is a placeholder.

The GH series ran from May 1980 to February 1982 (Chrysler badge until October 1980). In 1981 it
was sold as Sigma, GL, GLX and SE with 1.6, 2.0 and 2.6 engines.

## Body

| Item | Value | Confidence | Sources |
|---|---|---|---|
| Layout | front engine, rear-wheel drive, 4-door sedan | high | [1] [2] [3] |
| Length / width / height | 4450 / 1655 / 1360 mm | high | [1] [2] |
| Wheelbase | 2515 mm | high | [1] [2] |
| Track front / rear | 1370 / 1348 mm | medium | [2] |
| Kerb weight, 2.0 manual sedan | 1112-1115 kg (use **1115**) | high | [2] SE 2.0 5-speed 1112 kg; [4] Turbo 1115 kg; [1] estimates 1140 kg for the 4-speed |
| Fuel tank | 60 L | high | [1] [2] |
| Drag coefficient | 0.45 (estimate) | low | [1] (marked as estimated) |
| Wheels / tyres (SE) | 5J x 14, BR78 S14 (about 643 mm across) | medium | [2] |
| Brakes | front disc 254 mm, rear drum 228 mm | medium | [5] (1983 GJ GL 5-speed; GH not listed) |

## Engine: 4G52 "Astron 2000"

| Item | Value | Confidence | Sources |
|---|---|---|---|
| Type | inline 4, SOHC, 8 valves, chain-driven cam, iron block, aluminium hemi head, twin balance shafts | high | [6] |
| Bore x stroke | 84 x 90 mm, 1995 cc | high | [14] (3.307 x 3.5433 in) [6] [7] [8] [1]; [2] gives 85 x 88 / 1997 cc, which are the 4G63's numbers (a database mix-up) |
| Fuel system | single 2-barrel carburettor (Mikuni-Solex type, vacuum secondary) | medium | [1] "2-barrel SOHC"; [2] "1 carburettor"; carburettor make from forum posts on the Astron family |
| Firing order | 1-3-4-2 | high | [14]; forum |
| Output, Sigma / SE tune | **64.4 kW at 5200 rpm, 145.4 Nm at 2400 rpm** (DIN) | high | [1] [2]; Wikipedia gives 64 kW / 145 Nm for the 2.0 [3] |
| Output, GL tune | 70.1 kW at 5600 rpm, 152.3 Nm at 2400 rpm | high | [9] (1980 GL wagon), [5] (1983 GL); Wikipedia GJ/GN 70 kW / 152 Nm [3] |
| Compression ratio | open for the Australian tune; 9.5:1 for a 73 kW export 2.0 | open | [10] (another market's tune) |
| Idle speed / ignition timing at idle | 650 rpm / 6° BTDC | medium | [14]; a North American rebuilder, so probably the US-market setting |
| Crank journals | main 60.0 or 66.0 mm (2.362 / 2.598 in, two block versions), rod 53.0 mm (2.086 in) | medium | [14] |
| Piston pin | 22 mm (0.87 in) | medium | [14]; the 4G54 also uses 22 mm |
| Valves | stem 8.0 mm, length 112.8 mm intake / 111.7 mm exhaust, 45° faces; hot clearance 0.15 mm intake / 0.25 mm exhaust | medium | [14] |
| Oil capacity with filter | 5.0 L (1.32 US gal) | medium | [14] |
| Spark plug | NGK W14EX-U, gap 0.7-0.8 mm | medium | [14] |
| Redline, valve timing, cam lift, rod length, piston compression height | open | open | no factory 4G52 manual found online |

The two outputs are two tunes of the same engine. The higher GL figure appears in both 1980 and
1983 listings, so it is not a later revision.

## Gearbox and final drive

| Item | Value | Confidence | Sources |
|---|---|---|---|
| 1981 2.0 options | 4-speed manual (Sigma, GL), 5-speed manual (GL option, SE), 3-speed automatic | high | [1] [2] [11] |
| 5-speed (KM132) | 3.369 / 2.035 / 1.360 / 1.000 / 0.856 | medium | [12] (Starion, D50); top gear 0.86 in [10] and in a Galant Sigma 2.0 listing |
| KM132 reverse | open | open | |
| 4-speed ratios | open | open | |
| Final drive, 2.0 manual | **3.91** | medium | [10] (export 2.0 5-speed); see the check below. Owners give 3.42 or 3.23 for the GH 2.0, unsure which [13] |

**Check of the final drive against the claimed top speed.** The base 2.0 4-speed is claimed at
160 km/h [1]. With 4th gear at 1:1 and a 643 mm tyre (2.02 m around), 160 km/h is 1320
wheel rpm:

- With 3.91, the engine turns 5160 rpm, which is its power peak (5200). That is where a
  drag-limited car with direct top gear tops out.
- With 3.42 it would be 4510 rpm, and with 3.23 it would be 4260 rpm.

Rough drag at 160 km/h (Cd 0.45 at about 1.9 m² frontal area, plus rolling resistance) is about
52 kW at the wheels. That fits 64 kW at the crank with about 10 % driveline loss. The physics
supports 3.91 for the 2.0. The lower forum ratios more likely belong to the 2.6.

## In the game

- The game delivers the gearbox and final drive with the **engine part**, not the chassis. The
  vanilla 2.3 L I4's `CheckTag` FSM sets `finalGearRatio` (4.87), `idleRPM`, `revLimiterRPM`,
  `inertia`, clutch engagement, shift points and the sound clip. Its `Condition` FSM sets
  `maxPower`. The 4G52 numbers above therefore go on an engine part template.
- The chassis keeps the body values: mass 1115 kg, and wheelbase and track through the wheel hinges.

Applied:

- `assets/apocasigma.json`: mass 1115 kg; gears `[-3.369, 0, 3.369, 2.035, 1.360, 1.000, 0.856]`
  (KM132; reverse is open, set equal to 1st as a placeholder).
- `assets/astron_4g52.glb` + `.json`: a variant of the vanilla 2.3 L I4 part (prefab
  374403439354623823), so it uses that part's model, sound and logic until an Astron mesh is made.
  - Final drive 3.91 (`CheckTag` variable and the `FinalDriveRatio` reset value).
  - Idle 650 rpm.
  - Power per condition band 54.3 / 38.8 / 19.4 / 1. The game's power figure is 0.79-0.86 x the
    rated kW across all vanilla engines; the 2.3 L I4 uses 56 / 66.4 = 0.844, and
    64.4 kW x 0.844 = 54.3. The lower bands keep the vanilla 56 : 40 : 20 proportions.
  - Rev limit 6000 and inertia 0.25 are the vanilla part's values (the redline is open).
- `enginesim/astron_4g52.cfg`, appended to `BepInEx/config/EngineSim.engines.cfg`: section
  6920689386521205109 (the part's prefab id). It holds the five basic keys plus bore 84, stroke 90
  and firing order 1-3-4-2; Engine Sim's generic values fill the rest.

## Sources

1. automobile-catalog.com, 1980 Mitsubishi Sigma 2.0L (Australia), saved page.
2. carfolio.com, 1980 Mitsubishi Sigma SE 2.0 (Australian market), saved page.
3. Wikipedia, Chrysler Sigma. https://en.wikipedia.org/wiki/Chrysler_Sigma
4. automobile-catalog.com, 1981 Mitsubishi Sigma Turbo, saved page.
5. automobile-catalog.com, 1983 Mitsubishi Sigma GL 2.0L 5-speed, saved page.
6. Wikipedia, Mitsubishi Astron engine. https://en.wikipedia.org/wiki/Mitsubishi_Astron_engine
7. Wikipedia, Mitsubishi KE engine. https://en.wikipedia.org/wiki/Mitsubishi_KE_engine
8. Precision International, 4G52 Astron (Sigma GH 1980-82, 1995.04 cc, 84 x 90). https://www.precisionintl.com/collections/4g52-astron
9. automobile-catalog.com, 1980 Mitsubishi Sigma Wagon GL 2.0L, saved page.
10. carfolio.com, 1981 Mitsubishi Colt Sigma 2000 GLX. https://www.carfolio.com/mitsubishi-colt-sigma-2000-glx-37224
11. automobile-catalog.com, 1981 Mitsubishi (Australia) Sigma GH sedan range, saved page.
12. Project Zero G, Transmissions (KM132-M-CNL). https://projectzerog.com/transmissions.shtml
13. sigma-galant.com, Sigma Diff Ratios. https://sigma-galant.com/viewtopic.php?t=4862
14. TAM Engines, Quick Engine Specification: Mitsubishi 4G52 (specs_mit_4g52), saved PDF.
