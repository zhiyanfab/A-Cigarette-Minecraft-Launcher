using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ACML.App;

public static class BackgroundImageService
{
    public static ImageBrush DefaultBrush()
    {
        var brush = new ImageBrush(new BitmapImage(new Uri("pack://application:,,,/ACML;component/Assets/reference-ui.png")))
        { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new System.Windows.Rect(162, 81, 788, 443), Stretch = Stretch.UniformToFill };
        brush.Freeze(); return brush;
    }
    public static ImageBrush Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("背景图片不存在，请重新选择或恢复默认背景。");
        if (file.Length > 32 * 1024 * 1024) throw new InvalidOperationException("背景图片不能超过 32 MB。");
        using var stream = file.OpenRead();
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000) throw new InvalidOperationException("背景图片分辨率过高，请选择不超过 4000 万像素的图片。");
        stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Min(frame.PixelWidth, 3840); bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill }; brush.Freeze(); return brush;
    }
    public static string Import(string source, string dataDirectory)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp")) throw new InvalidOperationException("请选择 PNG、JPG 或 BMP 图片。");
        _ = Load(source);
        var directory = Path.Combine(dataDirectory, "backgrounds"); Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
        File.Copy(source, destination, false); return destination;
    }
    public static void RemoveManagedCopy(string? path, string dataDirectory)
    {
        if (string.IsNullOrEmpty(path)) return;
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Path.Combine(dataDirectory, "backgrounds")), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(full), "N", out _)) return;
        try { File.Delete(full); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
