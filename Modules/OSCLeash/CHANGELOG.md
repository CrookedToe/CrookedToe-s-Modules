# Changelog

All notable changes to this project will be documented in this file.

## Unreleased

### Fixed
- Yield to external OpenVR standing-pose writers instead of fighting OVRAS
- Automatically rebase and resume after a one-time or completed OVRAS pose change
- Use working-pose preview rather than committing chaperone state during motion
- Restore neutral VRChat movement and turn inputs when the module stops
- Keep cleanup ownership state across transient OpenVR failures
- Continue OpenVR maintenance when the VRChat player object is unavailable
- Restore existing configurable movement, vertical, turning, and return settings
- Prevent smoothed movement from exceeding the current leash pull or lingering after an axis reaches zero
- Brake immediately on direction reversal and require a stable opposite pull before moving back

### Changed
- Bound height drag with a configurable maximum distance
- Split movement, vertical physics, OpenVR ownership, and trace recording into focused components
- Sample and buffer debug traces to reduce update-loop overhead

### Added
- Regression tests for external pose ownership, safe cleanup, smoothing, and bounded return motion

## [0.2.3] - 2025-01-13

### Changed
- Increased update frequency from 30Hz to 60Hz for improved responsiveness
- Reduced movement smoothing delays and thresholds for faster grab response
- Optimized grab state detection for immediate processing

### Added
- Low-latency mode that activates automatically when leash is grabbed
- Immediate parameter processing bypass for grab events

## [0.2.2] - 2025-01-13

### Fixed
- Fixed state not properly cleaning up
- Fixed resource managment

### Changed
- decreased update rate to 30hz

### Added
- Added motion smoothing

## [0.2.1] - 2025-01-10

### Fixed
- Fixed installation instructions

### Removed
- Removed setting for leash name, opting for direct parameter configuration

## [0.2.0] - 2024-01-09

### Fixed
- gpu spikes leading to crashing

### Changed
- Simplified parameter setup - all parameters now use consistent naming (e.g., `XPositive`/`XNegative` instead of `X+`/`X-`)
- Leash direction is now set exclusively through module settings
- Movement calculations now match the original Python implementation exactly
- Up/Down compensation behavior now matches the original implementation

### Added
- Leash name setting for easier parameter configuration
- Direction dropdown setting for leash orientation
- Improved error handling and parameter validation

### Removed
- Support for legacy parameter names (`X+`, `Y+`, `Z+`)
- Automatic direction detection from parameter names
- Fixed value parameters - now reading all values from avatar

## [0.1.0] - 2024-01-08

### Added
- Initial release
- Basic movement control through physbone parameters
- Walking and running thresholds
- Up/Down movement compensation
- Optional turning control
- Integration with VRCOSC module system
