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

## OVR Advanced Settings compatibility

[OVR Advanced Settings](https://github.com/OpenVR-Advanced-Settings/OpenVR-AdvancedSettings) and OSCLeash both use OpenVR's single global standing-pose working copy. OVRAS does not expose a documented public API for another process to add an offset to its internal playspace state.

OSCLeash therefore uses a fail-safe ownership policy:

1. It captures the current OpenVR standing pose when the leash is grabbed.
2. It applies height through OpenVR's working-pose preview without committing the chaperone configuration to disk.
3. Before each write, it verifies that the live pose still matches OSCLeash's last write.
4. If OVRAS or another application changes the pose, OSCLeash yields and watches the external pose.
5. After the pose is stable for 250 ms, OSCLeash adopts it as the new baseline and automatically resumes height drag.
6. If the external application overwrites that retry, height drag latches off until the leash is released and grabbed again.

This prevents sustained writer-versus-writer jitter, preserves stable OVRAS offsets, and allows one-time OVRAS adjustments without a manual re-grab. Merely having OVRAS open does not suspend height drag because OVRAS skips standing-pose writes while its offsets and rotation are unchanged. Active OVRAS space drag, gravity, or turning cannot control the same standing pose at the exact same instant as OSCLeash height drag; OVRAS wins while it is moving, then OSCLeash resumes after it settles. Normal VRChat walking and turning from the leash continue.

## Installation

1. Enable the module in VRCOSC.
2. Import `OSCLeash.prefab` from the release into the Unity avatar project.
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
| `leash_enable` | Bool | Enables leash motion; defaults to enabled if never received |

## Settings

### Movement

| Setting | Default | Purpose |
|---|---:|---|
| Move Start | 0.15 | Leash stretch required before movement starts |
| Run Start | 0.70 | Leash stretch required before running starts |
| Pull Strength | 1.2 | How strongly pull maps to movement speed |

Movement smoothing applies only while acceleration is increasing. If the leash pull weakens or reaches zero, movement input brakes immediately instead of decaying from an older, stronger command. Direction reversals output zero until the opposite pull remains stable for 120 ms, preventing rapid forward/back or left/right correction loops.

Combined horizontal input is capped to a unit circle so diagonal pulls cannot command more total movement than straight pulls.

### Turning

| Setting | Default | Purpose |
|---|---:|---|
| Allow Turning | false | Allows side pulls to control VRChat turning |
| Leash Forward | North | Prefab forward axis used to calculate turning; normally North (+Z) |
| Turn Strength | 0.8 | Side-pull turn strength |

Comfort turning in VRChat can alter or suppress the resulting turn input.

### Height drag

| Setting | Default | Purpose |
|---|---:|---|
| Allow Height Drag | false | Allows vertical pulls to control OpenVR height |
| Height Speed | 1.0 | Maximum height speed in meters per second |
| Height Limit | 3 m | Safety bound from the position where the leash was grabbed |
| Return Height on Release | false | Returns to the original grab height after release |

Smoothing, vertical compensation, turn activation, and height-return physics use tested internal values. They are intentionally not exposed because changing them can make the control loop unstable or difficult to understand. Existing saved values for the ten retained settings continue to use their original keys.

## Troubleshooting

- **No movement:** Confirm OSC is enabled, VRCOSC is running, and parameter names and capitalization match.
- **No height drag:** Confirm SteamVR is active and `Enable Height Drag` is on.
- **Waiting for OpenVR pose warning:** OVRAS is moving the shared standing pose. Height drag resumes automatically after it is stable for 250 ms.
- **Height drag suspended warning:** OVRAS continued overwriting the automatic retry. Stop the active OVRAS motion, then release and grab the leash again.
- **Incorrect direction:** Match `Leash Direction` to the avatar setup.
- **No turning:** Enable turning and disable or reduce VRChat comfort turning.

For support, use the [VRCOSC Discord](https://discord.com/invite/vj4brHyvT5) or open a repository issue.
