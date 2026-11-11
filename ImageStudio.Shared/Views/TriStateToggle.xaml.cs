using System.Windows;
using System.Windows.Controls;

namespace ImageStudio.Views;

// Trójstanowy przełącznik (tylko / wszystkie / ukryj). Nie wie nic o NSFW: etykiety i podpowiedzi podaje wywołujący.
public partial class TriStateToggle : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(TriState), typeof(TriStateToggle),
            new FrameworkPropertyMetadata(TriState.Any, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, _) => ((TriStateToggle)d).ShowValue()));
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(TriStateToggle), new PropertyMetadata(""));
    public static readonly DependencyProperty OnlyTextProperty =
        DependencyProperty.Register(nameof(OnlyText), typeof(string), typeof(TriStateToggle), new PropertyMetadata(""));
    public static readonly DependencyProperty AnyTextProperty =
        DependencyProperty.Register(nameof(AnyText), typeof(string), typeof(TriStateToggle), new PropertyMetadata(""));
    public static readonly DependencyProperty ExcludeTextProperty =
        DependencyProperty.Register(nameof(ExcludeText), typeof(string), typeof(TriStateToggle), new PropertyMetadata(""));
    public static readonly DependencyProperty OnlyTipProperty =
        DependencyProperty.Register(nameof(OnlyTip), typeof(string), typeof(TriStateToggle), new PropertyMetadata(""));
    public static readonly DependencyProperty AnyTipProperty =
        DependencyProperty.Register(nameof(AnyTip), typeof(string), typeof(TriStateToggle), new PropertyMetadata(""));
    public static readonly DependencyProperty ExcludeTipProperty =
        DependencyProperty.Register(nameof(ExcludeTip), typeof(string), typeof(TriStateToggle), new PropertyMetadata(""));

    public TriStateToggle()
    {
        InitializeComponent();
        // Zaznaczenie segmentu (klik, spacja, strzałki) ustawia wartość; wartość zaznacza segment
        OnlyButton.Checked += (_, _) => Value = TriState.Only;
        AnyButton.Checked += (_, _) => Value = TriState.Any;
        ExcludeButton.Checked += (_, _) => Value = TriState.Exclude;
        ShowValue();
    }

    public TriState Value { get => (TriState)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string OnlyText { get => (string)GetValue(OnlyTextProperty); set => SetValue(OnlyTextProperty, value); }
    public string AnyText { get => (string)GetValue(AnyTextProperty); set => SetValue(AnyTextProperty, value); }
    public string ExcludeText { get => (string)GetValue(ExcludeTextProperty); set => SetValue(ExcludeTextProperty, value); }
    public string OnlyTip { get => (string)GetValue(OnlyTipProperty); set => SetValue(OnlyTipProperty, value); }
    public string AnyTip { get => (string)GetValue(AnyTipProperty); set => SetValue(AnyTipProperty, value); }
    public string ExcludeTip { get => (string)GetValue(ExcludeTipProperty); set => SetValue(ExcludeTipProperty, value); }

    private void ShowValue()
    {
        OnlyButton.IsChecked = Value == TriState.Only;
        AnyButton.IsChecked = Value == TriState.Any;
        ExcludeButton.IsChecked = Value == TriState.Exclude;
    }
}
