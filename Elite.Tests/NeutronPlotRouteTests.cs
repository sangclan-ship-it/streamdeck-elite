using System;
using System.IO;
using Elite;
using Xunit;

namespace Elite.Tests
{
    public class NeutronPlotRouteTests : IDisposable
    {
        private static readonly string CsvPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "TestData", "Sample CSV File.csv");

        public NeutronPlotRouteTests()
        {
            NeutronPlotRoute.CsvClear();
        }

        public void Dispose()
        {
            NeutronPlotRoute.CsvClear();
        }

        [Fact]
        public void CsvNew_WithSampleFile_SetsCorrectInitialState()
        {
            NeutronPlotRoute.CsvNew(CsvPath);

            // Sitting at the origin (waypoint 0). JumpSummary reports jumps *completed* (current
            // position), so at the origin it is "0/52" while the next target is waypoint 1.
            var snapshot = NeutronPlotRoute.SetSystemCurrent("Colonia");

            Assert.True(snapshot.IsLoaded);
            Assert.Equal(1, snapshot.WaypointTarget);
            Assert.Equal(52, snapshot.WaypointMax);
            Assert.Equal("Eol Prou RS-T d3-172", snapshot.SystemTarget);
            Assert.Equal("Neutron", snapshot.StarNeutron);
            Assert.Equal(0, snapshot.WaypointCurrent);
            Assert.Equal("0/52", snapshot.JumpSummary);
        }

        [Fact]
        public void RouteNext_CalledThreeTimes_AdvancesToCorrectWaypoint()
        {
            NeutronPlotRoute.CsvNew(CsvPath);

            NeutronPlotRoute.RouteNext();
            NeutronPlotRoute.RouteNext();
            NeutronPlotRoute.RouteNext();

            // JumpSummary tracks the player's actual position (jumps completed), independent of the
            // target waypoint: physically at waypoint 3 while targeting waypoint 4 → "3/52".
            var snapshot = NeutronPlotRoute.SetSystemCurrent("Dryooe Flyou XY-I d9-180");

            Assert.Equal(4, snapshot.WaypointTarget);
            Assert.Equal("Dryio Flyuae GR-W e1-887", snapshot.SystemTarget);
            Assert.Equal("Neutron", snapshot.StarNeutron);
            Assert.Equal(3, snapshot.WaypointCurrent);
            Assert.Equal("3/52", snapshot.JumpSummary);
        }

        [Fact]
        public void RouteNext_BeyondWaypointMax_ClampsAtMax()
        {
            NeutronPlotRoute.CsvNew(CsvPath);

            for (var i = 0; i < 100; i++)
                NeutronPlotRoute.RouteNext();

            var snapshot = NeutronPlotRoute.GetSnapshot();

            Assert.Equal(52, snapshot.WaypointTarget);
            Assert.Equal(52, snapshot.WaypointMax);
        }

        [Fact]
        public void RoutePrevious_BelowZero_ClampsAtZero()
        {
            NeutronPlotRoute.CsvNew(CsvPath);

            // go back past the start
            for (var i = 0; i < 10; i++)
                NeutronPlotRoute.RoutePrevious();

            var snapshot = NeutronPlotRoute.GetSnapshot();

            Assert.Equal(0, snapshot.WaypointTarget);
        }

        [Fact]
        public void CsvNew_WithTooFewRows_DoesNotLoad()
        {
            var tempPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempPath,
                    "\"System Name\",\"Distance\",\"Distance Remaining\",\"Fuel Left\",\"Fuel Used\",\"Refuel\",\"Neutron Star\"\r\n" +
                    "\"Origin\",\"0\",\"100\",\"128\",\"0\",\"No\",\"No\"\r\n" +
                    "\"Destination\",\"100\",\"0\",\"120\",\"8\",\"No\",\"Yes\"\r\n");

                var snapshot = NeutronPlotRoute.CsvNew(tempPath);

                Assert.False(snapshot.IsLoaded);
                Assert.Equal(0, snapshot.WaypointTarget);
            }
            finally
            {
                File.Delete(tempPath);
            }
        }

        [Fact]
        public void CsvNew_WithWrongColumnCount_DoesNotLoad()
        {
            var tempPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempPath,
                    "\"System Name\",\"Distance\",\"Distance Remaining\"\r\n" +
                    "\"A\",\"0\",\"100\"\r\n" +
                    "\"B\",\"50\",\"50\"\r\n" +
                    "\"C\",\"100\",\"0\"\r\n");

                var snapshot = NeutronPlotRoute.CsvNew(tempPath);

                Assert.False(snapshot.IsLoaded);
                Assert.Equal(0, snapshot.WaypointTarget);
            }
            finally
            {
                File.Delete(tempPath);
            }
        }

        [Fact]
        public void CsvNew_WithNonNumericDistanceColumn_DoesNotLoad()
        {
            var tempPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempPath,
                    "\"System Name\",\"Distance\",\"Distance Remaining\",\"Fuel Left\",\"Fuel Used\",\"Refuel\",\"Neutron Star\"\r\n" +
                    "\"A\",\"NOT_A_NUMBER\",\"100\",\"128\",\"0\",\"No\",\"No\"\r\n" +
                    "\"B\",\"NOT_A_NUMBER\",\"50\",\"120\",\"8\",\"No\",\"Yes\"\r\n" +
                    "\"C\",\"NOT_A_NUMBER\",\"0\",\"112\",\"8\",\"No\",\"Yes\"\r\n");

                var snapshot = NeutronPlotRoute.CsvNew(tempPath);

                Assert.False(snapshot.IsLoaded);
                Assert.Equal(0, snapshot.WaypointTarget);
            }
            finally
            {
                File.Delete(tempPath);
            }
        }
    }
}
