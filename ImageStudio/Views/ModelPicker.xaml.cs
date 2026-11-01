using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ImageStudio.Views;

public partial class ModelPicker : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ModelPicker));
    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(ModelPicker),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(ModelPicker));
    public static readonly DependencyProperty HasFiltersProperty =
        DependencyProperty.Register(nameof(HasFilters), typeof(bool), typeof(ModelPicker));

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public string? Header { get => (string?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    // Pokazuje przycisk panelu filtrów (filtry działają na DataContext rodzica, czyli MainViewModel)
    public bool HasFilters { get => (bool)GetValue(HasFiltersProperty); set => SetValue(HasFiltersProperty, value); }

    public ModelPicker()
    {
        InitializeComponent();
    }

    // Klik w pozycję zwija akordeon; klik w pasek przewijania czy puste miejsce go nie rusza
    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem)
            Accordion.IsExpanded = false;
    }
}
