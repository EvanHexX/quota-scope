using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT;

namespace QuotaScope.WinUI.Windows;

// Real acrylic for the glassmorphism mode. The XAML DesktopAcrylicBackdrop
// gives no control over tint/luminosity, which made the effect read as a
// slight color shift; driving DesktopAcrylicController directly lets the
// desktop behind the popup actually show through.
internal sealed class AcrylicBackdropHost : IDisposable
{
    private readonly Window _window;
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;

    public AcrylicBackdropHost(Window window) => _window = window;

    public static bool IsSupported
    {
        get
        {
            try
            {
                return DesktopAcrylicController.IsSupported();
            }
            catch
            {
                return false;
            }
        }
    }

    // Windows "Transparency effects" off makes every backdrop render as a flat
    // fallback color; surfaced in settings so the mode does not look broken.
    public static bool TransparencyEffectsEnabled
    {
        get
        {
            try
            {
                return new UISettings().AdvancedEffectsEnabled;
            }
            catch
            {
                return true;
            }
        }
    }

    public bool TryAttach(Color tint, bool darkTheme, GlassStrength strength)
    {
        if (!IsSupported) return false;
        try
        {
            _configuration ??= new SystemBackdropConfiguration
            {
                // Keep the effect alive even when the popup is not focused.
                IsInputActive = true
            };
            _configuration.Theme = darkTheme ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

            if (_controller is null)
            {
                _controller = new DesktopAcrylicController();
                _controller.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
                _controller.SetSystemBackdropConfiguration(_configuration);
            }

            try
            {
                // Base samples the desktop behind the window (Thin is subtler).
                _controller.Kind = DesktopAcrylicKind.Base;
            }
            catch
            {
            }

            _controller.TintColor = tint;
            _controller.TintOpacity = strength.TintOpacity;
            _controller.LuminosityOpacity = strength.LuminosityOpacity(darkTheme);
            _controller.FallbackColor = tint;
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("acrylic-backdrop", ex);
            Detach();
            return false;
        }
    }

    public void Detach()
    {
        try
        {
            _controller?.Dispose();
        }
        catch
        {
        }
        _controller = null;
    }

    public void Dispose() => Detach();
}
