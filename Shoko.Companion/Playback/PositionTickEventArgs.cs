using System;

namespace Shoko.Companion.Playback;

/// <summary>
/// Provides data for the <see cref="IPlaybackCoordinator.PositionTick"/> event.
///
/// <para>
///   The seek fact travels on the tick rather than beside it. A tick is
///   raised on the thread pool, and the timer's regular beat can land
///   between a seek and its report, so a flag kept anywhere else would be
///   consumed by whichever tick read it first. Carried here, it belongs to
///   exactly the one report built from this tick.
/// </para>
/// </summary>
public class PositionTickEventArgs : EventArgs
{
    /// <summary>
    /// Gets the position to report.
    /// </summary>
    public TimeSpan Position { get; }

    /// <summary>
    ///   Gets whether <see cref="Position"/> is where a seek mpv just
    ///   completed landed. True on the one tick raised for that seek and on
    ///   no other, so the report built from it is the only one that says
    ///   <c>Seeked</c>.
    /// </summary>
    public bool Seeked { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PositionTickEventArgs"/> class.
    /// </summary>
    /// <param name="position">The position to report.</param>
    /// <param name="seeked">Whether a seek just landed at <paramref name="position"/>.</param>
    public PositionTickEventArgs(TimeSpan position, bool seeked = false)
    {
        Position = position;
        Seeked = seeked;
    }
}
