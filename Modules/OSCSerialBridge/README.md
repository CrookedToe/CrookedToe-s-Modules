# OSC Serial Bridge

`OSC Serial Bridge` is a VRCOSC module that listens to one or more serial devices and forwards formatted packets to avatar parameters in VRChat.

It is designed for microcontrollers such as ESP32s and Arduinos that can write newline-delimited packets over a COM port.

## What It Does

- Lets you configure a dynamic list of serial devices in the module settings
- Lets you configure a dynamic list of packet-to-parameter mappings
- Lets you configure avatar-parameter-to-serial routes for sending data back to a device
- Supports `bool`, `float`, and `int` packet values
- Routes packets to avatar parameters using an alias map instead of requiring the packet name to match the avatar parameter directly

## Packet Format

Each serial packet must be sent on its own line using this format:

```text
{name:type:value}
```

Examples:

```text
{Jump:bool:true}
{VisemeStrength:float:0.42}
{Mode:int:2}
```

Rules:

- Packets must start with `{` and end with `}`
- `name` is the source name used by your mapping
- `type` must be `bool`, `float`, or `int`
- `value` must parse using invariant formatting
- Each packet must be newline-delimited so the serial reader can split it cleanly

## Arduino Example

```cpp
#include <Arduino.h>

namespace {
constexpr unsigned long REPORT_INTERVAL_MS = 100;
float testValue = 0.0f;
}

void setup() {
  Serial.begin(115200);
}

void loop() {
  Serial.printf("{test:float:%.3f}\r\n", testValue);

  testValue += 0.01f;
  if (testValue > 0.9f) {
    testValue = 0.0f;
  }

  delay(REPORT_INTERVAL_MS);
}
```

## Setup

### 1. Add Devices

In the `Serial Devices` setting:

- Give the device a friendly name
- Select the COM port
- Enter the baud rate, for example `115200`
- Enable the device

### 2. Add Mappings

In the `Parameter Mappings` setting:

- Set a friendly mapping name
- Set `Source Name` to the packet name your device sends
- Set `Avatar Parameter` to the exact VRChat avatar parameter name
- Optionally choose one or more allowed devices

If the allowed device list is empty, the mapping accepts packets from any configured device.
The packet type is inferred automatically from the serial packet itself.

### 3. Add Incoming Routes

In the `Incoming Routes` setting:

- Set a friendly route name
- Enter the incoming avatar parameter name you want to watch
- Select which configured serial device should receive it

When that avatar parameter changes, the module sends it to the selected device using the same packet format:

```text
{parameterName:type:value}
```

## Routing Behavior

- Packets are only forwarded when they match an enabled mapping
- Incoming avatar parameters can also be forwarded back to a specific serial device
- Mapping source names are matched case-insensitively
- Avatar parameter names are sent exactly as entered
- Every matched packet is forwarded immediately without deduplication
- Repeated errors are only logged once per module run

## Notes

- This module uses direct parameter sending so mappings can stay fully dynamic
- Because avatar parameter names are direct strings, they must match your avatar exactly
- Device and mapping changes are picked up while the module is running
- Serial writeback uses the same `{name:type:value}` format as incoming device packets

## Troubleshooting

- No response:
  - Verify the selected COM port and baud rate
  - Verify the device is sending newline-delimited packets
  - Verify the avatar parameter name matches exactly
- Packets are ignored:
  - Make sure the packet matches `{name:type:value}`
  - Make sure the mapping is enabled
  - Make sure the mapping allows the sending device
- Wrong values:
  - Use `.` as the decimal separator for floats
  - Make sure `bool` packets use `true` or `false`
