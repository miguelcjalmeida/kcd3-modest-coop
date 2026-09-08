using System.Text.Json;
using System.Text.Json.Serialization;

namespace ItemSwapAgent;

/// <summary>
/// Agent settings, read from itemswap-agent.json next to the executable.
/// Written with defaults on first run so there's something to edit.
/// </summary>
public sealed class Config
{
    public const string FileName = "itemswap-agent.json";

    /// <summary>"host" or "join".</summary>
    public string Role { get; set; } = "host";

    public string PlayerName { get; set; } = Environment.UserName;

    /// <summary>Host only: the port this agent listens on for joiners.</summary>
    public int ListenPort { get; set; } = 7777;

    /// <summary>Joiner only: "host:port" to connect to. Never point this at the game's own RemoteConsole port (4600) - see the security section in README.md.</summary>
    public string PeerAddress { get; set; } = "";

    /// <summary>
    /// Pre-shared passphrase, hashed before ever going on the wire (see
    /// Protocol.HashSecret). Everyone in the session must use the same one.
    /// </summary>
    public string SharedSecret { get; set; } = "change-me-please";

    /// <summary>
    /// Path to this machine's KCD2 kcd.log. Defaults to the standard retail
    /// install location, but that's just a starting guess - anyone with a
    /// custom Steam library folder needs a different path, which is why
    /// InteractiveSetup asks for this first, before anything else.
    /// </summary>
    public string KcdLogPath { get; set; } =
        @"C:\Program Files (x86)\Steam\steamapps\common\KingdomComeDeliverance2\kcd.log";

    /// <summary>KCD2's RemoteConsole. Always loopback - never point this across the network. See README.md's security section.</summary>
    public string RemoteConsoleHost { get; set; } = "127.0.0.1";
    public int RemoteConsolePort { get; set; } = 4600;

    [JsonIgnore]
    public static string DefaultPath =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, FileName);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Config Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
            {
                var fresh = new Config();
                fresh.Save(path);
                Console.WriteLine($"[config] No config yet - wrote defaults to {path} (the interactive setup below will overwrite them with your real answers).");
                return fresh;
            }

            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Options);
            if (config is null)
            {
                Console.WriteLine($"[config] {path} is empty, using defaults.");
                return new Config();
            }
            Console.WriteLine($"[config] Loaded {path}");
            return config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] Could not read {path} ({ex.Message}); using defaults.");
            return new Config();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, Options)); }
        catch (Exception ex) { Console.WriteLine($"[config] Could not write {path}: {ex.Message}"); }
    }
}
