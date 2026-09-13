using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using ShapesPath = Microsoft.UI.Xaml.Shapes.Path;

namespace SteamEyaWinUI.Pages;

public sealed partial class PersonalizationPage : Page, INotifyPropertyChanged
{
    private const uint OutputSize = 512;     // 导出头像边长（像素）
    private const uint PreviewPx = 184;      // 预览主位图分辨率（最大档 184，64/32 由它缩小显示）
    private const double StageW = 388;       // 裁剪台内容区宽（DIP，= 裁剪台 420 − 内边距 16×2）
    private const double StageH = 288;       // 裁剪台内容区高（DIP，= 裁剪台 320 − 内边距 16×2）
    private const double MaxZoom = 5;        // 背景图最大放大倍数（相对“适应”）
    private const double HandleHit = 16;     // 把手命中半径（DIP）
    private const double HandleSize = 10;    // 把手视觉边长（DIP）
    private const double MinSourcePx = 184;  // Steam 要求头像 ≥184px，选框最小映射到这么多源像素
    private const double MinSelFloor = 28;   // 选框最小显示边长兜底（DIP）
    private const int NicknameMaxLength = 64;

    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    // 当前源图（原始字节，导出/预览时按选框重新解码裁剪）。null = 尚未载入任何图片。
    private byte[]? _sourceBytes;
    private double _srcW;                 // 源图（含 EXIF 朝向）像素宽
    private double _srcH;
    private double _fitScale;             // “适应”时的显示缩放：裁剪台 DIP / 源像素（= zoom 1）
    private double _dispW;                // 适应时整图显示宽（DIP）= 源宽 × _fitScale
    private double _dispH;
    private double _zoom = 1;             // 背景图缩放倍数（≥1，1 = 适应）
    private double _panX;                 // 背景图左上角在裁剪台坐标系下的位移（DIP）
    private double _panY;
    private double _selX;                 // 方形选框左上角 X（裁剪台坐标系，DIP）
    private double _selY;
    private double _selSize;              // 方形选框边长（DIP）

    private enum DragMode { None, Pan, TL, T, TR, R, BR, B, BL, L }
    private DragMode _dragMode;
    private Point _dragStartPointer;
    private double _startPanX, _startPanY;

    // 覆盖层图元（首次载图时建好，之后只改位置/几何）。
    private bool _overlayBuilt;
    private ShapesPath? _dim;             // 选框外的暗化遮罩（EvenOdd 挖洞）
    private Rectangle? _selBorder;        // 选框白边
    private readonly Line[] _grid = new Line[4];       // 三分线
    private readonly Rectangle[] _handles = new Rectangle[8]; // 8 个把手：TL,T,TR,R,BR,B,BL,L

    private bool _loadingSettings;        // 初始填充昵称 / 简介 / 曾用名选项时屏蔽变更回写
    private bool _previewRendering;       // 预览正在异步渲染
    private bool _previewPending;         // 渲染期间又有新取景，渲染完后再来一遍（最新优先）

    public PersonalizationPage()
    {
        InitializeComponent();

        // 把缩放/平移后溢出的背景图裁到裁剪台内。
        CropArea.Clip = new RectangleGeometry { Rect = new Rect(0, 0, StageW, StageH) };

        Loc.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _loadingSettings = true;
        var settings = AppState.SettingsService.Load();
        NicknameBox.Text = settings.PersonaName ?? string.Empty;
        RealNameBox.Text = settings.ProfileRealName ?? string.Empty;
        SummaryBox.Text = settings.ProfileSummary ?? string.Empty;
        ClearAliasCheckBox.IsChecked = settings.ClearAliasHistoryOnPersonalize;
        _loadingSettings = false;
        UpdateNicknameCounter();

        // 首次进入时若已有保存的头像，载入裁剪区作为可继续微调的源图。
        if (_sourceBytes is null)
        {
            var avatarPath = AppState.SettingsService.PersonalizationAvatarPath;
            if (File.Exists(avatarPath))
            {
                _ = LoadSavedAvatarAsync(avatarPath);
            }
        }
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings))));
    }

    // ---- 昵称 / 简介 / 曾用名选项（随输入即时落盘） ----

    private void NicknameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateNicknameCounter();
        SaveSettings(settings => settings.PersonaName = NicknameBox.Text);
    }

    private void RealNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveSettings(settings => settings.ProfileRealName = RealNameBox.Text);
    }

    private void SummaryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveSettings(settings => settings.ProfileSummary = SummaryBox.Text);
    }

    private void ClearAliasCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        SaveSettings(settings => settings.ClearAliasHistoryOnPersonalize = ClearAliasCheckBox.IsChecked == true);
    }

    private void SaveSettings(Action<AppSettings> apply)
    {
        if (_loadingSettings)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        apply(settings);
        AppState.SettingsService.Save(settings);
    }

    private void UpdateNicknameCounter()
    {
        NicknameCounter.Text = $"{NicknameBox.Text.Length}/{NicknameMaxLength}";
    }

    // ---- 选图 / 载入 ----

    private async void PickImageButton_Click(object sender, RoutedEventArgs e)
    {
        // 用 Win32 经典文件对话框而非 WinRT FileOpenPicker：无包装 WinUI + 提权运行下
        // WinRT 选择器常常静默不弹窗（broker 失败且不抛异常），Win32 GetOpenFileName 则照常工作。
        try
        {
            var path = PickImageFileWin32(MainWindow.Hwnd);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            await LoadSourceAsync(await File.ReadAllBytesAsync(path));
        }
        catch (Exception ex)
        {
            AppLog.Error("打开头像图片选择器失败。", ex);
            // 用全局状态栏（始终可见），并带上原因，避免再次“没反应”却看不到错误。
            AppState.ShowStatus($"{Loc.T("Personalization_ImageLoadFailed")} {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task LoadSavedAvatarAsync(string path)
    {
        try
        {
            await LoadSourceAsync(await File.ReadAllBytesAsync(path));
        }
        catch (Exception ex)
        {
            AppLog.Error("载入已保存头像失败。", ex);
        }
    }

    private async Task LoadSourceAsync(byte[] bytes)
    {
        double w, h;
        using (var stream = new InMemoryRandomAccessStream())
        {
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            w = decoder.OrientedPixelWidth;
            h = decoder.OrientedPixelHeight;
        }

        if (w <= 0 || h <= 0)
        {
            ShowInfo(Loc.T("Personalization_ImageLoadFailed"), InfoBarSeverity.Error);
            return;
        }

        _srcW = w;
        _srcH = h;
        _sourceBytes = bytes;

        // 整图按比例缩放到裁剪台内（zoom 1 = 适应）。
        _fitScale = Math.Min(StageW / _srcW, StageH / _srcH);
        _dispW = _srcW * _fitScale;
        _dispH = _srcH * _fitScale;

        // 显示位图解码到 2x 裁剪台尺寸（适应时清晰；放大时由 RenderTransform 缩放，略有软化但预览/导出始终清晰）。
        // 不 dispose displayBitmap：SoftwareBitmapSource 内部持有它的引用，提前 dispose 会让渲染撞上已释放位图，
        // 快速导航时原生闪退(0xc000027b)。让 source 持有，旧 source 被替换后自然 GC。
        var displayBitmap = await DecodeScaledAsync(bytes, StageW * 2, StageH * 2);
        var displaySource = new SoftwareBitmapSource();
        await displaySource.SetBitmapAsync(displayBitmap);

        CropImage.Width = _dispW;
        CropImage.Height = _dispH;
        CropImage.Source = displaySource;

        // 初始：适应、居中。
        _zoom = 1;
        _panX = (StageW - _dispW) / 2;
        _panY = (StageH - _dispH) / 2;
        ApplyImageTransform();

        // 初始选框：裁剪台居中、取图内能放下的最大正方形。选框始终居中，靠平移/缩放背景图取景。
        CenterBox(Math.Min(_dispW, _dispH));

        CropArea.Visibility = Visibility.Visible;
        CropPlaceholder.Visibility = Visibility.Collapsed;

        BuildOverlay();
        UpdateOverlay();
        RenderPreviews();

        SaveAvatarButton.IsEnabled = true;
    }

    // 把整图按比例解码到 maxW×maxH 以内（不放大），返回 BGRA8 SoftwareBitmap。
    private static async Task<SoftwareBitmap> DecodeScaledAsync(byte[] bytes, double maxW, double maxH)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(bytes.AsBuffer());
        input.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(input);
        double w = decoder.OrientedPixelWidth;
        double h = decoder.OrientedPixelHeight;

        var scale = Math.Min(Math.Min(maxW / w, maxH / h), 1.0);
        var sw = Math.Max(1u, (uint)Math.Round(w * scale));
        var sh = Math.Max(1u, (uint)Math.Round(h * scale));

        var transform = new BitmapTransform
        {
            ScaledWidth = sw,
            ScaledHeight = sh,
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return SoftwareBitmap.CreateCopyFromBuffer(
            pixels.DetachPixelData().AsBuffer(),
            BitmapPixelFormat.Bgra8,
            (int)sw, (int)sh,
            BitmapAlphaMode.Premultiplied);
    }

    private void ApplyImageTransform()
    {
        CropImageTransform.ScaleX = _zoom;
        CropImageTransform.ScaleY = _zoom;
        CropImageTransform.TranslateX = _panX;
        CropImageTransform.TranslateY = _panY;
    }

    // 约束平移，使背景图始终覆盖选框（保证裁剪区落在图内）。
    private void ConstrainPan()
    {
        var iw = _dispW * _zoom;
        var ih = _dispH * _zoom;
        _panX = ClampSafe(_panX, _selX + _selSize - iw, _selX);
        _panY = ClampSafe(_panY, _selY + _selSize - ih, _selY);
    }

    // 选框始终居中于裁剪台，只改大小。
    private void CenterBox(double size)
    {
        _selSize = size;
        _selX = (StageW - size) / 2;
        _selY = (StageH - size) / 2;
    }

    // 容忍浮点误差的 Clamp：当 max 因舍入误差略小于 min 时不抛异常，取 min。
    private static double ClampSafe(double value, double min, double max)
        => max <= min ? min : Math.Clamp(value, min, max);

    // ---- 选框覆盖层 ----

    private void BuildOverlay()
    {
        if (_overlayBuilt)
        {
            return;
        }

        _dim = new ShapesPath
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
            IsHitTestVisible = false
        };
        CropOverlay.Children.Add(_dim);

        for (var i = 0; i < 4; i++)
        {
            _grid[i] = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            CropOverlay.Children.Add(_grid[i]);
        }

        _selBorder = new Rectangle
        {
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
        CropOverlay.Children.Add(_selBorder);

        for (var i = 0; i < 8; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x33)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            CropOverlay.Children.Add(_handles[i]);
        }

        _overlayBuilt = true;
    }

    private void UpdateOverlay()
    {
        if (!_overlayBuilt || _dim is null || _selBorder is null)
        {
            return;
        }

        // 暗化遮罩：外框 + 选框两条矩形，EvenOdd 把选框挖空。
        _dim.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children =
            {
                new RectangleGeometry { Rect = new Rect(0, 0, StageW, StageH) },
                new RectangleGeometry { Rect = new Rect(_selX, _selY, _selSize, _selSize) }
            }
        };

        Canvas.SetLeft(_selBorder, _selX);
        Canvas.SetTop(_selBorder, _selY);
        _selBorder.Width = _selSize;
        _selBorder.Height = _selSize;

        // 三分线
        var t3 = _selSize / 3;
        SetLine(_grid[0], _selX + t3, _selY, _selX + t3, _selY + _selSize);
        SetLine(_grid[1], _selX + 2 * t3, _selY, _selX + 2 * t3, _selY + _selSize);
        SetLine(_grid[2], _selX, _selY + t3, _selX + _selSize, _selY + t3);
        SetLine(_grid[3], _selX, _selY + 2 * t3, _selX + _selSize, _selY + 2 * t3);

        // 8 个把手（居中对齐到各点）
        var cx = _selX + _selSize / 2;
        var cy = _selY + _selSize / 2;
        var r = _selX + _selSize;
        var b = _selY + _selSize;
        PlaceHandle(0, _selX, _selY);  // TL
        PlaceHandle(1, cx, _selY);     // T
        PlaceHandle(2, r, _selY);      // TR
        PlaceHandle(3, r, cy);         // R
        PlaceHandle(4, r, b);          // BR
        PlaceHandle(5, cx, b);         // B
        PlaceHandle(6, _selX, b);      // BL
        PlaceHandle(7, _selX, cy);     // L
    }

    private static void SetLine(Line line, double x1, double y1, double x2, double y2)
    {
        line.X1 = x1;
        line.Y1 = y1;
        line.X2 = x2;
        line.Y2 = y2;
    }

    private void PlaceHandle(int i, double x, double y)
    {
        Canvas.SetLeft(_handles[i], x - HandleSize / 2);
        Canvas.SetTop(_handles[i], y - HandleSize / 2);
    }

    // ---- 选框拖动 / 缩放 / 背景平移 ----

    private void CropOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_sourceBytes is null)
        {
            return;
        }

        var pos = e.GetCurrentPoint(CropOverlay).Position;
        _dragMode = HitTest(pos);
        if (_dragMode == DragMode.None)
        {
            return;
        }

        _dragStartPointer = pos;
        _startPanX = _panX;
        _startPanY = _panY;
        CropOverlay.CapturePointer(e.Pointer);
    }

    private void CropOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_sourceBytes is null)
        {
            return;
        }

        var pos = e.GetCurrentPoint(CropOverlay).Position;

        if (_dragMode == DragMode.None)
        {
            UpdateHoverCursor(HitTest(pos));
            return;
        }

        if (_dragMode == DragMode.Pan)
        {
            _panX = _startPanX + (pos.X - _dragStartPointer.X);
            _panY = _startPanY + (pos.Y - _dragStartPointer.Y);
            ConstrainPan();
            ApplyImageTransform();
        }
        else
        {
            ApplyDrag(_dragMode, pos);
            ConstrainPan();          // 选框变大后可能需要移动背景图以继续覆盖
            ApplyImageTransform();
            UpdateOverlay();
        }

        RenderPreviews();
    }

    private void CropOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragMode = DragMode.None;
        CropOverlay.ReleasePointerCapture(e.Pointer);
    }

    // 滚轮缩放背景图（选框大小、位置不变）：上滚放大、下滚缩小，以光标处为锚点。
    private void CropOverlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_sourceBytes is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(CropOverlay);
        var cx = point.Position.X;
        var cy = point.Position.Y;

        var factor = point.Properties.MouseWheelDelta > 0 ? 1.1 : 1.0 / 1.1;
        var newZoom = ClampSafe(_zoom * factor, 1, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 1e-6)
        {
            e.Handled = true;
            return;
        }

        // 围绕光标缩放：光标对应的图像点保持在光标位置不动。
        _panX = cx - (cx - _panX) * (newZoom / _zoom);
        _panY = cy - (cy - _panY) * (newZoom / _zoom);
        _zoom = newZoom;
        ConstrainPan();
        ApplyImageTransform();
        RenderPreviews();
        e.Handled = true;   // 阻止滚轮冒泡到页面 ScrollViewer
    }

    private DragMode HitTest(Point p)
    {
        var cx = _selX + _selSize / 2;
        var cy = _selY + _selSize / 2;
        var r = _selX + _selSize;
        var b = _selY + _selSize;

        // 命中把手 = 缩放选框；其余（框内或框外）一律平移背景图。
        if (Near(p, _selX, _selY)) return DragMode.TL;
        if (Near(p, r, _selY)) return DragMode.TR;
        if (Near(p, r, b)) return DragMode.BR;
        if (Near(p, _selX, b)) return DragMode.BL;
        if (Near(p, cx, _selY)) return DragMode.T;
        if (Near(p, r, cy)) return DragMode.R;
        if (Near(p, cx, b)) return DragMode.B;
        if (Near(p, _selX, cy)) return DragMode.L;
        return DragMode.Pan;
    }

    private static bool Near(Point p, double x, double y)
        => Math.Abs(p.X - x) <= HandleHit && Math.Abs(p.Y - y) <= HandleHit;

    private void UpdateHoverCursor(DragMode mode)
    {
        var shape = mode switch
        {
            DragMode.TL or DragMode.BR => InputSystemCursorShape.SizeNorthwestSoutheast,
            DragMode.TR or DragMode.BL => InputSystemCursorShape.SizeNortheastSouthwest,
            DragMode.T or DragMode.B => InputSystemCursorShape.SizeNorthSouth,
            DragMode.L or DragMode.R => InputSystemCursorShape.SizeWestEast,
            DragMode.Pan => InputSystemCursorShape.SizeAll,
            _ => InputSystemCursorShape.Arrow
        };
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    // 边角/边缩放选框（始终居中、保持正方形）：裁剪台中心到光标的距离决定边长。
    // 上限为“适应”时的最大方形 min(dispW,dispH)——这样任意缩放下选框都 ≤ 背景图，恒能被覆盖。
    private void ApplyDrag(DragMode mode, Point pos)
    {
        var cx0 = StageW / 2;
        var cy0 = StageH / 2;
        var maxSel = Math.Min(_dispW, _dispH);
        var minSel = ClampSafe(MinSourcePx * _zoom * _fitScale, MinSelFloor, maxSel);

        var half = mode switch
        {
            DragMode.TL or DragMode.TR or DragMode.BR or DragMode.BL
                => Math.Max(Math.Abs(pos.X - cx0), Math.Abs(pos.Y - cy0)),
            DragMode.T or DragMode.B => Math.Abs(pos.Y - cy0),
            DragMode.L or DragMode.R => Math.Abs(pos.X - cx0),
            _ => _selSize / 2
        };

        CenterBox(ClampSafe(2 * half, minSel, maxSel));
    }

    // 选框（裁剪台坐标）→ 源图像素。背景图缩放/平移后，源坐标 = (选框 − pan) / (zoom × fitScale)。
    private (double X, double Y, double Size) SelectionInSource()
    {
        var s = _zoom * _fitScale;
        return ((_selX - _panX) / s, (_selY - _panY) / s, _selSize / s);
    }

    // ---- 预览（184 / 64 / 32）----

    // 按当前选框把源图裁剪成 184² 位图，三个尺寸预览共用（小档由 Image 缩小显示）。
    // 异步、最新优先：拖动时按解码速度自然限流，始终渲染最新取景。
    private async void RenderPreviews()
    {
        if (_sourceBytes is null)
        {
            return;
        }

        if (_previewRendering)
        {
            _previewPending = true;
            return;
        }

        _previewRendering = true;
        try
        {
            do
            {
                _previewPending = false;
                var bytes = _sourceBytes;
                if (bytes is null)
                {
                    break;
                }

                var (cropX, cropY, cropSize) = SelectionInSource();

                // 用 BitmapImage（编码成内存 PNG 再解码）而非 SoftwareBitmapSource：后者既不能同时挂到多个
                // Image 上，set 后又不能 dispose 其 SoftwareBitmap，否则快速导航时会原生闪退(0xc000027b)。
                // BitmapImage 自持数据、可被多个 Image 共享，导航期间稳定。
                var image = await CropToBitmapImageAsync(bytes, cropX, cropY, cropSize, PreviewPx);
                Preview184.Source = image;
                Preview64.Source = image;
                Preview32.Source = image;
            }
            while (_previewPending);
        }
        catch (Exception ex)
        {
            AppLog.Error("渲染头像预览失败。", ex);
        }
        finally
        {
            _previewRendering = false;
        }
    }

    // ---- 导出头像 ----

    private async void SaveAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceBytes is null)
        {
            return;
        }

        try
        {
            var (cropX, cropY, cropSize) = SelectionInSource();
            var outputPath = AppState.SettingsService.PersonalizationAvatarPath;
            await CropAndSaveJpegAsync(_sourceBytes, cropX, cropY, cropSize, outputPath);

            ShowInfo(Loc.T("Personalization_AvatarSaved"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("保存个性化头像失败。", ex);
            ShowInfo(Loc.T("Personalization_AvatarSaveFailed"), InfoBarSeverity.Error);
        }
    }

    // 源图 → 选框区 → outSize² BGRA8(Premultiplied) 像素。预览与导出共用同一裁剪几何，保证所见即所得。
    private static async Task<byte[]> CropToBgraPixelsAsync(
        byte[] sourceBytes, double cropX, double cropY, double cropSize, uint outSize)
    {
        using var inputStream = new InMemoryRandomAccessStream();
        await inputStream.WriteAsync(sourceBytes.AsBuffer());
        inputStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(inputStream);
        double srcW = decoder.OrientedPixelWidth;
        double srcH = decoder.OrientedPixelHeight;

        cropSize = Math.Min(cropSize, Math.Min(srcW, srcH));
        cropX = ClampSafe(cropX, 0, srcW - cropSize);
        cropY = ClampSafe(cropY, 0, srcH - cropSize);

        // BitmapTransform 先缩放后按 Bounds 裁剪：把整图按 f 缩放，使裁剪区恰好落到 outSize²。
        var f = outSize / cropSize;
        var scaledW = (uint)Math.Round(srcW * f);
        var scaledH = (uint)Math.Round(srcH * f);
        var boundsX = (uint)Math.Round(cropX * f);
        var boundsY = (uint)Math.Round(cropY * f);
        if (boundsX + outSize > scaledW)
        {
            boundsX = scaledW - outSize;
        }

        if (boundsY + outSize > scaledH)
        {
            boundsY = scaledH - outSize;
        }

        var transform = new BitmapTransform
        {
            ScaledWidth = scaledW,
            ScaledHeight = scaledH,
            Bounds = new BitmapBounds { X = boundsX, Y = boundsY, Width = outSize, Height = outSize },
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return pixels.DetachPixelData();
    }

    // 源图 → 选框区 → outSize² 的 BitmapImage（经内存 PNG 编解码）。BitmapImage 自持像素、可被多个
    // Image 共享、导航期间稳定，避免 SoftwareBitmapSource 的“不可共享 / set 后不可 dispose”闪退坑。
    private static async Task<BitmapImage> CropToBitmapImageAsync(
        byte[] sourceBytes, double cropX, double cropY, double cropSize, uint outSize)
    {
        var pixels = await CropToBgraPixelsAsync(sourceBytes, cropX, cropY, cropSize, outSize);

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            outSize, outSize,
            96, 96,
            pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    private static async Task CropAndSaveJpegAsync(
        byte[] sourceBytes, double cropX, double cropY, double cropSize, string outputPath)
    {
        var pixels = await CropToBgraPixelsAsync(sourceBytes, cropX, cropY, cropSize, OutputSize);

        using var outputStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            OutputSize, OutputSize,
            96, 96,
            pixels);
        await encoder.FlushAsync();

        outputStream.Seek(0);
        var encodedBytes = new byte[outputStream.Size];
        await outputStream.ReadAsync(encodedBytes.AsBuffer(), (uint)outputStream.Size, InputStreamOptions.None);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

        // 原子写：先写临时文件再整体替换，避免读取端撞上写入中的半截 JPEG。
        var tempPath = outputPath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, encodedBytes);
        File.Move(tempPath, outputPath, overwrite: true);
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        PageInfoBar.Message = message;
        PageInfoBar.Severity = severity;
        PageInfoBar.IsOpen = true;
    }

    // ---- Win32 文件对话框（替代在提权 / 无包装场景下不可靠的 WinRT FileOpenPicker）----

    [LibraryImport("comdlg32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileNameW(ref OpenFileName ofn);

    private static unsafe string? PickImageFileWin32(nint owner)
    {
        const int ofnExplorer = 0x00080000;
        const int ofnFileMustExist = 0x00001000;
        const int ofnPathMustExist = 0x00000800;
        const int ofnNoChangeDir = 0x00000008;
        const int maxFile = 4096;

        // 过滤器格式：「标签\0通配符\0\0」（双 null 结尾）。
        var filter = $"{Loc.T("Personalization_Avatar_FileType")}\0*.jpg;*.jpeg;*.png;*.bmp;*.webp\0\0";
        var title = Loc.T("Personalization_Btn_PickImage");
        var fileBuffer = new char[maxFile];

        fixed (char* filterPtr = filter)
        fixed (char* titlePtr = title)
        fixed (char* filePtr = fileBuffer)
        {
            var ofn = new OpenFileName
            {
                lStructSize = sizeof(OpenFileName),
                hwndOwner = owner,
                lpstrFilter = (nint)filterPtr,
                nFilterIndex = 1,
                lpstrFile = (nint)filePtr,
                nMaxFile = maxFile,
                lpstrTitle = (nint)titlePtr,
                Flags = ofnExplorer | ofnFileMustExist | ofnPathMustExist | ofnNoChangeDir
            };

            return GetOpenFileNameW(ref ofn) ? new string(filePtr) : null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public nint lpstrFilter;
        public nint lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public nint lpstrFile;
        public int nMaxFile;
        public nint lpstrFileTitle;
        public int nMaxFileTitle;
        public nint lpstrInitialDir;
        public nint lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public nint lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public nint lpTemplateName;
        public nint pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }
}
