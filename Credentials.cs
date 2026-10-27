using System.Text.Json;

namespace PartyScout;

internal sealed record Credentials(string Tomestone = "", string ClientId = "", string ClientSecret = "")
{
    internal bool HasTomestone => Tomestone.Length > 0;
    internal bool HasFFLogs => ClientId.Length > 0 && ClientSecret.Length > 0;
    internal Credentials Validated()
    {
        foreach (var value in new[] { Tomestone, ClientId, ClientSecret })
            if (value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException("Invalid credential format.");
        return this;
    }
    internal static Credentials Import(string path)
    {
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("Unexpected configuration size.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        string Read(string name) => doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
        return new Credentials(Read("TomestoneApiKey"), Read("FFLogsClientId"), Read("FFLogsClientSecret")).Validated();
    }
}
