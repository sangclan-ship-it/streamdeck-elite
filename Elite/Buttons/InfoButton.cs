using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BarRaider.SdTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Elite.Buttons
{
    [PluginActionId("com.mhwlng.elite.infobutton")]
    public class InfoButton : EliteKeypadBase
    {
        protected class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings()
            {
                return new PluginSettings
                {
                    PrimaryImageFilename = string.Empty,
                    InfoType = "currentFuel",
                    RefreshRate = "2000",
                    PrimaryColor = "#ffffff",
                    LabelColor = "#00aaff",
                    TextVerticalPosition = "55",
                    TextBold = "true"
                };
            }

            [FilenameProperty]
            [JsonProperty(PropertyName = "primaryImage")]
            public string PrimaryImageFilename { get; set; }

            [JsonProperty(PropertyName = "infoType")]
            public string InfoType { get; set; }

            [JsonProperty(PropertyName = "refreshRate")]
            public string RefreshRate { get; set; }

            [JsonProperty(PropertyName = "primaryColor")]
            public string PrimaryColor { get; set; }

            [JsonProperty(PropertyName = "labelColor")]
            public string LabelColor { get; set; }

            [JsonProperty(PropertyName = "textVerticalPosition")]
            public string TextVerticalPosition { get; set; }

            [JsonProperty(PropertyName = "textBold")]
            public string TextBold { get; set; }
        }

        private readonly SemaphoreSlim displayLock = new SemaphoreSlim(1, 1);
        private PluginSettings settings;
        private Timer refreshTimer;
        private Bitmap primaryImage;
        private string primaryFile;
        private SolidBrush primaryBrush = new SolidBrush(Color.White);
        private SolidBrush labelBrush = new SolidBrush(Color.FromArgb(0, 170, 255));

        public InfoButton(SDConnection connection, InitialPayload payload) : base(connection, payload)
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

            InitializeSettings();
            AsyncHelper.RunSync(HandleDisplay);
        }

        public override void KeyPressed(KeyPayload payload) { }
        public override void KeyReleased(KeyPayload payload) { }

        public override void Dispose()
        {
            refreshTimer?.Dispose();
            refreshTimer = null;
            displayLock?.Dispose();
            primaryImage?.Dispose();
            primaryBrush?.Dispose();
            labelBrush?.Dispose();
            base.Dispose();
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            BarRaider.SdTools.Tools.AutoPopulateSettings(settings, payload.Settings);
            InitializeSettings();
            AsyncHelper.RunSync(HandleDisplay);
        }

        private void InitializeSettings()
        {
            if (string.IsNullOrEmpty(settings.InfoType)) settings.InfoType = "currentFuel";
            if (string.IsNullOrEmpty(settings.RefreshRate)) settings.RefreshRate = "2000";
            if (string.IsNullOrEmpty(settings.PrimaryColor)) settings.PrimaryColor = "#ffffff";
            if (string.IsNullOrEmpty(settings.LabelColor)) settings.LabelColor = "#00aaff";
            if (string.IsNullOrEmpty(settings.TextVerticalPosition)) settings.TextVerticalPosition = "55";
            if (string.IsNullOrEmpty(settings.TextBold)) settings.TextBold = "true";

            try
            {
                var converter = new ColorConverter();
                primaryBrush?.Dispose();
                labelBrush?.Dispose();
                primaryBrush = new SolidBrush((Color)converter.ConvertFromString(settings.PrimaryColor));
                labelBrush = new SolidBrush((Color)converter.ConvertFromString(settings.LabelColor));

                primaryImage?.Dispose();
                primaryImage = null;
                primaryFile = null;

                if (File.Exists(settings.PrimaryImageFilename))
                {
                    primaryImage = (Bitmap)Image.FromFile(settings.PrimaryImageFilename);
                    primaryFile = Tools.FileToBase64(settings.PrimaryImageFilename, true);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.FATAL, "InfoButton InitializeSettings " + ex);
            }

            UpdateRefreshTimer();
            Connection.SetSettingsAsync(JObject.FromObject(settings)).Wait();
        }

        private void UpdateRefreshTimer()
        {
            var refreshRate = GetRefreshRate();
            if (refreshTimer == null)
            {
                refreshTimer = new Timer(_ => _ = RefreshAsync(), null, refreshRate, refreshRate);
                return;
            }

            refreshTimer.Change(refreshRate, refreshRate);
        }

        private int GetRefreshRate()
        {
            if (!int.TryParse(settings.RefreshRate, out var refreshRate))
            {
                return 2000;
            }

            switch (refreshRate)
            {
                case 100:
                case 200:
                case 500:
                case 1000:
                case 2000:
                case 5000:
                    return refreshRate;
                default:
                    return 2000;
            }
        }

        private async Task RefreshAsync()
        {
            if (!await displayLock.WaitAsync(0))
            {
                return;
            }

            try
            {
                await HandleDisplay();
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.FATAL, "InfoButton RefreshAsync " + ex);
            }
            finally
            {
                displayLock.Release();
            }
        }

        private async Task HandleDisplay()
        {
            var label = GetLabelText();
            var value = GetValueText();

            try
            {
                using (var bitmap = primaryImage != null ? new Bitmap(primaryImage) : CreateDefaultBitmap())
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        DrawText(graphics, label, value, bitmap.Width);
                    }

                    var imgBase64 = BarRaider.SdTools.Tools.ImageToBase64(bitmap, true);
                    await Connection.SetImageAsync(imgBase64);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.FATAL, "InfoButton HandleDisplay " + ex);

                if (!string.IsNullOrEmpty(primaryFile))
                {
                    await Connection.SetImageAsync(primaryFile);
                }
            }
        }

        private static Bitmap CreateDefaultBitmap()
        {
            var bitmap = new Bitmap(256, 256);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
            }

            return bitmap;
        }

        private string GetLabelText()
        {
            switch (settings.InfoType)
            {
                case "cargoMass":
                    return "CARGO";
                case "currentFuel":
                default:
                    return "FUEL";
            }
        }

        private string GetValueText()
        {
            switch (settings.InfoType)
            {
                case "cargoMass":
                    return EliteData.StatusData.Cargo.ToString("#,##0t", CultureInfo.InvariantCulture);
                case "currentFuel":
                default:
                    return EliteData.StatusData.Fuel.FuelMain.ToString("0.0t", CultureInfo.InvariantCulture);
            }
        }

        private void DrawText(Graphics graphics, string label, string value, int width)
        {
            var fontStyle = settings.TextBold == "true" ? FontStyle.Bold : FontStyle.Regular;
            var verticalPosition = double.TryParse(settings.TextVerticalPosition, out var parsedPosition)
                ? parsedPosition
                : 55.0;
            var startY = (float)(verticalPosition * (width / 256.0));

            for (var size = 58; size >= 10; size -= 2)
            {
                using (var valueFont = new Font("Arial", size, fontStyle))
                using (var labelFont = new Font("Arial", Math.Max(8, size / 3), fontStyle))
                {
                    var labelSize = graphics.MeasureString(label, labelFont);
                    var valueSize = graphics.MeasureString(value, valueFont);

                    if (labelSize.Width > width * 0.95f || valueSize.Width > width * 0.95f)
                    {
                        continue;
                    }

                    var totalHeight = labelSize.Height + valueSize.Height;
                    if (startY + totalHeight > width)
                    {
                        continue;
                    }

                    var labelX = (width - labelSize.Width) / 2.0f;
                    var valueX = (width - valueSize.Width) / 2.0f;
                    graphics.DrawString(label, labelFont, labelBrush, labelX, startY);
                    graphics.DrawString(value, valueFont, primaryBrush, valueX, startY + labelSize.Height * 0.9f);
                    return;
                }
            }
        }
    }
}
