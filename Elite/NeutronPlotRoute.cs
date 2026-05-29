using BarRaider.SdTools;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Elite
{
    public class NeutronPlotWaypoint
    {
        public string SystemName { get; set; } = string.Empty;
        public string DistanceRemaining { get; set; } = string.Empty;
        public bool IsRefuel { get; set; }
        public bool IsNeutron { get; set; }
    }

    public class NeutronPlotSnapshot
    {
        public string CsvPath { get; set; } = string.Empty;
        public bool IsLoaded { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public int IndexValue { get; set; }
        public int TotalRows { get; set; }
        public int TotalJumps { get; set; }
        public int CurrentJump { get; set; }
        public int JumpsRemaining { get; set; }
        public string TargetSystem { get; set; } = string.Empty;
        public string DestinationDistance { get; set; } = string.Empty;
        public bool IsRefuel { get; set; }
        public bool IsNeutron { get; set; }
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
            public long CsvLength { get; set; }
            public int IndexValue { get; set; }
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
                state.IndexValue = 0;
                state.CsvLastWriteTimeUtc = DateTime.MinValue;
                state.CsvLength = 0;

                ReloadRoute(resetIndexIfChanged: true);
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot SetIndexValue(int indexValue)
        {
            lock (SyncRoot)
            {
                state.IndexValue = ClampIndex(indexValue);
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
                              fileInfo.Length != state.CsvLength;

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
                    DistanceRemaining = GetColumn(row, 2),
                    IsRefuel = IsYes(GetColumn(row, 5)),
                    IsNeutron = IsYes(GetColumn(row, 6))
                });
            }

            state.CsvLastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            state.CsvLength = fileInfo.Length;

            if (fileChanged && resetIndexIfChanged)
            {
                state.IndexValue = GetInitialJumpIndex();
            }
            else
            {
                state.IndexValue = ClampIndex(state.IndexValue);
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

        private static int GetInitialJumpIndex()
        {
            if (Waypoints.Count >= 2)
            {
                return Math.Min(3, Waypoints.Count + 1);
            }

            return Waypoints.Count == 1 ? 2 : 0;
        }

        private static int ClampIndex(int indexValue)
        {
            if (Waypoints.Count == 0)
            {
                return 0;
            }

            if (indexValue < 2)
            {
                return 2;
            }

            var maxIndex = Waypoints.Count + 1;
            return indexValue > maxIndex ? maxIndex : indexValue;
        }

        private static NeutronPlotSnapshot CreateSnapshot(string errorMessage)
        {
            var snapshot = new NeutronPlotSnapshot
            {
                CsvPath = state.CsvPath,
                IsLoaded = Waypoints.Count > 0,
                ErrorMessage = errorMessage,
                IndexValue = state.IndexValue,
                TotalRows = Waypoints.Count + 1,
                TotalJumps = Math.Max(0, Waypoints.Count - 1)
            };

            if (Waypoints.Count == 0 || state.IndexValue == 0)
            {
                return snapshot;
            }

            var waypointIndex = state.IndexValue - 2;
            if (waypointIndex < 0 || waypointIndex >= Waypoints.Count)
            {
                return snapshot;
            }

            var waypoint = Waypoints[waypointIndex];
            snapshot.TargetSystem = waypoint.SystemName;
            snapshot.DestinationDistance = waypoint.DistanceRemaining;
            snapshot.IsRefuel = waypoint.IsRefuel;
            snapshot.IsNeutron = waypoint.IsNeutron;
            snapshot.CurrentJump = Math.Max(0, state.IndexValue - 2);
            snapshot.JumpsRemaining = Math.Max(0, snapshot.TotalJumps - snapshot.CurrentJump);
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
