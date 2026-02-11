using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace LongAudioApp;

/// <summary>
/// GA4 Client-Side Analytics (mimicking JS SDK via /g/collect).
/// - endpoint: https://www.google-analytics.com/g/collect
/// - restores geo/demographics by letting GA4 process client IP/Headers
/// - client_id persists across app launches
/// - session_id regenerates after 30 minutes of inactivity
/// </summary>
public static class AnalyticsService
{
    private static readonly HttpClient _httpClient;
    // Client-side endpoint (v2) - supports automatic geo/device detection
    private const string Endpoint = "https://www.google-analytics.com/g/collect";
    private const int SessionTimeoutMinutes = 30;

    // Loaded from firebase_config.json
    private static string _measurementId = "";
    // ApiSecret is NOT used for /g/collect

    // Session state
    private static string _clientId = "";
    private static string _sessionId = "";
    private static DateTime _lastActivity = DateTime.UtcNow;

    // App metadata (cached once)
    private static readonly string _appVersion;
    private static readonly string _screenResolution;
    private static readonly string _language;

    static AnalyticsService()
    {
        // Cache app metadata
        _appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        _language = CultureInfo.CurrentCulture.Name;

        // Screen resolution (WPF SystemParameters)
        try
        {
            _screenResolution = $"{(int)SystemParameters.PrimaryScreenWidth}x{(int)SystemParameters.PrimaryScreenHeight}";
        }
        catch
        {
            _screenResolution = "unknown";
        }

        _httpClient = new HttpClient();

        // CRITICAL: User-Agent and Accept-Language headers are REQUIRED for /g/collect 
        // to properly resolve device type, OS, and location (GeoIP).
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 TurboScribe/{_appVersion}");

        try
        {
            _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd(_language);
        }
        catch { /* ignore */ }
    }

    private static readonly string SettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "analytics_state.json");

    // ===== Public API =====

    public static void Initialize()
    {
        LoadConfig();
        LoadState();
        EnsureSession();

        _ = TrackEventAsync("session_start");
        _ = TrackEventAsync("app_start");
    }

    public static async Task TrackEventAsync(string eventName, object? extraParams = null)
    {
        if (!IsEnabled || string.IsNullOrEmpty(_measurementId)) return;

        try
        {
            string oldSession = _sessionId;
            EnsureSession();

            // If the session ID rotated due to the 30-minute timeout, fire session_start first
            if (oldSession != _sessionId && eventName != "session_start")
            {
                await SendEventInternal("session_start");
            }

            await SendEventInternal(eventName, extraParams);
        }
        catch
        {
            // Fail silently
        }
    }

    public static void TrackEvent(string eventName, object? extraParams = null)
    {
        _ = TrackEventAsync(eventName, extraParams);
    }

    public static bool IsEnabled { get; set; } = true;

    // ===== Internal =====

    private static async Task SendEventInternal(string eventName, object? extraParams = null)
    {
        // 1. Build Base URL with standard parameters
        // v=2 (Protocol Version)
        // tid (Measurement ID)
        // cid (Client ID)
        // sid (Session ID)
        // en (Event Name)
        // av (App Version) - standard param
        // ul (User Language) - standard param. Although header sends it, param is good fallback
        // sr (Screen Resolution) - standard param
        var sb = new System.Text.StringBuilder();
        sb.Append($"{Endpoint}?v=2");
        sb.Append($"&tid={Uri.EscapeDataString(_measurementId)}");
        sb.Append($"&cid={Uri.EscapeDataString(_clientId)}");
        sb.Append($"&sid={Uri.EscapeDataString(_sessionId)}");
        sb.Append($"&en={Uri.EscapeDataString(eventName)}");
        
        // Standard Demographics Params
        sb.Append($"&av={Uri.EscapeDataString(_appVersion)}");
        sb.Append($"&ul={Uri.EscapeDataString(_language)}");
        sb.Append($"&sr={Uri.EscapeDataString(_screenResolution)}");
        
        // Special: engagement_time_msec is required for session metrics
        // In /g/collect, it's often _et? But user asked for ep.engagement_time_msec=100
        sb.Append("&ep.engagement_time_msec=100");
        
        // Custom Platform Property
        sb.Append("&ep.platform=windows");

        // 2. Inject extraParams as ep.*
        if (extraParams != null)
        {
            try
            {
                var json = JsonSerializer.Serialize(extraParams);
                var dict = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
                
                if (dict != null)
                {
                    foreach (var kv in dict)
                    {
                        var key = kv.Key;
                        var val = kv.Value?.ToString() ?? "";
                        
                        // User instruction: Append each parameter using ep. prefix
                        sb.Append($"&ep.{Uri.EscapeDataString(key)}={Uri.EscapeDataString(val)}");
                    }
                }
            }
            catch { /* ignore serialization errors */ }
        }

        // 3. Send Request (POST empty body)
        // The query string contains all data.
        var fullUrl = sb.ToString();
        var response = await _httpClient.PostAsync(fullUrl, new StringContent(""));

#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[Analytics] Sent {eventName} to {fullUrl}");
        if (!response.IsSuccessStatusCode)
        {
             System.Diagnostics.Debug.WriteLine($"[Analytics] Failed: {response.StatusCode}");
        }
#endif

        _lastActivity = DateTime.UtcNow;
        SaveState();
    }

    // ===== Session Management =====

    private static void EnsureSession()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastActivity;

        if (string.IsNullOrEmpty(_sessionId) || elapsed.TotalMinutes > SessionTimeoutMinutes)
        {
            _sessionId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            _lastActivity = now;
            SaveState();
        }
    }

    // ===== Configuration =====

    private static void LoadConfig()
    {
        try
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "firebase_config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("measurementId", out var mid))
                    _measurementId = mid.GetString() ?? "";
                
                // ApiSecret is not used for /g/collect
            }
        }
        catch { }
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
                    IsEnabled = state.IsEnabled;
                }
            }
        }
        catch { }

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
                LastActivity = _lastActivity,
                IsEnabled = IsEnabled
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
        public bool IsEnabled { get; set; } = true;
    }
}
