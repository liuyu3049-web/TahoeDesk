using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace GlassShell.Core.SystemControl;

/// <summary>
/// WinRT GlobalSystemMediaTransportControlsSessionManager adapter for the Control Center Now Playing card.
/// </summary>
public sealed class MediaSessionService
{
    public async Task<MediaSessionInfo> GetCurrentMediaAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager?.GetCurrentSession();
            if (session is null)
            {
                return MediaSessionInfo.Empty;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            bool isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            byte[]? thumbBytes = null;
            if (props?.Thumbnail is not null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var netStream = stream.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await netStream.CopyToAsync(ms);
                    if (ms.Length > 0 && ms.Length < 4 * 1024 * 1024)
                    {
                        thumbBytes = ms.ToArray();
                    }
                }
                catch { }
            }

            string title = props?.Title?.Trim() ?? string.Empty;
            string artist = props?.Artist?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist))
            {
                return MediaSessionInfo.Empty;
            }

            return new MediaSessionInfo(true, title, artist, isPlaying, thumbBytes);
        }
        catch
        {
            return MediaSessionInfo.Empty;
        }
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager?.GetCurrentSession();
            return session is not null && await session.TryTogglePlayPauseAsync();
        }
        catch { return false; }
    }

    public async Task<bool> NextTrackAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager?.GetCurrentSession();
            return session is not null && await session.TrySkipNextAsync();
        }
        catch { return false; }
    }

    public async Task<bool> PreviousTrackAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager?.GetCurrentSession();
            return session is not null && await session.TrySkipPreviousAsync();
        }
        catch { return false; }
    }
}
