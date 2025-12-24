using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Core;

namespace AutoRestart;

[PluginMetadata(Id = "AutoRestart", Version = "1.0.0", Name = "AutoRestart", Author = "Praetor", Description = "Auto Restart for Counter-Strike 2")]
public class AutoRestart : BasePlugin {
    private static readonly HttpClient m_http_client = new HttpClient ();
    private const string SteamApiEndpoint = "https://api.steampowered.com/ISteamApps/UpToDateCheck/v0001/?appid=730&version={0}";
    private const float CheckIntervalSeconds = 300.0f; // 5 min
    private CancellationTokenSource? m_check_timer_token;
    private string? m_current_version = null;

    public AutoRestart(ISwiftlyCore core) : base(core) {
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) {
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager) {
    }

    public override void Load(bool hotReload) {
        GetSteamInfPatchVersion();
        
        if (m_current_version is null) {
            Core.Logger.LogError("AutoRestart: Failed to get current version, plugin disabled");
            return;
        }
        
        Core.Logger.LogInformation($"AutoRestart: Current version is {m_current_version}");
        m_check_timer_token = Core.Scheduler.DelayAndRepeatBySeconds(CheckIntervalSeconds, CheckIntervalSeconds, OnCheckTimer);
    }

    public override void Unload() {
        m_check_timer_token?.Cancel();
        m_check_timer_token = null;
    }

    private void OnCheckTimer() {
        Task.Run(CheckForUpdateAsync);
    }

    private async Task CheckForUpdateAsync() {
        try {
            var response = await m_http_client.GetStringAsync(string.Format(SteamApiEndpoint, m_current_version));
            using var doc = JsonDocument.Parse(response);

            var responseObj = doc.RootElement.GetProperty("response");
            
            if (!responseObj.GetProperty("success").GetBoolean()) {
                Core.Logger.LogWarning("AutoRestart: Steam API returned success=false");
                return;
            }
            
            bool upToDate = responseObj.GetProperty("up_to_date").GetBoolean();
            
            if (!upToDate) {
                int requiredVersion = responseObj.GetProperty("required_version").GetInt32();
                
                // Execute on main thread
                Core.Scheduler.NextTick(() => {
                    m_check_timer_token?.Cancel();
                    Core.Logger.LogInformation($"AutoRestart: CS2 update detected (v{requiredVersion})");
                    Core.PlayerManager.SendChat($"AutoRestart: CS2 update detected (v{requiredVersion})");
                    Core.Engine.ExecuteCommand("quit");
                });
            }
        }
        catch (Exception ex) {
            Core.Logger.LogError(ex, "AutoRestart: Error checking for update");
        }
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

    [Command("qqquit")]
    public void Quit(ICommandContext ctx) {
        Core.Logger.LogInformation("AutoRestart: qqquit command executed, quitting server.");
        Core.Engine.ExecuteCommand("quit");
    }
}
