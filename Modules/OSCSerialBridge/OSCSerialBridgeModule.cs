using System.Collections.Concurrent;
using System.Globalization;
using VRCOSC.App.SDK.Modules;
using VRCOSC.App.Utils;

namespace CrookedToe.Modules.OSCSerialBridge;

[ModuleTitle("OSC Serial Bridge")]
[ModuleDescription("Routes formatted serial packets from multiple COM devices into VRChat avatar parameters")]
[ModuleType(ModuleType.Generic)]
public class OSCSerialBridgeModule : Module
{
    private const int MaxPacketsPerTick = 250;

    private readonly ConcurrentQueue<QueuedSerialLine> queuedLines = new();
    private readonly HashSet<string> loggedErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> lastIncomingPackets = new(StringComparer.Ordinal);

    private SerialDeviceManager? deviceManager;
    private List<SerialMappingSnapshot> activeMappings = [];
    private List<SerialIncomingRouteSnapshot> activeIncomingRoutes = [];
    private volatile bool _isStopping;

    public SerialDeviceModuleSetting DevicesSetting => GetSetting<SerialDeviceModuleSetting>(SerialBridgeSetting.Devices);
    public SerialMappingModuleSetting MappingsSetting => GetSetting<SerialMappingModuleSetting>(SerialBridgeSetting.Mappings);
    public SerialIncomingRouteModuleSetting IncomingRoutesSetting => GetSetting<SerialIncomingRouteModuleSetting>(SerialBridgeSetting.IncomingRoutes);

    protected override void OnPreLoad()
    {
        CreateCustomSetting(SerialBridgeSetting.Devices, new SerialDeviceModuleSetting());
        CreateCustomSetting(SerialBridgeSetting.Mappings, new SerialMappingModuleSetting());
        CreateCustomSetting(SerialBridgeSetting.IncomingRoutes, new SerialIncomingRouteModuleSetting());

        CreateGroup("Devices", "Configure the serial ports this module should listen to", SerialBridgeSetting.Devices);
        CreateGroup("Mappings", "Map serial packet names to avatar parameters", SerialBridgeSetting.Mappings);
        CreateGroup("Incoming Routes", "Send incoming avatar parameters back to serial devices", SerialBridgeSetting.IncomingRoutes);
    }

    protected override Task<bool> OnModuleStart()
    {
        _isStopping = false;
        Log("Starting OSC Serial Bridge module...");

        deviceManager = new SerialDeviceManager(
            line => queuedLines.Enqueue(line),
            message => Log(message),
            LogErrorOnce);

        RebuildRuntimeConfiguration();
        Log("OSC Serial Bridge module started");
        return Task.FromResult(true);
    }

    protected override Task OnModuleStop()
    {
        Log("Stopping OSC Serial Bridge module...");

        _isStopping = true;

        deviceManager?.Dispose();
        deviceManager = null;
        activeMappings = [];
        activeIncomingRoutes = [];
        lastIncomingPackets.Clear();
        loggedErrors.Clear();

        while (queuedLines.TryDequeue(out _))
        {
        }

        Log("OSC Serial Bridge module stopped");
        return Task.CompletedTask;
    }

    [ModuleUpdate(ModuleUpdateMode.Custom, true, 500)]
    private void RefreshConfiguration()
    {
        if (_isStopping)
            return;

        RebuildRuntimeConfiguration();
    }

    [ModuleUpdate(ModuleUpdateMode.Custom, true, 20)]
    private void ProcessQueuedLines()
    {
        if (_isStopping)
            return;

        int processedPackets = 0;

        while (processedPackets < MaxPacketsPerTick && queuedLines.TryDequeue(out var queuedLine))
        {
            processedPackets++;
            ProcessQueuedLine(queuedLine);
        }
    }

    private void RebuildRuntimeConfiguration()
    {
        if (_isStopping)
            return;

        var enabledDevices = GetSettingValue<List<SerialDeviceConfig>>(SerialBridgeSetting.Devices)
            .Where(device => !string.IsNullOrWhiteSpace(device.Name.Value) && !string.IsNullOrWhiteSpace(device.PortName.Value))
            .Select(device => new SerialDeviceSnapshot(
                Sanitize(device.Name.Value, "Unknown Device"),
                device.PortName.Value.Trim(),
                Math.Max(1, device.BaudRate.Value),
                device.Enabled.Value))
            .ToList();

        activeMappings = GetSettingValue<List<SerialParameterMapping>>(SerialBridgeSetting.Mappings)
            .Where(mapping => mapping.Enabled.Value &&
                              !string.IsNullOrWhiteSpace(mapping.SourceName.Value) &&
                              !string.IsNullOrWhiteSpace(mapping.AvatarParameterName.Value))
            .Select(mapping => new SerialMappingSnapshot(
                Sanitize(mapping.Name.Value, mapping.SourceName.Value),
                mapping.SourceName.Value.Trim(),
                mapping.AvatarParameterName.Value.Trim(),
                mapping.DeviceNameFilter.Value.Trim()))
            .ToList();

        activeIncomingRoutes = GetSettingValue<List<SerialIncomingRoute>>(SerialBridgeSetting.IncomingRoutes)
            .Where(route => route.Enabled.Value &&
                            !string.IsNullOrWhiteSpace(route.AvatarParameterName.Value) &&
                            !string.IsNullOrWhiteSpace(route.DeviceName.Value))
            .Select(route => new SerialIncomingRouteSnapshot(
                Sanitize(route.Name.Value, route.AvatarParameterName.Value),
                route.AvatarParameterName.Value.Trim(),
                route.DeviceName.Value.Trim()))
            .ToList();

        deviceManager?.ApplyConfiguration(enabledDevices);
    }

    [ModuleUpdate(ModuleUpdateMode.Custom, true, 50)]
    private void ProcessIncomingRoutes()
    {
        if (_isStopping)
            return;

        foreach (var route in activeIncomingRoutes)
        {
            var parameter = FindParameter(route.AvatarParameterName);
            if (parameter == null) continue;

            SendIncomingParameterToDevice(route, parameter);
        }
    }

    private void ProcessQueuedLine(QueuedSerialLine queuedLine)
    {
        if (_isStopping)
            return;

        if (!BracePacketParser.TryParse(queuedLine.Line, out var packet, out var error))
        {
            LogErrorOnce(
                $"parse:{queuedLine.DeviceName}:{error}",
                $"Ignored invalid packet from '{queuedLine.DeviceName}': {error}. Raw: {queuedLine.Line}");
            return;
        }

        bool matchedAnyMapping = false;

        foreach (var mapping in activeMappings)
        {
            if (!mapping.Matches(queuedLine.DeviceName, packet)) continue;

            matchedAnyMapping = true;
            SendMappedPacket(mapping, packet);
        }

        if (!matchedAnyMapping)
        {
            LogErrorOnce(
                $"unmapped:{queuedLine.DeviceName}:{packet.Name}:{packet.ValueType}",
                $"No mapping matched packet '{packet.Name}' ({packet.ValueType}) from '{queuedLine.DeviceName}'");
        }
    }

    private void SendMappedPacket(SerialMappingSnapshot mapping, ParsedSerialPacket packet)
    {
        if (_isStopping)
            return;

        try
        {
            switch (packet.ValueType)
            {
                case SerialValueType.Bool:
                    SendParameter(mapping.AvatarParameterName, packet.BoolValue);
                    return;

                case SerialValueType.Float:
                    SendParameter(mapping.AvatarParameterName, packet.FloatValue);
                    return;

                case SerialValueType.Int:
                    SendParameter(mapping.AvatarParameterName, packet.IntValue);
                    return;
            }
        }
        catch (Exception ex)
        {
            LogErrorOnce(
                $"send:{mapping.AvatarParameterName}:{packet.ValueType}",
                $"Failed to send parameter '{mapping.AvatarParameterName}' from mapping '{mapping.Name}': {ex.Message}");
        }
    }

    private void SendIncomingParameterToDevice(SerialIncomingRouteSnapshot route, object parameter)
    {
        if (_isStopping)
            return;

        string? packetLine = TryBuildSerialPacket(route.AvatarParameterName, parameter);
        if (string.IsNullOrWhiteSpace(packetLine)) return;

        var cacheKey = $"{route.DeviceName}|{route.AvatarParameterName}";
        if (lastIncomingPackets.TryGetValue(cacheKey, out var previousPacket) && previousPacket == packetLine)
            return;

        if (deviceManager?.TryWriteToDevice(route.DeviceName, packetLine) == true)
        {
            lastIncomingPackets[cacheKey] = packetLine;
            return;
        }

        LogErrorOnce(
            $"incoming-route:{route.DeviceName}:{route.AvatarParameterName}",
            $"Failed to send incoming parameter '{route.AvatarParameterName}' to serial device '{route.DeviceName}'");
    }

    private string? TryBuildSerialPacket(string parameterName, object parameter)
    {
        if (TryGetParameterValueType(parameter, out var valueType))
        {
            return TryBuildSerialPacket(parameterName, parameter, valueType);
        }

        if (TryBuildFallbackSerialPacket(parameterName, parameter, out var packetLine))
        {
            return packetLine;
        }

        LogErrorOnce(
            $"incoming-type:{parameterName}",
            $"Incoming parameter '{parameterName}' could not be converted to bool, int, or float for serial output");
        return null;
    }

    private string? TryBuildSerialPacket(string parameterName, object parameter, SerialValueType valueType)
    {
        return valueType switch
        {
            SerialValueType.Bool when TryGetParameterValue(parameter, out bool boolValue) =>
                $"{{{parameterName}:bool:{boolValue.ToString().ToLowerInvariant()}}}",
            SerialValueType.Int when TryGetParameterValue(parameter, out int intValue) =>
                $"{{{parameterName}:int:{intValue}}}",
            SerialValueType.Float when TryGetParameterValue(parameter, out float floatValue) =>
                $"{{{parameterName}:float:{floatValue.ToString(CultureInfo.InvariantCulture)}}}",
            _ => null
        };
    }

    private static bool TryBuildFallbackSerialPacket(string parameterName, object parameter, out string? packetLine)
    {
        foreach (var valueType in new[] { SerialValueType.Bool, SerialValueType.Int, SerialValueType.Float })
        {
            packetLine = TryBuildFallbackSerialPacket(parameterName, parameter, valueType);
            if (packetLine != null)
                return true;
        }

        packetLine = null;
        return false;
    }

    private static string? TryBuildFallbackSerialPacket(string parameterName, object parameter, SerialValueType valueType)
    {
        return valueType switch
        {
            SerialValueType.Bool when TryGetParameterValue(parameter, out bool boolValue) =>
                $"{{{parameterName}:bool:{boolValue.ToString().ToLowerInvariant()}}}",
            SerialValueType.Int when TryGetParameterValue(parameter, out int intValue) =>
                $"{{{parameterName}:int:{intValue}}}",
            SerialValueType.Float when TryGetParameterValue(parameter, out float floatValue) =>
                $"{{{parameterName}:float:{floatValue.ToString(CultureInfo.InvariantCulture)}}}",
            _ => null
        };
    }

    private static bool TryGetParameterValueType(object parameter, out SerialValueType valueType)
    {
        foreach (var propertyName in new[] { "Type", "ValueType", "ParameterType" })
        {
            var property = parameter.GetType().GetProperty(propertyName);
            var rawValue = property?.GetValue(parameter);
            if (rawValue != null && TryMapParameterType(rawValue, out valueType))
                return true;
        }

        valueType = default;
        return false;
    }

    private static bool TryMapParameterType(object rawValue, out SerialValueType valueType)
    {
        switch (rawValue)
        {
            case Type type when type == typeof(bool):
                valueType = SerialValueType.Bool;
                return true;
            case Type type when type == typeof(int):
                valueType = SerialValueType.Int;
                return true;
            case Type type when type == typeof(float):
                valueType = SerialValueType.Float;
                return true;
        }

        var text = rawValue.ToString();
        if (text != null)
        {
            if (Enum.TryParse(text, true, out valueType))
                return true;

            switch (text.ToLowerInvariant())
            {
                case "boolean":
                case "bool":
                    valueType = SerialValueType.Bool;
                    return true;
                case "single":
                case "float":
                    valueType = SerialValueType.Float;
                    return true;
                case "int32":
                case "integer":
                case "int":
                    valueType = SerialValueType.Int;
                    return true;
            }
        }

        valueType = default;
        return false;
    }

    private static bool TryGetParameterValue<T>(object parameter, out T value)
    {
        try
        {
            value = ((dynamic)parameter).GetValue<T>();
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }

    private void LogErrorOnce(string key, string message)
    {
        if (!loggedErrors.Add(key)) return;
        Log(message);
    }

    private static string Sanitize(string preferredValue, string fallbackValue) =>
        string.IsNullOrWhiteSpace(preferredValue) ? fallbackValue.Trim() : preferredValue.Trim();
}
