using Dalamud.Configuration;
using PartyScout.Core;

namespace PartyScout;

[Serializable]
internal sealed class Configuration : Settings, IPluginConfiguration
{
    internal const int CurrentVersion = 6;
    public int Version { get; set; }
    public string TomestoneApiKey { get; set; } = "";
    public string FFLogsClientId { get; set; } = "";
    public string FFLogsClientSecret { get; set; } = "";
}
