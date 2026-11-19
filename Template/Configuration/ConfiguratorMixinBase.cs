using p5rpc.autonetwork.Configuration;
using Reloaded.Mod.Interfaces;

namespace p5rpc.autonetwork.Template.Configuration;

public class ConfiguratorMixinBase
{
    public virtual IUpdatableConfigurable[] MakeConfigurations(string configFolder)
    {
        return new IUpdatableConfigurable[]
        {
            Configurable<Config>.FromFile(Path.Combine(configFolder, "Config.json"), "General Config"),
        };
    }

    public virtual bool TryRunCustomConfiguration(Configurator configurator) => false;

    public virtual void Migrate(string oldDirectory, string newDirectory) { }
}
