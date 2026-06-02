using BarRaider.SdTools;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;
using System.Threading;
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
        public string SystemPrevious { get; set; } = string.Empty;
        public string SystemNext { get; set; } = string.Empty;
        public string SystemDestination { get; set; } = string.Empty;
        public int JumpRemaining { get; set; }
        public double JumpDistance { get; set; }
        public string JumpSummary { get; set; } = string.Empty;
        public double JumpPercent { get; set; }
        public double DistanceTravelled { get; set; }
        public double DistanceTarget { get; set; }
        public double DistanceDestination { get; set; }
        public bool IsRefuel { get; set; }
        public bool IsNeutron { get; set; }
        public string StarRefuel { get; set; } = string.Empty;
        public string StarNeutron { get; set; } = string.Empty;
    }

    public static class NeutronPlotRoute
    {
        private const string StateFileName = "neutronPlotState.json";
        private static readonly object SyncRoot = new object();
        private static readonly List<NeutronPlotWaypoint> Waypoints = new List<NeutronPlotWaypoint>();
        private static PersistedState state = new PersistedState();

        private class PersistedState
        {
            public string CsvPath { get; set; } = string.Empty;
            public DateTime CsvLastWriteTimeUtc { get; set; }
            public long CsvFileSizeBytes { get; set; }
            public int WaypointTarget { get; set; }
            public string SystemCurrent { get; set; } = string.Empty;
        }

        private static int WaypointMax => Waypoints.Count > 0 ? Waypoints.Count - 1 : 0;

        public static void Initialize()
        {
            lock (SyncRoot)
            {
                LoadState();
                ReloadRoute(resetIndexIfChanged: true);
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
            snapshot.JumpDistance = waypoint.JumpDistance;
            snapshot.IsRefuel = waypoint.IsRefuel;
            snapshot.IsNeutron = waypoint.IsNeutron;
            snapshot.WaypointTarget = Math.Max(0, state.WaypointTarget);
            snapshot.JumpRemaining = Math.Max(0, snapshot.WaypointMax - snapshot.WaypointTarget);
            snapshot.JumpSummary = $"{snapshot.WaypointTarget}/{snapshot.WaypointMax}";
            snapshot.JumpPercent = snapshot.WaypointTarget / (double)Math.Max(1, snapshot.WaypointMax) * 100.0;
            snapshot.StarRefuel = waypoint.IsRefuel ? "Refuel" : string.Empty;
            snapshot.StarNeutron = waypoint.IsNeutron ? "Neutron" : string.Empty;
            snapshot.SystemPrevious = state.WaypointTarget == 0
                ? string.Empty
                : Waypoints[state.WaypointTarget - 1].SystemName;
            snapshot.SystemNext = state.WaypointTarget == WaypointMax
                ? string.Empty
                : Waypoints[state.WaypointTarget + 1].SystemName;
            return snapshot;
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
