using System;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// DTO representing the status and progress of an AVDump process for a file.
/// </summary>
public class AVDumpDto
{
    /// <summary>
    /// The current status of the AVDump operation.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// The progress percentage of the AVDump operation (0.0 to 1.0).
    /// </summary>
    public double? Progress { get; set; }

    /// <summary>
    /// The number of CRC checks that succeeded during the AVDump process.
    /// </summary>
    public int? SucceededCreqCount { get; set; }

    /// <summary>
    /// The number of CRC checks that failed during the AVDump process.
    /// </summary>
    public int? FailedCreqCount { get; set; }

    /// <summary>
    /// The number of CRC checks still pending during the AVDump process.
    /// </summary>
    public int? PendingCreqCount { get; set; }

    /// <summary>
    /// The timestamp when the AVDump operation was started.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// The timestamp of the last successful dump operation.
    /// </summary>
    public DateTime? LastDumpedAt { get; set; }

    /// <summary>
    /// The version of AVDump used in the last operation.
    /// </summary>
    public string? LastVersion { get; set; }
}
