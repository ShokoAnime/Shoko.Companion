using System;

namespace Shoko.Companion.Mpv;

/// <summary>
/// Provides data for the <see cref="IMpvController.MpvEvent"/> event.
/// Contains the event name and optional payload from mpv.
/// </summary>
public class MpvEventArgs : EventArgs
{
    /// <summary>
    /// Gets the mpv event name (e.g. "file-loaded", "end-file", "shutdown").
    /// </summary>
    public string Event { get; }

    /// <summary>
    /// Gets the optional event payload data from mpv.
    /// </summary>
    public object? Data { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MpvEventArgs"/> class.
    /// </summary>
    /// <param name="event">The mpv event name.</param>
    /// <param name="data">Optional event payload data.</param>
    public MpvEventArgs(string @event, object? data = null)
    {
        Event = @event;
        Data = data;
    }
}
