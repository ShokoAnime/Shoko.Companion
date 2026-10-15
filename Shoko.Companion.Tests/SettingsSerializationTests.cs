using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shoko.Companion.Configuration;
using Xunit;

namespace Shoko.Companion.Tests;

public class SettingsSerializationTests
{
    [Fact]
    public void Enums_AreSavedByName()
    {
        var settings = new CompanionSettings { OnNewUrlAction = OnNewUrlBehavior.Replace };

        var json = JObject.Parse(JsonConvert.SerializeObject(settings, SettingsProvider.JsonSettings));

        Assert.Equal(nameof(OnNewUrlBehavior.Replace), json.Value<string>(nameof(CompanionSettings.OnNewUrlAction)));
    }

    [Fact]
    public void Enums_SavedAsNumbers_StillLoad()
    {
        var json = $$"""{"{{nameof(CompanionSettings.OnNewUrlAction)}}": {{(int)OnNewUrlBehavior.Replace}}}""";

        var settings = JsonConvert.DeserializeObject<CompanionSettings>(json, SettingsProvider.JsonSettings)!;

        Assert.Equal(OnNewUrlBehavior.Replace, settings.OnNewUrlAction);
    }
}
