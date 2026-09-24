using System;
using Newtonsoft.Json.Linq;

namespace Shoko.Companion.Server.Models;

/// <summary>
/// One entry of Shoko's <c>GET /api/v3/Plugin/Features</c>: a feature a
/// plugin advertises, identified by the plugin and the name.
/// </summary>
public class PluginFeatureDto
{
    /// <summary>The plugin advertising the feature.</summary>
    public Guid PluginID { get; set; }

    /// <summary>Lowercase kebab-case, unique within the plugin.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The feature's contract as <c>major.minor.patch</c>. A major bump
    /// changes the shape of <see cref="Metadata"/>.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The feature's parameters, shaped by the plugin.</summary>
    public JObject? Metadata { get; set; }
}
