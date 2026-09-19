using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EasyImageImporter.Core.Review;

namespace EasyImageImporter.App.ViewModels.Review;

/// <summary>
/// A thumbnail that loads itself the first time something shows it. Lists are virtualized, so
/// only thumbnails that are actually on screen get loaded.
/// </summary>
public sealed class LazyThumbnail(ThumbnailLoader loader, string imagePath, string sha256) : ObservableObject
{
    private Bitmap? _image;
    private bool _requested;

    public Bitmap? Image
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _ = LoadAsync();
            }
            return _image;
        }
    }

    private async Task LoadAsync()
    {
        var image = await loader.LoadAsync(this, imagePath, sha256);
        Dispatcher.UIThread.Post(() =>
        {
            _image = image;
            OnPropertyChanged(nameof(Image));
        });
    }

    /// <summary>Frees the bitmap. If it is still on screen, the binding asks again and it reloads.</summary>
    internal void Evict()
    {
        var old = _image;
        _image = null;
        _requested = false;
        OnPropertyChanged(nameof(Image));
        old?.Dispose();
    }
}

/// <summary>
/// Loads thumbnails two at a time off the UI thread, and keeps at most a few hundred bitmaps in
/// memory: a 1 000-frame burst must not eat the RAM of an older PC.
/// </summary>
public sealed class ThumbnailLoader(ThumbnailCache cache)
{
    private const int MaxLoaded = 300;
    private readonly SemaphoreSlim _gate = new(2);
    private readonly LinkedList<LazyThumbnail> _loaded = new();
    private readonly Lock _lock = new();

    public LazyThumbnail For(string imagePath, string sha256) => new(this, imagePath, sha256);

    internal async Task<Bitmap?> LoadAsync(LazyThumbnail owner, string imagePath, string sha256)
    {
        await _gate.WaitAsync();
        try
        {
            var bitmap = await Task.Run(() =>
            {
                var path = cache.GetOrCreate(imagePath, sha256);
                return path is null ? null : new Bitmap(path);
            });
            if (bitmap is not null) Remember(owner);
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Remember(LazyThumbnail owner)
    {
        LazyThumbnail? evict = null;
        lock (_lock)
        {
            _loaded.AddLast(owner);
            if (_loaded.Count > MaxLoaded)
            {
                evict = _loaded.First!.Value;
                _loaded.RemoveFirst();
            }
        }
        if (evict is not null) Dispatcher.UIThread.Post(evict.Evict);
    }
}
