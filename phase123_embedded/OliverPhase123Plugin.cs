using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInPlugin("oliver.tik.s2e.phase123", "OLIVER S2E Phase123", "0.1.0")]
public sealed class OliverPhase123Plugin : BasePlugin
{
    public override void Load()
    {
        OliverBootstrap.Init();
    }
}
