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

This is a VRCOSC module port of [ZenithVal's OSCLeash](https://github.com/ZenithVal/OSCLeash), rewritten in C# to work within VRCOSC's module system. This version features optimized low-latency response for immediate grab detection and movement, running at 60Hz with automatic performance scaling. For detailed information about the original implementation, advanced features, and troubleshooting, please visit the original repository.

> ⚠️ **WARNING**: This project is currently a Work In Progress. Features may be incomplete, unstable, or subject to significant changes. Use at your own risk and please report any issues you encounter.

# Quick Start Guide

## Requirements
- [VRCOSC](https://github.com/VolcanicArts/VRCOSC)
- .NET 8.0 Runtime
- Windows 10/11
- VRChat with OSC enabled

## OVRAdvancedSettings Compatibility

This module is now fully compatible with OVRAdvancedSettings! The leash system will:

- **Preserve OVRAdvancedSettings offsets**: All X, Y, and Z offsets from OVRAdvancedSettings are maintained
- **Detect OVRAdvancedSettings changes**: Automatically adapts when you adjust your playspace through OVRAdvancedSettings
- **Coordinate transformations**: Works alongside OVRAdvancedSettings' space drag, manual offsets, and physics systems
- **Maintain baseline state**: Captures and preserves your OVRAdvancedSettings configuration when the leash is grabbed

### How it works:
1. When OSCLeash initializes, it captures your current OVRAdvancedSettings state as the baseline
2. When you grab the leash, it refreshes the baseline to ensure compatibility
3. All vertical movement preserves your OVRAdvancedSettings X/Z offsets and rotation
4. The system automatically detects and adapts to OVRAdvancedSettings changes

## Known Issues
- None currently known - please report any issues you encounter

## Installation Steps

### 1. Module Setup
1. Enable the module in VRCOSC
2. Configure the module settings in VRCOSC's UI

### 2. Avatar Setup
0. Remove any existing Physbones on the leash
1. Import the package (`OSCLeash.prefab`) from releases into your Unity project
2. Place the prefab at the root of your model (NOT as a child of armature)
3. drag the first bone of your leash into the `Leash Start Bone` field
4. Click Auto Setup
5. Reset the OSC configuration on your avatar

### 3. Parameter Setup
The module requires the following parameters to be set up in your avatar:

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

The leash direction is set in the module settings:
- `North` - Front-facing leash (default)
- `South` - Back-facing leash
- `East` - Right-facing leash
- `West` - Left-facing leash

# Configuration

## Basic Settings
| Setting | Description | Default |
|---------|-------------|---------|
| Leash Direction | Direction the leash faces | North |
| Walk Deadzone | Minimum stretch for walking | 0.15 |
| Run Deadzone | Minimum stretch for running | 0.70 |
| Strength Multiplier | Movement speed multiplier | 1.2 |

## Up/Down Control Settings
| Setting | Description | Default |
|---------|-------------|---------|
| Up/Down Compensation | Compensation for vertical movement | 1.0 |
| Up/Down Deadzone | Vertical angle deadzone | 0.5 |

## Vertical Movement Settings
| Setting | Description | Default |
|---------|-------------|---------|
| Enable Vertical Movement | Enables OpenVR height control | false |
| Enable Gravity | Return to grab height when released | false |
| Vertical Speed | Vertical movement speed multiplier (0.1-5.0) | 1.0 |
| Vertical Deadzone | Minimum vertical pull needed (0-1) | 0.15 |
| Vertical Smoothing | Smoothing factor for height changes (0-1) | 0.8 |
| Vertical Angle | Required angle from horizontal (15-75°) | 45° |

## Turning Settings
| Setting | Description | Default |
|---------|-------------|---------|
| Turning Enabled | Enable turning control | false |
| Turning Multiplier | Turning speed multiplier | 0.80 |
| Turning Deadzone | Minimum stretch for turning | 0.15 |
| Turning Goal | Maximum turning angle in degrees | 90° |


# Troubleshooting

## Common Issues
- **No Movement Response**: 
  - Verify OSC is enabled in VRChat
  - Check that VRCOSC is running
  - Verify parameter names match exactly (including case)
  - Check that the leash parameter name is "Leash" 
- **Incorrect Movement**: 
  - Check physbone constraints and contact setup
  - Verify the leash direction setting matches your setup
- **No Turning**: 
  - Check that turning is enabled in settings
  - Verify the leash direction is set correctly

## Getting Help
- Join the [Discord](https://discord.com/invite/vj4brHyvT5) for VRCOSC support
- Create an [Issue](https://github.com/CrookedToe/OSCLeash/issues) for bug reports
- Check VRCOSC logs for any error messages- Enable debug logging for detailed state information
