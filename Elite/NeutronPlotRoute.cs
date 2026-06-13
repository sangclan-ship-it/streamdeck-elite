using BarRaider.SdTools;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Elite
{
    public class NeutronPlotWaypoint
    {
        public string SystemName { get; set; } = string.Empty;
        public double JumpDistance { get; set; }
        public double DistanceRemaining { get; set; }
        public bool IsRefuel { get; set; }
        public bool IsNeutron { get; set; }
        public double CumulativeDistance { get; set; }

        // Spansh system id64 — stored for Phase 2 per-system scoopable enrichment. 0 for CSV routes.
        public long Id64 { get; set; }
    }

    public class NeutronPlotSnapshot
    {
        public string CsvPath { get; set; } = string.Empty;
        public bool IsLoaded { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public int WaypointTarget { get; set; }
        public int WaypointMax { get; set; }
        public string SystemCurrent { get; set; } = string.Empty;
        public string RouteStatus { get; set; } = string.Empty;
        public int WaypointCurrent { get; set; } = -1;
        public string SystemTarget { get; set; } = string.Empty;
        public string SystemDestination { get; set; } = string.Empty;
        public int JumpRemaining { get; set; }
        public string JumpSummary { get; set; } = string.Empty;
        public double JumpPercent { get; set; }
        public double DistanceTravelled { get; set; }
        public double DistanceTarget { get; set; }
        public double DistanceDestination { get; set; }
        public bool IsRefuel { get; set; }
        public bool IsNeutron { get; set; }
        public string StarRefuel { get; set; } = string.Empty;
        public string StarNeutron { get; set; } = string.Empty;

        // Spansh auto-plot state
        public bool IsPlotting { get; set; }
        public bool IsSpanshRoute { get; set; }
        public string SpanshError { get; set; } = string.Empty;

        // Nearest scoopable star to the target system, in light-seconds (Phase 2 EDSM enrichment).
        // double.NaN = not yet looked up; -1 = looked up, none found; >= 0 = nearest scoopable distance.
        public double ScoopableLs { get; set; } = double.NaN;
    }

    public static class NeutronPlotRoute
    {
        private const string StateFileName = "neutronPlotState.json";
        private const string SpanshRouteFileName = "neutronSpanshRoute.json";
        private const string FuelCacheFileName = "neutronFuelStars.json";

        // Phase 2 — EDSM fuel-star enrichment. Name-keyed cache (case-insensitive): system name →
        // nearest scoopable star distance in Ls (-1 = none found). Persisted across sessions so each
        // system is queried at most once, regardless of CSV vs Spansh route source.
        private static readonly Dictionary<string, double> fuelCache =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> enrichInFlight =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object SyncRoot = new object();
        private static readonly List<NeutronPlotWaypoint> Waypoints = new List<NeutronPlotWaypoint>();
        private static PersistedState state = new PersistedState();

        // Spansh auto-plot — shared HttpClient + cancellable background polling task
        private static readonly HttpClient Http = new HttpClient();
        private static volatile bool isPlotting;
        private static string spanshError = string.Empty;
        private static DateTime spanshErrorAt = DateTime.MinValue;
        private static readonly TimeSpan SpanshErrorDisplay = TimeSpan.FromSeconds(8);
        private static CancellationTokenSource spanshCts;

        public static bool IsPlotting => isPlotting;

        private class PersistedState
        {
            public string CsvPath { get; set; } = string.Empty;
            public DateTime CsvLastWriteTimeUtc { get; set; }
            public long CsvFileSizeBytes { get; set; }
            public int WaypointTarget { get; set; }
            public string SystemCurrent { get; set; } = string.Empty;
            public bool IsSpanshRoute { get; set; }
        }

        private static int WaypointMax => Waypoints.Count > 0 ? Waypoints.Count - 1 : 0;

        public static void Initialize()
        {
            lock (SyncRoot)
            {
                LoadState();
                LoadFuelCache();
                if (state.IsSpanshRoute)
                {
                    LoadSpanshRouteFromDisk();
                }
                else
                {
                    ReloadRoute(resetIndexIfChanged: true);
                }
            }
        }

        public static NeutronPlotSnapshot RefreshFromDisk()
        {
            lock (SyncRoot)
            {
                ReloadRoute(resetIndexIfChanged: true);
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot GetSnapshot()
        {
            lock (SyncRoot)
            {
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot CsvNew(string csvPath)
        {
            lock (SyncRoot)
            {
                // Choosing a CSV cancels any in-progress Spansh fetch and replaces the active route.
                CancelSpanshPlot();
                state.IsSpanshRoute = false;
                DeleteSpanshRouteFile();

                state.CsvPath = csvPath ?? string.Empty;
                state.WaypointTarget = 0;
                state.CsvLastWriteTimeUtc = DateTime.MinValue;
                state.CsvFileSizeBytes = 0;

                ReloadRoute(resetIndexIfChanged: true);
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot CsvClear()
        {
            lock (SyncRoot)
            {
                // Clear File cancels any in-progress Spansh fetch and clears the active route.
                CancelSpanshPlot();
                state.IsSpanshRoute = false;
                DeleteSpanshRouteFile();

                state.CsvPath = string.Empty;
                state.CsvLastWriteTimeUtc = default;
                state.CsvFileSizeBytes = 0;
                ClearRouteState();
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot SetWaypointTarget(int waypointTarget)
        {
            lock (SyncRoot)
            {
                state.WaypointTarget = ClampIndex(waypointTarget);
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RouteClear()
        {
            lock (SyncRoot)
            {
                ClearRouteState();
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RouteInitialize()
        {
            lock (SyncRoot)
            {
                state.WaypointTarget = GetInitialWaypoint();
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RouteNext()
        {
            lock (SyncRoot)
            {
                state.WaypointTarget = Math.Min(state.WaypointTarget + 1, WaypointMax);
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RoutePrevious()
        {
            lock (SyncRoot)
            {
                state.WaypointTarget = Math.Max(state.WaypointTarget - 1, 0);
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RouteSelect()
        {
            lock (SyncRoot)
            {
                var snapshot = CreateSnapshot(string.Empty);
                if (!string.IsNullOrEmpty(snapshot.SystemTarget))
                {
                    var text = snapshot.SystemTarget;
                    var thread = new Thread(() =>
                    {
                        try { Clipboard.SetText(text); }
                        catch (Exception ex) { Logger.Instance.LogMessage(TracingLevel.ERROR, "RouteSelect clipboard: " + ex); }
                    });
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    thread.Join();
                }
                return snapshot;
            }
        }

        public static NeutronPlotSnapshot SetSystemCurrent(string systemName)
        {
            lock (SyncRoot)
            {
                state.SystemCurrent = systemName ?? string.Empty;
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RouteAutoAdvance(string systemName)
        {
            lock (SyncRoot)
            {
                state.SystemCurrent = systemName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(systemName) && Waypoints.Count > 0)
                {
                    var lastIndex = -1;
                    for (var i = 0; i < Waypoints.Count; i++)
                    {
                        if (string.Equals(Waypoints[i].SystemName, systemName, StringComparison.OrdinalIgnoreCase))
                            lastIndex = i;
                    }
                    if (lastIndex >= 0)
                    {
                        var next = Math.Min(lastIndex + 1, WaypointMax);
                        if (next != state.WaypointTarget)
                        {
                            state.WaypointTarget = next;
                            SaveState();
                        }
                    }
                }
                return CreateSnapshot(string.Empty);
            }
        }

        private static void ReloadRoute(bool resetIndexIfChanged)
        {
            // Spansh routes live in their own JSON file, not a CSV on disk.
            if (state.IsSpanshRoute)
            {
                LoadSpanshRouteFromDisk();
                return;
            }

            Waypoints.Clear();

            if (string.IsNullOrWhiteSpace(state.CsvPath) || !File.Exists(state.CsvPath))
            {
                return;
            }

            var fileInfo = new FileInfo(state.CsvPath);
            var fileChanged = fileInfo.LastWriteTimeUtc != state.CsvLastWriteTimeUtc ||
                              fileInfo.Length != state.CsvFileSizeBytes;

            var rows = ReadCsvRows(state.CsvPath);

            if (!CsvCheck(rows))
            {
                ClearRouteState();
                SaveState();
                return;
            }

            foreach (var row in rows)
            {
                if (row.Count == 0)
                {
                    continue;
                }

                Waypoints.Add(new NeutronPlotWaypoint
                {
                    SystemName = GetColumn(row, 0),
                    JumpDistance = ParseDouble(GetColumn(row, 1)),
                    DistanceRemaining = ParseDouble(GetColumn(row, 2)),
                    IsRefuel = IsYes(GetColumn(row, 5)),
                    IsNeutron = IsYes(GetColumn(row, 6))
                });
            }

            // Precompute cumulative distances for O(1) distance lookups
            var cumulative = 0.0;
            foreach (var wp in Waypoints)
            {
                cumulative += wp.JumpDistance;
                wp.CumulativeDistance = cumulative;
            }

            state.CsvLastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            state.CsvFileSizeBytes = fileInfo.Length;

            if (fileChanged && resetIndexIfChanged)
            {
                state.WaypointTarget = GetInitialWaypoint();
            }
            else
            {
                state.WaypointTarget = ClampIndex(state.WaypointTarget);
            }

            SaveState();
        }

        private static List<List<string>> ReadCsvRows(string csvPath)
        {
            var rows = new List<List<string>>();
            var isHeader = true;

            foreach (var line in File.ReadLines(csvPath))
            {
                if (isHeader)
                {
                    isHeader = false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                rows.Add(ParseCsvLine(line));
            }

            return rows;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var values = new List<string>();
            var value = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    values.Add(value.ToString());
                    value.Clear();
                }
                else
                {
                    value.Append(c);
                }
            }

            values.Add(value.ToString());
            return values;
        }

        private static string GetColumn(IReadOnlyList<string> row, int index)
        {
            return index < row.Count ? row[index] : string.Empty;
        }

        private static void ClearRouteState()
        {
            Waypoints.Clear();
            state.WaypointTarget = 0;
        }

        private static bool IsYes(string value)
        {
            return string.Equals(value?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static double ParseDouble(string value)
        {
            return double.TryParse(value?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0.0;
        }

        private static bool CsvCheck(List<List<string>> rows)
        {
            if (rows.Count <= 2) return false;
            foreach (var row in rows)
            {
                if (row.Count != 7) return false;
                if (!double.TryParse(row[1]?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return false;
                if (!double.TryParse(row[2]?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return false;
            }
            return true;
        }

        private static int GetInitialWaypoint()
        {
            return Math.Min(1, WaypointMax);
        }

        private static int ClampIndex(int waypointTarget)
        {
            if (Waypoints.Count == 0)
            {
                return 0;
            }

            if (waypointTarget < 0) 
            {
                return 0;
            }

            var maxIndex = Waypoints.Count - 1;
            return waypointTarget > maxIndex ? maxIndex : waypointTarget;
        }

        private static string ComputeRouteStatus(string systemCurrent)
        {
            if (string.IsNullOrWhiteSpace(systemCurrent))
                return "OFF ROUTE";

            var isAtDest   = string.Equals(Waypoints[WaypointMax].SystemName, systemCurrent, StringComparison.OrdinalIgnoreCase);
            if (isAtDest)   return "At Dest";

            var isAtOrigin = string.Equals(Waypoints[0].SystemName, systemCurrent, StringComparison.OrdinalIgnoreCase);
            if (isAtOrigin) return "At Origin";

            for (var i = 1; i < Waypoints.Count - 1; i++)
            {
                if (string.Equals(Waypoints[i].SystemName, systemCurrent, StringComparison.OrdinalIgnoreCase))
                    return "On Route";
            }

            return "OFF ROUTE";
        }

        private static NeutronPlotSnapshot CreateSnapshot(string errorMessage)
        {
            var snapshot = new NeutronPlotSnapshot
            {
                CsvPath = state.CsvPath,
                IsLoaded = Waypoints.Count > 0,
                ErrorMessage = errorMessage,
                WaypointTarget = state.WaypointTarget,
                WaypointMax = WaypointMax,
                SystemCurrent = state.SystemCurrent,
                IsPlotting = isPlotting,
                IsSpanshRoute = state.IsSpanshRoute,
                SpanshError = (DateTime.UtcNow - spanshErrorAt) < SpanshErrorDisplay ? spanshError : string.Empty,
            };

            if (Waypoints.Count == 0)
            {
                snapshot.RouteStatus = "NO ROUTE";
                return snapshot;
            }

            // SystemDestination is always the last row, independent of WaypointTarget
            snapshot.SystemDestination = Waypoints[WaypointMax].SystemName;

            snapshot.RouteStatus = ComputeRouteStatus(state.SystemCurrent);

            // WaypointCurrent — index of SystemCurrent in the route (last occurrence, -1 if off-route)
            var waypointCurrentIndex = -1;
            if (!string.IsNullOrWhiteSpace(state.SystemCurrent))
            {
                for (var i = 0; i < Waypoints.Count; i++)
                {
                    if (string.Equals(Waypoints[i].SystemName, state.SystemCurrent, StringComparison.OrdinalIgnoreCase))
                        waypointCurrentIndex = i;
                }
            }
            snapshot.WaypointCurrent = waypointCurrentIndex;

            // Distance fields based on WaypointCurrent
            if (waypointCurrentIndex >= 0)
            {
                var cumCurrent = Waypoints[waypointCurrentIndex].CumulativeDistance;
                snapshot.DistanceTravelled    = cumCurrent;
                snapshot.DistanceDestination  = Waypoints[WaypointMax].CumulativeDistance - cumCurrent;

                var waypointTargetClamped = Math.Max(0, Math.Min(state.WaypointTarget, WaypointMax));
                snapshot.DistanceTarget = waypointTargetClamped >= waypointCurrentIndex
                    ? Waypoints[waypointTargetClamped].CumulativeDistance - cumCurrent
                    : 0.0;
            }

            // WaypointTarget fields
            var waypointIndex = state.WaypointTarget;
            if (waypointIndex < 0 || waypointIndex >= Waypoints.Count)
            {
                return snapshot;
            }

            var waypoint = Waypoints[waypointIndex];
            snapshot.SystemTarget = waypoint.SystemName;
            snapshot.IsRefuel = waypoint.IsRefuel;
            snapshot.IsNeutron = waypoint.IsNeutron;

            // Phase 2: surface the cached scoopable distance for the target and lazily enrich it.
            // Triggered here so Next/Previous/auto-advance (button + dial) all enrich the new target.
            snapshot.ScoopableLs = fuelCache.TryGetValue(waypoint.SystemName, out var ls) ? ls : double.NaN;
            EnsureEnriched(waypoint.SystemName);
            snapshot.WaypointTarget = Math.Max(0, state.WaypointTarget);

            // Use WaypointCurrent for position metrics; fall back to WaypointTarget-1 when off-route
            var positionIndex = snapshot.WaypointCurrent >= 0
                ? snapshot.WaypointCurrent
                : Math.Max(0, state.WaypointTarget - 1);
            snapshot.JumpRemaining = Math.Max(0, snapshot.WaypointMax - positionIndex);
            snapshot.JumpSummary = $"{positionIndex}/{snapshot.WaypointMax}";
            var totalDistance = snapshot.DistanceTravelled + snapshot.DistanceDestination;
            snapshot.JumpPercent = totalDistance > 0
                ? snapshot.DistanceTravelled / totalDistance * 100.0
                : 0.0;
            snapshot.StarRefuel = waypoint.IsRefuel ? "Refuel" : string.Empty;
            snapshot.StarNeutron = waypoint.IsNeutron ? "Neutron" : string.Empty;
            return snapshot;
        }

        // ---- Spansh auto-plot ------------------------------------------------

        // Kicks off a background neutron-route fetch from Spansh using live ship/target data.
        // Origin = current system, Destination = FSD-targeted system, Range = laden jump range.
        // Returns false (and leaves any existing route untouched) when it cannot start — no FSD target,
        // no origin, no range, or a fetch already running. Callers flash the SD alert icon on false.
        // The current route is only replaced once a new Spansh result successfully arrives.
        public static bool StartSpanshPlot(int efficiency)
        {
            lock (SyncRoot)
            {
                if (isPlotting) return false;

                var from = EliteData.StarSystem;
                var to = EliteData.FsdTargetName;
                var range = ComputeLadenRange();

                if (string.IsNullOrWhiteSpace(from))
                {
                    SetSpanshError("NO ORIGIN");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(to))
                {
                    SetSpanshError("NO TARGET");
                    return false;
                }
                if (range <= 0)
                {
                    SetSpanshError("NO RANGE");
                    return false;
                }

                if (efficiency < 1) efficiency = 1;
                if (efficiency > 100) efficiency = 100;

                spanshError = string.Empty;
                spanshCts?.Cancel();
                spanshCts?.Dispose();
                spanshCts = new CancellationTokenSource();
                isPlotting = true;

                var ct = spanshCts.Token;
                _ = Task.Run(() => FetchFromSpanshAsync(from, to, range, efficiency, ct));
                return true;
            }
        }

        // Unboosted laden jump range — same FSD formula as the buttons, without the neutron boost
        // multiplier (Spansh applies neutron supercharge itself).
        private static double ComputeLadenRange()
        {
            if (EliteData.FSDOptimalMass > 0 && EliteData.FSDMaxFuelPerJump > 0 && EliteData.UnladenMass > 0)
            {
                var totalMass = EliteData.UnladenMass + EliteData.StatusData.Fuel.FuelMain + EliteData.StatusData.Cargo;
                var fsdRange = EliteData.FSDOptimalMass / totalMass
                    * Math.Pow(EliteData.FSDMaxFuelPerJump / EliteData.FSDLinearConstant, 1.0 / EliteData.FSDPowerConstant);
                return fsdRange + EliteData.GuardianFSDBonus;
            }

            return EliteData.BaseJumpRange > 0 ? EliteData.BaseJumpRange : EliteData.LastJumpDistance;
        }

        private static async Task FetchFromSpanshAsync(string from, string to, double range, int efficiency, CancellationToken ct)
        {
            try
            {
                var submitUrl = "https://spansh.co.uk/api/route" +
                                "?from=" + Uri.EscapeDataString(from) +
                                "&to=" + Uri.EscapeDataString(to) +
                                "&range=" + range.ToString("0.00", CultureInfo.InvariantCulture) +
                                "&efficiency=" + efficiency.ToString(CultureInfo.InvariantCulture);

                var submitResp = await Http.GetAsync(submitUrl, ct).ConfigureAwait(false);
                if (!submitResp.IsSuccessStatusCode)
                {
                    SetSpanshError("API ERROR");
                    EndPlotting();
                    return;
                }

                var submitBody = await submitResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var submit = JsonConvert.DeserializeObject<SpanshSubmitResponse>(submitBody);
                if (submit == null || string.IsNullOrEmpty(submit.Job))
                {
                    SetSpanshError("NO JOB");
                    EndPlotting();
                    return;
                }

                // Poll until HTTP 200 (complete). 202 = still processing. No overall timeout.
                var resultsUrl = "https://spansh.co.uk/api/results/" + submit.Job;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var resp = await Http.GetAsync(resultsUrl, ct).ConfigureAwait(false);
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var parsed = JsonConvert.DeserializeObject<SpanshResultResponse>(body);
                        var jumps = parsed?.Result?.SystemJumps;
                        if (jumps == null || jumps.Count == 0)
                        {
                            SetSpanshError("NO ROUTE");
                            EndPlotting();
                            return;
                        }

                        ApplySpanshResult(jumps);
                        EndPlotting();
                        return;
                    }

                    if (resp.StatusCode == HttpStatusCode.Accepted)
                    {
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                        continue;
                    }

                    SetSpanshError("API ERROR");
                    EndPlotting();
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelled by Clear/Choose File — leave route as-is, just stop plotting.
                EndPlotting();
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute FetchFromSpanshAsync " + ex);
                SetSpanshError("API ERROR");
                EndPlotting();
            }
        }

        private static void EndPlotting()
        {
            lock (SyncRoot) { isPlotting = false; }
        }

        private static void SetSpanshError(string message)
        {
            spanshError = message;
            spanshErrorAt = DateTime.UtcNow;
        }

        private static void CancelSpanshPlot()
        {
            try { spanshCts?.Cancel(); }
            catch (Exception ex) { Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute CancelSpanshPlot " + ex); }
            isPlotting = false;
            spanshError = string.Empty;
        }

        // Converts the Spansh system_jumps array into internal waypoints + persists to JSON.
        // distance_jumped is the per-hop distance (verified against XYZ coords); cumulative is summed
        // exactly like the CSV path. The neutron API provides no scoopable/refuel data (Phase 2).
        private static void ApplySpanshResult(List<SpanshSystemJump> jumps)
        {
            lock (SyncRoot)
            {
                Waypoints.Clear();

                var cumulative = 0.0;
                foreach (var j in jumps)
                {
                    cumulative += j.DistanceJumped;
                    Waypoints.Add(new NeutronPlotWaypoint
                    {
                        SystemName = j.System ?? string.Empty,
                        JumpDistance = j.DistanceJumped,
                        DistanceRemaining = j.DistanceLeft,
                        IsNeutron = j.NeutronStar,
                        IsRefuel = false,
                        CumulativeDistance = cumulative,
                        Id64 = j.Id64
                    });
                }

                state.IsSpanshRoute = true;
                state.CsvPath = string.Empty;
                state.CsvLastWriteTimeUtc = default;
                state.CsvFileSizeBytes = 0;
                state.WaypointTarget = GetInitialWaypoint();

                // Anchor route position to where the player actually is right now.
                if (!string.IsNullOrWhiteSpace(EliteData.StarSystem))
                    state.SystemCurrent = EliteData.StarSystem;

                SaveSpanshRoute();
                SaveState();
            }
        }

        private static void LoadSpanshRouteFromDisk()
        {
            Waypoints.Clear();
            try
            {
                var path = GetSpanshRouteFilePath();
                if (!File.Exists(path))
                {
                    state.IsSpanshRoute = false;
                    return;
                }

                var wps = JsonConvert.DeserializeObject<List<NeutronPlotWaypoint>>(File.ReadAllText(path));
                if (wps == null || wps.Count == 0)
                {
                    state.IsSpanshRoute = false;
                    return;
                }

                Waypoints.AddRange(wps);
                state.WaypointTarget = ClampIndex(state.WaypointTarget);
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute LoadSpanshRouteFromDisk " + ex);
                Waypoints.Clear();
                state.IsSpanshRoute = false;
            }
        }

        private static void SaveSpanshRoute()
        {
            try
            {
                var path = GetSpanshRouteFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(Waypoints, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute SaveSpanshRoute " + ex);
            }
        }

        private static void DeleteSpanshRouteFile()
        {
            try
            {
                var path = GetSpanshRouteFilePath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute DeleteSpanshRouteFile " + ex);
            }
        }

        private static string GetSpanshRouteFilePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "EliteDangerousStreamDeck", SpanshRouteFileName);
        }

        private class SpanshSubmitResponse
        {
            [JsonProperty("job")] public string Job { get; set; }
            [JsonProperty("status")] public string Status { get; set; }
        }

        private class SpanshResultResponse
        {
            [JsonProperty("result")] public SpanshResult Result { get; set; }
        }

        private class SpanshResult
        {
            [JsonProperty("system_jumps")] public List<SpanshSystemJump> SystemJumps { get; set; }
        }

        private class SpanshSystemJump
        {
            [JsonProperty("system")] public string System { get; set; }
            [JsonProperty("distance_jumped")] public double DistanceJumped { get; set; }
            [JsonProperty("distance_left")] public double DistanceLeft { get; set; }
            [JsonProperty("neutron_star")] public bool NeutronStar { get; set; }
            [JsonProperty("jumps")] public int Jumps { get; set; }
            [JsonProperty("id64")] public long Id64 { get; set; }
        }

        // ---- Phase 2: EDSM fuel-star enrichment ------------------------------

        // Fires a one-time background EDSM lookup for a system if not already cached or in flight.
        // Must be called under SyncRoot. Cheap no-op once a system is known.
        private static void EnsureEnriched(string systemName)
        {
            if (string.IsNullOrWhiteSpace(systemName)) return;
            if (fuelCache.ContainsKey(systemName)) return;
            if (!enrichInFlight.Add(systemName)) return;
            _ = Task.Run(() => EnrichFromEdsmAsync(systemName));
        }

        private static async Task EnrichFromEdsmAsync(string systemName)
        {
            try
            {
                var url = "https://www.edsm.net/api-system-v1/bodies?systemName=" + Uri.EscapeDataString(systemName);
                var resp = await Http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return; // leave uncached so a later trigger retries

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var parsed = JsonConvert.DeserializeObject<EdsmBodiesResponse>(body);

                // Nearest scoopable star by arrival distance; -1 when the system has none (or is unknown).
                var nearest = -1.0;
                if (parsed?.Bodies != null)
                {
                    foreach (var b in parsed.Bodies)
                    {
                        if (!string.Equals(b.Type, "Star", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!IsScoopable(b.SubType)) continue;
                        if (nearest < 0 || b.DistanceToArrival < nearest)
                            nearest = b.DistanceToArrival;
                    }
                }

                lock (SyncRoot)
                {
                    fuelCache[systemName] = nearest;
                    SaveFuelCache();
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute EnrichFromEdsmAsync " + ex);
            }
            finally
            {
                lock (SyncRoot) { enrichInFlight.Remove(systemName); }
            }
        }

        // Scoopable = main-sequence KGBFOAM. EDSM subType examples: "M (Red dwarf) Star",
        // "K (Yellow-Orange) Star", "A (Blue-White) Star". Non-scoopable names start with a word
        // ("Neutron Star", "White Dwarf", "Black Hole", "T Tauri Star") so the class letter must be
        // followed by a space or '(' to avoid false matches like "Black".
        private static bool IsScoopable(string subType)
        {
            if (string.IsNullOrEmpty(subType)) return false;
            var c = char.ToUpperInvariant(subType[0]);
            if ("OBAFGKM".IndexOf(c) < 0) return false;
            return subType.Length == 1 || subType[1] == ' ' || subType[1] == '(';
        }

        private static void LoadFuelCache()
        {
            try
            {
                var path = GetFuelCacheFilePath();
                if (!File.Exists(path)) return;

                var loaded = JsonConvert.DeserializeObject<Dictionary<string, double>>(File.ReadAllText(path));
                fuelCache.Clear();
                if (loaded != null)
                {
                    foreach (var kvp in loaded)
                        fuelCache[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute LoadFuelCache " + ex);
            }
        }

        private static void SaveFuelCache()
        {
            try
            {
                var path = GetFuelCacheFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(fuelCache, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute SaveFuelCache " + ex);
            }
        }

        private static string GetFuelCacheFilePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "EliteDangerousStreamDeck", FuelCacheFileName);
        }

        private class EdsmBodiesResponse
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("id64")] public long Id64 { get; set; }
            [JsonProperty("bodies")] public List<EdsmBody> Bodies { get; set; }
        }

        private class EdsmBody
        {
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("subType")] public string SubType { get; set; }
            [JsonProperty("distanceToArrival")] public double DistanceToArrival { get; set; }
        }

        private static void LoadState()
        {
            try
            {
                var path = GetStateFilePath();
                if (!File.Exists(path))
                {
                    state = new PersistedState();
                    return;
                }

                state = JsonConvert.DeserializeObject<PersistedState>(File.ReadAllText(path)) ?? new PersistedState();
            }
            catch (Exception ex)
            {
                state = new PersistedState();
                Logger.Instance.LogMessage(TracingLevel.FATAL, "NeutronPlotRoute LoadState " + ex);
            }
        }

        private static void SaveState()
        {
            try
            {
                var path = GetStateFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.FATAL, "NeutronPlotRoute SaveState " + ex);
            }
        }

        private static string GetStateFilePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "EliteDangerousStreamDeck", StateFileName);
        }
    }
}
