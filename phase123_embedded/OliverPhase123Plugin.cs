using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInPlugin("oliver.tik.s2e.phase123", "OLIVER S2E Phase123", "0.3.1")]
[BepInDependency("StreamToEarn_S2E_SupermarketSimulator", BepInDependency.DependencyFlags.HardDependency)]
public sealed class OliverPhase123Plugin : BasePlugin
{
    public override void Load()
    {
        // v0.3.1 keeps the proven v0.1.5 billboard/image/frame path.
        // Original S2E still owns port 55001 and performs all spawns.
        // OLIVER only normalizes alternate username/avatar keys before S2E reads them.
        OliverBootstrap.BeginDeferred();
        OliverBootstrap.TryInitializeAfterS2E();
    }
}
