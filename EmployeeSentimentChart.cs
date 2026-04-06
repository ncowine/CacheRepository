// =============================================================================
//  EmployeeSentimentChart — Complete WPF + Telerik MVVM Line Chart (single file)
//
//  SETUP:
//    1. Add NuGet: Telerik.Windows.Controls.Chart
//    2. Copy the three regions below into your project:
//         • Models        → Models/EmployeeDataPoint.cs
//         • ViewModels    → ViewModels/EmployeeChartViewModel.cs
//         • Converters    → Converters/SentimentLabelConverter.cs
//    3. Paste the XAML block (at the bottom of this file) into your View .xaml
//    4. Register the namespace + converter in the View (shown in XAML block)
// =============================================================================


// ─────────────────────────────────────────────────────────────────────────────
// REGION 1 — MODEL
// File: Models/EmployeeDataPoint.cs
// ─────────────────────────────────────────────────────────────────────────────
namespace TelerikLineChart.Models
{
    /// <summary>
    /// A single data point for one employee on the chart.
    /// Month  = X-axis category label  (e.g. "Jan")
    /// Value  = raw sentiment integer   (0 = Neutral, 1 = Pos1, 2 = Pos2 …)
    /// </summary>
    public class EmployeeDataPoint
    {
        public string Month { get; set; }
        public double Value { get; set; }
    }
}


// ─────────────────────────────────────────────────────────────────────────────
// REGION 2 — VIEW-MODEL
// File: ViewModels/EmployeeChartViewModel.cs
// ─────────────────────────────────────────────────────────────────────────────
using System.Collections.Generic;
using System.Collections.ObjectModel;
using TelerikLineChart.Models;

namespace TelerikLineChart.ViewModels
{
    // ── Sentiment label map (single source of truth) ──────────────────────────
    public static class SentimentLabels
    {
        public static readonly Dictionary<double, string> Map = new Dictionary<double, string>
        {
            { -2, "Negative 2" },
            { -1, "Negative 1" },
            {  0, "Neutral"    },
            {  1, "Positive 1" },
            {  2, "Positive 2" },
            {  3, "Positive 3" },
            {  4, "Positive 4" },
        };

        public static string GetLabel(double value) =>
            Map.TryGetValue(value, out var label) ? label : value.ToString();
    }

    // ── Wraps one employee's name + data series ────────────────────────────────
    public class EmployeeSeriesModel
    {
        public string EmployeeName { get; set; }
        public ObservableCollection<EmployeeDataPoint> DataPoints { get; set; }
    }

    // ── Main ViewModel ─────────────────────────────────────────────────────────
    public class EmployeeChartViewModel
    {
        /// <summary>
        /// Bound to ChartSeriesProvider.Source — one LineSeries per item.
        /// Add more EmployeeSeriesModel entries here to get more lines.
        /// </summary>
        public ObservableCollection<EmployeeSeriesModel> EmployeeSeries { get; set; }

        public EmployeeChartViewModel()
        {
            EmployeeSeries = new ObservableCollection<EmployeeSeriesModel>
            {
                new EmployeeSeriesModel
                {
                    EmployeeName = "Emp1",
                    DataPoints   = new ObservableCollection<EmployeeDataPoint>
                    {
                        new EmployeeDataPoint { Month = "Jan",   Value = 1 },
                        new EmployeeDataPoint { Month = "Feb",   Value = 4 },
                        new EmployeeDataPoint { Month = "March", Value = 2 },
                    }
                },
                new EmployeeSeriesModel
                {
                    EmployeeName = "Emp2",
                    DataPoints   = new ObservableCollection<EmployeeDataPoint>
                    {
                        new EmployeeDataPoint { Month = "Jan",   Value = 4 },
                        new EmployeeDataPoint { Month = "Feb",   Value = 0 },
                        new EmployeeDataPoint { Month = "March", Value = 3 },
                    }
                }
            };
        }
    }
}


// ─────────────────────────────────────────────────────────────────────────────
// REGION 3 — CONVERTER
// File: Converters/SentimentLabelConverter.cs
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Globalization;
using System.Windows.Data;
using TelerikLineChart.ViewModels; // for SentimentLabels

namespace TelerikLineChart.Converters
{
    /// <summary>
    /// Converts a raw numeric sentiment value to a human-readable label.
    /// Used on both the Y-axis tick labels and the hover tooltip.
    ///   0  →  "Neutral"
    ///   1  →  "Positive 1"
    ///   2  →  "Positive 2"
    ///   etc.
    /// </summary>
    [ValueConversion(typeof(double), typeof(string))]
    public class SentimentLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
                              object parameter, CultureInfo culture)
        {
            if (value is double d)
                return SentimentLabels.GetLabel(d);

            if (double.TryParse(value?.ToString(), out double parsed))
                return SentimentLabels.GetLabel(parsed);

            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType,
                                  object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}


/*
═══════════════════════════════════════════════════════════════════════════════
 XAML — Paste into EmployeeChartView.xaml
 (UserControl, Window, or Page — adjust root tag to match your project)
═══════════════════════════════════════════════════════════════════════════════

<UserControl x:Class="TelerikLineChart.Views.EmployeeChartView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:telerik="http://schemas.telerik.com/2008/xaml/presentation"
             xmlns:vm="clr-namespace:TelerikLineChart.ViewModels"
             xmlns:conv="clr-namespace:TelerikLineChart.Converters">

    <!-- ── DataContext (pure MVVM — no code-behind required) ─────────────── -->
    <UserControl.DataContext>
        <vm:EmployeeChartViewModel />
    </UserControl.DataContext>

    <!-- ── Resources ─────────────────────────────────────────────────────── -->
    <UserControl.Resources>
        <conv:SentimentLabelConverter x:Key="SentimentLabelConverter" />
    </UserControl.Resources>

    <Grid Background="#1E1E2E" Margin="20">

        <telerik:RadCartesianChart Palette="Fluent">

            <!-- ── X-Axis ──────────────────────────────────────────────── -->
            <telerik:RadCartesianChart.HorizontalAxis>
                <telerik:CategoricalAxis Title="Month"
                                         Foreground="White"
                                         LineStroke="#555" />
            </telerik:RadCartesianChart.HorizontalAxis>

            <!-- ── Y-Axis — tick labels converted to sentiment words ────── -->
            <telerik:RadCartesianChart.VerticalAxis>
                <telerik:LinearAxis Title="Sentiment"
                                    Foreground="White"
                                    LineStroke="#555"
                                    Minimum="-2"
                                    Maximum="4"
                                    MajorStep="1">
                    <telerik:LinearAxis.LabelTemplate>
                        <DataTemplate>
                            <!-- Converter turns 0 → "Neutral", 1 → "Positive 1", etc. -->
                            <TextBlock Text="{Binding Converter={StaticResource SentimentLabelConverter}}"
                                       Foreground="White"
                                       FontSize="11" />
                        </DataTemplate>
                    </telerik:LinearAxis.LabelTemplate>
                </telerik:LinearAxis>
            </telerik:RadCartesianChart.VerticalAxis>

            <!-- ── Background grid lines ───────────────────────────────── -->
            <telerik:RadCartesianChart.Grid>
                <telerik:CartesianChartGrid MajorLinesVisibility="Y"
                                            MajorYLineDashArray="4 2"
                                            MajorYLineStroke="#333" />
            </telerik:RadCartesianChart.Grid>

            <!-- ── Series provider — auto-generates one LineSeries per employee ── -->
            <telerik:RadCartesianChart.SeriesProvider>
                <telerik:ChartSeriesProvider Source="{Binding EmployeeSeries}">
                    <telerik:ChartSeriesProvider.SeriesDescriptors>

                        <telerik:CategoricalSeriesDescriptor ItemsSourcePath="DataPoints"
                                                             CategoryPath="Month"
                                                             ValuePath="Value">
                            <telerik:CategoricalSeriesDescriptor.Style>
                                <Style TargetType="telerik:LineSeries">

                                    <!-- Legend entry shows employee name -->
                                    <Setter Property="LegendSettings">
                                        <Setter.Value>
                                            <telerik:SeriesLegendSettings TitleBinding="{Binding EmployeeName}" />
                                        </Setter.Value>
                                    </Setter>

                                    <!-- Data-point markers (ellipse per point) -->
                                    <Setter Property="PointTemplate">
                                        <Setter.Value>
                                            <DataTemplate>
                                                <Ellipse Width="10" Height="10"
                                                         Fill="White"
                                                         Stroke="#1E1E2E"
                                                         StrokeThickness="1.5" />
                                            </DataTemplate>
                                        </Setter.Value>
                                    </Setter>

                                    <!-- Hover tooltip — shows sentiment label, not raw number -->
                                    <Setter Property="TrackBallInfoTemplate">
                                        <Setter.Value>
                                            <DataTemplate>
                                                <StackPanel Background="#2A2A3E"
                                                            Orientation="Vertical"
                                                            Margin="0" >
                                                    <Border BorderBrush="#444"
                                                            BorderThickness="1"
                                                            CornerRadius="4"
                                                            Padding="10,6">
                                                        <StackPanel>
                                                            <!-- Month label -->
                                                            <TextBlock Foreground="#AAB"
                                                                       FontSize="11"
                                                                       Text="{Binding DataPoint.DataItem.Month}" />
                                                            <!-- Employee: Sentiment Label -->
                                                            <TextBlock Foreground="White"
                                                                       FontWeight="Bold"
                                                                       FontSize="13">
                                                                <Run Text="{Binding SeriesDisplayName}" />
                                                                <Run Text=": " />
                                                                <Run Text="{Binding DataPoint.DataItem.Value,
                                                                     Converter={StaticResource SentimentLabelConverter}}" />
                                                            </TextBlock>
                                                        </StackPanel>
                                                    </Border>
                                                </StackPanel>
                                            </DataTemplate>
                                        </Setter.Value>
                                    </Setter>

                                    <Setter Property="StrokeThickness" Value="2.5" />

                                </Style>
                            </telerik:CategoricalSeriesDescriptor.Style>
                        </telerik:CategoricalSeriesDescriptor>

                    </telerik:ChartSeriesProvider.SeriesDescriptors>
                </telerik:ChartSeriesProvider>
            </telerik:RadCartesianChart.SeriesProvider>

            <!-- ── Legend (auto-coloured per series) ───────────────────── -->
            <telerik:RadCartesianChart.LegendSettings>
                <telerik:SeriesLegendSettings />
            </telerik:RadCartesianChart.LegendSettings>

            <!-- ── Behaviours: crosshair + tooltip on hover ─────────────── -->
            <telerik:RadCartesianChart.Behaviors>
                <telerik:ChartTrackBallBehavior ShowIntersectionPoints="True"
                                                ShowTrackInfo="True" />
                <telerik:ChartTooltipBehavior TriggerOn="Hover" />
            </telerik:RadCartesianChart.Behaviors>

        </telerik:RadCartesianChart>

    </Grid>
</UserControl>

═══════════════════════════════════════════════════════════════════════════════
 QUICK-REFERENCE — Adding a new employee
═══════════════════════════════════════════════════════════════════════════════

 In EmployeeChartViewModel constructor, just add:

     new EmployeeSeriesModel
     {
         EmployeeName = "Emp3",
         DataPoints = new ObservableCollection<EmployeeDataPoint>
         {
             new EmployeeDataPoint { Month = "Jan",   Value = -1 },
             new EmployeeDataPoint { Month = "Feb",   Value =  2 },
             new EmployeeDataPoint { Month = "March", Value =  0 },
         }
     }

 No XAML changes required. Telerik auto-assigns the next palette colour.

═══════════════════════════════════════════════════════════════════════════════
 QUICK-REFERENCE — Adding a new sentiment level
═══════════════════════════════════════════════════════════════════════════════

 In SentimentLabels.Map, add one line:

     { 5, "Excellent" }

 The Y-axis and tooltip both pick it up automatically.

*/
