using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LootLogger.App.Services;

/// <summary>
/// Item pictures from the game's public image server, saved in %AppData%\LootLogger\icons
/// so each one is downloaded only once.
/// </summary>
public static class ItemIcons
{
    private const string UrlFormat = "https://render.albiononline.com/v1/item/{0}.png?size=64";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Downloads = new(4);
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private static string Folder => Path.Combine(AppPaths.DataFolder, "icons");

    /// <summary>The picture for an item id like "T6_HEAD_PLATE_SET1@2"; null when it can't be found.</summary>
    public static Task<ImageSource?> GetAsync(string itemId) =>
        string.IsNullOrEmpty(itemId) ? Task.FromResult<ImageSource?>(null) : Loaded.GetOrAdd(itemId, LoadAsync);

    private static async Task<ImageSource?> LoadAsync(string itemId)
    {
        var path = Path.Combine(Folder, SafeFileName(itemId) + ".png");
        try
        {
            if (!File.Exists(path))
            {
                await Downloads.WaitAsync();
                try
                {
                    var bytes = await Http.GetByteArrayAsync(string.Format(UrlFormat, itemId));
                    Directory.CreateDirectory(Folder);
                    await File.WriteAllBytesAsync(path, bytes);
                }
                finally
                {
                    Downloads.Release();
                }
            }

            return await Task.Run(() => Decode(path));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or NotSupportedException or FormatException or UnauthorizedAccessException)
        {
            if (e is NotSupportedException or FormatException)
            {
                // A broken file: delete it so it is downloaded again.
                try { File.Delete(path); } catch (IOException) { }
            }

            // Offline or the item has no picture: try again next session.
            Loaded.TryRemove(itemId, out _);
            return null;
        }
    }

    private static ImageSource Decode(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 64;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string SafeFileName(string itemId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(itemId.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
