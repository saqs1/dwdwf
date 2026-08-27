using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInPlugin("oliver.tik.s2e.phase123", "OLIVER S2E Phase123", "0.1.3")]
[BepInDependency("StreamToEarn_S2E_SupermarketSimulator", BepInDependency.DependencyFlags.HardDependency)]
public sealed class OliverPhase123Plugin : BasePlugin
{
    public override void Load()
    {
        // The hard dependency uses the ORIGINAL S2E GUID extracted from its BepInPlugin metadata.
        // Therefore original S2E is loaded first, then OLIVER registers its IL2CPP types and patches.
        OliverBootstrap.BeginDeferred();
        OliverBootstrap.TryInitializeAfterS2E();
    }
}
