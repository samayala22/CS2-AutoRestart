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

public class PluginEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "github";

    [JsonPropertyName("asset")]
    public string Asset { get; set; } = "";

    [JsonPropertyName("destination")]
    public string Destination { get; set; } = "";

    [JsonPropertyName("depth")]
    public int Depth { get; set; } = 0;
}

public class PluginConfig
{
    public bool Enabled { get; set; } = true;
    public float CheckIntervalSeconds { get; set; } = 300.0f; // 5 minutes
    public float QuitIntervalSeconds { get; set; } = 60.0f; // 1 minute
    public string SteamApiEndpoint { get; set; } = "https://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={0}";
    public int ScheduledRestartHour { get; set; } = 5; // 5 AM local time
    public string PluginsJsonPath { get; set; } = "/server-config/plugins.json";
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

        m_restart_conditions.Add(CheckScheduledRestart);
        m_restart_conditions.Add(CheckSteamUpdate);
        m_restart_conditions.Add(CheckPluginUpdates);

        m_check_timer_token = Core.Scheduler.DelayAndRepeatBySeconds(m_config.CheckIntervalSeconds, m_config.CheckIntervalSeconds, OnCheckTimer);
    }

    public override void Unload() {
        m_check_timer_token?.Cancel();
        m_check_timer_token = null;
        m_restart_conditions.Clear();
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
        Core.Logger.LogInformation($"AutoRestart: Quit scheduled — {reason}");
        Core.PlayerManager.SendChat(Helper.Colored($"[red]ATTENTION[default] | Server will restart when all players have left. Reason: {reason}"));
        m_check_timer_token = Core.Scheduler.RepeatBySeconds(m_config.QuitIntervalSeconds, () => {
            if (Core.PlayerManager.PlayerCount == 0) {
                Core.Logger.LogInformation("AutoRestart: No players online, quitting now.");
                Core.Engine.ExecuteCommand("quit");
            }
        });
    }

    // ── Restart Conditions ─────────────────────────────────────────────

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
                Core.Logger.LogWarning("AutoRestart: Steam API returned success=false");
                return (false, "");
            }

            bool updateAvailable = !responseObj.GetProperty("up_to_date").GetBoolean();
            return (updateAvailable, "CS2 update");
        }
        catch (Exception ex) {
            Core.Logger.LogError(ex, "AutoRestart: Error checking for Steam update");
        }
        return (false, "");
    }

    private async Task<(bool Triggered, string Reason)> CheckPluginUpdates() {
        try {
            if (!File.Exists(m_config.PluginsJsonPath)) {
                Core.Logger.LogWarning($"AutoRestart: plugins.json not found at {m_config.PluginsJsonPath}");
                return (false, "");
            }

            string json = await File.ReadAllTextAsync(m_config.PluginsJsonPath);
            var plugins = JsonSerializer.Deserialize<List<PluginEntry>>(json);
            if (plugins == null) return (false, "");

            var githubPlugins = plugins.Where(p => p.Origin == "github").ToList();
            if (githubPlugins.Count == 0) return (false, "");

            string? token = Environment.GetEnvironmentVariable("GITHUB_APIKEY");
            List<string> outdated = new();

            foreach (var plugin in githubPlugins) {
                try {
                    string latestTag = await FetchGitHubLatestTag(plugin.Name, token);
                    if (latestTag != plugin.Tag) {
                        Core.Logger.LogInformation($"AutoRestart: Plugin {plugin.Name} has update: {plugin.Tag} -> {latestTag}");
                        outdated.Add(plugin.Name);
                    }
                }
                catch (Exception ex) {
                    Core.Logger.LogError(ex, $"AutoRestart: Error checking plugin {plugin.Name}");
                }
            }

            if (outdated.Count > 0) {
                return (true, $"Plugin update");
            }
        }
        catch (Exception ex) {
            Core.Logger.LogError(ex, "AutoRestart: Error checking plugin updates");
        }
        return (false, "");
    }

    private static async Task<string> FetchGitHubLatestTag(string repoFullName, string? token) {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{repoFullName}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CS2-AutoRestart", "1.0"));
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await m_http_client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("tag_name").GetString() ?? "";
    }

    private void GetSteamInfPatchVersion() {
        try {
            string steamInfPath = Path.Combine(Core.CSGODirectory, "steam.inf");
            
            if (!File.Exists(steamInfPath)) {
                Core.Logger.LogError($"AutoRestart: steam.inf not found at {steamInfPath}");
                return;
            }

            string contents = File.ReadAllText(steamInfPath);
            var match = Regex.Match(contents, @"PatchVersion=(\d+\.\d+\.\d+\.\d+)");
            
            if (!match.Success) {
                Core.Logger.LogError("AutoRestart: PatchVersion not found in steam.inf");
                return;
            }
            
            m_current_version = match.Groups[1].Value;
        } catch (Exception ex) {
            Core.Logger.LogError(ex, "AutoRestart: Error reading steam.inf");
        }
    }
}
