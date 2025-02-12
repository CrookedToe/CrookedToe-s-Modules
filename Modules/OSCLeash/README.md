# OSCLeash

<div align="center">
    <h3>
        A VRChat OSC module for VRCOSC that enables avatar movement control through physbone parameters.<br>
        Perfect for leashes, tails, or hand holding!
    </h3>
    <p>
        <a href="https://github.com/CrookedToe/OSCLeash/blob/main/LICENSE"><img alt="License" src="https://img.shields.io/github/license/ZenithVal/OSCLeash?label=License"></a>
    </p>
</div>

This is a VRCOSC module port of [ZenithVal's OSCLeash](https://github.com/ZenithVal/OSCLeash), rewritten in C# to work within VRCOSC's module system. While the core functionality remains the same*, this version integrates directly with VRCOSC for a more streamlined experience.

# Quick Start Guide

## Requirements
- [VRCOSC](https://github.com/VolcanicArts/VRCOSC)
- .NET 8.0 Runtime
- Windows 10/11
- VRChat with OSC enabled

## Installation Steps

### 1. Module Setup
1. Enable the module in VRCOSC
2. Configure the module settings in VRCOSC's UI

### 2. Avatar Setup
1. Import the Unity package from releases into your Unity project
2. Drag the `OSCLeash` prefab onto your avatar in the hierarchy
3. Select the first bone of your leash and drag it into the the prefab settings 
4. click "Auto Setup"

### 3. Parameter Setup
The module uses the following parameters:

| Parameter | Description |
|-----------|-------------|
| `Leash_IsGrabbed` | Physbone grab state |
| `Leash_Stretch` | Physbone stretch value |
| `Leash_Z+` | Forward movement value |
| `Leash_Z-` | Backward movement value |
| `Leash_X+` | Right movement value |
| `Leash_X-` | Left movement value |
| `Leash_Y+` | Up movement value |
| `Leash_Y-` | Down movement value |

# Configuration

## Movement Settings
| Setting | Description | Default |
|---------|-------------|---------|
| Leash Direction | Direction the leash faces (North/South/East/West) | North |
| Walk Deadzone | Minimum stretch required to start walking | 0.15 |
| Run Deadzone | Minimum stretch required to start running | 0.70 |
| Strength Multiplier | Overall movement speed multiplier (0.1-5.0) | 1.2 |

## Vertical Movement Settings
| Setting | Description | Default |
|---------|-------------|---------|
| Vertical Movement Enabled | Enables OpenVR height adjustment | false |
| Vertical Movement Multiplier | Speed of height changes (0.1-5.0) | 1.0 |
| Vertical Movement Deadzone | Minimum pull needed for height change | 0.15 |
| Vertical Movement Smoothing | Smoothness of height changes (0-1) | 0.95 |
| Vertical Angle | Required angle for vertical movement (15-75°) | 45° |

## Turning Settings
| Setting | Description | Default |
|---------|-------------|---------|
| Turning Enabled | Enable turning control | false |
| Turning Multiplier | Turning speed multiplier (0.1-2.0) | 0.80 |
| Turning Deadzone | Minimum stretch for turning | 0.15 |
| Turning Goal | Maximum turning angle (0-180°) | 90° |

# Troubleshooting

## Common Issues
- **No Movement Response**: 
  - Verify OSC is enabled in VRChat
  - Check that VRCOSC is running
  - Verify parameter names match exactly (including case)
  - Check that the leash name in settings matches your parameter prefix
- **Incorrect Movement**: 
  - Check that the leash direction setting matches your setup
  - Verify the auto-setup completed successfully
- **No Vertical Movement**: 
  - Ensure SteamVR is running
  - Check that Vertical Movement is enabled in settings
  - Pull at an angle greater than the Vertical/Horizontal Compensation setting
- **No Turning**: 
  - Verify turning is enabled in settings
  - Check that the leash direction matches your setup
  - Pull beyond the turning deadzone threshold

## Getting Help
- Join the [Discord](https://discord.com/invite/vj4brHyvT5) for VRCOSC support
- Create an [Issue](https://github.com/CrookedToe/OSCLeash/issues) for bug reports
- Check VRCOSC logs for any error messages
