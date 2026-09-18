# Leash degradation investigation — 2026-09-04

The local development build's output path could wait on VRCOSC's UI thread for seconds. Its compatibility workaround never activated because it looked up a property that is actually a field. Incoming leash data continued arriving while old movement commands were being published, explaining delayed correction and overshoot.

## Evidence reviewed

- Both retained `crookedtoe-diagnostics.jsonl*` files: 131,406 records, 2026-08-07 03:55 UTC through 2026-09-04 07:26 UTC.
- All nine legacy leash JSONL captures: 1,751,074 records, 7,508,658,549 bytes, no malformed records. These June/July captures predate the current development build and were treated as historical evidence.
- All 1,268 files in the host's retained logs directory, including runtime, terminal and module-debug logs.
- The locally installed `packages/local/CrookedToesModules.dll`, including its compiled compatibility workaround, and the actual installed VRCOSC 2026.807.0 implementation. The repository builds against SDK 2026.501.0.

No private avatar IDs or raw user logs are included in this report.

## Latest reproduced incident

The September 4 UTC session corresponds to the evening of September 3 in America/Denver. The host loaded four local modules; the bounded recorder shows OSCLeash and OSCAudioReaction running. Repeated module restarts occurred within the same process (PID 13384).

| Observation | Evidence |
| --- | --- |
| Leash output blocked | At 05:36:45 UTC, 15 output batches averaged **616.67 ms**, maximum **1,624.12 ms**. |
| Input remained responsive | At 05:36:45 UTC, 829 input callbacks averaged **0.00047 ms**, maximum **0.0249 ms**; maximum arrival gap **84.43 ms**. |
| Audio shared the stall | At 05:36:43 UTC, audio publication peaked at **1,231.32 ms**, while audio frame processing peaked at **0.8444 ms**. |
| No corresponding pool backlog | The 05:36:42 runtime record reports **0** pending thread-pool work, **0.40%** process CPU, and unchanged generation-2 collection count. |
| Restart coincided with recovery | Modules restarted at 05:36:55. The next ten-second leash aggregate peaked at **5.18 ms**, and audio publication at **5.81 ms**. |
| Protection never reported activation | There are no `vrcosc_dispatch_workaround` events anywhere in the retained original diagnostics. |
| Stale timing appeared in health logs | At 05:36:38 UTC, input age was reported as **-0.6 s**, because the log reused a timestamp from before blocking work. |

Across the complete bounded capture, leash control-loop work peaked at 2,205.85 ms and audio publication at 3,088.43 ms. These measurements identify a shared publication stall, not expensive leash direction calculation. They do not identify which UI activity originally made the host slow.

## Defects fixed

1. **Compatibility lookup silently failed.** `AppManager.VRChatOscClient` is a field in the SDK and installed host; the workaround used `GetProperty` only. It now resolves the field, supports a property fallback, and records lookup status even when no new observer needs patching.
2. **OSC sends synchronously waited for the chatbox UI.** The host calls sent observers synchronously; `ChatBoxPreviewView.OnVRChatOSCMessageSent` invokes the dispatcher before checking the packet address. The corrected wrapper filters non-chat packets before dispatch. Other observers remain subscribed.
3. **The dormant workaround accessed WPF state from a worker.** `Window.GetWindow(view)` reads dependency properties and was called from the module thread. That access has been removed.
4. **Preview callbacks could accumulate and retain views.** Replacement subscriptions now hold weak view references, remove dead subscriptions during scanning, and retain at most one queued UI operation and the latest chatbox value per live view.
5. **Module logging also blocked control work.** The host's `Module.Log`/`LogDebug` path invokes UI listeners and writes files synchronously. Leash and audio logging now use one process-wide background worker with 128 pending entries. Full queues drop log messages instead of blocking; `DroppedModuleLogs` reports this separately from dropped diagnostic records. The worker is shared across restarts.
6. **Old pull commands survived a blocked stage.** Leash samples gates and direction together, rechecks input after OpenVR work, and checks release/freshness during each output batch. A stalled or invalidated batch switches to neutral and repairs already-sent channels. Fresh movement resumes on the next publication.
7. **Timing and build diagnostics obscured the problem.** Health timestamps are sampled after work and ages cannot become negative. Each module start records its assembly MVID and host version so later captures can be matched to the exact local build.

## Findings that do not justify additional changes

- The older per-frame tracing wrote roughly 7.5 GB. Current diagnostics already use bounded rotation and a bounded writer queue; legacy capture files were preserved.
- Older captures contain smoothing and gravity settings removed before this investigation. Current direction reversal and deadzone behavior is covered by the existing motion tests; old behavior was not reintroduced.
- All 179 bounded `openvr_failure` records are connection `ReadFailed` results. Connection failures already have a retry interval. The captures contain no external-writer conflict events and no evidence of an accumulating pose-ownership error.
- Historical logs contain failures from old Audio Direction code and other packages. The current active audio callback and frame processing stayed fast during the identified incident. There is no recorded evidence requiring changes to voice-emotion or serial processing for this failure.

## Validation

The 99-test suite passes against both SDK 2026.501.0 and the installed host's VRCOSC.App 2026.807.0 assembly. New regression coverage includes:

- Resolving the actual SDK client field and patching the actual SDK preview handler from a worker thread.
- Sending 100,000 movement observer notifications while the UI thread does not pump: zero UI operations queued.
- Publishing 100,000 preview updates while the UI is stalled: one pending operation, newest value delivered when the UI resumes.
- Garbage-collecting views with pending updates and pruning subscriptions after 100 preview creations.
- Blocking the host logger while 100,000 producer calls finish with a fixed queue bound; later delivery survives a thrown log callback.
- Neutralizing release during a send, stale input, and slow output; resuming on the next fresh publication.

A live, multi-hour VRChat/SteamVR session has not been run with the fix. The tests establish the corrected blocking, lifetime, and stale-command behavior; native calls, UDP loss, and unrelated host UI problems cannot be ruled out by these logs alone.


## Follow-up — 2026-09-06

Reviewed 30,158 additional records from September 5 03:26 UTC through September 6 08:56 UTC: four module runs totaling 8.73 hours. The recorded module MVID is `9fe9e2c9-9db4-448e-a83d-ad6652d438de`, host 2026.807.0. Each host process reported one patched preview observer at startup.

| Operation | Weighted mean | Maximum |
| --- | ---: | ---: |
| Leash movement publication | 0.085 ms | 6.382 ms |
| Leash control loop | 0.024 ms | 27.331 ms |
| Audio parameter publication | 0.109 ms | 17.853 ms |
| OpenVR stage | 0.003 ms | 15.094 ms |

There were zero slow-player-send or slow-audio-publication events, zero dropped diagnostic records, and zero dropped module logs. This supports the reported recovery from the original multi-second output stalls. Incoming OSC silences still occur; those are handled by the existing freshness guard and do not demonstrate a recurrence of the output blockage.

### OVR height-return defect

The previous backend used `GetWorkingStandingZeroPoseToRawTrackingPose` as though it represented the currently active origin. It actually reads the client's working chaperone copy. The checked-in OVR Advanced Settings `MoveCenterTabController` implementation sets its own working standing pose and calls `ShowWorkingSetPreview`. That preview can change the user's active origin without updating the leash client's working copy. Therefore a grab could capture the old floor, and external-writer checks could compare against a stale local copy and miss OVR changes entirely. The absence of external-writer events in the older logs does not establish that no external playspace changes occurred.

The backend now reads `IVRSystem.GetRawZeroPoseToStandingAbsoluteTrackingPose`, validates the rigid transform, and inverts it to the standing-to-raw convention required by chaperone writes. This applies to baseline capture, return, recovery and shutdown checks. No fallback to a stale working copy is used when the live transform is unavailable. A conflict detected during re-grab now enters the same external-writer recovery path as a conflict during pulling.

API conventions were checked against [Valve's OpenVR header](https://github.com/ValveSoftware/openvr/blob/master/headers/openvr.h). The OVR preview-write implementation is in the repository's `OpenVR-AdvancedSettings-master/src/tabcontrollers/MoveCenterTabController.cpp` around lines 2719–2722.

Five additional regression tests exercise the real pose backend with separate active and working poses. They cover inverse rotation/translation, preserving existing OVR drag through an entire release return, OVR takeover during return, takeover before shutdown, and invalid/unavailable tracking transforms. The full suite passes with 104 tests. `height_grab_baseline` and `height_release` events now record the reference height and leash-owned offset for hardware follow-up.

SteamVR was not running during this follow-up, so the new height fix has simulated-runtime validation; an in-headset check remains necessary. The performance confirmation above uses actual subsequent user-session logs.
