namespace ItemSwapAgent;

/// <summary>
/// Prompts for the handful of settings that actually differ between
/// sessions (host/join, name, port/peer address, shared secret), pre-filled
/// with whatever was used last time so hitting Enter repeats it. Skipped
/// entirely when stdin isn't a real interactive console (piped/redirected -
/// e.g. future scripted/automated runs), in which case the loaded config
/// file is used as-is with no prompts.
/// </summary>
public static class InteractiveSetup
{
    public static Config Run(Config existing)
    {
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("[setup] stdin is not interactive - skipping prompts, using itemswap-agent.json as-is.");
            return existing;
        }

        Console.WriteLine("=== ItemSwap setup - press Enter on any question to keep the [default] ===");

        // Asked first and separately from everything else: the default
        // assumes the standard Steam install location, which is wrong for
        // anyone with a custom Steam library folder (a second drive, a
        // custom path chosen at install time, etc) - get this wrong and
        // every other feature silently does nothing, since the agent can
        // never see the game's own log output.
        var kcdLogPath = AskPath(
            "Path to your KCD2 kcd.log (adjust if your Steam library isn't in the default location)",
            existing.KcdLogPath);

        var role = AskChoice("Host or join?", existing.Role == "join" ? "join" : "host", "host", "join");
        var name = Ask("Your name", string.IsNullOrWhiteSpace(existing.PlayerName) ? Environment.UserName : existing.PlayerName);

        var config = new Config
        {
            Role = role,
            PlayerName = name,
            SharedSecret = Ask("Shared secret (must match everyone else's exactly)", existing.SharedSecret),
            KcdLogPath = kcdLogPath,
            RemoteConsoleHost = existing.RemoteConsoleHost,
            RemoteConsolePort = existing.RemoteConsolePort,
        };

        if (role == "host")
        {
            config.ListenPort = AskInt("Port to listen on (share this + your address with your friends)", existing.ListenPort);
            config.PeerAddress = existing.PeerAddress;
        }
        else
        {
            var defaultPeer = string.IsNullOrWhiteSpace(existing.PeerAddress) ? "" : existing.PeerAddress;
            string peer;
            while (true)
            {
                peer = Ask("Host address to join (ip:port, from whoever is hosting)", defaultPeer);
                if (!string.IsNullOrWhiteSpace(peer) && peer.Contains(':')) break;
                Console.WriteLine("  Please enter it as ip:port, e.g. 192.168.1.10:7777");
            }
            config.PeerAddress = peer;
            config.ListenPort = existing.ListenPort;
        }

        config.Save();
        Console.WriteLine($"[setup] Saved to {Config.DefaultPath}");
        Console.WriteLine();
        return config;
    }

    private static string Ask(string prompt, string defaultValue)
    {
        Console.Write(string.IsNullOrEmpty(defaultValue) ? $"{prompt}: " : $"{prompt} [{defaultValue}]: ");
        var input = Console.ReadLine();
        return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
    }

    /// <summary>
    /// Like Ask, but warns (without blocking) if the resulting path doesn't
    /// exist - could be a genuine typo, or just that the game hasn't been
    /// launched with -devmode yet to create the log file, so it isn't worth
    /// hard-failing over.
    /// </summary>
    private static string AskPath(string prompt, string defaultValue)
    {
        var path = Ask(prompt, defaultValue);
        if (!File.Exists(path))
        {
            Console.WriteLine($"  Note: {path} doesn't exist yet. That's fine if you haven't launched " +
                "the game with -devmode yet - otherwise, double-check the path (e.g. a custom Steam library location).");
        }
        return path;
    }

    private static int AskInt(string prompt, int defaultValue)
    {
        while (true)
        {
            var raw = Ask(prompt, defaultValue.ToString());
            if (int.TryParse(raw, out var value)) return value;
            Console.WriteLine("  Please enter a number.");
        }
    }

    private static string AskChoice(string prompt, string defaultValue, params string[] choices)
    {
        while (true)
        {
            var raw = Ask($"{prompt} ({string.Join("/", choices)})", defaultValue).ToLowerInvariant();
            if (choices.Contains(raw)) return raw;
            Console.WriteLine($"  Please enter one of: {string.Join(", ", choices)}");
        }
    }
}
