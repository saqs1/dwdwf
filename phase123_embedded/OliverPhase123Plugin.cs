using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInPlugin("oliver.tik.s2e.phase123", "OLIVER S2E Phase123", "0.3.0")]
[BepInDependency("StreamToEarn_S2E_SupermarketSimulator", BepInDependency.DependencyFlags.HardDependency)]
public sealed class OliverPhase123Plugin : BasePlugin
{
    public override void Load()
    {
        // v0.3.0: exact proven v0.1.5 billboard path + text-only Arabic repair.
        // Original S2E remains solely responsible for HTTP/spawn/name/avatar creation.
        OliverBootstrap.BeginDeferred();
        OliverBootstrap.TryInitializeAfterS2E();
    }
}
