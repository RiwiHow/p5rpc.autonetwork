using System.ComponentModel;
using p5rpc.autonetwork.Template.Configuration;

namespace p5rpc.autonetwork.Configuration;

public class Config : Configurable<Config>
{
    [DisplayName("Auto Enable Network")]
    [Description("Automatically enables Thief's Union network features on startup (works with or without Persona Essentials Intro Skip).")]
    [DefaultValue(true)]
    public bool AutoEnableNetwork { get; set; } = true;

    [DisplayName("Skip Network Result Prompt")]
    [Description("Skips the confirmation/notice screen after network login succeeds or fails (when Intro Skip is off).")]
    [DefaultValue(true)]
    public bool SkipNetworkResultPrompt { get; set; } = true;
}

public class ConfiguratorMixin : ConfiguratorMixinBase
{
}
