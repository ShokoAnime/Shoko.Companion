using System;
using System.Collections.Generic;
using System.Threading;
using Shoko.Companion.Configuration;
using Shoko.Companion.Playback;
using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
///   Holds <see cref="PlaybackSessionManager"/> to the one rule that keeps a
///   watch record to a single writer: while a media session is connected the
///   server owns the record and this companion writes none of it, and
///   ownership is only ever re-read at an item boundary.
///
///   <para>
///     Every failure here is silent in production — nothing errors, the
///     watch state is just written twice, or written by whichever half saw
///     less of the viewing.
///   </para>
/// </summary>
[Collection("SharedSettings")]
public class PlaybackSessionManagerSyncTests : IDisposable
{
    private readonly bool _syncingEnabled;
    private readonly int _skipCount;
    private readonly bool _privacyMode;

    public PlaybackSessionManagerSyncTests()
    {
        var s = SettingsProvider.Instance.Settings;
        _syncingEnabled = s.PlaybackSyncingEnabled;
        _skipCount = s.SyncUserDataInitialSkipEventCount;
        _privacyMode = s.PrivacyMode;

        s.PlaybackSyncingEnabled = true;
        s.PrivacyMode = false;
        // The stop scrobble is gated behind the initial skip count as well;
        // zero it so these tests measure the ownership rule and nothing else.
        s.SyncUserDataInitialSkipEventCount = 0;
    }

    public void Dispose()
    {
        var s = SettingsProvider.Instance.Settings;
        s.PlaybackSyncingEnabled = _syncingEnabled;
        s.SyncUserDataInitialSkipEventCount = _skipCount;
        s.PrivacyMode = _privacyMode;
        s.RestrictedContentPlaying = false;
        GC.SuppressFinalize(this);
    }

    private static PlaybackSessionManager NewManager(out List<ScrobbleRequestEventArgs> scrobbles, out CountdownEvent gate)
    {
        var manager = new PlaybackSessionManager();
        var seen = new List<ScrobbleRequestEventArgs>();
        var signal = new CountdownEvent(1);

        manager.ScrobbleRequested += (_, e) =>
        {
            lock (seen)
                seen.Add(e);
            signal.Signal();
        };

        scrobbles = seen;
        gate = signal;
        return manager;
    }

    private static void Start(PlaybackSessionManager manager, int fileId)
        => manager.StartSession(fileId, 0, 1_000_000, false, "Series", "Episode", 1, 1, 12, null, 1);

    private static void Next(PlaybackSessionManager manager, int fileId)
        => manager.OnNextFile(fileId, 0, 1_000_000, false, "Series", "Episode", 2, 1, 12, null, 1);

    /// <summary>Scrobbles are raised on a background task, so wait for one.</summary>
    private static bool Raised(CountdownEvent gate)
        => gate.Wait(TimeSpan.FromSeconds(5));

    /// <summary>
    ///   Wait long enough that a scrobble would have landed if one were
    ///   coming. Only used for the negative assertions.
    /// </summary>
    private static bool NotRaised(CountdownEvent gate)
        => !gate.Wait(TimeSpan.FromMilliseconds(500));

    [Fact]
    public void NoMediaSession_CompanionScrobblesTheItemItself()
    {
        var manager = NewManager(out var scrobbles, out var gate);

        Start(manager, 42);
        manager.OnPositionChanged(999_000);
        manager.EndSession(999_000);

        Assert.True(Raised(gate));
        var only = Assert.Single(scrobbles);
        Assert.Equal(42, only.FileId);
        Assert.True(only.PersistUserData);
    }

    [Fact]
    public void MediaSessionConnectedBeforeTheItem_CompanionWritesNothing()
    {
        var manager = NewManager(out var scrobbles, out var gate);
        manager.MediaSessionConnected = true;

        Start(manager, 42);
        manager.OnPauseChanged(true);
        manager.OnPauseChanged(false);
        manager.OnPositionChanged(999_000);
        manager.EndSession(999_000);

        Assert.True(NotRaised(gate));
        Assert.Empty(scrobbles);
    }

    [Fact]
    public void MediaSessionConnectsMidItem_CompanionStandsDownImmediately()
    {
        var manager = NewManager(out var scrobbles, out var gate);

        Start(manager, 42);
        manager.OnPositionChanged(400_000);

        // Stopping is immediate: the server has been watching this playback
        // through the state reports since it registered, so it can complete
        // the record it takes over.
        manager.MediaSessionConnected = true;

        manager.OnPositionChanged(999_000);
        manager.EndSession(999_000);

        Assert.True(NotRaised(gate));
        Assert.Empty(scrobbles);
    }

    [Fact]
    public void MediaSessionGoesAwayMidItem_CompanionDoesNotTakeOverThisItem()
    {
        var manager = NewManager(out var scrobbles, out var gate);
        manager.MediaSessionConnected = true;

        Start(manager, 42);
        manager.OnPositionChanged(800_000);

        // The item is 80% gone and the server owns all of it. Taking over here
        // would mark it watched on the strength of the last 20%.
        manager.MediaSessionConnected = false;

        manager.OnPositionChanged(999_000);
        manager.EndSession(999_000);

        Assert.True(NotRaised(gate));
        Assert.Empty(scrobbles);
    }

    [Fact]
    public void MediaSessionGoesAwayMidItem_CompanionResumesOnTheNextItem()
    {
        var manager = NewManager(out var scrobbles, out var gate);
        manager.MediaSessionConnected = true;

        Start(manager, 42);
        manager.OnPositionChanged(800_000);
        manager.MediaSessionConnected = false;

        // The playlist advances — an item boundary, and the first point at
        // which "who owns this record" has one answer again.
        Next(manager, 43);
        manager.OnPositionChanged(999_000);
        manager.EndSession(999_000);

        Assert.True(Raised(gate));
        var only = Assert.Single(scrobbles);
        Assert.Equal(43, only.FileId);
    }

    [Fact]
    public void MediaSessionGoesAwayBetweenItems_CompanionOwnsTheNextItem()
    {
        var manager = NewManager(out var scrobbles, out var gate);
        manager.MediaSessionConnected = true;

        Start(manager, 42);
        manager.EndSession(999_000);
        manager.MediaSessionConnected = false;

        Start(manager, 43);
        manager.OnPositionChanged(999_000);
        manager.EndSession(999_000);

        Assert.True(Raised(gate));
        var only = Assert.Single(scrobbles);
        Assert.Equal(43, only.FileId);
    }

    [Fact]
    public void MediaSessionConnectedAcrossAPlaylist_NoItemIsWrittenTwice()
    {
        var manager = NewManager(out var scrobbles, out var gate);
        manager.MediaSessionConnected = true;

        Start(manager, 42);
        manager.FinalizeCurrentFile();
        Next(manager, 43);
        manager.FinalizeCurrentFile();
        Next(manager, 44);
        manager.EndSession(999_000);

        Assert.True(NotRaised(gate));
        Assert.Empty(scrobbles);
    }

    [Fact]
    public void NoMediaSession_EachPlaylistItemIsFinalizedOnce()
    {
        var manager = NewManager(out var scrobbles, out _);

        Start(manager, 42);
        manager.OnPositionChanged(999_000);
        manager.FinalizeCurrentFile();
        Next(manager, 43);
        manager.OnPositionChanged(999_000);
        manager.EndSession(999_000);

        // Two items, two records, one writer each.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (scrobbles)
                if (scrobbles.Count >= 2)
                    break;
            Thread.Sleep(20);
        }

        lock (scrobbles)
        {
            Assert.Equal(2, scrobbles.Count);
            // Each event is raised on its own task, so which lands first is
            // not ours to assert — that both items were written exactly once
            // is the whole claim.
            var ids = scrobbles.ConvertAll(s => s.FileId);
            ids.Sort();
            Assert.Equal([42, 43], ids);
        }
    }
}
