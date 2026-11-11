using System.Collections.ObjectModel;
using System.ComponentModel;
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
                if (widthChanged) Height = ImageAspect.Snap(Width / _aspect);
                else Width = ImageAspect.Snap(Height * _aspect);
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
        if (ImageAspect.Ratio(ratio) is not { } value) return;

        _aspect = value;
        _syncing = true;
        try
        {
            Height = ImageAspect.Snap(Width / _aspect);
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
            Width = ImageAspect.Snap(pixelWidth * scale);
            Height = ImageAspect.Snap(pixelHeight * scale);
        }
        finally
        {
            _syncing = false;
        }
        _aspect = (double)pixelWidth / pixelHeight;
    }
}
