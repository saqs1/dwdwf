using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

public sealed class OliverProfileVisualDriver : MonoBehaviour
{
    private bool _done;
    private float _nextTry;

    public OliverProfileVisualDriver(IntPtr pointer) : base(pointer) { }

    private void Update()
    {
        if (_done || Time.realtimeSinceStartup < _nextTry) return;
        _nextTry = Time.realtimeSinceStartup + 0.25f;

        try
        {
            Renderer renderer = GetComponent<Renderer>();
            if (renderer == null || renderer.material == null || renderer.material.mainTexture == null) return;

            Texture texture = renderer.material.mainTexture;
            int width = texture.width;
            int height = texture.height;
            if (width > 0 && height > 0)
            {
                if (width > height)
                {
                    float sx = (float)height / width;
                    renderer.material.mainTextureScale = new Vector2(sx, 1f);
                    renderer.material.mainTextureOffset = new Vector2((1f - sx) * 0.5f, 0f);
                }
                else if (height > width)
                {
                    float sy = (float)width / height;
                    renderer.material.mainTextureScale = new Vector2(1f, sy);
                    renderer.material.mainTextureOffset = new Vector2(0f, (1f - sy) * 0.5f);
                }
                else
                {
                    renderer.material.mainTextureScale = Vector2.one;
                    renderer.material.mainTextureOffset = Vector2.zero;
                }
            }

            OliverFrameFactory.AttachFrame(transform);
            _done = true;
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogDebug($"[OLIVER] Auto Fit/frame retry: {ex.Message}");
        }
    }
}

internal static class OliverFrameFactory
{
    private static Texture2D _frameTexture;
    private static Material _frameMaterial;
    private static bool _loadAttempted;

    internal static void AttachFrame(Transform imageTransform)
    {
        if (imageTransform == null || imageTransform.Find("OLIVER_ProfileFrame") != null) return;
        EnsureFrameLoaded();
        if (_frameTexture == null || _frameMaterial == null) return;

        GameObject frame = GameObject.CreatePrimitive(PrimitiveType.Quad);
        frame.name = "OLIVER_ProfileFrame";
        frame.transform.SetParent(imageTransform, false);
        frame.transform.localPosition = new Vector3(0f, 0f, -0.012f);
        frame.transform.localRotation = Quaternion.identity;
        frame.transform.localScale = Vector3.one * 1.28f;
        Collider collider = frame.GetComponent<Collider>();
        if (collider != null) UnityEngine.Object.Destroy(collider);
        Renderer renderer = frame.GetComponent<Renderer>();
        if (renderer != null) renderer.material = _frameMaterial;
    }

    private static void EnsureFrameLoaded()
    {
        if (_loadAttempted) return;
        _loadAttempted = true;
        try
        {
            string path = Path.Combine(Paths.PluginPath, "Oliver_Royal_Frame.png");
            if (!File.Exists(path))
            {
                OliverBootstrap.LogSource?.LogWarning($"[OLIVER] Frame PNG not found: {path}");
                return;
            }

            using Image<Rgba32> image = Image.Load<Rgba32>(path);
            int width = image.Width;
            int height = image.Height;
            Rgba32[] pixels = new Rgba32[width * height];
            image.CopyPixelDataTo(pixels);
            byte[] rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int src = y * width + x;
                    int dst = ((height - 1 - y) * width + x) * 4;
                    Rgba32 p = pixels[src];
                    rgba[dst] = p.R;
                    rgba[dst + 1] = p.G;
                    rgba[dst + 2] = p.B;
                    rgba[dst + 3] = p.A;
                }
            }

            _frameTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            _frameTexture.LoadRawTextureData(Il2CppStructArray<byte>.op_Implicit(rgba));
            _frameTexture.Apply(false, true);
            _frameTexture.wrapMode = TextureWrapMode.Clamp;
            _frameTexture.filterMode = FilterMode.Bilinear;

            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent") ?? Shader.Find("Unlit/Texture");
            if (shader == null) return;
            _frameMaterial = new Material(shader);
            _frameMaterial.mainTexture = _frameTexture;
            _frameMaterial.color = Color.white;
            OliverBootstrap.LogSource?.LogInfo("[OLIVER] Royal PNG frame loaded.");
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogWarning($"[OLIVER] Frame PNG could not load: {ex.Message}");
        }
    }
}
