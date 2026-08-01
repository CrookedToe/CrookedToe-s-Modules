using CrookedToe.Modules.OSCSerialBridge.UI;
using Newtonsoft.Json;
using VRCOSC.App.SDK.Modules.Attributes.Settings;
using VRCOSC.App.Utils;

namespace CrookedToe.Modules.OSCSerialBridge;

public class SerialDeviceModuleSetting : ListModuleSetting<SerialDeviceConfig>
{
    public SerialDeviceModuleSetting()
        : base("Serial Devices", "Configure the COM devices this module should listen to", typeof(SerialDeviceModuleSettingView), [])
    {
    }

    protected override SerialDeviceConfig CreateItem() => new();
}

[JsonObject(MemberSerialization.OptIn)]
public class SerialDeviceConfig : IEquatable<SerialDeviceConfig>
{
    [JsonProperty("id")]
    public string ID { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("name")]
    public Observable<string> Name { get; set; } = new("New Device");

    [JsonProperty("port_name")]
    public Observable<string> PortName { get; set; } = new(string.Empty);

    [JsonProperty("baud_rate")]
    public Observable<int> BaudRate { get; set; } = new(115200);

    [JsonProperty("enabled")]
    public Observable<bool> Enabled { get; set; } = new(true);

    [JsonConstructor]
    public SerialDeviceConfig()
    {
    }

    public bool Equals(SerialDeviceConfig? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;

        return Name.Equals(other.Name) &&
               PortName.Equals(other.PortName) &&
               BaudRate.Equals(other.BaudRate) &&
               Enabled.Equals(other.Enabled);
    }

    public override bool Equals(object? obj) => obj is SerialDeviceConfig other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Name, PortName, BaudRate, Enabled);
}

public class SerialIncomingRouteModuleSetting : ListModuleSetting<SerialIncomingRoute>
{
    public SerialIncomingRouteModuleSetting()
        : base("Incoming Routes", "Map incoming avatar parameters back to serial devices", typeof(SerialIncomingRouteModuleSettingView), [])
    {
    }

    protected override SerialIncomingRoute CreateItem() => new();
}

[JsonObject(MemberSerialization.OptIn)]
public class SerialIncomingRoute : IEquatable<SerialIncomingRoute>
{
    [JsonProperty("id")]
    public string ID { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("name")]
    public Observable<string> Name { get; set; } = new("New Incoming Route");

    [JsonProperty("avatar_parameter_name")]
    public Observable<string> AvatarParameterName { get; set; } = new(string.Empty);

    [JsonProperty("device_name")]
    public Observable<string> DeviceName { get; set; } = new(string.Empty);

    [JsonProperty("enabled")]
    public Observable<bool> Enabled { get; set; } = new(true);

    [JsonConstructor]
    public SerialIncomingRoute()
    {
    }

    public bool Equals(SerialIncomingRoute? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;

        return Name.Equals(other.Name) &&
               AvatarParameterName.Equals(other.AvatarParameterName) &&
               DeviceName.Equals(other.DeviceName) &&
               Enabled.Equals(other.Enabled);
    }

    public override bool Equals(object? obj) => obj is SerialIncomingRoute other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Name, AvatarParameterName, DeviceName, Enabled);
}

public class SerialMappingModuleSetting : ListModuleSetting<SerialParameterMapping>
{
    public SerialMappingModuleSetting()
        : base("Parameter Mappings", "Map incoming serial packet names to avatar parameters", typeof(SerialMappingModuleSettingView), [])
    {
    }

    protected override SerialParameterMapping CreateItem() => new();
}

[JsonObject(MemberSerialization.OptIn)]
public class SerialParameterMapping : IEquatable<SerialParameterMapping>
{
    [JsonProperty("id")]
    public string ID { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("name")]
    public Observable<string> Name { get; set; } = new("New Mapping");

    [JsonProperty("source_name")]
    public Observable<string> SourceName { get; set; } = new(string.Empty);

    [JsonProperty("avatar_parameter_name")]
    public Observable<string> AvatarParameterName { get; set; } = new(string.Empty);

    [JsonProperty("device_name_filter")]
    public Observable<string> DeviceNameFilter { get; set; } = new(string.Empty);

    [JsonProperty("enabled")]
    public Observable<bool> Enabled { get; set; } = new(true);

    [JsonConstructor]
    public SerialParameterMapping()
    {
    }

    public bool Equals(SerialParameterMapping? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;

        return Name.Equals(other.Name) &&
               SourceName.Equals(other.SourceName) &&
               AvatarParameterName.Equals(other.AvatarParameterName) &&
               DeviceNameFilter.Equals(other.DeviceNameFilter) &&
               Enabled.Equals(other.Enabled);
    }

    public override bool Equals(object? obj) => obj is SerialParameterMapping other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Name, SourceName, AvatarParameterName, Enabled);
}
