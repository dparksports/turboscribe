using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LongAudioApp;

/// <summary>
/// GA4 Measurement Protocol analytics with session management.
/// - client_id persists across app launches (stored in settings.json)
/// - session_id regenerates after 30 minutes of inactivity
/// - engagement_time_msec is injected into every event
/// </summary>
public static class AnalyticsService
{
    private static readonly HttpClient _httpClient = new();
    private const string Endpoint = "https://www.google-analytics.com/mp/collect";
    private const string MeasurementId = "G-B387NLSSJX";
    private const string ApiSecret = "ch411kMtTRW7z_3XEUlmiw";
    private const int SessionTimeoutMinutes = 30;

    private static string _clientId = "";
    private static string _sessionId = "";
    private static DateTime _lastActivity = DateTime.UtcNow;

    private static readonly string SettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "analytics_state.json");

    /// <summary>Initialize on app startup — loads or creates client_id, starts session.</summary>
    public static void Initialize()
    {
        LoadState();
        RefreshSession();
    }

    /// <summary>Fire-and-forget an event with optional parameters.</summary>
    public static async Task TrackEventAsync(string eventName, object? extraParams = null)
    {
        try
        {
            RefreshSession();

            // Build params dict with session fields injected
            var paramsDict = new System.Collections.Generic.Dictionary<string, object>
            {
                ["session_id"] = _sessionId,
                ["engagement_time_msec"] = "100"
            };

            // Merge any extra params
            if (extraParams != null)
            {
                var json = JsonSerializer.Serialize(extraParams);
                var extra = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
                if (extra != null)
                {
                    foreach (var kv in extra)
                        paramsDict[kv.Key] = kv.Value;
                }
            }

            var payload = new
            {
                client_id = _clientId,
                events = new[]
                {
                    new
                    {
                        name = eventName,
                        @params = paramsDict
                    }
                }
            };

            var url = $"{Endpoint}?measurement_id={MeasurementId}&api_secret={ApiSecret}";
            var body = JsonSerializer.Serialize(payload);
            await _httpClient.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));

            _lastActivity = DateTime.UtcNow;
            SaveState();
        }
        catch
        {
            // Analytics should never crash the app
        }
    }

    /// <summary>Convenience fire-and-forget wrapper (no await needed).</summary>
    public static void TrackEvent(string eventName, object? extraParams = null)
    {
        _ = TrackEventAsync(eventName, extraParams);
    }

    // ===== Session Management =====

    private static void RefreshSession()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastActivity;

        if (string.IsNullOrEmpty(_sessionId) || elapsed.TotalMinutes > SessionTimeoutMinutes)
        {
            // New session
            _sessionId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            _lastActivity = now;
            SaveState();
        }
    }

    // ===== Persistence =====

    private static void LoadState()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var state = JsonSerializer.Deserialize<AnalyticsState>(json);
                if (state != null)
                {
                    _clientId = state.ClientId ?? "";
                    _sessionId = state.SessionId ?? "";
                    _lastActivity = state.LastActivity;
                }
            }
        }
        catch { }

        // Ensure client_id exists
        if (string.IsNullOrEmpty(_clientId))
        {
            _clientId = Guid.NewGuid().ToString();
            SaveState();
        }
    }

    private static void SaveState()
    {
        try
        {
            var state = new AnalyticsState
            {
                ClientId = _clientId,
                SessionId = _sessionId,
                LastActivity = _lastActivity
            };
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    private class AnalyticsState
    {
        public string? ClientId { get; set; }
        public string? SessionId { get; set; }
        public DateTime LastActivity { get; set; }
    }
}
