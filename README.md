# CrookedToe's VRCOSC Modules

A collection of modules for [VRCOSC](https://github.com/VolcanicArts/VRCOSC).

## Modules

### OSCLeash
A sophisticated module for controlling avatar movement through physbone parameters, includes the features:
- Leash-based movement control
- Dynamic height adjustment via OpenVR
- Customizable turning and movement settings
- Smooth, physics-based interactions

[Learn more about OSCLeash and setup instructions](Modules/OSCLeash/README.md)

### OSCAudioReaction
Real-time audio visualization module that:
- Captures system audio output
- Provides stereo direction detection
- Sends volume information to VRChat
- Enables dynamic audio-reactive avatars

[Learn more about OSCAudioReaction](Modules/OSCAudioReaction/README.md)

## Requirements

- [VRCOSC](https://github.com/VolcanicArts/VRCOSC) (latest version)
- Windows 10/11
- .NET 8.0 Runtime
- VRChat with OSC enabled

## Installation

1. Add this repository to VRCOSC:
   - Open VRCOSC
   - Go to Packages > CrookedToe's Modules
   - Add the package

2. Enable and Configure Modules:
   - Navigate to the Modules tab
   - Enable desired modules
   - Configure settings for each module
   - Refer to individual module READMEs for specific setup instructions

## Development

These modules are developed using:
- C# and .NET 8.0
- VRCOSC SDK

Each module is self-contained in its own directory under `Modules/` with:
- Dedicated README
- Comprehensive documentation
- Unit tests (where applicable)
- Example configurations

### Project Structure
```
Modules/
├── OSCLeash/           # Avatar movement control
│   ├── README.md
│   └── ...
├── OSCAudioReaction/   # Audio visualization
│   ├── README.md
│   └── ...
└── ...
```

## Contributing

Contributions are welcome! To contribute:

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

Please ensure your code:
- Follows existing coding style
- Includes appropriate documentation
- Has been tested thoroughly

## Support

- Join the [VRCOSC Discord](https://discord.com/invite/vj4brHyvT5) for community support
- Create an [Issue](https://github.com/CrookedToe/CrookedToe-s-Modules/issues) for bug reports
- Check individual module READMEs for specific troubleshooting

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Credits

- [VRCOSC](https://github.com/VolcanicArts/VRCOSC) by VolcanicArts
- [VRC-OSC-Audio-Reaction](https://github.com/Codel1417/VRC-OSC-Audio-Reaction) by Codel1417
- Original [OSCLeash](https://github.com/ZenithVal/OSCLeash) concept by ZenithVal