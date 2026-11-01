using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Configuration;
using ImageStudio.Services;
using Microsoft.Extensions.Logging;

namespace ImageStudio.ViewModels;

public sealed partial class MainViewModel
{
    // --- Proporcje wyjścia ---

    partial void OnWidthChanged(int value)
    {
        MarkDirty();
        OnDimensionChanged(widthChanged: true);
    }

    partial void OnHeightChanged(int value)
    {
        MarkDirty();
        OnDimensionChanged(widthChanged: false);
    }

    private void OnDimensionChanged(bool widthChanged)
    {
        if (_syncing) return;

        _syncing = true;
        try
        {
            if (KeepAspect)
            {
                if (widthChanged) Height = Snap(Width / _aspect);
                else Width = Snap(Height * _aspect);
            }
            else if (Height > 0)
            {
                _aspect = (double)Width / Height;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    [RelayCommand]
    private void SetAspect(string? ratio)
    {
        var parts = ratio?.Split(':');
        if (parts is not { Length: 2 }
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            || h <= 0)
            return;

        _aspect = w / h;
        _syncing = true;
        try
        {
            Height = Snap(Width / _aspect);
        }
        finally
        {
            _syncing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSource))]
    private void ApplySourceAspect()
    {
        if (SourceImage is null) return;
        ApplyAspectFrom(SourceImage.PixelWidth, SourceImage.PixelHeight);
    }

    // Wymiary wyjścia biorą proporcje z obrazu podstawowego, z limitem długiego boku
    private void ApplyAspectFrom(int pixelWidth, int pixelHeight)
    {
        var scale = Math.Min(1.0, _settings.MaxSide / (double)Math.Max(pixelWidth, pixelHeight));
        _syncing = true;
        try
        {
            Width = Snap(pixelWidth * scale);
            Height = Snap(pixelHeight * scale);
        }
        finally
        {
            _syncing = false;
        }
        _aspect = (double)pixelWidth / pixelHeight;
    }

    // Wielokrotność 8, minimum 8
    private static int Snap(double value) => Math.Max(8, (int)Math.Round(value / 8.0) * 8);
}
