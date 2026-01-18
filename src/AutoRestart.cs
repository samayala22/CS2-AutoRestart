using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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

public class PluginConfig
{
    public bool Enabled { get; set; } = true;
    public float CheckIntervalSeconds { get; set; } = 300.0f; // 5 minutes
    public float QuitIntervalSeconds { get; set; } = 60.0f; // 1 minute
    public string SteamApiEndpoint { get; set; } = "https://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={0}";
    public int ScheduledRestartHour { get; set; } = 5; // 5 AM local time
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
    private ServiceProvider? m_provider;

    private static readonly HttpClient m_http_client = new();
    private PluginConfig m_config = new();
    private CancellationTokenSource? m_check_timer_token;
    private string? m_current_version = null;

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
        m_check_timer_token = Core.Scheduler.DelayAndRepeatBySeconds(m_config.CheckIntervalSeconds, m_config.CheckIntervalSeconds, OnCheckTimer);
    }

    public override void Unload() {
        m_check_timer_token?.Cancel();
        m_check_timer_token = null;
        m_provider?.Dispose();
    }

    private void OnCheckTimer() {
        Task.Run(async () => {
            if (IsScheduledRestartTime() || await IsUpdateAvailableAsync()) {
                Core.Scheduler.NextTick(ScheduleQuit);
            }
        });
    }

    private bool IsScheduledRestartTime() {
        return DateTime.Now.Hour == m_config.ScheduledRestartHour;
    }

    private async Task<bool> IsUpdateAvailableAsync() {
        try {
            var response = await m_http_client.GetStringAsync(string.Format(m_config.SteamApiEndpoint, m_current_version));
            using var doc = JsonDocument.Parse(response);

            var responseObj = doc.RootElement.GetProperty("response");
            
            if (!responseObj.GetProperty("success").GetBoolean()) {
                Core.Logger.LogWarning("AutoRestart: Steam API returned success=false");
                return false;
            }
            
            return !responseObj.GetProperty("up_to_date").GetBoolean();
        }
        catch (Exception ex) {
            Core.Logger.LogError(ex, "AutoRestart: Error checking for update");
        }
        return false;
    }

    private void ScheduleQuit() {
        m_check_timer_token?.Cancel();
        Core.Logger.LogInformation("AutoRestart: Quit scheduled.");
        Core.PlayerManager.SendChat("Server will restart when all players have left the game.");
        m_check_timer_token = Core.Scheduler.RepeatBySeconds(m_config.QuitIntervalSeconds, () => {
            if (Core.PlayerManager.PlayerCount == 0) {
                Core.Logger.LogInformation("AutoRestart: No players online, quitting now.");
                Core.Engine.ExecuteCommand("quit");
            }
        });
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
