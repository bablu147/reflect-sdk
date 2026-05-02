using System;
using System.Collections.Generic;
using System.Text;
using Reflect.Internal.Debug;
using UnityEngine;

namespace Reflect.Internal.Debug
{
    /// <summary>
    /// In-game developer overlay. Attached to the SDK's hidden GameObject whenever
    /// <see cref="ReflectConfig.IsOverlayEnabled"/> is true — i.e. in debug mode
    /// (null BaseUrl) OR when a real BaseUrl is paired with
    /// <see cref="ReflectConfig.EnableDebugOverlay"/>. Renders a floating,
    /// draggable <b>R</b> button; tapping it opens a tabbed inspection panel
    /// (Overview / Device / Referral / Events / Network / Logs) so developers can
    /// watch the SDK work and — in production inspection mode — see exactly what
    /// was sent to the Worker and what came back.
    /// </summary>
    internal sealed class ReflectDebugOverlay : MonoBehaviour
    {
        // ── Public handles that ReflectSDK populates ──
        internal Func<DeviceSnapshot>   DeviceSnapshotProvider;
        internal Func<ReferralSnapshot> ReferralSnapshotProvider;
        internal Func<int>              QueueSizeProvider;
        internal Func<IosTrackingStatus> AttStatusProvider;
        internal Func<string>           UserIdProvider;
        internal Func<string>           BaseUrlProvider;

        // ── UI state ──
        private enum Tab { Overview, Device, Referral, Events, Network, Logs }
        private Tab _tab = Tab.Overview;
        private bool _open;
        private Rect _buttonRect;
        private bool _dragging;
        private Vector2 _dragStart;
        private Vector2 _buttonStart;
        private float _pressStartTime;
        private Vector2 _overviewScroll;
        private Vector2 _deviceScroll;
        private Vector2 _referralScroll;
        private Vector2 _eventsScroll;
        private Vector2 _networkScroll;
        private Vector2 _logsScroll;
        private string _selectedEventId;
        private int _selectedNetworkSeq = -1;
        private bool _logAutoScroll = true;
        private bool _stylesBuilt;

        // ── Styles (lazily built in OnGUI; can't touch GUIStyle before first OnGUI) ──
        private GUIStyle _styleButton;
        private GUIStyle _stylePanel;
        private GUIStyle _styleHeader;
        private GUIStyle _styleTab;
        private GUIStyle _styleTabActive;
        private GUIStyle _styleKey;
        private GUIStyle _styleVal;
        private GUIStyle _styleBanner;
        private GUIStyle _styleBannerInspect;
        private GUIStyle _styleLogInfo;
        private GUIStyle _styleLogWarn;
        private GUIStyle _styleLogError;
        private GUIStyle _styleSmall;
        private GUIStyle _styleJson;
        private GUIStyle _styleNetOk;
        private GUIStyle _styleNetClient;
        private GUIStyle _styleNetServer;
        private GUIStyle _styleNetPending;

        private Texture2D _texButton;
        private Texture2D _texPanel;
        private Texture2D _texBanner;
        private Texture2D _texBannerInspect;
        private Texture2D _texTab;
        private Texture2D _texTabActive;
        private Texture2D _texSeparator;
        private Texture2D _texNetOk;
        private Texture2D _texNetClient;
        private Texture2D _texNetServer;
        private Texture2D _texNetPending;

        private const float ButtonSize = 56f;
        private const float Margin     = 12f;

        private void Awake()
        {
            // Default position: top-right, respecting safe area on notched phones.
            var safe = Screen.safeArea;
            float x = safe.xMax - ButtonSize - Margin;
            float y = Screen.height - safe.yMax + Margin; // IMGUI y grows downward
            _buttonRect = new Rect(x, y, ButtonSize, ButtonSize);
        }

        private void OnGUI()
        {
            BuildStylesIfNeeded();

            // While the panel is open the floating button is hidden so its
            // tap zone can't collide with the panel's own Close button.
            if (_open) DrawPanel();
            else       DrawFloatingButton();
        }

        // ────────────────────────── Floating button ──────────────────────────

        private void DrawFloatingButton()
        {
            HandleDrag();

            // Badge with queue count when there are pending events.
            int queued = QueueSizeProvider != null ? QueueSizeProvider() : 0;

            GUI.Box(_buttonRect, GUIContent.none, _styleButton);
            var labelRect = _buttonRect;
            GUI.Label(labelRect, "R", _styleHeader);

            if (queued > 0)
            {
                var badge = new Rect(_buttonRect.xMax - 20, _buttonRect.y - 4, 22, 22);
                GUI.Box(badge, queued > 99 ? "99+" : queued.ToString(), _styleBanner);
            }
        }

        private void HandleDrag()
        {
            var e = Event.current;
            if (e == null) return;

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (_buttonRect.Contains(e.mousePosition))
                    {
                        _dragging = true;
                        _dragStart = e.mousePosition;
                        _buttonStart = new Vector2(_buttonRect.x, _buttonRect.y);
                        _pressStartTime = Time.unscaledTime;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (_dragging)
                    {
                        var delta = e.mousePosition - _dragStart;
                        _buttonRect.x = Mathf.Clamp(_buttonStart.x + delta.x, 0, Screen.width - ButtonSize);
                        _buttonRect.y = Mathf.Clamp(_buttonStart.y + delta.y, 0, Screen.height - ButtonSize);
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (_dragging)
                    {
                        var moved = (e.mousePosition - _dragStart).sqrMagnitude;
                        var dt = Time.unscaledTime - _pressStartTime;
                        // Treat as a tap if finger moved <8px AND pressed <400ms.
                        if (moved < 64f && dt < 0.4f) _open = !_open;
                        _dragging = false;
                        e.Use();
                    }
                    break;
            }
        }

        // ────────────────────────────── Panel ────────────────────────────────

        private void DrawPanel()
        {
            var safe = Screen.safeArea;
            float y = Screen.height - safe.yMax + Margin;
            var panel = new Rect(Margin, y,
                Screen.width - Margin * 2,
                safe.height - Margin * 2);

            GUI.Box(panel, GUIContent.none, _stylePanel);

            // Header bar
            var headerHeight = 44f;
            var header = new Rect(panel.x, panel.y, panel.width, headerHeight);
            GUI.Label(new Rect(header.x + 12, header.y + 8, header.width - 80, headerHeight - 8),
                      SdkVersion.FullTag, _styleHeader);

            var closeRect = new Rect(header.xMax - 48, header.y + 8, 36, 28);
            if (GUI.Button(closeRect, "×", _styleHeader)) _open = false;

            // Mode banner — red in debug mode (no BaseUrl), yellow in inspection
            // mode (BaseUrl set + overlay explicitly enabled). Either way the
            // banner is a reminder that this UI is for developers only.
            var bannerHeight = 28f;
            string url = BaseUrlProvider != null ? BaseUrlProvider() : null;
            bool debugMode = string.IsNullOrEmpty(url);
            bool hasBanner = true;
            var bannerRect = new Rect(panel.x, header.yMax, panel.width, bannerHeight);
            if (debugMode)
            {
                GUI.Box(bannerRect, GUIContent.none, _styleBanner);
                GUI.Label(new Rect(bannerRect.x + 12, bannerRect.y + 5, bannerRect.width - 12, bannerHeight),
                    "DEBUG MODE — no BaseUrl set. Events are captured but NOT dispatched.",
                    _styleHeader);
            }
            else
            {
                GUI.Box(bannerRect, GUIContent.none, _styleBannerInspect);
                GUI.Label(new Rect(bannerRect.x + 12, bannerRect.y + 5, bannerRect.width - 12, bannerHeight),
                    "INSPECTION MODE — events ARE being dispatched. See Network tab for traffic.",
                    _styleHeader);
            }

            // Tab bar
            float tabY = header.yMax + (hasBanner ? bannerHeight : 0);
            float tabHeight = 38f;
            DrawTabBar(new Rect(panel.x + 6, tabY, panel.width - 12, tabHeight));

            // Content area
            var content = new Rect(panel.x + 12, tabY + tabHeight + 4,
                                    panel.width - 24, panel.yMax - (tabY + tabHeight + 16));
            switch (_tab)
            {
                case Tab.Overview: DrawOverview(content); break;
                case Tab.Device:   DrawDevice(content);   break;
                case Tab.Referral: DrawReferral(content); break;
                case Tab.Events:   DrawEvents(content);   break;
                case Tab.Network:  DrawNetwork(content);  break;
                case Tab.Logs:     DrawLogs(content);     break;
            }
        }

        private void DrawTabBar(Rect rect)
        {
            string[] labels = { "Overview", "Device", "Referral", "Events", "Network", "Logs" };
            float w = rect.width / labels.Length;
            for (int i = 0; i < labels.Length; i++)
            {
                var r = new Rect(rect.x + i * w, rect.y, w - 2, rect.height);
                var style = (int)_tab == i ? _styleTabActive : _styleTab;
                if (GUI.Button(r, labels[i], style)) _tab = (Tab)i;
            }
        }

        // ────────────────────────────── Tabs ─────────────────────────────────

        private void DrawOverview(Rect rect)
        {
            var snap = DeviceSnapshotProvider?.Invoke();
            var refSnap = ReferralSnapshotProvider?.Invoke();
            var att = AttStatusProvider != null ? AttStatusProvider() : IosTrackingStatus.NotDetermined;
            var uid = UserIdProvider?.Invoke();
            var url = BaseUrlProvider?.Invoke();

            _overviewScroll = GUI.BeginScrollView(rect, _overviewScroll,
                new Rect(0, 0, rect.width - 20, 640));
            float y = 0;
            y = Row("SDK version",    SdkVersion.Value, y);
            y = Row("Base URL",       string.IsNullOrEmpty(url) ? "(none — debug mode)" : url, y);
            y = Row("Install UUID",   InstallUuidStore.Value ?? "(not yet generated)", y);
            y = Row("First launch",   InstallUuidStore.IsFirstLaunch ? "yes" : "no (returning user)", y);
            y = Row("User ID",        string.IsNullOrEmpty(uid) ? "(not set)" : uid, y);
            y = Row("ATT status",     att.ToString(), y);
            y = Row("Device info",    snap != null ? "collected" : "pending…", y);
            y = Row("Referral",       refSnap != null ? "collected" : "pending…", y);
            y = Row("Queue size",     (QueueSizeProvider != null ? QueueSizeProvider() : 0).ToString(), y);
            y = Row("Total enqueued", ReflectDebugEventLog.TotalEnqueued.ToString(), y);
            y = Row("Total sent",     ReflectDebugEventLog.TotalSent.ToString(), y);
            y = Row("Total failed",   ReflectDebugEventLog.TotalFailed.ToString(), y);
            y = Row("Total dropped",  ReflectDebugEventLog.TotalDropped.ToString(), y);
            y = Row("App bundle",     snap?.AppBundleId ?? "—", y);
            y = Row("App version",    snap?.AppVersion ?? "—", y);
            y = Row("OS",             snap != null ? $"{snap.Os} {snap.OsVersion}" : "—", y);
            y = Row("Device",         snap != null ? $"{snap.DeviceManufacturer} {snap.DeviceModel}" : "—", y);
            GUI.EndScrollView();
        }

        private void DrawDevice(Rect rect)
        {
            var d = DeviceSnapshotProvider?.Invoke();
            if (d == null)
            {
                GUI.Label(rect, "Device info not yet collected. Give it a second…", _styleVal);
                return;
            }

            _deviceScroll = GUI.BeginScrollView(rect, _deviceScroll,
                new Rect(0, 0, rect.width - 20, 1400));
            float y = 0;
            y = Section("Identifiers", y);
            y = Row("gaid",        Mask(d.Gaid), y);
            y = Row("lat_enabled", d.LatEnabled.ToString(), y);
            y = Row("idfa",        Mask(d.Idfa), y);
            y = Row("idfv",        Mask(d.Idfv), y);
            y = Row("android_id",  Mask(d.AndroidId), y);

            y = Section("OS / hardware", y);
            y = Row("os",            d.Os, y);
            y = Row("os_version",    d.OsVersion, y);
            y = Row("api_level",     d.ApiLevel.ToString(), y);
            y = Row("model",         d.DeviceModel, y);
            y = Row("manufacturer",  d.DeviceManufacturer, y);
            y = Row("brand",         d.DeviceBrand, y);
            y = Row("cpu_arch",      d.CpuArch, y);
            y = Row("screen",        $"{d.ScreenWidth}×{d.ScreenHeight} @ {d.ScreenDensity}dpi", y);
            y = Row("total_ram_mb",  d.TotalRamMb.ToString(), y);

            y = Section("App", y);
            y = Row("bundle_id",         d.AppBundleId, y);
            y = Row("version",           d.AppVersion, y);
            y = Row("version_code",      d.AppVersionCode.ToString(), y);
            y = Row("install_source",    d.InstallSource, y);
            y = Row("first_install_time", UnixMsToIso(d.FirstInstallTime), y);
            y = Row("last_update_time",   UnixMsToIso(d.LastUpdateTime), y);

            y = Section("Locale", y);
            y = Row("language",  d.Language, y);
            y = Row("locale",    d.Locale, y);
            y = Row("timezone",  $"{d.Timezone} ({d.TimezoneOffsetMinutes:+#;-#;0}min)", y);

            y = Section("Network", y);
            y = Row("connection",  d.ConnectionType, y);
            y = Row("carrier",     d.Carrier, y);
            y = Row("carrier_mcc", d.CarrierMcc, y);
            y = Row("carrier_mnc", d.CarrierMnc, y);

            y = Section("Fraud signals", y);
            y = Row("is_emulator",          d.IsEmulator.ToString(), y);
            y = Row("is_rooted",            d.IsRooted.ToString(), y);
            y = Row("vpn_detected",         d.VpnDetected.ToString(), y);
            y = Row("mock_location_enabled", d.MockLocationEnabled.ToString(), y);
            GUI.EndScrollView();
        }

        private void DrawReferral(Rect rect)
        {
            var r = ReferralSnapshotProvider?.Invoke();
            if (r == null)
            {
                GUI.Label(rect, "Referral not yet collected.\n\n" +
                                "On Android this comes from Play Install Referrer —\n" +
                                "only available on real devices installed from the Play Store.\n\n" +
                                "On iOS this comes from AdServices (iOS 14.3+) and is null for organic installs.",
                          _styleVal);
                return;
            }

            _referralScroll = GUI.BeginScrollView(rect, _referralScroll,
                new Rect(0, 0, rect.width - 20, 800));
            float y = 0;
            y = Section("Source", y);
            y = Row("source",              r.Source, y);
            y = Row("google_play_instant", r.GooglePlayInstant.ToString(), y);

            y = Section("Raw", y);
            y = Row("raw", string.IsNullOrEmpty(r.Raw) ? "(empty)" : r.Raw, y);

            y = Section("Timestamps", y);
            y = Row("click_ts",         UnixSecToIso(r.ReferrerClickTs), y);
            y = Row("install_ts",       UnixSecToIso(r.InstallBeginTs), y);
            y = Row("click_server_ts",  UnixSecToIso(r.ReferrerClickServerTs), y);
            y = Row("install_server_ts", UnixSecToIso(r.InstallBeginServerTs), y);

            if (!string.IsNullOrEmpty(r.AttributionToken))
            {
                y = Section("iOS AdServices", y);
                y = Row("attribution_token", r.AttributionToken.Length > 40
                    ? r.AttributionToken.Substring(0, 40) + "…" : r.AttributionToken, y);
            }

            if (r.ParsedParams != null && r.ParsedParams.Count > 0)
            {
                y = Section("Parsed params", y);
                foreach (var kv in r.ParsedParams) y = Row(kv.Key, kv.Value, y);
            }
            GUI.EndScrollView();
        }

        private void DrawEvents(Rect rect)
        {
            var events = ReflectDebugEventLog.Snapshot();
            float rowH = 32f;
            float totalH = Mathf.Max(rect.height, events.Count * rowH + 40);
            _eventsScroll = GUI.BeginScrollView(rect, _eventsScroll,
                new Rect(0, 0, rect.width - 20, totalH));

            if (events.Count == 0)
            {
                GUI.Label(new Rect(0, 0, rect.width - 20, 40),
                    "No events yet. Call ReflectSDK.TrackEvent(...) from your game code.",
                    _styleVal);
                GUI.EndScrollView();
                return;
            }

            // Render newest-first.
            float y = 0;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                var label = $"{e.TimeUtc.ToLocalTime():HH:mm:ss}  {e.EventName}  [{e.Status}]";
                var row = new Rect(0, y, rect.width - 20, rowH - 2);
                if (GUI.Button(row, label, _styleTab))
                    _selectedEventId = _selectedEventId == e.EventId ? null : e.EventId;
                y += rowH;

                if (_selectedEventId == e.EventId)
                {
                    var json = Prettify(e.Json);
                    var jsonH = _styleJson.CalcHeight(new GUIContent(json), rect.width - 40);
                    GUI.Label(new Rect(8, y, rect.width - 40, jsonH), json, _styleJson);
                    y += jsonH + 6;
                    if (!string.IsNullOrEmpty(e.Note))
                    {
                        GUI.Label(new Rect(8, y, rect.width - 40, 22), "note: " + e.Note, _styleSmall);
                        y += 24;
                    }
                }
            }
            GUI.EndScrollView();
        }

        private void DrawNetwork(Rect rect)
        {
            var entries = ReflectNetworkLog.Snapshot();

            // Toolbar (entry count + clear).
            var toolbar = new Rect(rect.x, rect.y, rect.width, 26);
            GUI.Label(new Rect(toolbar.x, toolbar.y + 2, 260, 22),
                      $"{entries.Count} / 30 requests", _styleSmall);
            if (GUI.Button(new Rect(toolbar.xMax - 90, toolbar.y, 80, 22), "Clear", _styleTab))
            {
                ReflectNetworkLog.Clear();
                _selectedNetworkSeq = -1;
            }

            var listRect = new Rect(rect.x, toolbar.yMax + 4, rect.width, rect.height - 30);

            if (entries.Count == 0)
            {
                string hint = string.IsNullOrEmpty(BaseUrlProvider?.Invoke())
                    ? "DEBUG MODE: no BaseUrl set, so nothing is being sent. Set BaseUrl on ReflectConfig to see real traffic here."
                    : "No requests yet. The dispatcher flushes on its interval (default 30s) or when the queue hits BatchSize.";
                GUI.Label(listRect, hint, _styleVal);
                return;
            }

            // Render newest-first. Measure each entry's height up-front so the
            // scroll-view's content-rect is correct (expanded entries need more room).
            float contentW = listRect.width - 20;
            float totalH = 0;
            var heights = new float[entries.Count];
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                float h = 34f; // compact row
                if (entries[i].Seq == _selectedNetworkSeq)
                    h += MeasureExpandedNetEntry(entries[i], contentW);
                heights[i] = h;
                totalH += h + 4;
            }

            _networkScroll = GUI.BeginScrollView(listRect, _networkScroll,
                new Rect(0, 0, contentW, Mathf.Max(listRect.height, totalH)));

            float y = 0;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var en = entries[i];
                var rowH = 32f;
                var rowR = new Rect(0, y, contentW, rowH);

                // Compact row: a status-colored button.
                string summary = string.Format(
                    "{0:HH:mm:ss.fff}  {1}  {2}  →  {3}  ({4:F0}ms)  [{5}ev, {6}B]",
                    en.StartedUtc.ToLocalTime(),
                    en.Method,
                    ShortUrl(en.Url),
                    en.Status == ReflectNetworkLog.Status.Pending ? "…" : en.ResponseCode.ToString(),
                    en.DurationMs,
                    en.BatchSize,
                    en.RequestBytes);

                var style = StyleForNet(en.Status);
                if (GUI.Button(rowR, summary, style))
                    _selectedNetworkSeq = _selectedNetworkSeq == en.Seq ? -1 : en.Seq;
                y += rowH + 2;

                if (en.Seq == _selectedNetworkSeq)
                {
                    y = DrawExpandedNetEntry(en, y, contentW);
                    y += 2;
                }
            }
            GUI.EndScrollView();
        }

        private float MeasureExpandedNetEntry(ReflectNetworkLog.Entry en, float contentW)
        {
            float h = 0;
            // Full URL
            h += 22;
            // Request headers section
            h += 24; // section label
            if (en.RequestHeaders != null)
                h += en.RequestHeaders.Count * 18;
            // Request body section
            h += 24;
            var bodyPretty = Prettify(en.RequestBodyPreview);
            h += _styleJson.CalcHeight(new GUIContent(bodyPretty), contentW - 16) + 4;
            // Response section
            h += 24;
            var resp = string.IsNullOrEmpty(en.ResponseBodyPreview)
                ? (en.Status == ReflectNetworkLog.Status.Pending ? "(pending…)" : "(empty)")
                : Prettify(en.ResponseBodyPreview);
            h += _styleJson.CalcHeight(new GUIContent(resp), contentW - 16) + 4;
            if (!string.IsNullOrEmpty(en.ErrorDetail))
            {
                h += 24;
                h += _styleLogError.CalcHeight(new GUIContent(en.ErrorDetail), contentW - 16) + 4;
            }
            return h + 10;
        }

        private float DrawExpandedNetEntry(ReflectNetworkLog.Entry en, float y, float contentW)
        {
            // Full URL
            GUI.Label(new Rect(8, y, contentW - 16, 22), en.Url, _styleSmall);
            y += 22;

            // Request headers.
            GUI.Label(new Rect(8, y, contentW - 16, 22), "REQUEST HEADERS", _styleHeader);
            y += 24;
            if (en.RequestHeaders != null)
            {
                foreach (var kv in en.RequestHeaders)
                {
                    // Mask the HMAC signature so over-the-shoulder viewers don't
                    // snap a photo of the secret-derived value.
                    string val = kv.Key == "X-Reflect-Signature" && kv.Value != null && kv.Value.Length > 12
                        ? kv.Value.Substring(0, 8) + "…" + kv.Value.Substring(kv.Value.Length - 4)
                        : kv.Value;
                    GUI.Label(new Rect(12, y, 200, 18), kv.Key, _styleKey);
                    GUI.Label(new Rect(212, y, contentW - 220, 18), val, _styleSmall);
                    y += 18;
                }
            }

            // Request body.
            GUI.Label(new Rect(8, y, contentW - 16, 22), "REQUEST BODY", _styleHeader);
            y += 24;
            var pretty = Prettify(en.RequestBodyPreview);
            var prettyH = _styleJson.CalcHeight(new GUIContent(pretty), contentW - 16);
            GUI.Label(new Rect(12, y, contentW - 16, prettyH), pretty, _styleJson);
            y += prettyH + 4;

            // Response.
            var respTitle = en.Status == ReflectNetworkLog.Status.Pending
                ? "RESPONSE (pending)"
                : $"RESPONSE — HTTP {en.ResponseCode}  ({en.DurationMs:F0}ms)";
            GUI.Label(new Rect(8, y, contentW - 16, 22), respTitle, _styleHeader);
            y += 24;
            var resp = string.IsNullOrEmpty(en.ResponseBodyPreview)
                ? (en.Status == ReflectNetworkLog.Status.Pending ? "(pending…)" : "(empty)")
                : Prettify(en.ResponseBodyPreview);
            var respH = _styleJson.CalcHeight(new GUIContent(resp), contentW - 16);
            GUI.Label(new Rect(12, y, contentW - 16, respH), resp, _styleJson);
            y += respH + 4;

            if (!string.IsNullOrEmpty(en.ErrorDetail))
            {
                GUI.Label(new Rect(8, y, contentW - 16, 22), "ERROR", _styleHeader);
                y += 24;
                var errH = _styleLogError.CalcHeight(new GUIContent(en.ErrorDetail), contentW - 16);
                GUI.Label(new Rect(12, y, contentW - 16, errH), en.ErrorDetail, _styleLogError);
                y += errH + 4;
            }
            return y;
        }

        private GUIStyle StyleForNet(ReflectNetworkLog.Status s)
        {
            switch (s)
            {
                case ReflectNetworkLog.Status.Ok:           return _styleNetOk;
                case ReflectNetworkLog.Status.ClientError:  return _styleNetClient;
                case ReflectNetworkLog.Status.ServerError:  return _styleNetServer;
                case ReflectNetworkLog.Status.NetworkError: return _styleNetServer;
                default:                                    return _styleNetPending;
            }
        }

        private static string ShortUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            int pathStart = scheme > 0 ? url.IndexOf('/', scheme + 3) : -1;
            return pathStart > 0 ? url.Substring(pathStart) : url;
        }

        private void DrawLogs(Rect rect)
        {
            var logs = ReflectLogBuffer.Snapshot();

            // Toolbar
            var toolbar = new Rect(rect.x, rect.y, rect.width, 26);
            GUI.Label(new Rect(toolbar.x, toolbar.y + 2, 200, 22),
                      $"{logs.Count} / 500 entries", _styleSmall);
            if (GUI.Button(new Rect(toolbar.xMax - 180, toolbar.y, 80, 22),
                           _logAutoScroll ? "Auto ✓" : "Auto", _styleTab))
                _logAutoScroll = !_logAutoScroll;
            if (GUI.Button(new Rect(toolbar.xMax - 90, toolbar.y, 80, 22), "Clear", _styleTab))
                ReflectLogBuffer.Clear();

            var listRect = new Rect(rect.x, toolbar.yMax + 4, rect.width, rect.height - 30);

            // Measure total height for the view-content rect.
            float contentW = listRect.width - 20;
            float totalH = 0;
            var heights = new float[logs.Count];
            for (int i = 0; i < logs.Count; i++)
            {
                var h = StyleFor(logs[i].Level).CalcHeight(new GUIContent(FormatLogLine(logs[i])), contentW);
                heights[i] = h;
                totalH += h + 2;
            }

            if (_logAutoScroll) _logsScroll.y = Mathf.Max(0, totalH - listRect.height);

            _logsScroll = GUI.BeginScrollView(listRect, _logsScroll,
                new Rect(0, 0, contentW, Mathf.Max(listRect.height, totalH)));

            float y = 0;
            for (int i = 0; i < logs.Count; i++)
            {
                var line = FormatLogLine(logs[i]);
                GUI.Label(new Rect(0, y, contentW, heights[i]), line, StyleFor(logs[i].Level));
                y += heights[i] + 2;
            }
            GUI.EndScrollView();
        }

        // ────────────────────── Rendering primitives ─────────────────────────

        private float Row(string key, string val, float y)
        {
            GUI.Label(new Rect(0, y, 170, 22), key, _styleKey);
            var h = _styleVal.CalcHeight(new GUIContent(val ?? "—"), 560);
            GUI.Label(new Rect(170, y, 560, Mathf.Max(22, h)), val ?? "—", _styleVal);
            return y + Mathf.Max(24, h + 2);
        }

        private float Section(string title, float y)
        {
            if (y > 0) y += 10;
            GUI.Label(new Rect(0, y, 700, 22), title.ToUpper(), _styleHeader);
            y += 24;
            GUI.DrawTexture(new Rect(0, y, 700, 1), _texSeparator);
            return y + 6;
        }

        private GUIStyle StyleFor(ReflectLogBuffer.Level l)
        {
            switch (l)
            {
                case ReflectLogBuffer.Level.Warn:  return _styleLogWarn;
                case ReflectLogBuffer.Level.Error: return _styleLogError;
                default:                           return _styleLogInfo;
            }
        }

        private static string FormatLogLine(ReflectLogBuffer.Entry e)
            => $"{e.TimeUtc.ToLocalTime():HH:mm:ss.fff}  {e.Message}";

        private static string Mask(string id)
        {
            if (string.IsNullOrEmpty(id)) return "(null)";
            if (id.Length <= 8) return id;
            return id.Substring(0, 4) + "…" + id.Substring(id.Length - 4);
        }

        private static string UnixMsToIso(long ms)
            => ms <= 0 ? "—" : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        private static string UnixSecToIso(long s)
            => s <= 0 ? "—" : DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

        private static string Prettify(string json)
        {
            if (string.IsNullOrEmpty(json)) return "(empty)";
            var sb = new StringBuilder(json.Length + 64);
            int indent = 0;
            bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inStr = !inStr;
                if (!inStr)
                {
                    if (c == '{' || c == '[') { sb.Append(c); sb.Append('\n'); sb.Append(' ', ++indent * 2); continue; }
                    if (c == '}' || c == ']') { sb.Append('\n'); sb.Append(' ', --indent * 2); sb.Append(c); continue; }
                    if (c == ',')             { sb.Append(c); sb.Append('\n'); sb.Append(' ', indent * 2); continue; }
                    if (c == ':')             { sb.Append(": "); continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ────────────────────────── Styles ───────────────────────────────────

        private void BuildStylesIfNeeded()
        {
            if (_stylesBuilt) return;
            _stylesBuilt = true;

            _texButton        = SolidTexture(new Color(0.12f, 0.45f, 0.95f, 0.92f));
            _texPanel         = SolidTexture(new Color(0.09f, 0.10f, 0.13f, 0.96f));
            _texBanner        = SolidTexture(new Color(0.82f, 0.21f, 0.21f, 1f));
            _texBannerInspect = SolidTexture(new Color(0.85f, 0.58f, 0.12f, 1f));
            _texTab           = SolidTexture(new Color(0.18f, 0.20f, 0.24f, 1f));
            _texTabActive     = SolidTexture(new Color(0.12f, 0.45f, 0.95f, 1f));
            _texSeparator     = SolidTexture(new Color(1f, 1f, 1f, 0.12f));
            // Network-row backgrounds — keyed to HTTP outcome so failures pop at a glance.
            _texNetOk         = SolidTexture(new Color(0.15f, 0.45f, 0.22f, 1f));
            _texNetClient     = SolidTexture(new Color(0.75f, 0.45f, 0.12f, 1f));
            _texNetServer     = SolidTexture(new Color(0.70f, 0.18f, 0.18f, 1f));
            _texNetPending    = SolidTexture(new Color(0.30f, 0.32f, 0.36f, 1f));

            _styleButton = new GUIStyle(GUI.skin.box) { normal = { background = _texButton } };
            _stylePanel  = new GUIStyle(GUI.skin.box) { normal = { background = _texPanel } };
            _styleBanner = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _texBanner, textColor = Color.white },
                fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, fontSize = 13
            };
            _styleBannerInspect = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _texBannerInspect, textColor = Color.white },
                fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, fontSize = 13
            };

            _styleHeader = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = Color.white },
                fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, fontSize = 16
            };

            _styleTab = new GUIStyle(GUI.skin.button)
            {
                normal   = { background = _texTab, textColor = new Color(0.85f, 0.85f, 0.85f) },
                hover    = { background = _texTab, textColor = Color.white },
                active   = { background = _texTabActive, textColor = Color.white },
                alignment = TextAnchor.MiddleCenter, fontSize = 13,
                border = new RectOffset(2, 2, 2, 2), padding = new RectOffset(4, 4, 4, 4)
            };
            _styleTabActive = new GUIStyle(_styleTab)
            {
                normal = { background = _texTabActive, textColor = Color.white },
                fontStyle = FontStyle.Bold
            };

            _styleKey = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.65f, 0.78f, 0.95f) },
                fontStyle = FontStyle.Bold, fontSize = 12
            };
            _styleVal = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = Color.white },
                wordWrap = true, fontSize = 12
            };
            _styleSmall = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) },
                fontSize = 11
            };
            // Try a few common mono OS fonts; fall back to the default skin font
            // if none are available (GUIStyle.font == null uses GUI.skin.font).
            var mono = Font.CreateDynamicFontFromOSFont("Courier New", 11)
                    ?? Font.CreateDynamicFontFromOSFont("CourierNewPSMT", 11)
                    ?? Font.CreateDynamicFontFromOSFont("monospace", 11);

            _styleJson = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.8f, 0.95f, 0.8f) },
                wordWrap = true, fontSize = 11
            };
            if (mono != null) _styleJson.font = mono;

            _styleLogInfo = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) },
                wordWrap = true, fontSize = 11
            };
            if (mono != null) _styleLogInfo.font = mono;
            _styleLogWarn = new GUIStyle(_styleLogInfo)
            {
                normal = { textColor = new Color(1f, 0.82f, 0.3f) }
            };
            _styleLogError = new GUIStyle(_styleLogInfo)
            {
                normal = { textColor = new Color(1f, 0.45f, 0.45f) }
            };

            // Network-row button styles — all four use the base tab shape and
            // override just the background tint so outcome is readable at a glance.
            GUIStyle NetRow(Texture2D bg) => new GUIStyle(GUI.skin.button)
            {
                normal    = { background = bg, textColor = Color.white },
                hover     = { background = bg, textColor = Color.white },
                active    = { background = bg, textColor = Color.white },
                alignment = TextAnchor.MiddleLeft, fontSize = 11,
                padding   = new RectOffset(8, 8, 4, 4),
                font      = mono
            };
            _styleNetOk      = NetRow(_texNetOk);
            _styleNetClient  = NetRow(_texNetClient);
            _styleNetServer  = NetRow(_texNetServer);
            _styleNetPending = NetRow(_texNetPending);
        }

        private static Texture2D SolidTexture(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        private void OnDestroy()
        {
            DestroyTex(_texButton);
            DestroyTex(_texPanel);
            DestroyTex(_texBanner);
            DestroyTex(_texBannerInspect);
            DestroyTex(_texTab);
            DestroyTex(_texTabActive);
            DestroyTex(_texSeparator);
            DestroyTex(_texNetOk);
            DestroyTex(_texNetClient);
            DestroyTex(_texNetServer);
            DestroyTex(_texNetPending);
        }

        private static void DestroyTex(Texture2D t)
        {
            if (t == null) return;
#if UNITY_EDITOR
            DestroyImmediate(t);
#else
            Destroy(t);
#endif
        }
    }
}
