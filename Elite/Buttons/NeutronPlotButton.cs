using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BarRaider.SdTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Elite.Buttons
{
    [PluginActionId("com.mhwlng.elite.neutronplot")]
    public class NeutronPlotButton : EliteKeypadBase
    {
        protected class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings() => new PluginSettings
            {
                CsvPath           = string.Empty,
                ClearFile         = string.Empty,
                Function          = "nextSystem",
                FunctionLongPress = string.Empty,
                InfoUpper         = string.Empty,
                InfoUpperColor    = "#ffffff",
                InfoMid           = string.Empty,
                InfoMidColor      = "#ffffff",
                InfoLower         = string.Empty,
                InfoLowerColor    = "#ffffff"
            };

            [FilenameProperty]
            [JsonProperty(PropertyName = "csvPath")]
            public string CsvPath { get; set; }

            [JsonProperty(PropertyName = "clearFile")]
            public string ClearFile { get; set; }

            [JsonProperty(PropertyName = "function")]
            public string Function { get; set; }

            [JsonProperty(PropertyName = "functionLongPress")]
            public string FunctionLongPress { get; set; }

            [JsonProperty(PropertyName = "infoUpper")]
            public string InfoUpper { get; set; }

            [JsonProperty(PropertyName = "infoUpperColor")]
            public string InfoUpperColor { get; set; }

            [JsonProperty(PropertyName = "infoMid")]
            public string InfoMid { get; set; }

            [JsonProperty(PropertyName = "infoMidColor")]
            public string InfoMidColor { get; set; }

            [JsonProperty(PropertyName = "infoLower")]
            public string InfoLower { get; set; }

            [JsonProperty(PropertyName = "infoLowerColor")]
            public string InfoLowerColor { get; set; }
        }

        private static readonly TimeSpan LongPressThreshold = TimeSpan.FromMilliseconds(2000);

        private readonly SemaphoreSlim displayLock = new SemaphoreSlim(1, 1);
        private PluginSettings settings;
        private Bitmap neutronStarImage;
        private DateTime keyPressedAt = DateTime.MinValue;

        public NeutronPlotButton(SDConnection connection, InitialPayload payload) : base(connection, payload)
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

            LoadNeutronStarImage();
            AsyncHelper.RunSync(HandleDisplay);
        }

        public override void KeyPressed(KeyPayload payload)
        {
            keyPressedAt = DateTime.UtcNow;
        }

        public override void KeyReleased(KeyPayload payload)
        {
            var elapsed = DateTime.UtcNow - keyPressedAt;
            keyPressedAt = DateTime.MinValue;

            if (elapsed >= LongPressThreshold)
                ExecuteFunction(settings.FunctionLongPress);
            else
                ExecuteFunction(settings.Function);
        }

        public override async void OnTick()
        {
            base.OnTick();
            await HandleDisplay();
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            var previousCsvPath = settings.CsvPath;
            BarRaider.SdTools.Tools.AutoPopulateSettings(settings, payload.Settings);

            if (settings.ClearFile == "true")
            {
                NeutronPlotRoute.CsvClear();
                settings.CsvPath   = string.Empty;
                settings.ClearFile = string.Empty;
                Connection.SetSettingsAsync(JObject.FromObject(settings)).Wait();
            }
            else if (!string.IsNullOrEmpty(settings.CsvPath) && settings.CsvPath != previousCsvPath)
            {
                if (File.Exists(settings.CsvPath))
                {
                    var snapshot = NeutronPlotRoute.CsvNew(settings.CsvPath);
                    if (!snapshot.IsLoaded)
                    {
                        Connection.ShowAlert().Wait();
                        _ = AutoClearInvalidCsvAsync();
                    }
                }
                else
                {
                    Connection.ShowAlert().Wait();
                    _ = AutoClearInvalidCsvAsync();
                }
            }

            AsyncHelper.RunSync(HandleDisplay);
        }

        public override void Dispose()
        {
            displayLock?.Dispose();
            neutronStarImage?.Dispose();
            base.Dispose();
        }

        private async Task AutoClearInvalidCsvAsync()
        {
            try
            {
                await Task.Delay(1000);
                NeutronPlotRoute.CsvClear();
                settings.CsvPath   = string.Empty;
                settings.ClearFile = string.Empty;
                await Connection.SetSettingsAsync(JObject.FromObject(settings));
                await HandleDisplay();
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotButton AutoClearInvalidCsvAsync: " + ex);
            }
        }

        private void LoadNeutronStarImage()
        {
            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var imagePath = Path.Combine(pluginDir ?? string.Empty, "Images", "Neutron Star.png");
                if (File.Exists(imagePath))
                    neutronStarImage = (Bitmap)Image.FromFile(imagePath);
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotButton LoadNeutronStarImage: " + ex);
            }
        }

        private async Task HandleDisplay()
        {
            if (!await displayLock.WaitAsync(0)) return;
            try
            {
                var snapshot = NeutronPlotRoute.GetSnapshot();

                using (var bitmap = neutronStarImage != null
                    ? new Bitmap(neutronStarImage)
                    : CreateDefaultBitmap())
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    DrawInfoZones(graphics, bitmap.Width, snapshot);

                    var imgBase64 = BarRaider.SdTools.Tools.ImageToBase64(bitmap, true);
                    await Connection.SetImageAsync(imgBase64);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotButton HandleDisplay: " + ex);
            }
            finally
            {
                displayLock.Release();
            }
        }

        private static Bitmap CreateDefaultBitmap()
        {
            var bitmap = new Bitmap(256, 256);
            using (var g = Graphics.FromImage(bitmap))
                g.Clear(Color.Black);
            return bitmap;
        }

        private void DrawInfoZones(Graphics graphics, int width, NeutronPlotSnapshot snapshot)
        {
            if (!snapshot.IsLoaded)
            {
                DrawCentredText(graphics, width, "NO ROUTE", width * 0.42f, Color.Gray);
                return;
            }

            // Info zone text implemented in sub-phases 3.4 – 3.6
        }

        private static void DrawCentredText(Graphics graphics, int width, string text, float y, Color color)
        {
            for (var size = 24; size >= 8; size -= 2)
            {
                using (var font = new Font("Arial", size, FontStyle.Bold))
                using (var brush = new SolidBrush(color))
                {
                    var measured = graphics.MeasureString(text, font);
                    if (measured.Width > width * 0.9f) continue;
                    var x = (width - measured.Width) / 2f;
                    graphics.DrawString(text, font, brush, x, y);
                    return;
                }
            }
        }

        private void ExecuteFunction(string function)
        {
            // Implemented in sub-phases 3.2 – 3.3
        }
    }
}
