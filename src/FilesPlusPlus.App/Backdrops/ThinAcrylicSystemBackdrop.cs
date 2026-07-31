using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FilesPlusPlus.App.Backdrops;

public sealed class ThinAcrylicSystemBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            return;
        }

        _controller ??= new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
            TintColor = Color.FromArgb(0xFF, 0x22, 0x7D, 0xDA),
            TintOpacity = 0.16f,
            LuminosityOpacity = 0.58f,
            FallbackColor = Color.FromArgb(0xFF, 0xD8, 0xE8, 0xF4),
        };

        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        _controller?.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, xamlRoot));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
    }
}
