using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using Assistant.Controllers;

namespace Assistant.UI.Controls
{
    /// <summary>
    /// A lightweight self-drawn WPF chart used by the API usage page.
    /// Two modes are supported:
    ///  - LineArea: daily request counts as a blue area line chart.
    ///  - StackedBar: daily input/output tokens as a stacked bar chart with
    ///    compact Y-axis labels (K/M units).
    /// Hovering near a data point shows a tooltip with the day's details.
    /// </summary>
    public enum UsageChartMode
    {
        LineArea,
        StackedBar
    }

    public class UsageChart : FrameworkElement
    {
        private const double MarginLeft = 46;
        private const double MarginRight = 12;
        private const double MarginTop = 14;
        private const double MarginBottom = 28;

        private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
        private static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x73, 0xE8));
        private static readonly Brush AreaBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x1E, 0x73, 0xE8));
        private static readonly Brush InputBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xD5));
        private static readonly Brush OutputBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x00));
        private static readonly Typeface LabelTypeface = new Typeface("Segoe UI");

        private List<ApiUsageTracker.DayPoint> data = new List<ApiUsageTracker.DayPoint>();
        private ToolTip tip;

        public UsageChartMode Mode { get; set; }

        public List<ApiUsageTracker.DayPoint> Data
        {
            get { return data; }
            set
            {
                data = value ?? new List<ApiUsageTracker.DayPoint>();
                InvalidateVisual();
            }
        }

        public UsageChart()
        {
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            ToolTipService.SetInitialShowDelay(this, 0);
            ToolTipService.SetShowDuration(this, 60000);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double width = ActualWidth;
            double height = ActualHeight;
            if (width < 80 || height < 60)
                return;

            Rect plot = new Rect(MarginLeft, MarginTop, Math.Max(0, width - MarginLeft - MarginRight), Math.Max(0, height - MarginTop - MarginBottom));
            if (plot.Width <= 0 || plot.Height <= 0)
                return;

            int count = data.Count;
            double maxValue = ComputeMaxValue();
            if (maxValue <= 0)
                maxValue = 1;

            // Draw the Y grid lines and labels.
            DrawGrid(dc, plot, maxValue);

            if (count == 0)
            {
                DrawCenteredHint(dc, plot);
                return;
            }

            double[] xs = new double[count];
            for (int i = 0; i < count; i++)
            {
                xs[i] = count == 1
                    ? plot.Left + plot.Width / 2.0
                    : plot.Left + plot.Width * i / (count - 1.0);
            }

            if (Mode == UsageChartMode.StackedBar)
                DrawStackedBars(dc, plot, xs, maxValue);
            else
                DrawLineArea(dc, plot, xs, maxValue);

            DrawXLabels(dc, plot, xs);
        }

        private void DrawGrid(DrawingContext dc, Rect plot, double maxValue)
        {
            const int segments = 4;
            double yStep = plot.Height / segments;
            for (int i = 0; i <= segments; i++)
            {
                double y = plot.Top + yStep * i;
                dc.DrawLine(new Pen(i == segments ? AxisBrush : GridBrush, i == segments ? 1.0 : 1.0),
                    new Point(plot.Left, y), new Point(plot.Right, y));

                double value = maxValue * (segments - i) / segments;
                string label = Mode == UsageChartMode.StackedBar ? FormatCompact(value) : value.ToString("N0", CultureInfo.InvariantCulture);
                FormattedText ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    LabelTypeface, 10, LabelBrush, 1.25);
                dc.DrawText(ft, new Point(plot.Left - ft.Width - 6, y - ft.Height / 2.0));
            }

            dc.DrawLine(new Pen(AxisBrush, 1.0), new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        }

        private double ComputeMaxValue()
        {
            double max = 0;
            foreach (ApiUsageTracker.DayPoint point in data)
            {
                double value = Mode == UsageChartMode.StackedBar
                    ? point.InputTokens + point.OutputTokens
                    : point.Requests;
                if (value > max)
                    max = value;
            }
            if (max <= 0)
                return 1;

            // Round up to a "nice" value so labels stay readable.
            double rounded = max * 1.15;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rounded)));
            double normalized = rounded / magnitude;
            double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
            return nice * magnitude;
        }

        private void DrawLineArea(DrawingContext dc, Rect plot, double[] xs, double maxValue)
        {
            int count = data.Count;
            Point[] points = new Point[count];
            for (int i = 0; i < count; i++)
            {
                double value = data[i].Requests;
                double y = plot.Bottom - (value / maxValue) * plot.Height;
                points[i] = new Point(xs[i], y);
            }

            // Filled area under the line.
            StreamGeometry area = new StreamGeometry();
            using (StreamGeometryContext ctx = area.Open())
            {
                ctx.BeginFigure(points[0], true, true);
                for (int i = 1; i < count; i++)
                    ctx.LineTo(points[i], true, false);
                ctx.LineTo(new Point(points[count - 1].X, plot.Bottom), true, false);
                ctx.LineTo(new Point(points[0].X, plot.Bottom), true, false);
            }
            area.Freeze();
            dc.DrawGeometry(AreaBrush, null, area);

            // Blue polyline.
            StreamGeometry line = new StreamGeometry();
            using (StreamGeometryContext ctx = line.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                for (int i = 1; i < count; i++)
                    ctx.LineTo(points[i], true, false);
            }
            line.Freeze();
            dc.DrawGeometry(null, new Pen(LineBrush, 1.6), line);

            // Points.
            for (int i = 0; i < count; i++)
                dc.DrawEllipse(LineBrush, null, points[i], 2.4, 2.4);
        }

        private void DrawStackedBars(DrawingContext dc, Rect plot, double[] xs, double maxValue)
        {
            int count = data.Count;
            double barWidth = Math.Min(34, plot.Width / count * 0.62);
            for (int i = 0; i < count; i++)
            {
                ApiUsageTracker.DayPoint point = data[i];
                double inputH = (point.InputTokens / maxValue) * plot.Height;
                double outputH = (point.OutputTokens / maxValue) * plot.Height;
                double x = xs[i] - barWidth / 2.0;

                double inputY = plot.Bottom - inputH - outputH;
                double outputY = plot.Bottom - outputH;

                if (inputH > 0)
                    dc.DrawRectangle(InputBrush, null, new Rect(x, inputY, barWidth, inputH));
                if (outputH > 0)
                    dc.DrawRectangle(OutputBrush, null, new Rect(x, outputY, barWidth, outputH));
            }
        }

        private void DrawXLabels(DrawingContext dc, Rect plot, double[] xs)
        {
            int count = data.Count;
            int maxLabels = 6;
            HashSet<int> chosen = new HashSet<int>();
            if (count <= maxLabels)
            {
                for (int i = 0; i < count; i++)
                    chosen.Add(i);
            }
            else
            {
                chosen.Add(0);
                chosen.Add(count - 1);
                for (int step = 1; step <= maxLabels; step++)
                {
                    int index = (int)Math.Round((count - 1) * step / (double)(maxLabels - 1));
                    if (index >= 1 && index < count - 1 && chosen.Add(index) && chosen.Count >= maxLabels)
                        break;
                }
            }

            foreach (int i in chosen)
            {
                string label = data[i].Date.ToString("MM-dd", CultureInfo.InvariantCulture);
                FormattedText ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    LabelTypeface, 10, LabelBrush, 1.25);
                double x = xs[i] - ft.Width / 2.0;
                x = Math.Max(plot.Left, Math.Min(plot.Right - ft.Width, x));
                dc.DrawText(ft, new Point(x, plot.Bottom + 6));
            }
        }

        private void DrawCenteredHint(DrawingContext dc, Rect plot)
        {
            string hint = Assistant.Localization.Strings.ApiUsageNoData;
            FormattedText ft = new FormattedText(hint, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelTypeface, 12, LabelBrush, 1.25);
            dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2.0, plot.Top + (plot.Height - ft.Height) / 2.0));
        }

        /// <summary>
        /// Formats a token count compactly: 1.2M, 850K, 1,234.
        /// </summary>
        private static string FormatCompact(double value)
        {
            if (value >= 1000000)
                return (value / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            if (value >= 1000)
                return (value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (data.Count == 0)
                return;

            Point pos = e.GetPosition(this);
            Rect plot = new Rect(MarginLeft, MarginTop, Math.Max(0, ActualWidth - MarginLeft - MarginRight), Math.Max(0, ActualHeight - MarginTop - MarginBottom));
            if (pos.Y < MarginTop - 8 || pos.Y > plot.Bottom + 8)
            {
                CloseTip();
                return;
            }

            int index = FindNearestIndex(pos.X);
            if (index < 0 || index >= data.Count)
            {
                CloseTip();
                return;
            }

            ApiUsageTracker.DayPoint point = data[index];
            string text = BuildTooltip(point);
            if (tip == null)
            {
                tip = new ToolTip();
            }
            tip.Content = text;
            ToolTip = tip;
            tip.PlacementTarget = this;
            tip.IsOpen = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            CloseTip();
        }

        private void CloseTip()
        {
            if (tip != null)
                tip.IsOpen = false;
        }

        private int FindNearestIndex(double mouseX)
        {
            int count = data.Count;
            if (count == 1)
                return Math.Abs(mouseX - (MarginLeft + (ActualWidth - MarginLeft - MarginRight) / 2.0)) <= 14 ? 0 : -1;

            double step = (ActualWidth - MarginLeft - MarginRight) / (count - 1.0);
            double tolerance = Math.Max(12, step * 0.55);
            double raw = (mouseX - MarginLeft) / step;
            int nearest = (int)Math.Round(raw);
            if (nearest < 0 || nearest >= count)
                return -1;

            double distance = Math.Abs(mouseX - (MarginLeft + step * nearest));
            return distance <= tolerance ? nearest : -1;
        }

        private string BuildTooltip(ApiUsageTracker.DayPoint point)
        {
            string date = point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return date + Environment.NewLine
                + Assistant.Localization.Strings.ApiUsageTipRequests + " " + point.Requests.ToString("N0")
                + Environment.NewLine
                + Assistant.Localization.Strings.ApiUsageTipInput + " " + point.InputTokens.ToString("N0")
                + Environment.NewLine
                + Assistant.Localization.Strings.ApiUsageTipOutput + " " + point.OutputTokens.ToString("N0")
                + Environment.NewLine
                + Assistant.Localization.Strings.ApiUsageTipTotal + " " + point.TotalTokens.ToString("N0");
        }
    }
}
