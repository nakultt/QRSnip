using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using QRSnip.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace QRSnip.Services;

/// <summary>Decodes pictures with Windows Imaging (any format with a codec installed).</summary>
internal static class PictureDecoder
{
    private const int ComponentNotFound = unchecked((int)0x88982F50); // no codec (e.g. HEIC without the extension)
    private const int BadImage = unchecked((int)0x88982F60);
    private const int BadHeader = unchecked((int)0x88982F61);
    private const int UnknownFormat = unchecked((int)0x88982F07);

    /// <summary>Decodes to premultiplied BGRA, upright (EXIF orientation applied) and flattened onto white.</summary>
    public static async Task<BgraImage> DecodeAsync(IRandomAccessStream stream, CancellationToken cancellationToken)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return ImageOps.FlattenOnWhite(new BgraImage(pixels, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.PixelWidth * 4));
    }

    /// <summary>A short, friendly reason a picture couldn't be read.</summary>
    public static string Describe(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => "The file couldn't be found.",
        UnauthorizedAccessException => "QRSnip isn't allowed to open this file.",
        COMException { HResult: ComponentNotFound } =>
            "Windows can't read this kind of picture. HEIC and WebP pictures need the free HEIF or WebP Image Extensions from the Microsoft Store.",
        COMException { HResult: BadImage or BadHeader or UnknownFormat } => "This file isn't a picture QRSnip can read, or it's damaged.",
        _ => "This picture couldn't be read.",
    };
}

/// <summary>A picture file (opened, dropped, pasted as a file, or passed with --file).</summary>
internal sealed class FileImageSource(StorageFile file) : IImageSource
{
    public string Name => file.Name;

    public static async Task<FileImageSource> FromPathAsync(string path) =>
        new(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)));

    public async Task<BgraImage> GetImageAsync(CancellationToken cancellationToken = default)
    {
        using var stream = await file.OpenReadAsync().AsTask(cancellationToken);
        return await PictureDecoder.DecodeAsync(stream, cancellationToken);
    }
}

/// <summary>A picture on the clipboard, e.g. right after Win+Shift+S or "Copy image".</summary>
internal sealed class StreamImageSource(RandomAccessStreamReference reference, string name) : IImageSource
{
    public string Name => name;

    public async Task<BgraImage> GetImageAsync(CancellationToken cancellationToken = default)
    {
        using var stream = await reference.OpenReadAsync().AsTask(cancellationToken);
        return await PictureDecoder.DecodeAsync(stream, cancellationToken);
    }
}

/// <summary>A path that couldn't even be opened; reports the error when read.</summary>
internal sealed class FailedImageSource(string name, Exception error) : IImageSource
{
    public string Name => name;

    public Task<BgraImage> GetImageAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<BgraImage>(error);
}
