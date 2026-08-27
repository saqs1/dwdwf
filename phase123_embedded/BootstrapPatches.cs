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
    private static bool _driverCreated;

    public static void BeginDeferred()
    {
        if (LogSource == null)
            LogSource = BepInEx.Logging.Logger.CreateLogSource("S2E OLIVER Phase123");

        if (_driverCreated || _initialized) return;

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<OliverDeferredInitDriver>();
        }
        catch (Exception ex)
        {
            LogSource.LogDebug($"[OLIVER] Deferred driver registration note: {ex.Message}");
        }

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<OliverProfileVisualDriver>();
        }
        catch (Exception ex)
        {
            LogSource.LogDebug($"[OLIVER] Profile driver registration note: {ex.Message}");
        }

        try
        {
            GameObject host = new GameObject("OLIVER_S2E_Phase123_DeferredInit");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<OliverDeferredInitDriver>();
            _driverCreated = true;
            LogSource.LogInfo("[OLIVER] Phase123 loaded safely; waiting for original S2E plugin before patching.");
        }
        catch (Exception ex)
        {
            LogSource.LogError($"[OLIVER] Could not start deferred initialization: {ex}");
        }
    }

    internal static bool TryInitializeAfterS2E()
    {
        if (_initialized) return true;

        try
        {
            Type s2ePlugin = FindOriginalS2EPluginType();
            if (s2ePlugin == null) return false;

            MethodInfo attachText = FindMethod(s2ePlugin, "AttachBillboardText");
            MethodInfo attachImage = FindMethod(s2ePlugin, "AttachBillboardImage");
            if (attachText == null || attachImage == null)
            {
                LogSource.LogWarning("[OLIVER] Original S2E is loaded, but billboard methods are not available yet; retrying.");
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
            LogSource.LogInfo("[OLIVER] Original S2E detected. Phase 1+2+3 patches are ACTIVE.");
            LogSource.LogInfo("[OLIVER] Arabic/Unicode + 130% profile + Auto Fit + PNG frame enabled.");
            LogSource.LogInfo("[OLIVER] Original S2E HTTP port remains 55001.");
            return true;
        }
        catch (Exception ex)
        {
            LogSource.LogWarning($"[OLIVER] Deferred init retry: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static Type FindOriginalS2EPluginType()
    {
        Assembly[] assemblies;
        try
        {
            assemblies = AppDomain.CurrentDomain.GetAssemblies();
        }
        catch
        {
            return null;
        }

        foreach (Assembly assembly in assemblies)
        {
            if (assembly == null) continue;

            string assemblyName = string.Empty;
            try { assemblyName = assembly.GetName().Name ?? string.Empty; } catch { }

            if (assemblyName.IndexOf("Oliver", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            Type candidate = null;
            try
            {
                // GetType by exact name avoids Harmony AccessTools.TypeByName, which scans
                // every Unity assembly and produces ReflectionTypeLoadException spam on IL2CPP.
                candidate = assembly.GetType("Plugin", false, false);
            }
            catch
            {
                continue;
            }

            if (candidate == null) continue;

            MethodInfo text = FindMethod(candidate, "AttachBillboardText");
            MethodInfo image = FindMethod(candidate, "AttachBillboardImage");
            if (text != null && image != null)
                return candidate;
        }

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

public sealed class OliverDeferredInitDriver : MonoBehaviour
{
    private float _nextAttempt;

    public OliverDeferredInitDriver(IntPtr pointer) : base(pointer) { }

    private void Update()
    {
        if (Time.realtimeSinceStartup < _nextAttempt) return;
        _nextAttempt = Time.realtimeSinceStartup + 0.5f;

        if (OliverBootstrap.TryInitializeAfterS2E())
        {
            try { UnityEngine.Object.Destroy(gameObject); } catch { }
        }
    }
}

internal static class OliverS2EPatches
{
    // Harmony positional arguments (__0/__1) keep this stable even if the original
    // decompiler gave different parameter names.
    internal static void AfterAttachBillboardText(GameObject __0, string __1)
    {
        GameObject parent = __0;
        string displayText = __1;

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

    internal static void AfterAttachBillboardImage(GameObject __0, string __1)
    {
        GameObject parent = __0;

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
