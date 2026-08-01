using System.Globalization;

namespace CrookedToe.Modules.OSCSerialBridge;

internal static class BracePacketParser
{
    public static bool TryParse(string line, out ParsedSerialPacket packet, out string error)
    {
        packet = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            error = "Packet was empty";
            return false;
        }

        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            error = "Packet must start with '{' and end with '}'";
            return false;
        }

        var content = trimmed[1..^1].Trim();
        var parts = content.Split(':', 3);
        if (parts.Length != 3)
        {
            error = "Packet must match {name:type:value}";
            return false;
        }

        var name = parts[0].Trim();
        var typeText = parts[1].Trim();
        var valueText = parts[2].Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Packet name was empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(valueText))
        {
            error = "Packet value was empty";
            return false;
        }

        if (!TryParseValueType(typeText, out var valueType))
        {
            error = $"Unsupported packet type '{typeText}'";
            return false;
        }

        return valueType switch
        {
            SerialValueType.Bool => TryParseBool(name, valueText, out packet, out error),
            SerialValueType.Float => TryParseFloat(name, valueText, out packet, out error),
            SerialValueType.Int => TryParseInt(name, valueText, out packet, out error),
            _ => Fail($"Unsupported packet type '{typeText}'", out packet, out error)
        };
    }

    private static bool TryParseValueType(string value, out SerialValueType valueType) =>
        Enum.TryParse(value, true, out valueType);

    private static bool TryParseBool(string name, string valueText, out ParsedSerialPacket packet, out string error)
    {
        if (!bool.TryParse(valueText, out var value))
            return Fail($"Invalid bool value '{valueText}'", out packet, out error);

        packet = ParsedSerialPacket.ForBool(name, value);
        error = string.Empty;
        return true;
    }

    private static bool TryParseFloat(string name, string valueText, out ParsedSerialPacket packet, out string error)
    {
        if (!float.TryParse(valueText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
            return Fail($"Invalid float value '{valueText}'", out packet, out error);

        packet = ParsedSerialPacket.ForFloat(name, value);
        error = string.Empty;
        return true;
    }

    private static bool TryParseInt(string name, string valueText, out ParsedSerialPacket packet, out string error)
    {
        if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return Fail($"Invalid int value '{valueText}'", out packet, out error);

        packet = ParsedSerialPacket.ForInt(name, value);
        error = string.Empty;
        return true;
    }

    private static bool Fail(string errorMessage, out ParsedSerialPacket packet, out string error)
    {
        packet = default;
        error = errorMessage;
        return false;
    }
}
