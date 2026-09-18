# Changelog

All notable changes to this project will be documented in this file.

## [1.3.8] - 2026-09-18

### Fixed
- Read the active OpenVR tracking origin instead of the client's chaperone working copy, preserving OVR Advanced Settings space drag when capturing and returning to grab height.
- Detect OVR takeover during height return and cleanup even if the local working copy still contains the leash's last write.
- Enter external-pose recovery when a takeover is detected on re-grab, rather than continuing with a stale height offset.
- Reject invalid tracking transforms and add grab/release baseline diagnostics.
- Activate the VRCOSC dispatcher workaround by resolving the actual client field.
- Keep movement/audio sends independent of chatbox UI delays; bound preview updates and release closed views.
- Move leash/audio host logging to a shared bounded worker so UI and file writes do not stall control work.
- Recheck leash input after OpenVR work and neutralize commands invalidated during a stalled output batch.
- Record the exact development-build identity and correct negative health ages.

See [the investigation](DEGRADATION-AUDIT.md) for capture evidence and regression validation.

## [1.3.7] - 2026-08-23

### Added
- Optional `leash_disable` parameter. `true` disables motion; missing or `false` is safe.

### Changed
- Publish a complete VRChat movement state, including zeroes, and keep sending every channel even if one send fails or stalls.
- Pause movement after a short OSC gap and clear the old pull vector before accepting recovered input. A two-second drop still requires release and a new grab.
- Simplified the README and removed the OVR Advanced Settings compatibility note.

### Fixed
- One failed or slow VRChat input channel no longer aborts the rest of the movement state.

## [1.3.6] - 2026-08-01

### Fixed
- Yield to external OpenVR standing-pose writers instead of fighting them
- Automatically rebase and resume after a one-time or completed external pose change
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
