using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInPlugin("oliver.tik.s2e.phase123", "OLIVER S2E Phase123", "0.2.1")]
[BepInDependency("StreamToEarn_S2E_SupermarketSimulator", BepInDependency.DependencyFlags.HardDependency)]
public sealed class OliverPhase123Plugin : BasePlugin
{
    public override void Load()
    {
        // PERFORMANCE-SAFE MODE:
        // Keep the verified original S2E listener on 55001 untouched.
        // Do not start any OLIVER HTTP listener/proxy and do not duplicate/log
        // every spawn request. OLIVER only enhances billboard objects passively
        // after the original S2E has created them.
        OliverBootstrap.BeginDeferred();
        OliverBootstrap.LogSource?.LogInfo("[OLIVER] v0.2.1 PERFORMANCE-SAFE mode. No HTTP bridge/proxy is running.");
        OliverBootstrap.LogSource?.LogInfo("[OLIVER] Original S2E owns port 55001 directly; passive visual enhancement only.");
    }
}
