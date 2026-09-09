using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Core;

namespace AutoRestart;

// An entry in plugins.json (desired state, written by the user).
public class PluginEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    // Optional pin. When set, this exact tag is wanted instead of the latest.
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

// An entry in .plugin-state.json (installed state, written by the installer).
public class StateEntry
{
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";
}

public class PluginConfig
{
    public bool Enabled { get; set; } = true;
    public float CheckIntervalSeconds { get; set; } = 300.0f; // 5 minutes
    public float QuitIntervalSeconds { get; set; } = 60.0f; // 1 minute
    public string SteamApiEndpoint { get; set; } = "https://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={0}";
    public int ScheduledRestartHour { get; set; } = 5; // 5 AM local time
    public string PluginsJsonPath { get; set; } = "/server-config/plugins.json";
    public string PluginStatePath { get; set; } = "/home/steam/cs2/.plugin-state.json";
}

[PluginMetadata(
    Id = "AutoRestart",
    #if WORKFLOW
        Version = "WORKFLOW_VERSION",
    #else
        Version = "Local",
    #endif
    Name = "AutoRestart",
    Author = "Praetor",
    Description = "Auto Restart for Counter-Strike 2"
)]
public class AutoRestart : BasePlugin {
    private delegate Task<(bool Triggered, string Reason)> RestartCondition();

    private ServiceProvider? m_provider;

    private static readonly HttpClient m_http_client = new();
    private PluginConfig m_config = new();
    private CancellationTokenSource? m_check_timer_token;
    private string? m_current_version = null;
    private readonly List<RestartCondition> m_restart_conditions = new();
    private List<PluginEntry> m_plugins = new();
    private Dictionary<string, string> m_installed = new();
    private readonly Dictionary<string, string> m_etags = new();

    public AutoRestart(ISwiftlyCore core) : base(core) {
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) {
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager) {
    }

    private void InitializeConfiguration() {
        Core.Configuration
            .InitializeJsonWithModel<PluginConfig>("config.jsonc", "Main")
            .Configure(builder => {
                builder.AddJsonFile("config.jsonc", optional: false, reloadOnChange: true);
            });
    }

    private void InitializeDependencyInjection() {
        ServiceCollection services = new();
        services.AddSwiftly(Core);
        services.AddOptionsWithValidateOnStart<PluginConfig>().BindConfiguration("Main");

        m_provider = services.BuildServiceProvider();
        m_config = m_provider.GetRequiredService<IOptions<PluginConfig>>().Value;
    }

    public override void Load(bool hotReload) {
        InitializeConfiguration();
        InitializeDependencyInjection();

        GetSteamInfPatchVersion();
        LoadPlugins();

        m_restart_conditions.Add(CheckScheduledRestart);
        m_restart_conditions.Add(CheckSteamUpdate);
        m_restart_conditions.Add(CheckPluginUpdates);

        m_check_timer_token = Core.Scheduler.DelayAndRepeatBySeconds(m_config.CheckIntervalSeconds, m_config.CheckIntervalSeconds, OnCheckTimer);

        string configPath = Core.Configuration.GetConfigPath("config.jsonc");
        if (!File.Exists(configPath)) {
            Core.Logger.LogError($"config.jsonc not found at {configPath}");
            return;
        }

        File.WriteAllText("/tmp/autorestart_loaded", "");
    }

    public override void Unload() {
        m_check_timer_token?.Cancel();
        m_check_timer_token = null;
        m_restart_conditions.Clear();
        m_plugins.Clear();
        m_installed.Clear();
        m_etags.Clear();
        m_provider?.Dispose();
    }

    private void OnCheckTimer() {
        Task.Run(async () => {
            foreach (var condition in m_restart_conditions) {
                var (triggered, reason) = await condition();
                if (triggered) {
                    Core.Scheduler.NextTick(() => ScheduleQuit(reason));
                    return;
                }
            }
        });
    }

    private void ScheduleQuit(string reason) {
        m_check_timer_token?.Cancel();
        Core.Logger.LogInformation($"Quit scheduled — {reason}");
        m_check_timer_token = Core.Scheduler.RepeatBySeconds(m_config.QuitIntervalSeconds, () => {
            if (Core.PlayerManager.PlayerCount == 0) {
                Core.Logger.LogInformation("No players online, quitting now.");
                Core.Engine.ExecuteCommand("quit");
            }
        });
    }

    private Task<(bool Triggered, string Reason)> CheckScheduledRestart() {
        bool triggered = DateTime.Now.Hour == m_config.ScheduledRestartHour;
        return Task.FromResult((triggered, "Scheduled restart"));
    }

    private async Task<(bool Triggered, string Reason)> CheckSteamUpdate() {
        try {
            var response = await m_http_client.GetStringAsync(string.Format(m_config.SteamApiEndpoint, m_current_version));
            using var doc = JsonDocument.Parse(response);

            var responseObj = doc.RootElement.GetProperty("response");

            if (!responseObj.GetProperty("success").GetBoolean()) {
                Core.Logger.LogWarning("Steam API returned success=false");
                return (false, "");
            }

            bool updateAvailable = !responseObj.GetProperty("up_to_date").GetBoolean();
            return (updateAvailable, "CS2 update");
        }
        catch (Exception ex) {
            Core.Logger.LogWarning(ex, "Error checking for Steam update");
        }
        return (false, "");
    }

    private async Task<(bool Triggered, string Reason)> CheckPluginUpdates() {
        // Re-read both files every check so edits to plugins.json, such as a new
        // pin, are noticed without a restart.
        LoadPlugins();
        if (m_plugins.Count == 0) return (false, "");

        string? token = Environment.GetEnvironmentVariable("GITHUB_APIKEY");

        foreach (var plugin in m_plugins) {
            m_installed.TryGetValue(plugin.Name, out string? installed);

            // Pinned plugins want one specific tag, so there is nothing to ask
            // GitHub. A mismatch means the installer has not applied the pin yet.
            if (!string.IsNullOrEmpty(plugin.Version)) {
                if (plugin.Version != installed) {
                    Core.Logger.LogInformation($"Plugin {plugin.Name} pinned to {plugin.Version}, installed {installed ?? "nothing"}");
                    return (true, "Plugin pin changed");
                }
                continue;
            }

            try {
                m_etags.TryGetValue(plugin.Name, out string? etag);
                var result = await FetchGitHubLatestTag(plugin.Name, token, etag);
                if (result == null) continue; // not modified or nothing usable
                var (latestTag, newEtag) = result.Value;
                m_etags[plugin.Name] = newEtag;
                if (latestTag.Contains("beta")) continue; // skip beta versions
                if (latestTag != installed) {
                    Core.Logger.LogInformation($"Plugin {plugin.Name} has update: {installed ?? "nothing"} -> {latestTag}");
                    return (true, "Plugin update");
                }
            }
            catch (Exception ex) {
                Core.Logger.LogWarning(ex, $"Error checking plugin {plugin.Name}");
            }
        }

        // Installed but no longer wanted: the installer uninstalls it on boot.
        foreach (var name in m_installed.Keys) {
            if (!m_plugins.Any(p => p.Name == name)) {
                Core.Logger.LogInformation($"Plugin {name} removed from plugins.json, pending uninstall");
                return (true, "Plugin removed");
            }
        }

        return (false, "");
    }

    private static async Task<(string Tag, string NewEtag)?> FetchGitHubLatestTag(string repoFullName, string? token, string? etag) {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repoFullName}/releases?per_page=1");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CS2-AutoRestart", "1.0"));
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (!string.IsNullOrEmpty(etag))
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));

        var response = await m_http_client.SendAsync(request);

        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            return null;

        response.EnsureSuccessStatusCode();

        string? newEtag = response.Headers.ETag?.ToString();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.GetArrayLength() == 0) return null;
        if (newEtag == null) return null;
        string? tag = doc.RootElement[0].GetProperty("tag_name").GetString();
        if (tag == null) return null;
        return (tag, newEtag);
    }

    private void LoadPlugins() {
        m_plugins = ReadJson<List<PluginEntry>>(m_config.PluginsJsonPath) ?? new();
        var state = ReadJson<Dictionary<string, StateEntry>>(m_config.PluginStatePath) ?? new();
        m_installed = state.ToDictionary(entry => entry.Key, entry => entry.Value.Tag);
    }

    private T? ReadJson<T>(string path) {
        try {
            if (!File.Exists(path)) {
                Core.Logger.LogWarning($"{path} not found");
                return default;
            }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception ex) {
            Core.Logger.LogWarning(ex, $"Error loading {path}");
            return default;
        }
    }

    private void GetSteamInfPatchVersion() {
        try {
            string steamInfPath = Path.Combine(Core.CSGODirectory, "steam.inf");

            if (!File.Exists(steamInfPath)) {
                Core.Logger.LogError($"steam.inf not found at {steamInfPath}");
                return;
            }

            string contents = File.ReadAllText(steamInfPath);
            var match = Regex.Match(contents, @"PatchVersion=(\d+\.\d+\.\d+\.\d+)");

            if (!match.Success) {
                Core.Logger.LogError("PatchVersion not found in steam.inf");
                return;
            }

            m_current_version = match.Groups[1].Value;
        } catch (Exception ex) {
            Core.Logger.LogError(ex, "Error reading steam.inf");
        }
    }
}
