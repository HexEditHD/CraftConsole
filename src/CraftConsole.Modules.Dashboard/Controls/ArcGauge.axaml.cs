using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CraftConsole.Modules.Dashboard.Controls;

public partial class ArcGauge : UserControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ArcGauge, double>(nameof(Value), defaultValue: 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ArcGauge, double>(nameof(Maximum), defaultValue: 100.0);

    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<ArcGauge, string>(nameof(Unit), defaultValue: "%");

    public static readonly StyledProperty<IBrush?> GaugeColorProperty =
        AvaloniaProperty.Register<ArcGauge, IBrush?>(nameof(GaugeColor));

    public static readonly StyledProperty<string> ValueFormatProperty =
        AvaloniaProperty.Register<ArcGauge, string>(nameof(ValueFormat), defaultValue: "{}{0:F0}");

    // Computed — updated when Value or Maximum changes
    public static readonly StyledProperty<double> SweepAngleProperty =
        AvaloniaProperty.Register<ArcGauge, double>(nameof(SweepAngle), defaultValue: 0.0);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public IBrush? GaugeColor
    {
        get => GetValue(GaugeColorProperty);
        set => SetValue(GaugeColorProperty, value);
    }

    public string ValueFormat
    {
        get => GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    public double SweepAngle
    {
        get => GetValue(SweepAngleProperty);
        private set => SetValue(SweepAngleProperty, value);
    }

    static ArcGauge()
    {
        ValueProperty.Changed.AddClassHandler<ArcGauge>((g, _) => g.UpdateSweepAngle());
        MaximumProperty.Changed.AddClassHandler<ArcGauge>((g, _) => g.UpdateSweepAngle());
    }

    private void UpdateSweepAngle()
    {
        SweepAngle = Maximum > 0 ? Math.Clamp(Value / Maximum, 0.0, 1.0) * 270.0 : 0.0;
    }
}
