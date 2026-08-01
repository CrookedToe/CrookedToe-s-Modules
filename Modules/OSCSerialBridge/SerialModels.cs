using Newtonsoft.Json;

namespace CrookedToe.Modules.OSCSerialBridge;

public enum SerialBridgeSetting
{
    Devices,
    Mappings,
    IncomingRoutes
}

[JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
public enum SerialValueType
{
    Bool,
    Float,
    Int
}

internal readonly record struct SerialDeviceSnapshot(string Name, string PortName, int BaudRate, bool Enabled);

internal readonly record struct SerialMappingSnapshot(
    string Name,
    string SourceName,
    string AvatarParameterName,
    string DeviceNameFilter)
{
    public bool Matches(string deviceName, ParsedSerialPacket packet)
    {
        if (!SourceName.Equals(packet.Name, StringComparison.OrdinalIgnoreCase)) return false;

        return string.IsNullOrWhiteSpace(DeviceNameFilter) ||
               DeviceNameFilter.Equals(deviceName, StringComparison.OrdinalIgnoreCase);
    }
}

internal readonly record struct QueuedSerialLine(string DeviceName, string Line);

internal readonly record struct SerialIncomingRouteSnapshot(
    string Name,
    string AvatarParameterName,
    string DeviceName);

internal readonly record struct ParsedSerialPacket(
    string Name,
    SerialValueType ValueType,
    bool BoolValue,
    int IntValue,
    float FloatValue)
{
    public static ParsedSerialPacket ForBool(string name, bool value) =>
        new(name, SerialValueType.Bool, value, default, default);

    public static ParsedSerialPacket ForFloat(string name, float value) =>
        new(name, SerialValueType.Float, default, default, value);

    public static ParsedSerialPacket ForInt(string name, int value) =>
        new(name, SerialValueType.Int, default, value, default);
}
