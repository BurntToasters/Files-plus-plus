using System.Runtime.InteropServices.WindowsRuntime;
using FilesPlusPlus.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FilesPlusPlus.App.Controls;

/// <summary>
/// Displays the real Windows shell icon or live thumbnail for a file system item, falling back to a
/// tinted glyph while the image loads (or when no shell image is available). Designed to live inside
/// virtualized list/grid item templates, so loads are cancelled and restarted on container recycling.
/// </summary>
public sealed partial class FileThumbnail : Grid
{
    private readonly Image _image;
    private readonly FontIcon _glyphShadow;
    private readonly FontIcon _glyph;
    private CancellationTokenSource? _loadCts;
    private bool _isLoaded;

    public FileThumbnail()
    {
        _glyphShadow = new FontIcon
        {
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 1, 0, 0),
            Opacity = 0.45,
            IsHitTestVisible = false
        };

        _glyph = new FontIcon
        {
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        Children.Add(_glyphShadow);
        Children.Add(_glyph);
        Children.Add(_image);

        ApplyFallbackGlyph();
        ApplyDisplaySize();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath),
        typeof(string),
        typeof(FileThumbnail),
        new PropertyMetadata(null, OnSourceChanged));

    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public static readonly DependencyProperty IsDirectoryProperty = DependencyProperty.Register(
        nameof(IsDirectory),
        typeof(bool),
        typeof(FileThumbnail),
        new PropertyMetadata(false, OnSourceChanged));

    public bool IsDirectory
    {
        get => (bool)GetValue(IsDirectoryProperty);
        set => SetValue(IsDirectoryProperty, value);
    }

    public static readonly DependencyProperty DisplaySizeProperty = DependencyProperty.Register(
        nameof(DisplaySize),
        typeof(double),
        typeof(FileThumbnail),
        new PropertyMetadata(18.0, OnDisplaySizeChanged));

    public double DisplaySize
    {
        get => (double)GetValue(DisplaySizeProperty);
        set => SetValue(DisplaySizeProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FileThumbnail thumbnail)
        {
            thumbnail.ApplyFallbackGlyph();
            thumbnail.BeginLoad();
        }
    }

    private static void OnDisplaySizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FileThumbnail thumbnail)
        {
            thumbnail.ApplyDisplaySize();
            thumbnail.BeginLoad();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        BeginLoad();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        CancelLoad();
    }

    private void ApplyDisplaySize()
    {
        Width = DisplaySize;
        Height = DisplaySize;
        var glyphSize = Math.Max(10, DisplaySize * 0.82);
        _glyph.FontSize = glyphSize;
        _glyphShadow.FontSize = glyphSize;
    }

    private void ApplyFallbackGlyph()
    {
        _glyph.Glyph = IsDirectory ? "\uE8B7" : "\uE8A5";
        _glyphShadow.Glyph = _glyph.Glyph;
        _glyph.Foreground = ResolveBrush(IsDirectory ? "ShellFolderIconBrush" : "ShellFileIconBrush", Microsoft.UI.Colors.SteelBlue);
        _glyphShadow.Foreground = ResolveBrush("ShellIconShadowBrush", Microsoft.UI.Colors.Transparent);
    }

    private static Brush ResolveBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current?.Resources is { } resources &&
            resources.TryGetValue(key, out var value) &&
            value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }

    private void BeginLoad()
    {
        CancelLoad();

        if (!_isLoaded)
        {
            return;
        }

        var path = FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowFallback();
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _ = LoadAsync(path, ResolveRequestPixelSize(), cts);
    }

    private int ResolveRequestPixelSize()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        var target = DisplaySize * (scale <= 0 ? 1.0 : scale);

        return target switch
        {
            <= 24 => 32,
            <= 40 => 48,
            <= 72 => 96,
            _ => 256
        };
    }

    private async Task LoadAsync(string path, int pixelSize, CancellationTokenSource cts)
    {
        try
        {
            var pixels = await ShellThumbnailService.Instance
                .GetPixelsAsync(path, pixelSize, ShellThumbnailKind.ThumbnailOrIcon, cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts))
            {
                return;
            }

            if (pixels is null)
            {
                ShowFallback();
                return;
            }

            var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                await stream.WriteAsync(pixels.Bgra.AsMemory(0, pixels.Bgra.Length), cts.Token).ConfigureAwait(true);
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts))
            {
                return;
            }

            bitmap.Invalidate();
            _image.Source = bitmap;
            ShowImage();
        }
        catch (OperationCanceledException)
        {
            // Why: Expected when the container is recycled mid-load.
        }
        catch
        {
            ShowFallback();
        }
    }

    private void ShowImage()
    {
        _image.Visibility = Visibility.Visible;
        _glyph.Visibility = Visibility.Collapsed;
        _glyphShadow.Visibility = Visibility.Collapsed;
    }

    private void ShowFallback()
    {
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        _glyph.Visibility = Visibility.Visible;
        _glyphShadow.Visibility = Visibility.Visible;
    }

    private void CancelLoad()
    {
        var existing = _loadCts;
        _loadCts = null;
        if (existing is not null)
        {
            existing.Cancel();
            existing.Dispose();
        }
    }
}
