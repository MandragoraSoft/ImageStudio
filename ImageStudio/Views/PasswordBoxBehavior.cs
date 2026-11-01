using System.Windows;
using System.Windows.Controls;

namespace ImageStudio.Views;

// Dwukierunkowe wiązanie PasswordBox z ViewModelem (PasswordBox nie ma właściwości Password do bindingu)
public static class PasswordBoxBehavior
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached("BoundPassword", typeof(string), typeof(PasswordBoxBehavior),
            // Domyślnie null: powiązanie pustego "" też jest zmianą i podpina PasswordChanged.
            // BindsTwoWayByDefault: bez tego binding bez jawnego Mode jest jednokierunkowy i wpisany klucz nie trafia do ViewModelu
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty IsAttachedProperty =
        DependencyProperty.RegisterAttached("IsAttached", typeof(bool), typeof(PasswordBoxBehavior),
            new PropertyMetadata(false));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached("IsUpdating", typeof(bool), typeof(PasswordBoxBehavior),
            new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject d) => (string)d.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;

        if (!(bool)d.GetValue(IsAttachedProperty))
        {
            box.PasswordChanged += OnPasswordChanged;
            d.SetValue(IsAttachedProperty, true);
        }

        // Zmiana z ViewModelu: aktualizujemy pole, chyba że to echo z PasswordChanged
        if ((bool)d.GetValue(IsUpdatingProperty)) return;
        var value = (string?)e.NewValue ?? "";
        if (box.Password != value) box.Password = value;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(IsUpdatingProperty, true);
        SetBoundPassword(box, box.Password);
        box.SetValue(IsUpdatingProperty, false);
    }
}
