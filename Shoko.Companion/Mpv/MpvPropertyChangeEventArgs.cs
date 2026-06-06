using System;

namespace Shoko.Companion.Mpv;

/// <summary>
/// Provides data for the <see cref="IMpvController.PropertyChanged"/> event.
/// Contains the property ID, name, and new value from mpv.
/// </summary>
public class MpvPropertyChangeEventArgs : EventArgs
{
    /// <summary>
    /// Gets the observer ID used when the property was registered.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// Gets the name of the mpv property that changed.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the new value of the mpv property.
    /// </summary>
    public object? Data { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MpvPropertyChangeEventArgs"/> class.
    /// </summary>
    /// <param name="id">The observer ID used when the property was registered.</param>
    /// <param name="name">The name of the mpv property that changed.</param>
    /// <param name="data">The new value of the mpv property.</param>
    public MpvPropertyChangeEventArgs(int id, string name, object? data)
    {
        Id = id;
        Name = name;
        Data = data;
    }
}
