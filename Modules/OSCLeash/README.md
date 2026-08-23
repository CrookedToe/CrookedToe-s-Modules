# OSCLeash

OSCLeash is a VRCOSC module that maps avatar leash, tail, or hand-holding parameters to VRChat movement. It can optionally move the OpenVR standing origin for height drag.

This is a C# port of [ZenithVal's OSCLeash](https://github.com/ZenithVal/OSCLeash).

> **Warning:** Height drag changes the OpenVR playspace. Start with conservative settings and test somewhere safe.

## Requirements

- VRCOSC with the 2026 SDK
- .NET 10 Desktop Runtime
- Windows 10 or 11
- SteamVR/OpenVR for height drag
- VRChat with OSC enabled

## Installation

1. Enable the module in VRCOSC.
2. Download and import the OSCLeash setup package attached to the [latest GitHub release](https://github.com/CrookedToe/CrookedToe-s-Modules/releases/latest).
3. Place the prefab at the avatar root, not under the armature.
4. Assign the first leash bone to `Leash Start Bone`.
5. Run Auto Setup.
6. Reset the avatar's OSC configuration.

## Avatar parameters

| Parameter | Type | Description |
|---|---|---|
| `Leash_IsGrabbed` | Bool | Leash grab state |
| `Leash_Stretch` | Float | Leash stretch |
| `Leash_Z+` | Float | Forward pull |
| `Leash_Z-` | Float | Backward pull |
| `Leash_X+` | Float | Right pull |
| `Leash_X-` | Float | Left pull |
| `Leash_Y+` | Float | Upward pull |
| `Leash_Y-` | Float | Downward pull |
| `leash_disable` | Bool | Optional. `true` disables motion. Missing or `false` leaves it enabled. |

## Settings

### Movement

| Setting | Default | Purpose |
|---|---:|---|
| Move Start | 0.15 | Stretch required before walking starts |
| Run Start | 0.70 | Stretch required before running starts |
| Pull Strength | 1.2 | How strongly pull maps to movement speed |

Diagonal pulls cannot command more total movement than a straight pull.

Short OSC gaps pause movement until input returns. If OSC is gone for about two seconds, release and grab again before continuing.

### Turning

| Setting | Default | Purpose |
|---|---:|---|
| Allow Turning | false | Lets side pulls turn the avatar |
| Leash Forward | North | Prefab forward axis used for turning; most prefabs use North (+Z) |
| Turn Strength | 0.8 | How strongly a side pull turns |

VRChat comfort turning can reduce or block this input. Turning does not apply while height drag is active.

### Height drag

| Setting | Default | Purpose |
|---|---:|---|
| Allow Height Drag | false | Lets vertical pulls move the OpenVR playspace |
| Height Speed | 1.0 | Maximum height speed in meters per second |
| Height Limit | 3 m | Maximum distance from the height where the leash was grabbed |
| Return Height on Release | false | Returns to the grab height after release |

Height drag needs SteamVR, a grab, stretch past Move Start, and a mostly vertical pull. Speed scales with stretch. Return Height accelerates toward the grab height and stops on the target.

If another application moves the SteamVR playspace during a grab, height drag waits and then continues from the new height. If that keeps happening, release and grab again.

## Troubleshooting

- **No movement:** Confirm OSC is enabled, VRCOSC is running, and parameter names match exactly.
- **Movement stopped after an OSC interruption:** Release and re-grab the leash.
- **No height drag:** Confirm SteamVR is running and `Allow Height Drag` is on, then pull mostly up or down with the leash stretched.
- **Height drag stopped mid-grab:** Another application moved the playspace. Release and grab again if it does not resume.
- **Incorrect direction:** Set `Leash Forward` to match the prefab.
- **No turning:** Enable `Allow Turning` and disable or reduce VRChat comfort turning.

For support, use the [VRCOSC Discord](https://discord.com/invite/vj4brHyvT5) or open a repository issue.
