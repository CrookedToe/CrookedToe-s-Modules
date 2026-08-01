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
- Apply lateral and vertical corrections directly from the current leash signal
- Stop movement immediately at the deadzone or on release
- Require stretch before height drag and scale vertical speed by current stretch
- Retain and retry neutral VRChat inputs until every owned channel is released
- Clear cached leash parameters on avatar and player lifecycle changes
- Preserve OpenVR cleanup ownership after transient shutdown failures
- Point prefab setup and documentation to the package attached to each GitHub release

### Changed
- Bound height drag with a configurable maximum distance
- Split movement, vertical physics, and OpenVR ownership into focused components
- Reduce the settings menu from 22 controls to 10 clearly named user choices
- Use tested internal values for smoothing, compensation, turn gating, and height-return physics
- Cap combined diagonal movement to the same maximum magnitude as straight movement
- Use one 16 ms control loop for VRChat movement and OpenVR height writes
- Remove movement smoothing, reversal timers, output rate gates, vertical write queues, and the height-drag grab cooldown
- Replace threshold-heavy return gravity with a bounded accelerated return that stops exactly at the target

### Added
- Regression tests for external pose ownership, safe cleanup, smoothing, and bounded return motion
- Trace-derived control-loop regression coverage for vertical pulls, diagonal ramps, reversals, and axis drops
- Direct stop, reversal, vertical-limit, and captured-sequence regression coverage
- Failure-injection coverage for VRChat input cleanup, avatar-state reset, OpenVR writes, and prefab integrity

### Removed
- Remove runtime debug trace recording after converting captured failure windows into regression fixtures

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
