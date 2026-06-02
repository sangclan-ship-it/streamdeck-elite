using System;
using System.Threading;
using System.Threading.Tasks;
using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Elite.Buttons
{
    [PluginActionId("com.mhwlng.elite.neutronplotdial")]
    public class NeutronPlotDial : EliteDialBase
    {
        protected class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings() => new PluginSettings
            {
                InfoTouch         = string.Empty,
                InfoTouchColor    = "#ffffff",
                FunctionLongPress = string.Empty,
            };

            [JsonProperty(PropertyName = "infoTouch")]
            public string InfoTouch { get; set; }

            [JsonProperty(PropertyName = "infoTouchColor")]
            public string InfoTouchColor { get; set; }

            [JsonProperty(PropertyName = "functionLongPress")]
            public string FunctionLongPress { get; set; }
        }

        private static readonly TimeSpan LongPressThreshold = TimeSpan.FromMilliseconds(2000);

        private PluginSettings settings;
        private DateTime dialPressedAt = DateTime.MinValue;

        public NeutronPlotDial(SDConnection connection, InitialPayload payload) : base(connection, payload)
        {
            if (payload.Settings == null || payload.Settings.Count == 0)
            {
                settings = PluginSettings.CreateDefaultSettings();
                Connection.SetSettingsAsync(JObject.FromObject(settings)).Wait();
            }
            else
            {
                settings = payload.Settings.ToObject<PluginSettings>();
            }
        }

        public override void DialRotate(DialRotatePayload payload)
        {
            var ticks = Math.Abs(payload.Ticks);
            if (payload.Ticks > 0)
            {
                for (var i = 0; i < ticks; i++)
                    NeutronPlotRoute.RouteNext();
            }
            else if (payload.Ticks < 0)
            {
                for (var i = 0; i < ticks; i++)
                    NeutronPlotRoute.RoutePrevious();
            }
        }

        public override void DialDown(DialPayload payload)
        {
            dialPressedAt = DateTime.UtcNow;
        }

        public override void DialUp(DialPayload payload)
        {
            var elapsed = DateTime.UtcNow - dialPressedAt;
            dialPressedAt = DateTime.MinValue;

            if (elapsed >= LongPressThreshold)
                ExecuteFunction(settings.FunctionLongPress);
            else
                NeutronPlotRoute.RouteSelect();
        }

        public override void TouchPress(TouchpadPressPayload payload)
        {
            if (payload.IsLongPress)
                ExecuteFunction(settings.FunctionLongPress);
            else
                NeutronPlotRoute.RouteSelect();
        }

        public override async void OnTick()
        {
            base.OnTick();
            try
            {
                var snapshot = NeutronPlotRoute.GetSnapshot();
                var value = snapshot.IsLoaded
                    ? GetDisplayValue(settings.InfoTouch, snapshot)
                    : "NO ROUTE";

                await Connection.SetFeedbackAsync(new JObject
                {
                    ["title"] = "NEUTRON",
                    ["value"] = value ?? string.Empty,
                });
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotDial OnTick: " + ex);
            }
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            BarRaider.SdTools.Tools.AutoPopulateSettings(settings, payload.Settings);
        }

        private static string GetDisplayValue(string infoType, NeutronPlotSnapshot snapshot)
        {
            switch (infoType)
            {
                case "targetSystemName":    return snapshot.SystemTarget;
                case "previousSystemName":  return snapshot.SystemPrevious;
                case "nextSystemName":      return snapshot.SystemNext;
                case "jumpDistance":        return $"{snapshot.JumpDistance:#,##0.0} LY";
                case "destinationDistance": return $"{snapshot.DestinationDistance:#,##0.0} LY";
                case "currentJumpNumber":   return snapshot.WaypointCurrent.ToString();
                case "totalJumps":          return snapshot.WaypointMax.ToString();
                case "jumpsRemaining":      return snapshot.JumpRemaining.ToString();
                case "jumpSummary":         return snapshot.JumpSummary;
                case "tripPercentage":      return $"{snapshot.JumpPercent:F1}%";
                case "refuelAtTarget":      return snapshot.StarRefuel;
                case "neutronAtTarget":     return snapshot.StarNeutron;
                default:                    return string.Empty;
            }
        }

        private static void ExecuteFunction(string function)
        {
            switch (function)
            {
                case "initializeRoute":
                    NeutronPlotRoute.RouteInitialize();
                    break;
                case "clearRoute":
                    NeutronPlotRoute.CsvClear();
                    break;
            }
        }
    }
}
