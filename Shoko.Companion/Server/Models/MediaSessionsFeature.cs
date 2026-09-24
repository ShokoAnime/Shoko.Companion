using System;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// The server's <c>media-sessions</c> feature, read from its metadata:
/// everything the companion needs to connect, so no route is written
/// down on this side.
/// </summary>
public sealed class MediaSessionsFeature
{
    /// <summary>The plugin that advertised the feature.</summary>
    public required Guid PluginId { get; init; }

    /// <summary>The SignalR hub path, relative to the server's base URL.</summary>
    public required string HubPath { get; init; }

    /// <summary>
    /// The versioned REST base path, without a trailing slash, that
    /// session control and the stream endpoints hang off.
    /// </summary>
    public required string ApiPath { get; init; }
}
