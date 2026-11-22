using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PdfMangaViewer;

public readonly record struct PageSize(double Width, double Height)
{
    public bool IsLandscape => Width > Height * 1.05;
}

public readonly record struct PixelSize(int Width, int Height);

/// <summary>PDF / ZIP・CBZ / 画像フォルダを共通に扱うドキュメント抽象。</summary>
public interface IMangaDocument : IDisposable
{
    string SourcePath { get; }
    string DisplayName { get; }
    string FileName { get; }
    uint PageCount { get; }

    Task<PageSize> GetPageSizeAsync(uint index);
    Task<BitmapImage> RenderPageAsync(uint index, PixelSize desired);
}

public static class MangaDocumentFactory
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".cbz",
    };

    public static bool IsImageExtension(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsArchiveExtension(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path));

    public static bool IsPdfExtension(string path) =>
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupportedBookFile(string path) =>
        IsPdfExtension(path) || IsArchiveExtension(path);

    public static bool IsSupportedPath(string path)
    {
        if (Directory.Exists(path))
        {
            return true;
        }

        return File.Exists(path) && (IsSupportedBookFile(path) || IsImageExtension(path));
    }

    /// <summary>同じ親フォルダ内の前後の本（PDF/ZIP/CBZ または画像フォルダ）のパスを探す。</summary>
    public static string? FindSiblingBook(string currentPath, int direction)
    {
        try
        {
            string fullPath = Path.GetFullPath(currentPath);
            string? parentDir = Path.GetDirectoryName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
            {
                return null;
            }

            List<string> candidates;
            if (Directory.Exists(fullPath))
            {
                candidates = Directory.EnumerateDirectories(parentDir)
                    .Where(HasAnyImageFile)
                    .OrderBy(Path.GetFileName, NaturalSortComparer.Instance)
                    .ToList();
            }
            else
            {
                candidates = Directory.EnumerateFiles(parentDir)
                    .Where(IsSupportedBookFile)
                    .OrderBy(Path.GetFileName, NaturalSortComparer.Instance)
                    .ToList();
            }

            int currentIndex = candidates.FindIndex(p =>
                string.Equals(Path.GetFullPath(p), fullPath, StringComparison.OrdinalIgnoreCase));

            if (currentIndex < 0)
            {
                return null;
            }

            int nextIndex = currentIndex + direction;
            if (nextIndex < 0 || nextIndex >= candidates.Count)
            {
                return null;
            }

            return candidates[nextIndex];
        }
        catch
        {
            return null;
        }
    }

    private static bool HasAnyImageFile(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath).Any(IsImageExtension);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Windows.Data.Pdf ベースの PDF ドキュメント。</summary>
public sealed class PdfMangaDocument : IMangaDocument
{
    private readonly PdfDocument _pdf;
    private readonly Dictionary<uint, PageSize> _sizes = new();

    private PdfMangaDocument(PdfDocument pdf, string sourcePath, string displayName, string fileName)
    {
        _pdf = pdf;
        SourcePath = sourcePath;
        DisplayName = displayName;
        FileName = fileName;
    }

    public string SourcePath { get; }
    public string DisplayName { get; }
    public string FileName { get; }
    public uint PageCount => _pdf.PageCount;

    public static async Task<PdfMangaDocument> OpenAsync(StorageFile file, string? password = null)
    {
        PdfDocument pdf = string.IsNullOrEmpty(password)
            ? await PdfDocument.LoadFromFileAsync(file)
            : await PdfDocument.LoadFromFileAsync(file, password);

        return new PdfMangaDocument(pdf, file.Path, file.DisplayName, file.Name);
    }

    public Task<PageSize> GetPageSizeAsync(uint index)
    {
        if (_sizes.TryGetValue(index, out PageSize cached))
        {
            return Task.FromResult(cached);
        }

        using PdfPage page = _pdf.GetPage(index);
        var size = new PageSize(
            Math.Max(1.0, page.Size.Width),
            Math.Max(1.0, page.Size.Height));

        _sizes[index] = size;
        return Task.FromResult(size);
    }

    public async Task<BitmapImage> RenderPageAsync(uint index, PixelSize desired)
    {
        using PdfPage page = _pdf.GetPage(index);
        using var stream = new InMemoryRandomAccessStream();

        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, desired.Width),
            DestinationHeight = (uint)Math.Max(1, desired.Height),
            BitmapEncoderId = BitmapEncoder.BmpEncoderId,
        };

        await page.RenderToStreamAsync(stream, options);
        stream.Seek(0);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    public void Dispose()
    {
        // PdfDocument は IDisposable ではない
    }
}

/// <summary>ZIP / CBZ アーカイブ内の画像をページとして扱うドキュメント。</summary>
public sealed class ZipMangaDocument : IMangaDocument
{
    private readonly ZipArchive _archive;
    private readonly List<ZipArchiveEntry> _entries;
    private readonly Dictionary<uint, PageSize> _sizes = new();
    private readonly object _lock = new();
    private bool _disposed;

    private ZipMangaDocument(ZipArchive archive, List<ZipArchiveEntry> entries, string sourcePath)
    {
        _archive = archive;
        _entries = entries;
        SourcePath = sourcePath;
        FileName = Path.GetFileName(sourcePath);
        DisplayName = Path.GetFileNameWithoutExtension(sourcePath);
    }

    public string SourcePath { get; }
    public string DisplayName { get; }
    public string FileName { get; }
    public uint PageCount => (uint)_entries.Count;

    public static ZipMangaDocument Open(string path)
    {
        ZipArchive archive = ZipFile.OpenRead(path);
        List<ZipArchiveEntry> entries = archive.Entries
            .Where(e =>
                !string.IsNullOrEmpty(e.Name) &&
                !e.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) &&
                MangaDocumentFactory.IsImageExtension(e.Name))
            .OrderBy(e => e.FullName, NaturalSortComparer.Instance)
            .ToList();

        if (entries.Count == 0)
        {
            archive.Dispose();
            throw new InvalidOperationException("アーカイブ内に表示できる画像が見つかりませんでした。");
        }

        return new ZipMangaDocument(archive, entries, path);
    }

    public async Task<PageSize> GetPageSizeAsync(uint index)
    {
        if (_sizes.TryGetValue(index, out PageSize cached))
        {
            return cached;
        }

        byte[] bytes = ReadEntryBytes((int)index);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        var size = new PageSize(
            Math.Max(1.0, decoder.OrientedPixelWidth),
            Math.Max(1.0, decoder.OrientedPixelHeight));

        _sizes[index] = size;
        return size;
    }

    public async Task<BitmapImage> RenderPageAsync(uint index, PixelSize desired)
    {
        byte[] bytes = ReadEntryBytes((int)index);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);

        var bitmap = new BitmapImage();
        if (desired.Width > 0 && desired.Height > 0)
        {
            PageSize natural = await GetPageSizeAsync(index);
            if (desired.Width < natural.Width * 0.95)
            {
                bitmap.DecodePixelWidth = desired.Width;
                bitmap.DecodePixelType = DecodePixelType.Physical;
            }
        }

        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private byte[] ReadEntryBytes(int index)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using Stream entryStream = _entries[index].Open();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            return ms.ToArray();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _archive.Dispose();
        }
    }
}

/// <summary>画像フォルダをページとして扱うドキュメント。</summary>
public sealed class FolderMangaDocument : IMangaDocument
{
    private readonly List<string> _files;
    private readonly Dictionary<uint, PageSize> _sizes = new();

    private FolderMangaDocument(List<string> files, string folderPath)
    {
        _files = files;
        SourcePath = folderPath;
        string trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        FileName = string.IsNullOrEmpty(name) ? folderPath : name;
        DisplayName = FileName;
    }

    public string SourcePath { get; }
    public string DisplayName { get; }
    public string FileName { get; }
    public uint PageCount => (uint)_files.Count;

    public static FolderMangaDocument Open(string folderPath, out uint? initialPageIndex, string? initialImagePath = null)
    {
        List<string> files = Directory.EnumerateFiles(folderPath)
            .Where(MangaDocumentFactory.IsImageExtension)
            .OrderBy(Path.GetFileName, NaturalSortComparer.Instance)
            .ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException("フォルダ内に表示できる画像が見つかりませんでした。");
        }

        initialPageIndex = null;
        if (!string.IsNullOrEmpty(initialImagePath))
        {
            string fullInitial = Path.GetFullPath(initialImagePath);
            int idx = files.FindIndex(f =>
                string.Equals(Path.GetFullPath(f), fullInitial, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                initialPageIndex = (uint)idx;
            }
        }

        return new FolderMangaDocument(files, folderPath);
    }

    public async Task<PageSize> GetPageSizeAsync(uint index)
    {
        if (_sizes.TryGetValue(index, out PageSize cached))
        {
            return cached;
        }

        StorageFile file = await StorageFile.GetFileFromPathAsync(_files[(int)index]);
        using IRandomAccessStream stream = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

        var size = new PageSize(
            Math.Max(1.0, decoder.OrientedPixelWidth),
            Math.Max(1.0, decoder.OrientedPixelHeight));

        _sizes[index] = size;
        return size;
    }

    public async Task<BitmapImage> RenderPageAsync(uint index, PixelSize desired)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(_files[(int)index]);
        using IRandomAccessStream stream = await file.OpenReadAsync();

        var bitmap = new BitmapImage();
        if (desired.Width > 0 && desired.Height > 0)
        {
            PageSize natural = await GetPageSizeAsync(index);
            if (desired.Width < natural.Width * 0.95)
            {
                bitmap.DecodePixelWidth = desired.Width;
                bitmap.DecodePixelType = DecodePixelType.Physical;
            }
        }

        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    public void Dispose()
    {
    }
}
