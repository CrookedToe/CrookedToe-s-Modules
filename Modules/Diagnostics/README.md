# Bounded performance diagnostics

This debug branch records low-overhead JSON Lines diagnostics for every active CrookedToe module. It is intended for the gradual OSCLeash degradation investigation, while retaining enough process-wide context to identify interference from another module or the runtime.

## Location and retention

The default directory is:

`%LOCALAPPDATA%\VRCOSC\diagnostics\CrookedToe`

Set `CROOKEDTOE_DIAGNOSTICS_DIR` before starting VRCOSC to use another directory. The active file is `crookedtoe-diagnostics.jsonl`. It rotates at 16 MiB and retains eight files total (the active file plus `.1` through `.7`), so storage has a hard ceiling of approximately 128 MiB. Oldest data is removed first.

At the recorder's normal aggregate rate with every module enabled, this is intended to preserve at least a full day and typically several days of continuous diagnostics. Rotation is size-based rather than time-based, so quieter configurations retain a longer history. Copy the directory after reproducing an issue to guarantee that capture is preserved.

The file writer uses a 1,024-record bounded queue. Module callbacks never wait for the writer. If storage cannot keep up, the next `runtime` record reports the number in `DroppedRecords`.

## Record types

- `probe`: ten-second aggregate for one operation. `Count`, `MeanMs`, and `MaxMs` show execution cost; `MaxGapMs` and `LateStarts` show scheduling stalls; `AllocatedBytes` exposes allocation growth; `Failures` is reserved for explicitly failed measurements.
- `runtime`: process CPU, working/private memory, managed heap size, garbage-collection counts, OS and thread-pool thread counts, pending work, finalizers, and dropped diagnostic records.
- `event`: lifecycle, recovery, OSC input staleness, slow or failed output transport, VRCOSC dispatcher-workaround activation, OpenVR ownership conflicts, and serial queue depth/rates.
- `session`: process and diagnostics writer boundaries.

Null fields are omitted. Times are UTC and numeric durations are milliseconds.

## Reading a degradation capture

Start with OSCLeash `control_loop` records around the first noticeable slowdown:

1. Rising `MaxGapMs` with a stable low `MaxMs` means the callback was scheduled late; correlate the timestamp with process CPU, GC collection jumps, thread-pool backlog, and other modules' probes.
2. Rising `MaxMs` means work inside OSCLeash became slower. Compare `settings_refresh`, `openvr_stage`, and `player_input_stage` at the same timestamp.
3. Rising `AllocatedBytes`, managed heap, collection counts, or private memory identifies allocation or native-resource pressure over time.
4. A growing serial `queue_snapshot` depth or slow audio/voice probes can reveal another module monopolizing work near the stall.
5. Check nearby OSCLeash events for stale OSC input, VRChat input failures, or an external OpenVR pose writer.

Reproduce from a fresh VRCOSC start, note the UTC time when degradation becomes visible, then preserve the entire directory before another long run rotates it.
