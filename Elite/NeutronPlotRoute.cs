using BarRaider.SdTools;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;

namespace Elite
{
    public class NeutronPlotWaypoint
    {
        public string SystemName { get; set; } = string.Empty;
        public double JumpDistance {get; set; }
        public double DistanceRemaining { get; set; }
        public bool IsRefuel { get; set; }
        public bool IsNeutron { get; set; }
    }

    public class NeutronPlotSnapshot
    {
        public string CsvPath { get; set; } = string.Empty;
        public bool IsLoaded { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public int WaypointCurrent { get; set; }
        public int WaypointMax { get; set; }
        public string SystemTarget { get; set; }
        public string SystemPrevious { get; set; }
        public string SystemNext { get; set; }
        public string SystemDestination  { get; set; }
        public int JumpRemaining { get; set; }
        public double JumpDistance {get; set; }
        public string JumpSummary { get; set; }
        public double JumpPercent {get; set; }
        public double DestinationDistance { get; set; }
        public bool IsRefuel { get; set; }
        public bool IsNeutron { get; set; }
        public string StarRefuel { get; set; }
        public string StarNeutron { get; set; }
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
            public int WaypointCurrent { get; set; }
            public int WaypointMax { get; set; }
        }

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

        public static NeutronPlotSnapshot SetCsvPath(string csvPath)
        {
            lock (SyncRoot)
            {
                state.CsvPath = csvPath ?? string.Empty;
                state.WaypointCurrent = 0;
                state.CsvLastWriteTimeUtc = DateTime.MinValue;
                state.CsvFileSizeBytes = 0;

                ReloadRoute(resetIndexIfChanged: true);
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot SetWaypointCurrent(int waypointCurrent)
        {
            lock (SyncRoot)
            {
                state.WaypointCurrent = ClampIndex(waypointCurrent);
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot ClearRoute()
        {
            lock (SyncRoot)
            {
                Waypoints.Clear();
                state = new PersistedState();
                SaveState();
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

            state.WaypointMax = Waypoints.Count > 0 ? Waypoints.Count - 1 : 0;
            state.CsvLastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            state.CsvFileSizeBytes = fileInfo.Length;

            if (fileChanged && resetIndexIfChanged)
            {
                state.WaypointCurrent = GetInitialWaypoint();
            }
            else
            {
                state.WaypointCurrent = ClampIndex(state.WaypointCurrent);
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

        private static bool IsYes(string value)
        {
            return string.Equals(value?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static double ParseDouble(string value)
        {
            return double.TryParse(value?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0.0;
        }

        private static int GetInitialWaypoint()
        {
            return Math.Min(1, state.WaypointMax);
        }

        private static int ClampIndex(int waypointCurrent)
        {
            if (Waypoints.Count == 0)
            {
                return 0;
            }

            if (waypointCurrent < 0) 
            {
                return 0;
            }

            var maxIndex = Waypoints.Count - 1;
            return waypointCurrent > maxIndex ? maxIndex : waypointCurrent;
        }

        private static NeutronPlotSnapshot CreateSnapshot(string errorMessage)
        {
            var snapshot = new NeutronPlotSnapshot
            {
                CsvPath = state.CsvPath,
                IsLoaded = Waypoints.Count > 0,
                ErrorMessage = errorMessage,
                WaypointCurrent = state.WaypointCurrent,
                WaypointMax = state.WaypointMax,
            };

            if (Waypoints.Count == 0)
            {
                return snapshot;
            }

            // SystemDestination is always the last row, independent of WaypointCurrent
            snapshot.SystemDestination = Waypoints[state.WaypointMax].SystemName;

            var waypointIndex = state.WaypointCurrent;
            if (waypointIndex < 0 || waypointIndex >= Waypoints.Count)
            {
                return snapshot;
            }

            var waypoint = Waypoints[waypointIndex];
            snapshot.SystemTarget = waypoint.SystemName;
            snapshot.JumpDistance = waypoint.JumpDistance;
            snapshot.DestinationDistance = waypoint.DistanceRemaining;
            snapshot.IsRefuel = waypoint.IsRefuel;
            snapshot.IsNeutron = waypoint.IsNeutron;
            snapshot.WaypointCurrent = Math.Max(0, state.WaypointCurrent);
            snapshot.JumpRemaining = Math.Max(0, snapshot.WaypointMax - snapshot.WaypointCurrent);
            snapshot.SystemPrevious = state.WaypointCurrent == 0
                ? string.Empty
                : Waypoints[state.WaypointCurrent - 1].SystemName;
            snapshot.SystemNext = state.WaypointCurrent == state.WaypointMax
                ? string.Empty
                : Waypoints[state.WaypointCurrent + 1].SystemName;
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
