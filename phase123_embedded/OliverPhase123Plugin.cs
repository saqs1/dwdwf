using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInPlugin("oliver.tik.s2e.phase123", "OLIVER S2E Phase123", "0.1.4")]
[BepInDependency("StreamToEarn_S2E_SupermarketSimulator", BepInDependency.DependencyFlags.HardDependency)]
public sealed class OliverPhase123Plugin : BasePlugin
{
    public override void Load()
    {
        // Original S2E is guaranteed to be loaded first by the verified hard dependency.
        OliverBootstrap.BeginDeferred();
        OliverBootstrap.TryInitializeAfterS2E();
    }
}
