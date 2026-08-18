using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shoko.Companion.Playback;
using Shoko.Companion.Server;
using Shoko.Companion.Server.Models;

namespace Shoko.Companion.Tests;

/// <summary>
///   A coordinator that does nothing, for tests of the pieces of
///   <see cref="Shoko.Companion.Server.MediaSessionClient"/> that never
///   reach it. It holds <see cref="MediaSessionId"/> because the client
///   pushes the session id onto the coordinator as it registers.
/// </summary>
public sealed class StubPlaybackCoordinator : IPlaybackCoordinator
{
    public PlaybackState CurrentState => PlaybackState.Idle;

    public MediaItemInfoDto? PreviousItem => null;

    public MediaItemInfoDto? CurrentItem => null;

    public MediaItemInfoDto? NextItem => null;

    public IReadOnlyList<MediaItemInfoDto> CurrentPlaylist => [];

    public Guid? MediaSessionId { get; set; }

    public double CurrentPositionSeconds => 0;

    public double? DurationSeconds => null;

    public int CurrentVolume => 100;

    public bool CurrentMuted => false;

    public double CurrentPlaybackSpeed => 1.0;

    public bool? CurrentFullscreen => false;

    public PlaybackTrackSelectionDto? CurrentTracks => null;

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged { add { } remove { } }

    public event EventHandler<TimeSpan>? PositionTick { add { } remove { } }

    public event EventHandler? VolumeStateChanged { add { } remove { } }

    public event EventHandler? ViewStateChanged { add { } remove { } }

    public event EventHandler? PlaylistChanged { add { } remove { } }

    public Task PlayAsync(string shokoUrl, TimeSpan? startPosition = null, bool? append = null) => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;

    public Task PauseAsync() => Task.CompletedTask;

    public Task ResumeAsync() => Task.CompletedTask;

    public Task SkipNextAsync() => Task.CompletedTask;

    public Task SkipPreviousAsync() => Task.CompletedTask;

    public Task SeekAsync(TimeSpan positionSeconds) => Task.CompletedTask;

    public Task SetVolumeAsync(int? volume, bool? muted) => Task.CompletedTask;

    public Task SetPlaybackRateAsync(double rate) => Task.CompletedTask;

    public Task SetFullscreenAsync(bool isFullscreen) => Task.CompletedTask;

    public Task SetTracksAsync(PlaybackTrackSelectionDto tracks) => Task.CompletedTask;

    public Task JumpToPlaylistItemAsync(string streamUrl) => Task.CompletedTask;

    public Task AddToPlaylistAsync(IReadOnlyList<PlaylistAddition> items, int? atIndex) => Task.CompletedTask;

    public Task RemoveFromPlaylistAsync(IReadOnlyList<string> streamUrls) => Task.CompletedTask;

    public Task MovePlaylistItemAsync(int fromIndex, int toIndex) => Task.CompletedTask;

    public Task<byte[]?> CaptureScreenshotAsync(TimeSpan? position = null) => Task.FromResult<byte[]?>(null);

    public Task ShowOsdTextAsync(string text, int durationMs = 3000) => Task.CompletedTask;
}
