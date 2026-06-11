using System;
using System.Collections.Generic;
using EliteJournalReader;
using EliteJournalReader.Events;

namespace Elite
{
    public class NavRouteSnapshot
    {
        public bool IsLoaded { get; set; }
        public int WaypointCurrent { get; set; } = 0;
        public int WaypointMax { get; set; } = 0;
        public string SystemCurrent { get; set; } = string.Empty;
        public string SystemTarget { get; set; } = string.Empty;
        public string SystemDestination { get; set; } = string.Empty;
        public int JumpsRemaining { get; set; }
        public string JumpSummary { get; set; } = string.Empty;
        public double TripPercent { get; set; }
        public double HopDistance { get; set; }
        public double DistanceTravelled { get; set; }
        public double DistanceDestination { get; set; }
        public bool IsFuelStar { get; set; }
        public bool IsNeutronStar { get; set; }
        public int JumpsToNextFuelStar { get; set; } = -1;
        public int EstJumpsInTank { get; set; } = -1;
    }

    public static class NavRouteService
    {
        private static readonly object SyncRoot = new object();

        // Full waypoint list including origin — NOT Skip(1) — so we have all StarPos
        private static readonly List<RouteItem> Waypoints = new List<RouteItem>();
        private static double[] _cumulativeDistance = Array.Empty<double>();

        // Index into Waypoints of the player's current position. 0 = at origin.
        private static int _waypointCurrent = 0;

        private static readonly HashSet<string> ScoopableClasses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "O", "B", "A", "F", "G", "K", "M" };

        public static void Initialize()
        {
            // NavRouteWatcher fires immediately on StartWatching if NavRoute.json exists,
            // so no file-read needed here — just ensure state is clean.
            lock (SyncRoot)
            {
                _waypointCurrent = 0;
            }
        }

        public static void OnNavRouteUpdated(object sender, NavRouteEvent.NavRouteEventArgs info)
        {
            lock (SyncRoot)
            {
                Waypoints.Clear();

                if (info?.Route != null && info.Route.Length >= 2)
                {
                    foreach (var r in info.Route)
                        Waypoints.Add(r);

                    BuildCumulativeDistances();

                    // Try to position WaypointCurrent at the player's current system
                    var idx = FindSystem(EliteData.StarSystem);
                    _waypointCurrent = idx >= 0 ? idx : 0;
                }
                else
                {
                    _cumulativeDistance = Array.Empty<double>();
                    _waypointCurrent = 0;
                }
            }
        }

        // Called on FSDJump — advances WaypointCurrent to the jumped-to system
        public static void AutoAdvance(string systemName)
        {
            lock (SyncRoot)
            {
                if (Waypoints.Count < 2) return;
                var idx = FindSystem(systemName);
                if (idx >= 0)
                    _waypointCurrent = idx;
            }
        }

        public static NavRouteSnapshot GetSnapshot()
        {
            lock (SyncRoot)
            {
                var snap = new NavRouteSnapshot
                {
                    SystemCurrent = EliteData.StarSystem ?? string.Empty
                };

                if (Waypoints.Count < 2)
                    return snap; // IsLoaded = false

                snap.IsLoaded     = true;
                snap.WaypointMax  = Waypoints.Count - 1;
                snap.WaypointCurrent = _waypointCurrent;

                // Jump counts
                snap.JumpsRemaining = snap.WaypointMax - _waypointCurrent;
                snap.JumpSummary    = $"{_waypointCurrent}/{snap.WaypointMax}";
                snap.TripPercent    = snap.WaypointMax > 0
                    ? (double)_waypointCurrent / snap.WaypointMax * 100.0
                    : 0.0;

                // System names
                var targetIdx = _waypointCurrent + 1;
                if (targetIdx <= snap.WaypointMax)
                    snap.SystemTarget = Waypoints[targetIdx].StarSystem;
                snap.SystemDestination = Waypoints[snap.WaypointMax].StarSystem;

                // Distances
                if (_cumulativeDistance.Length > snap.WaypointMax)
                {
                    snap.DistanceTravelled   = _cumulativeDistance[_waypointCurrent];
                    snap.DistanceDestination = _cumulativeDistance[snap.WaypointMax] - _cumulativeDistance[_waypointCurrent];
                }

                if (targetIdx <= snap.WaypointMax)
                    snap.HopDistance = HopDist(_waypointCurrent, targetIdx);

                // Star type flags for the TARGET system
                if (targetIdx <= snap.WaypointMax)
                {
                    var targetClass = Waypoints[targetIdx].StarClass ?? string.Empty;
                    snap.IsFuelStar    = ScoopableClasses.Contains(targetClass);
                    snap.IsNeutronStar = string.Equals(targetClass, "N", StringComparison.OrdinalIgnoreCase);
                }

                // Jumps to next fuel star (scan forward from target)
                snap.JumpsToNextFuelStar = CalcJumpsToNextFuelStar(_waypointCurrent);

                // Estimated jumps remaining in tank
                snap.EstJumpsInTank = CalcEstJumpsInTank(_waypointCurrent);

                return snap;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static void BuildCumulativeDistances()
        {
            _cumulativeDistance = new double[Waypoints.Count];
            _cumulativeDistance[0] = 0.0;
            for (var i = 1; i < Waypoints.Count; i++)
                _cumulativeDistance[i] = _cumulativeDistance[i - 1] + HopDist(i - 1, i);
        }

        private static double HopDist(int fromIdx, int toIdx)
        {
            var a = Waypoints[fromIdx].StarPos;
            var b = Waypoints[toIdx].StarPos;
            var dx = (double)(b.X - a.X);
            var dy = (double)(b.Y - a.Y);
            var dz = (double)(b.Z - a.Z);
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static int FindSystem(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (var i = 0; i < Waypoints.Count; i++)
                if (string.Equals(Waypoints[i].StarSystem, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private static int CalcJumpsToNextFuelStar(int fromIdx)
        {
            // Scan forward from the hop AFTER fromIdx
            for (var i = fromIdx + 1; i < Waypoints.Count; i++)
            {
                if (ScoopableClasses.Contains(Waypoints[i].StarClass ?? string.Empty))
                    return i - fromIdx;
            }
            return -1; // none found
        }

        private static int CalcEstJumpsInTank(int fromIdx)
        {
            if (EliteData.FSDOptimalMass <= 0 || EliteData.FSDMaxFuelPerJump <= 0 ||
                EliteData.FSDLinearConstant <= 0 || EliteData.UnladenMass <= 0)
                return -1;

            var fuel     = EliteData.StatusData.Fuel.FuelMain;
            var maxFuel  = EliteData.FSDMaxFuelPerJump * 8; // approximate tank size; use fuelMain as cap below
            var cargo    = EliteData.StatusData.Cargo;
            var jumps    = 0;

            for (var i = fromIdx; i < Waypoints.Count - 1; i++)
            {
                var dist        = HopDist(i, i + 1);
                var totalMass   = EliteData.UnladenMass + fuel + cargo;
                var fuelNeeded  = EliteData.FSDLinearConstant *
                    Math.Pow(dist * totalMass / EliteData.FSDOptimalMass, EliteData.FSDPowerConstant);
                fuelNeeded = Math.Min(fuelNeeded, EliteData.FSDMaxFuelPerJump);

                if (fuel < fuelNeeded) break;

                fuel -= fuelNeeded;
                jumps++;

                // Full scoop at scoopable stars
                if (ScoopableClasses.Contains(Waypoints[i + 1].StarClass ?? string.Empty))
                    fuel = EliteData.StatusData.Fuel.FuelMain; // restore to current tank level as proxy for full
            }

            return jumps;
        }
    }
}
