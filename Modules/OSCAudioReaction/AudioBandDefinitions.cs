namespace CrookedToe.Modules.OSCAudioReaction;

internal readonly record struct AudioBandDefinition(
    string Name,
    string ParameterName,
    string RangeLabel,
    float LowFrequency,
    float HighFrequency,
    float DirectionWeight);

internal static class AudioBandDefinitions
{
    public const int Count = 7;

    public static readonly AudioBandDefinition[] All =
    [
        new("Sub Bass", "audio_subbass", "20-60Hz", 20f, 60f, 0.8f),
        new("Bass", "audio_bass", "60-250Hz", 60f, 250f, 1.0f),
        new("Low Mid", "audio_lowmid", "250-500Hz", 250f, 500f, 1.2f),
        new("Mid", "audio_mid", "500-2000Hz", 500f, 2000f, 1.5f),
        new("Upper Mid", "audio_uppermid", "2000-4000Hz", 2000f, 4000f, 1.3f),
        new("Presence", "audio_presence", "4000-6000Hz", 4000f, 6000f, 1.1f),
        new("Brilliance", "audio_brilliance", "6000-20000Hz", 6000f, 20000f, 0.9f)
    ];

}
