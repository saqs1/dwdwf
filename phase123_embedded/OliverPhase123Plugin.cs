using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInPlugin("oliver.tik.s2e.phase123", "OLIVER S2E Phase123", "0.2.0")]
[BepInDependency("StreamToEarn_S2E_SupermarketSimulator", BepInDependency.DependencyFlags.HardDependency)]
public sealed class OliverPhase123Plugin : BasePlugin
{
    public override void Load()
    {
        // S2E listens internally on 55101 in this package.
        // OLIVER owns public 55001, reconstructs viewer metadata from every
        // StreamToEarn request format we can safely recognize, then forwards
        // canonical JSON to the original S2E handler.
        OliverBootstrap.BeginDeferred();
        OliverHttpCompatBridge.Start();
    }
}
