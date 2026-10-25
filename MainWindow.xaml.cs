using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace PdfMangaViewer;

public sealed partial class MainWindow : Window
{
    /// <summary>キャッシュに保持するページ数の上限。</summary>
    private const int MaxCachedPages = 8;

    /// <summary>レンダリングする画像の長辺の上限（px）。メモリ使用量の抑制用。</summary>
    private const double MaxRenderEdge = 2600;

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfMangaViewer");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private sealed record CachedPage(BitmapImage Image, int PixelWidth, int PixelHeight);

    private readonly record struct PageSize(double Width, double Height);

    private readonly record struct PixelSize(int Width, int Height);

    /// <summary>見開き（2ページ表示）1組分のレイアウト。</summary>
    private readonly record struct SpreadLayout(
        PixelSize FirstRenderSize,
        PixelSize SecondRenderSize,
        double DisplayHeight,
        double FirstDisplayWidth,
        double SecondDisplayWidth);

    private readonly Dictionary<uint, CachedPage> _cache = new();
    private readonly LinkedList<uint> _lru = new();
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    private readonly DispatcherTimer _hideControlsTimer = new();
    private readonly DispatcherTimer _resizeTimer = new();

    private PdfDocument? _document;
    private string? _displayName;
    private uint _pageIndex;
    private bool _rightToLeft = true;
    private SpreadMode _spreadMode = SpreadMode.Off;
    private bool _reverseKeys = true;
    private bool _controlsVisible;
    private bool _isFullScreen;
    private bool _dialogOpen;
    private string? _pendingOpenPath;

    private int _documentVersion;
    private int _renderVersion;
    private int _prefetchGeneration;
    private DateTime _lastWheelTurn = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        ConfigureInitialWindow();
        ConfigureTitleBar();

        _hideControlsTimer.Interval = TimeSpan.FromSeconds(1.0);
        _hideControlsTimer.Tick += (_, _) => HideControls();

        _resizeTimer.Interval = TimeSpan.FromMilliseconds(250);
        _resizeTimer.Tick += (_, _) => OnResizeSettled();

        LoadSettings();
        UpdateSpreadUi();
        UpdateReadingDirectionUi();
        UpdateNavigationUi();
    }

    // ------------------------------------------------------------------
    // 起動・ファイルを開く
    // ------------------------------------------------------------------

    /// <summary>起動直後に開く PDF を指定する（コマンドライン引数用）。</summary>
    public void ScheduleOpen(string path)
    {
        if (RootGrid.IsLoaded)
        {
            _ = OpenPdfAsync(path);
        }
        else
        {
            _pendingOpenPath = path;
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);

        if (_pendingOpenPath is { } path)
        {
            _pendingOpenPath = null;
            await OpenPdfAsync(path);
        }
    }

    private async Task OpenPdfAsync(string path)
    {
        StorageFile file;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("ファイルを開けません", ex.Message);
            return;
        }

        await OpenPdfAsync(file);
    }

    private async Task OpenPdfAsync(StorageFile file)
    {
        int version = ++_documentVersion;
        _renderVersion++;
        _prefetchGeneration++;
        SetBusy(true);

        try
        {
            PdfDocument document = await PdfDocument.LoadFromFileAsync(file);
            if (version != _documentVersion)
            {
                return;
            }

            _document = document;
            _displayName = file.DisplayName;
            _pageIndex = 0;

            _cache.Clear();
            _lru.Clear();

            PageImage.Source = null;
            SpreadLeftImage.Source = null;
            SpreadRightImage.Source = null;
            SpreadPanel.Visibility = Visibility.Collapsed;
            PageImage.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
            FileNameText.Text = file.Name;

            UpdatePageLabel();
            await ShowViewAsync(0);
            ShowControls();
        }
        catch (Exception ex)
        {
            if (version != _documentVersion)
            {
                return;
            }

            await ShowErrorAsync("PDFを開けませんでした", $"{file.Name}\n\n{ex.Message}");
        }
        finally
        {
            if (version == _documentVersion)
            {
                SetBusy(false);
            }
        }
    }

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        picker.FileTypeFilter.Add(".pdf");

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await OpenPdfAsync(file);
        }
    }

    // ------------------------------------------------------------------
    // ページ表示
    // ------------------------------------------------------------------

    /// <summary>現在の表示モード（単ページ／見開き）に応じてページを表示する。</summary>
    private async Task ShowViewAsync(uint start)
    {
        if (_document is null)
        {
            return;
        }

        start = NormalizeToView(start);
        _pageIndex = start;
        _prefetchGeneration++;
        UpdatePageLabel();

        PixelSize viewport = GetViewportPixels();
        int version = ++_renderVersion;

        try
        {
            if (!IsTwoPageView(start))
            {
                PixelSize desired = GetRenderSize(start, viewport);
                CachedPage? page = await GetPageForDisplayAsync(start, desired, version);
                if (page is null)
                {
                    return;
                }

                PageImage.Source = page.Image;
                PageImage.Visibility = Visibility.Visible;
                SpreadPanel.Visibility = Visibility.Collapsed;
                StartPrefetch(start, viewport);
                return;
            }

            SpreadLayout layout = GetSpreadLayout(start, viewport);

            CachedPage? first = await GetPageForDisplayAsync(start, layout.FirstRenderSize, version);
            if (first is null)
            {
                return;
            }

            CachedPage? second = await GetPageForDisplayAsync(start + 1, layout.SecondRenderSize, version);
            if (second is null)
            {
                return;
            }

            // 右綴じでは若いページを右、後のページを左に置く
            SpreadLeftImage.Width = _rightToLeft ? layout.SecondDisplayWidth : layout.FirstDisplayWidth;
            SpreadLeftImage.Height = layout.DisplayHeight;
            SpreadLeftImage.Source = _rightToLeft ? second.Image : first.Image;

            SpreadRightImage.Width = _rightToLeft ? layout.FirstDisplayWidth : layout.SecondDisplayWidth;
            SpreadRightImage.Height = layout.DisplayHeight;
            SpreadRightImage.Source = _rightToLeft ? first.Image : second.Image;

            SpreadPanel.Visibility = Visibility.Visible;
            PageImage.Visibility = Visibility.Collapsed;
            StartPrefetch(start, viewport);
        }
        catch (Exception ex)
        {
            if (version != _renderVersion)
            {
                return;
            }

            SetBusy(false);
            await ShowErrorAsync("ページを表示できません", $"ページ {start + 1}\n\n{ex.Message}");
        }
    }

    private async Task<CachedPage?> GetPageForDisplayAsync(uint index, PixelSize desired, int version)
    {
        if (TryGetCached(index, desired, out CachedPage? cached))
        {
            return cached;
        }

        CachedPage rendered = await RenderPageAsync(index, desired.Width, desired.Height);
        if (version != _renderVersion)
        {
            return null;
        }

        CachePage(index, rendered);
        return rendered;
    }

    private async Task<CachedPage> RenderPageAsync(uint index, int width, int height)
    {
        await _renderGate.WaitAsync();
        try
        {
            PdfDocument? document = _document;
            if (document is null)
            {
                throw new InvalidOperationException("PDFが読み込まれていません。");
            }

            using PdfPage page = document.GetPage(index);
            using var stream = new InMemoryRandomAccessStream();

            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, width),
                DestinationHeight = (uint)Math.Max(1, height),
            };

            await page.RenderToStreamAsync(stream, options);
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            return new CachedPage(bitmap, width, height);
        }
        finally
        {
            _renderGate.Release();
        }
    }

    /// <summary>現在のウィンドウサイズ（物理ピクセル）を返す。</summary>
    private PixelSize GetViewportPixels()
    {
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        int width = (int)Math.Round(RootGrid.ActualWidth * scale);
        int height = (int)Math.Round(RootGrid.ActualHeight * scale);

        if (width < 64 || height < 64)
        {
            // レイアウト確定前に呼ばれた場合はウィンドウのクライアントサイズを使う
            SizeInt32 client = AppWindow.ClientSize;
            width = Math.Max(1, client.Width);
            height = Math.Max(1, client.Height);
        }

        return new PixelSize(width, height);
    }

    private PageSize GetPageSize(uint index)
    {
        if (_document is null)
        {
            return new PageSize(1, 1);
        }

        using PdfPage page = _document.GetPage(index);
        return new PageSize(
            Math.Max(1.0, page.Size.Width),
            Math.Max(1.0, page.Size.Height));
    }

    /// <summary>ページのアスペクト比を保ったまま画面に収まるレンダリングサイズを計算する。</summary>
    private PixelSize GetRenderSize(uint index, PixelSize viewport)
    {
        PageSize page = GetPageSize(index);

        double scale = Math.Min(viewport.Width / page.Width, viewport.Height / page.Height);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1.0;
        }

        double longEdge = Math.Max(page.Width, page.Height) * scale;
        if (longEdge > MaxRenderEdge)
        {
            scale *= MaxRenderEdge / longEdge;
        }

        return new PixelSize(
            (int)Math.Max(1, Math.Round(page.Width * scale)),
            (int)Math.Max(1, Math.Round(page.Height * scale)));
    }

    /// <summary>1ページを指定した高さ（物理ピクセル）に合わせたレンダリングサイズを返す。</summary>
    private static PixelSize ScaleToRenderSize(PageSize page, double targetHeightPx)
    {
        double height = Math.Max(1.0, targetHeightPx);
        double width = height * (page.Width / page.Height);

        double longEdge = Math.Max(width, height);
        if (longEdge > MaxRenderEdge)
        {
            double factor = MaxRenderEdge / longEdge;
            width *= factor;
            height *= factor;
        }

        return new PixelSize(
            (int)Math.Max(1, Math.Round(width)),
            (int)Math.Max(1, Math.Round(height)));
    }

    /// <summary>見開き2ページ分の表示サイズとレンダリングサイズを計算する。</summary>
    private SpreadLayout GetSpreadLayout(uint start, PixelSize viewport)
    {
        PageSize first = GetPageSize(start);
        PageSize second = GetPageSize(start + 1);

        double firstRatio = first.Width / first.Height;
        double secondRatio = second.Width / second.Height;

        double displayHeightPx = Math.Min(
            viewport.Height,
            viewport.Width / (firstRatio + secondRatio));

        if (double.IsNaN(displayHeightPx) || double.IsInfinity(displayHeightPx) || displayHeightPx <= 0)
        {
            displayHeightPx = viewport.Height;
        }

        double raster = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (raster <= 0)
        {
            raster = 1.0;
        }

        return new SpreadLayout(
            ScaleToRenderSize(first, displayHeightPx),
            ScaleToRenderSize(second, displayHeightPx),
            displayHeightPx / raster,
            displayHeightPx * firstRatio / raster,
            displayHeightPx * secondRatio / raster);
    }

    /// <summary>指定ページを含む表示（単ページ／見開き）の開始ページ番号を返す。</summary>
    private uint NormalizeToView(uint index)
    {
        if (_document is null)
        {
            return 0;
        }

        if (index >= _document.PageCount)
        {
            index = _document.PageCount - 1;
        }

        switch (_spreadMode)
        {
            case SpreadMode.CoverSingle:
                if (index == 0)
                {
                    return 0;
                }

                return index % 2 == 1 ? index : index - 1;

            case SpreadMode.PairFromStart:
                return index % 2 == 0 ? index : index - 1;

            case SpreadMode.FirstTwoSingle:
                if (index <= 1)
                {
                    return index;
                }

                return index % 2 == 0 ? index : index - 1;

            default:
                return index;
        }
    }

    /// <summary>現在の見開きモードで、その開始ページが2ページ表示になるかを返す。</summary>
    private bool IsTwoPageView(uint start)
    {
        if (_spreadMode == SpreadMode.Off ||
            _document is null ||
            start + 1 >= _document.PageCount)
        {
            return false;
        }

        return _spreadMode switch
        {
            SpreadMode.CoverSingle => start % 2 == 1,
            SpreadMode.PairFromStart => start % 2 == 0,
            SpreadMode.FirstTwoSingle => start >= 2 && start % 2 == 0,
            _ => false,
        };
    }

    private uint GetNextViewStart(uint start)
    {
        return _spreadMode switch
        {
            SpreadMode.CoverSingle => start == 0 ? 1 : start + 2,
            SpreadMode.PairFromStart => start + 2,
            SpreadMode.FirstTwoSingle => start switch
            {
                0 => 1,
                1 => 2,
                _ => start + 2,
            },
            _ => start + 1,
        };
    }

    private uint GetPreviousViewStart(uint start)
    {
        if (start == 0)
        {
            return 0;
        }

        return _spreadMode switch
        {
            SpreadMode.CoverSingle => start <= 1 ? 0 : start - 2,
            SpreadMode.PairFromStart => start <= 1 ? 0 : start - 2,
            SpreadMode.FirstTwoSingle => start switch
            {
                1 => 0,
                2 => 1,
                _ => start - 2,
            },
            _ => start - 1,
        };
    }

    /// <summary>ページ単体のレンダリングサイズを、そのページが属する表示モードから求める。</summary>
    private PixelSize GetPageDesiredSize(uint pageIndex, PixelSize viewport)
    {
        uint viewStart = NormalizeToView(pageIndex);

        if (!IsTwoPageView(viewStart))
        {
            return GetRenderSize(pageIndex, viewport);
        }

        SpreadLayout layout = GetSpreadLayout(viewStart, viewport);
        return pageIndex == viewStart ? layout.FirstRenderSize : layout.SecondRenderSize;
    }

    // ------------------------------------------------------------------
    // キャッシュ・先読み
    // ------------------------------------------------------------------

    private bool TryGetCached(uint index, PixelSize desired, [NotNullWhen(true)] out CachedPage? page)
    {
        if (_cache.TryGetValue(index, out CachedPage? cached) &&
            cached.PixelWidth >= desired.Width * 0.92 &&
            cached.PixelHeight >= desired.Height * 0.92)
        {
            Touch(index);
            page = cached;
            return true;
        }

        if (_cache.Remove(index))
        {
            _lru.Remove(index);
        }

        page = null!;
        return false;
    }

    private void Touch(uint index)
    {
        if (_lru.Remove(index))
        {
            _lru.AddLast(index);
        }
    }

    private void CachePage(uint index, CachedPage page)
    {
        if (_cache.Remove(index))
        {
            _lru.Remove(index);
        }

        _cache[index] = page;
        _lru.AddLast(index);

        while (_cache.Count > MaxCachedPages && _lru.First is { } oldest)
        {
            _lru.RemoveFirst();
            _cache.Remove(oldest.Value);
        }
    }

    private void StartPrefetch(uint viewStart, PixelSize viewport)
    {
        int generation = ++_prefetchGeneration;
        _ = PrefetchAsync(viewStart, viewport, generation);
    }

    /// <summary>次／前の表示（単ページまたは見開き）を裏でレンダリングして、ページ送りを軽くする。</summary>
    private async Task PrefetchAsync(uint viewStart, PixelSize viewport, int generation)
    {
        try
        {
            await Task.Delay(120);
            if (generation != _prefetchGeneration || _document is null)
            {
                return;
            }

            var pages = new List<uint>(4);
            AddViewPages(pages, GetNextViewStart(viewStart));

            uint previous = GetPreviousViewStart(viewStart);
            if (previous != viewStart)
            {
                AddViewPages(pages, previous);
            }

            foreach (uint pageIndex in pages)
            {
                if (generation != _prefetchGeneration || _document is null)
                {
                    return;
                }

                if (pageIndex >= _document.PageCount)
                {
                    continue;
                }

                PixelSize desired = GetPageDesiredSize(pageIndex, viewport);
                if (TryGetCached(pageIndex, desired, out _))
                {
                    continue;
                }

                CachedPage rendered = await RenderPageAsync(pageIndex, desired.Width, desired.Height);

                if (generation != _prefetchGeneration)
                {
                    return;
                }

                CachePage(pageIndex, rendered);
            }
        }
        catch
        {
            // 先読みは失敗しても無視する
        }
    }

    private void AddViewPages(List<uint> pages, uint start)
    {
        if (_document is null || start >= _document.PageCount || pages.Contains(start))
        {
            return;
        }

        pages.Add(start);

        if (IsTwoPageView(start) && !pages.Contains(start + 1))
        {
            pages.Add(start + 1);
        }
    }

    // ------------------------------------------------------------------
    // ナビゲーション
    // ------------------------------------------------------------------

    private void GoNext()
    {
        if (_document is null)
        {
            return;
        }

        uint next = GetNextViewStart(_pageIndex);
        if (next < _document.PageCount)
        {
            _ = ShowViewAsync(next);
        }
    }

    private void GoPrevious()
    {
        if (_document is null)
        {
            return;
        }

        uint previous = GetPreviousViewStart(_pageIndex);
        if (previous != _pageIndex)
        {
            _ = ShowViewAsync(previous);
        }
    }

    private void JumpTo(uint index)
    {
        if (_document is null)
        {
            return;
        }

        uint target = NormalizeToView(index);
        if (target == _pageIndex)
        {
            return;
        }

        _ = ShowViewAsync(target);
    }

    private void UpdatePageLabel()
    {
        if (_document is null)
        {
            PageLabel.Text = "0 / 0";
            Title = "PDF Manga Viewer";
            return;
        }

        uint end = IsTwoPageView(_pageIndex) ? _pageIndex + 1 : _pageIndex;
        uint first = Math.Min(_pageIndex + 1, _document.PageCount);
        uint last = Math.Min(end + 1, _document.PageCount);
        string range = first == last ? $"{first}" : $"{first}-{last}";

        PageLabel.Text = $"{range} / {_document.PageCount}";

        string name = _displayName ?? "PDF";
        Title = $"{name} ({range}/{_document.PageCount}) - PDF Manga Viewer";
    }

    private void UpdateNavigationUi()
    {
        ToolTipService.SetToolTip(PrevButton, _reverseKeys ? "次のページ (←)" : "前のページ (←)");
        ToolTipService.SetToolTip(NextButton, _reverseKeys ? "前のページ (→)" : "次のページ (→)");
    }

    // ------------------------------------------------------------------
    // キーボード・マウス
    // ------------------------------------------------------------------

    private void LeftKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (_reverseKeys)
        {
            GoNext();
        }
        else
        {
            GoPrevious();
        }
    }

    private void RightKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (_reverseKeys)
        {
            GoPrevious();
        }
        else
        {
            GoNext();
        }
    }

    private void UpKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        GoPrevious();
    }

    private void DownKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        GoNext();
    }

    private void NextKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        GoNext();
    }

    private void PreviousKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        GoPrevious();
    }

    private void FirstPageKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        JumpTo(0);
    }

    private void LastPageKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (_document is not null)
        {
            JumpTo(_document.PageCount - 1);
        }
    }

    private async void OpenFileKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PickAndOpenAsync();
    }

    private void FullScreenKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullScreen();
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        if (_controlsVisible)
        {
            // 表示中はマウス操作で自動非表示のタイマーをリセット
            ShowControls();
            return;
        }

        // 非表示時はメニューバー付近に近づいたときだけ再表示する
        if (IsPointNearControlsBar(e.GetCurrentPoint(RootGrid).Position))
        {
            ShowControls();
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Pointer);

        if (_document is null)
        {
            return;
        }

        // メニュー表示中にメニュー以外の場所をクリックしたら閉じる
        if (_controlsVisible && !IsPointInsideControlsBar(e.GetCurrentPoint(RootGrid).Position))
        {
            HideControls();
            return;
        }

        ShowControls();
    }

    private bool IsPointInsideControlsBar(Point position)
    {
        if (!TryGetControlsBarBounds(out Rect bounds))
        {
            return false;
        }

        return position.X >= bounds.X
            && position.X <= bounds.X + bounds.Width
            && position.Y >= bounds.Y
            && position.Y <= bounds.Y + bounds.Height;
    }

    /// <summary>メニューバーの周囲にマウスがあるか（再表示用のホットゾーン）。</summary>
    private bool IsPointNearControlsBar(Point position)
    {
        if (!TryGetControlsBarBounds(out Rect bounds))
        {
            return false;
        }

        const double margin = 32;

        return position.X >= bounds.X - margin
            && position.X <= bounds.X + bounds.Width + margin
            && position.Y >= bounds.Y - margin
            && position.Y <= bounds.Y + bounds.Height + margin;
    }

    private bool TryGetControlsBarBounds(out Rect bounds)
    {
        bounds = default;

        if (ControlsBar.ActualWidth <= 0 || ControlsBar.ActualHeight <= 0)
        {
            return false;
        }

        var transform = ControlsBar.TransformToVisual(RootGrid);
        Point topLeft = transform.TransformPoint(new Point(0, 0));
        Point bottomRight = transform.TransformPoint(
            new Point(ControlsBar.ActualWidth, ControlsBar.ActualHeight));

        bounds = new Rect(
            topLeft.X,
            topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);
        return true;
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_document is not null)
        {
            HideControls();
        }
    }

    private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        int delta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if ((now - _lastWheelTurn).TotalMilliseconds < 180)
        {
            e.Handled = true;
            return;
        }

        _lastWheelTurn = now;
        e.Handled = true;

        if (delta < 0)
        {
            GoNext();
        }
        else
        {
            GoPrevious();
        }
    }

    // ------------------------------------------------------------------
    // 操作バー
    // ------------------------------------------------------------------

    private void ShowControls()
    {
        if (_document is null)
        {
            return;
        }

        _hideControlsTimer.Stop();
        _hideControlsTimer.Start();

        if (_controlsVisible)
        {
            return;
        }

        _controlsVisible = true;
        ControlsBar.IsHitTestVisible = true;
        AnimateOpacity(ControlsBar, 1, 140);
        AnimateOpacity(FileChip, 1, 140);
    }

    private void HideControls()
    {
        _hideControlsTimer.Stop();

        if (!_controlsVisible)
        {
            return;
        }

        _controlsVisible = false;
        ControlsBar.IsHitTestVisible = false;
        AnimateOpacity(ControlsBar, 0, 260);
        AnimateOpacity(FileChip, 0, 260);
    }

    private static void AnimateOpacity(UIElement element, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);
        await PickAndOpenAsync();
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);

        if (_reverseKeys)
        {
            GoNext();
        }
        else
        {
            GoPrevious();
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);

        if (_reverseKeys)
        {
            GoPrevious();
        }
        else
        {
            GoNext();
        }
    }

    private void RtlToggle_Click(object sender, RoutedEventArgs e)
    {
        _rightToLeft = RtlToggle.IsChecked == true;
        UpdateReadingDirectionUi();
        SaveSettings();

        RootHost.Focus(FocusState.Programmatic);
        ShowControls();

        // 見開き表示中は左右のページを即座に入れ替える
        _ = ShowViewAsync(_pageIndex);
    }

    private void UpdateReadingDirectionUi()
    {
        RtlToggle.IsChecked = _rightToLeft;
        ToolTipService.SetToolTip(
            RtlToggle,
            _rightToLeft
                ? "右から左に表示中（クリックで左から右へ）"
                : "左から右に表示中（クリックで右から左へ）");
    }

    private void ReverseKeysToggle_Click(object sender, RoutedEventArgs e)
    {
        _reverseKeys = ReverseKeysToggle.IsChecked == true;
        UpdateNavigationUi();
        RootHost.Focus(FocusState.Programmatic);
        ShowControls();
    }

    private void SpreadModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item ||
            item.Tag is not string tag ||
            !Enum.TryParse(tag, ignoreCase: true, out SpreadMode mode))
        {
            return;
        }

        _spreadMode = mode;
        UpdateSpreadUi();
        SaveSettings();

        RootHost.Focus(FocusState.Programmatic);
        ShowControls();
        _ = ShowViewAsync(_pageIndex);
    }

    private void SpreadFlyout_Opening(object sender, object e)
    {
        // メニューを開いている間は操作バーを消さない
        _hideControlsTimer.Stop();
    }

    private void SpreadFlyout_Closed(object sender, object e)
    {
        if (_controlsVisible)
        {
            ShowControls();
        }
    }

    private void UpdateSpreadUi()
    {
        SpreadButton.Content = _spreadMode switch
        {
            SpreadMode.CoverSingle => "見開き: 表紙のみ",
            SpreadMode.PairFromStart => "見開き: 2枚組",
            SpreadMode.FirstTwoSingle => "見開き: 2枚単独",
            _ => "見開き: なし",
        };

        SpreadOffItem.IsChecked = _spreadMode == SpreadMode.Off;
        SpreadCoverItem.IsChecked = _spreadMode == SpreadMode.CoverSingle;
        SpreadPairItem.IsChecked = _spreadMode == SpreadMode.PairFromStart;
        SpreadFirstTwoItem.IsChecked = _spreadMode == SpreadMode.FirstTwoSingle;
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            string json = File.ReadAllText(SettingsPath);
            ViewerSettings? settings = JsonSerializer.Deserialize<ViewerSettings>(json);

            if (settings is not null)
            {
                if (settings.Spread is { } spread &&
                    Enum.TryParse(spread, ignoreCase: true, out SpreadMode mode))
                {
                    _spreadMode = mode;
                }

                _rightToLeft = settings.RightToLeft;
            }
        }
        catch
        {
            // 設定ファイルが読めない場合は既定値（単ページ）のまま
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var settings = new ViewerSettings
            {
                Spread = _spreadMode.ToString(),
                RightToLeft = _rightToLeft,
            };

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // 設定の保存に失敗しても動作は継続する
        }
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);
        ToggleFullScreen();
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;

        AppWindow.SetPresenter(_isFullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);

        FullScreenButton.Content = _isFullScreen ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(
            FullScreenButton,
            _isFullScreen ? "全画面を終了 (F11)" : "全画面 (F11)");

        if (_document is not null)
        {
            ShowControls();
        }
    }

    // ------------------------------------------------------------------
    // ウィンドウサイズ変更
    // ------------------------------------------------------------------

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeSettled()
    {
        _resizeTimer.Stop();

        if (_document is null)
        {
            return;
        }

        // キャッシュが足りなければ ShowViewAsync 内で再レンダリングされる
        _ = ShowViewAsync(_pageIndex);
    }

    // ------------------------------------------------------------------
    // ドラッグ＆ドロップ
    // ------------------------------------------------------------------

    private void RootGrid_DragEnter(object sender, DragEventArgs e)
    {
        UpdateDragOverlay(e);
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        UpdateDragOverlay(e);
    }

    private void RootGrid_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateDragOverlay(DragEventArgs e)
    {
        bool hasFiles = e.DataView.Contains(StandardDataFormats.StorageItems);
        DropOverlay.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;

        if (hasFiles)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "PDFを開く";
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        StorageFile? pdf = items
            .OfType<StorageFile>()
            .FirstOrDefault(f => string.Equals(f.FileType, ".pdf", StringComparison.OrdinalIgnoreCase));

        if (pdf is null)
        {
            await ShowErrorAsync("PDFではありません", "PDFファイルをドロップしてください。");
            return;
        }

        await OpenPdfAsync(pdf);
    }

    // ------------------------------------------------------------------
    // ユーティリティ
    // ------------------------------------------------------------------

    private void SetBusy(bool busy)
    {
        LoadingRing.IsActive = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        if (_dialogOpen || RootGrid.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "閉じる",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot,
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
            RootHost.Focus(FocusState.Programmatic);
        }
    }

    private void ConfigureInitialWindow()
    {
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 work = display.WorkArea;

        int width = Math.Clamp((int)(work.Width * 0.70), 960, 1400);
        int height = Math.Clamp((int)(work.Height * 0.75), 640, 1000);
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);

        int x = work.X + (work.Width - width) / 2;
        int y = work.Y + (work.Height - height) / 2;

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void ConfigureTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var background = Windows.UI.Color.FromArgb(255, 10, 10, 12);
        var foreground = Windows.UI.Color.FromArgb(255, 240, 240, 245);
        var inactiveForeground = Windows.UI.Color.FromArgb(255, 150, 150, 155);
        var hoverBackground = Windows.UI.Color.FromArgb(255, 40, 40, 46);
        var pressedBackground = Windows.UI.Color.FromArgb(255, 60, 60, 66);

        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.BackgroundColor = background;
        titleBar.InactiveBackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
        titleBar.ButtonPressedForegroundColor = Colors.White;
    }
}
