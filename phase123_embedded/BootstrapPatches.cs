using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

public static class OliverBootstrap
{
    internal static ManualLogSource LogSource;
    private static Harmony _harmony;
    private static bool _initialized;

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        LogSource = BepInEx.Logging.Logger.CreateLogSource("S2E OLIVER Phase123");

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<OliverProfileVisualDriver>();
        }
        catch (Exception ex)
        {
            LogSource.LogWarning($"[OLIVER] Driver registration skipped: {ex.Message}");
        }

        try
        {
            Type s2ePlugin = AccessTools.TypeByName("Plugin");
            if (s2ePlugin == null)
            {
                LogSource.LogError("[OLIVER] Original S2E Plugin type was not found.");
                return;
            }

            MethodInfo attachText = AccessTools.Method(s2ePlugin, "AttachBillboardText");
            MethodInfo attachImage = AccessTools.Method(s2ePlugin, "AttachBillboardImage");
            if (attachText == null || attachImage == null)
            {
                LogSource.LogError("[OLIVER] S2E billboard methods were not found.");
                return;
            }

            _harmony = new Harmony("oliver.tik.s2e.phase123.embedded");
            _harmony.Patch(
                attachText,
                postfix: new HarmonyMethod(typeof(OliverS2EPatches), nameof(OliverS2EPatches.AfterAttachBillboardText)));
            _harmony.Patch(
                attachImage,
                postfix: new HarmonyMethod(typeof(OliverS2EPatches), nameof(OliverS2EPatches.AfterAttachBillboardImage)));

            LogSource.LogInfo("[OLIVER] Embedded Phase 1+2+3 active: Arabic/Unicode + 130% profile + Auto Fit + PNG frame.");
            LogSource.LogInfo("[OLIVER] Original S2E HTTP port remains 55001.");
        }
        catch (Exception ex)
        {
            LogSource.LogError($"[OLIVER] Embedded Phase123 init failed: {ex}");
        }
    }
}

internal static class OliverS2EPatches
{
    internal static void AfterAttachBillboardText(GameObject parent, string displayText)
    {
        try
        {
            if (parent == null || string.IsNullOrEmpty(displayText)) return;

            Transform textTransform = parent.transform.Find("BillboardText");
            if (textTransform == null) return;

            GameObject textObject = textTransform.gameObject;
            var rawComponents = textObject.GetComponents(Il2CppType.Of<Component>());
            foreach (var raw in rawComponents)
            {
                Component component = raw.TryCast<Component>();
                if (component == null) continue;

                string fullName = component.GetIl2CppType()?.FullName ?? string.Empty;
                if (!fullName.Equals("TMPro.TextMeshPro", StringComparison.Ordinal) &&
                    !fullName.StartsWith("TMPro.TextMeshPro", StringComparison.Ordinal))
                    continue;

                OliverUnicodeText.Apply(component, displayText);
                break;
            }
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogWarning($"[OLIVER] Username enhancement skipped safely: {ex.Message}");
        }
    }

    internal static void AfterAttachBillboardImage(GameObject parent, string url)
    {
        try
        {
            if (parent == null) return;

            Transform newest = null;
            int bestId = int.MinValue;
            for (int i = 0; i < parent.transform.childCount; i++)
            {
                Transform child = parent.transform.GetChild(i);
                if (child == null) continue;

                string name = child.name ?? string.Empty;
                if (!name.StartsWith("BillboardImage_", StringComparison.Ordinal)) continue;

                int id = child.gameObject.GetInstanceID();
                if (id > bestId)
                {
                    bestId = id;
                    newest = child;
                }
            }

            if (newest == null) return;

            // Original S2E scale is 0.30. Phase 2 target is exactly 130% => 0.39.
            newest.localScale = Vector3.one * 0.39f;

            if (newest.gameObject.GetComponent<OliverProfileVisualDriver>() == null)
                newest.gameObject.AddComponent<OliverProfileVisualDriver>();
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogWarning($"[OLIVER] Profile enhancement skipped safely: {ex.Message}");
        }
    }
}
