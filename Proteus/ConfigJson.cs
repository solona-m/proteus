using Newtonsoft.Json;

namespace Proteus;

/// <summary>
/// The settings text, made exactly as Dalamud's <c>PluginConfigurations.SerializeConfig</c> makes it (that method is
/// internal): indented, <c>$type</c> on objects, simple assembly names. The background writer
/// (<see cref="ConfigSaveScheduler"/>) puts this text in the same file <c>SavePluginConfig</c> writes, and Dalamud's
/// <c>LoadForType</c> reads it back with no type handling.
/// </summary>
internal static class ConfigJson
{
    internal static readonly JsonSerializerSettings Settings = new()
    {
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        TypeNameHandling = TypeNameHandling.Objects,
    };

    public static string Serialize(object config) => JsonConvert.SerializeObject(config, Formatting.Indented, Settings);
}
