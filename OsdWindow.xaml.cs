using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinRT;
using WinRT.Interop;

namespace LockOsd;

public sealed partial class OsdWindow : Window
{
    private const double HeightDip = 48;
    private const double SlideDip = 24;
    private const float ContentRiseDip = 6;
    private const string LockGlyph = "";
    private const string UnlockGlyph = "";

    // Fluent durations from the Windows App SDK theme resources:
    // ControlFasterAnimationDuration and ControlFastAnimationDuration.
    private static readonly TimeSpan ContentOutDuration = TimeSpan.FromMilliseconds(83);
    private static readonly TimeSpan ContentInDuration = TimeSpan.FromMilliseconds(167);

    private readonly IntPtr _hwnd;
    private readonly DesktopAcrylicController _acrylic = new();
    private readonly SystemBackdropConfiguration _backdropConfig = new();
    private readonly UISettings _uiSettings = new();
    private readonly DispatcherQueueTimer _holdTimer;
    private readonly WindowMotion _motion;
    private readonly Visual _contentVisual;
    private readonly TextBlock _measureText = new();

    // Kept in a field so the GC does not collect the delegate while native code holds it.
    private SubclassProc? _windowProc;
    private IReadOnlyList<string> _labels;
    private DisplayConfig _display;
    private (string Text, bool IsOn) _pendingContent;
    private bool _isSwappingContent;

    public OsdWindow(IReadOnlyList<string> labels, DisplayConfig display)
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _labels = labels;
        _display = display;
        _measureText.Style = StateText.Style;

        ElementCompositionPreview.SetIsTranslationEnabled(ContentPanel, true);
        _contentVisual = ElementCompositionPreview.GetElementVisual(ContentPanel);
        _contentVisual.Properties.InsertVector3("Translation", Vector3.Zero);

        var queue = DispatcherQueue.GetForCurrentThread();
        _motion = new WindowMotion(_hwnd, queue, () => AppWindow.Hide());

        _holdTimer = queue.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(display.DurationMs);
        _holdTimer.IsRepeating = false;
        _holdTimer.Tick += (_, _) => Dismiss();

        ConfigureWindow();
        ConfigureBackdrop();
        WarmUp();
    }

    /// <summary>Takes effect the next time the OSD is shown.</summary>
    public void ApplySettings(IReadOnlyList<string> labels, DisplayConfig display)
    {
        _labels = labels;
        _display = display;
        _holdTimer.Interval = TimeSpan.FromMilliseconds(display.DurationMs);
    }

    public void ShowState(string text, bool isOn)
    {
        if (_uiSettings.AnimationsEnabled && _motion.IsOnScreen)
        {
            CrossfadeContent(text, isOn);
        }
        else
        {
            SnapContent(text, isOn);
        }

        PlaceOnDisplay();

        if (!AppWindow.IsVisible)
        {
            _motion.ApplyHidden();
            AppWindow.Show(false);
        }

        if (_uiSettings.AnimationsEnabled)
        {
            _motion.AnimateTo(true);
        }
        else
        {
            _motion.JumpTo(true);
        }

        _holdTimer.Stop();
        _holdTimer.Start();
    }

    private void Dismiss()
    {
        if (_uiSettings.AnimationsEnabled)
        {
            _motion.AnimateTo(false);
        }
        else
        {
            _motion.JumpTo(false);
        }
    }

    // The first frame of a XAML window is slow. Rendering it invisibly here means the first
    // real key press animates from frame one instead of dropping the start of the entrance.
    private void WarmUp()
    {
        SnapContent(_labels[0], isOn: false);
        PlaceOnDisplay();
        _motion.ApplyHidden();
        AppWindow.Show(false);
        _holdTimer.Start();
    }

    private void CrossfadeContent(string text, bool isOn)
    {
        _pendingContent = (text, isOn);
        if (_isSwappingContent)
        {
            return;
        }

        _isSwappingContent = true;
        var compositor = _contentVisual.Compositor;

        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(1, 0, compositor.CreateLinearEasingFunction());
        fadeOut.Duration = ContentOutDuration;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _contentVisual.StartAnimation("Opacity", fadeOut);
        batch.End();
        batch.Completed += (_, _) =>
        {
            _isSwappingContent = false;
            SetContent(_pendingContent.Text, _pendingContent.IsOn);
            PlayContentIn(compositor);
        };
    }

    private void PlayContentIn(Compositor compositor)
    {
        var decelerate = compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));

        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(1, 1, compositor.CreateLinearEasingFunction());
        fadeIn.Duration = ContentInDuration;

        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, ContentRiseDip, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, decelerate);
        rise.Duration = ContentInDuration;

        _contentVisual.StartAnimation("Opacity", fadeIn);
        _contentVisual.StartAnimation("Translation", rise);
    }

    private void SnapContent(string text, bool isOn)
    {
        _contentVisual.StopAnimation("Opacity");
        _contentVisual.StopAnimation("Translation");
        _contentVisual.Opacity = 1;
        _contentVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        SetContent(text, isOn);
    }

    private void SetContent(string text, bool isOn)
    {
        StateText.Text = text;
        StateIcon.Glyph = isOn ? LockGlyph : UnlockGlyph;
        StateIcon.Foreground = (Brush)Application.Current.Resources[
            isOn ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
    }

    private void ConfigureWindow()
    {
        AppWindow.IsShownInSwitchers = false;

        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);

        // Only after SetPresenter: set earlier it is lost, and the presenter then rewrites any
        // SetWindowPos that tries to make the window topmost, leaving it behind maximized windows.
        presenter.IsAlwaysOnTop = true;

        // Click-through and focus-free: the OSD must never interrupt what the user is doing.
        var exStyle = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        exStyle |= WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow;
        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(exStyle));

        _windowProc = WindowProc;
        SetWindowSubclass(_hwnd, _windowProc, UIntPtr.Zero, UIntPtr.Zero);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        var cornerPreference = DwmwcpRound;
        DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

        // The XAML stroke is the visible edge; the default DWM border is far too bright here.
        var noBorder = DwmwaColorNone;
        DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref noBorder, sizeof(int));
    }

    // A borderless presenter keeps a 3 px frame that a layered window paints solid white, and
    // AppWindow restores the frame styles if they are removed. Giving the client the whole
    // window leaves no frame area to paint.
    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        if (msg == WmNcCalcSize && wParam != IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ConfigureBackdrop()
    {
        // A no-activate window is never input-active, and acrylic turns opaque when inactive.
        _backdropConfig.IsInputActive = true;
        _acrylic.Kind = DesktopAcrylicKind.Thin;
        _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);

        Root.ActualThemeChanged += (_, _) => ApplyBackdropTheme();
        ApplyBackdropTheme();
    }

    private void ApplyBackdropTheme()
    {
        var isDark = Root.ActualTheme == ElementTheme.Dark;
        _backdropConfig.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }

    private void PlaceOnDisplay()
    {
        var foreground = GetForegroundWindow();
        var area = _display.Monitor == OsdMonitor.Active && foreground != IntPtr.Zero
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(foreground), DisplayAreaFallback.Primary)
            : DisplayArea.Primary;

        GetDpiForMonitor(Win32Interop.GetMonitorFromDisplayId(area.DisplayId), MdtEffectiveDpi, out var dpi, out _);
        var scale = dpi / 96.0;

        var width = (int)Math.Ceiling(MeasureFixedWidth() * scale);
        var height = (int)Math.Ceiling(HeightDip * scale);
        var work = area.WorkArea;
        var margin = (int)Math.Round(_display.MarginDip * scale);
        var isTop = _display.Position == OsdPosition.Top;

        var rest = new PointInt32(
            work.X + ((work.Width - width) / 2),
            isTop ? work.Y + margin : work.Y + work.Height - height - margin);

        // The hidden position is toward the nearest screen edge, so the OSD slides in from it.
        var slidePx = (int)Math.Round(SlideDip * scale);
        _motion.SetRest(rest, isTop ? -slidePx : slidePx);
        AppWindow.Resize(new SizeInt32(width, height));
    }

    // Fits the longest label so a state change crossfades in place instead of resizing the window.
    private double MeasureFixedWidth()
    {
        Root.Measure(new Size(double.PositiveInfinity, HeightDip));
        var currentText = MeasureText(StateText.Text);
        var longestText = _labels.Max(MeasureText);
        return Root.DesiredSize.Width - currentText + longestText;
    }

    private double MeasureText(string text)
    {
        _measureText.Text = text;
        _measureText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _measureText.DesiredSize.Width;
    }

    private const uint WmNcCalcSize = 0x0083;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const long WsExTransparent = 0x00000020;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
    private const int MdtEffectiveDpi = 0;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
