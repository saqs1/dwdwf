using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInPlugin("oliver.tik.s2e.phase123", "OLIVER S2E Phase123", "0.1.9")]
[BepInDependency("StreamToEarn_S2E_SupermarketSimulator", BepInDependency.DependencyFlags.HardDependency)]
public sealed class OliverPhase123Plugin : BasePlugin
{
    public override void Load()
    {
        // S2E listens internally on 55101 in this package.
        // OLIVER owns historical public port 55001 and accepts the different
        // request formats StreamToEarn has used (query, JSON, form and command strings).
        OliverBootstrap.BeginDeferred();
        OliverHttpCompatBridge.Start();
    }
}
