using System;
using System.Linq;
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
    private static bool _typesRegistered;

    public static void BeginDeferred()
    {
        if (LogSource == null)
            LogSource = BepInEx.Logging.Logger.CreateLogSource("S2E OLIVER Phase123");

        if (_typesRegistered) return;

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<OliverProfileVisualDriver>();
            _typesRegistered = true;
            LogSource.LogInfo("[OLIVER] Visual driver registered. Patching original S2E billboard methods now.");
        }
        catch (Exception ex)
        {
            LogSource.LogWarning($"[OLIVER] Profile driver registration note: {ex.Message}");
        }
    }

    internal static bool TryInitializeAfterS2E()
    {
        if (_initialized) return true;

        try
        {
            // The billboard creation methods belong to SupermarketSimulatorTikTok, not the BepInEx Plugin class.
            // Resolve this exact type only; never scan all Unity types with AccessTools.TypeByName.
            Type s2eType = FindExactType("SupermarketSimulatorTikTok");
            if (s2eType == null)
            {
                LogSource.LogError("[OLIVER] SupermarketSimulatorTikTok type was not found after S2E loaded.");
                return false;
            }

            MethodInfo attachText = FindMethod(s2eType, "AttachBillboardText");
            MethodInfo attachImage = FindMethod(s2eType, "AttachBillboardImage");
            if (attachText == null || attachImage == null)
            {
                LogSource.LogError($"[OLIVER] Billboard methods missing on {s2eType.FullName}. Text={attachText != null}, Image={attachImage != null}");
                return false;
            }

            MethodInfo textPostfix = typeof(OliverS2EPatches).GetMethod(
                nameof(OliverS2EPatches.AfterAttachBillboardText),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo imagePostfix = typeof(OliverS2EPatches).GetMethod(
                nameof(OliverS2EPatches.AfterAttachBillboardImage),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            if (textPostfix == null || imagePostfix == null)
            {
                LogSource.LogError("[OLIVER] Internal patch methods are missing.");
                return false;
            }

            _harmony = new Harmony("oliver.tik.s2e.phase123.standalone");
            _harmony.Patch(attachText, postfix: new HarmonyMethod(textPostfix));
            _harmony.Patch(attachImage, postfix: new HarmonyMethod(imagePostfix));

            _initialized = true;
            LogSource.LogInfo("[OLIVER] SupermarketSimulatorTikTok detected. Phase 1+2+3 patches are ACTIVE.");
            LogSource.LogInfo("[OLIVER] Arabic/Unicode + 130% profile + Auto Fit + PNG frame enabled.");
            LogSource.LogInfo("[OLIVER] Original S2E HTTP port remains 55001.");
            return true;
        }
        catch (Exception ex)
        {
            LogSource.LogError($"[OLIVER] Phase123 patch activation failed: {ex}");
            return false;
        }
    }

    private static Type FindExactType(string fullName)
    {
        try
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null) continue;
                try
                {
                    Type t = assembly.GetType(fullName, false, false);
                    if (t != null) return t;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static MethodInfo FindMethod(Type type, string name)
    {
        try
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
        }
        catch
        {
            return null;
        }
    }
}

internal static class OliverS2EPatches
{
    internal static void AfterAttachBillboardText(GameObject __0, string __1)
    {
        try
        {
            GameObject parent = __0;
            string displayText = __1;
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

    internal static void AfterAttachBillboardImage(GameObject __0, string __1)
    {
        try
        {
            GameObject parent = __0;
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

            // Original S2E scale 0.30 x 130% = 0.39.
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
