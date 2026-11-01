using System.Windows;
using System.Windows.Controls;

namespace ImageStudio.Views;

public partial class ModelFilterBar : UserControl
{
    public static readonly DependencyProperty ShowImageFilterProperty =
        DependencyProperty.Register(nameof(ShowImageFilter), typeof(bool), typeof(ModelFilterBar), new PropertyMetadata(true));

    // "Przyjmuje obraz" ma sens dla modeli obrazów, edycji, wideo i czatu, nie dla modeli dźwięku
    public bool ShowImageFilter { get => (bool)GetValue(ShowImageFilterProperty); set => SetValue(ShowImageFilterProperty, value); }

    public ModelFilterBar()
    {
        InitializeComponent();
    }
}
