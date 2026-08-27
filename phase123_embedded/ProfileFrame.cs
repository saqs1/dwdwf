using System;
using System.IO;
using BepInEx;
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

            // Phase 2 Auto Fit: center-crop the texture without stretching the profile image.
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

        // Do not require UnityEngine.PhysicsModule in the helper build. The billboard itself
        // controls placement and this visual quad is only used as the PNG overlay.
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
                    Rgba32 pixel = pixels[src];
                    rgba[dst] = pixel.R;
                    rgba[dst + 1] = pixel.G;
                    rgba[dst + 2] = pixel.B;
                    rgba[dst + 3] = pixel.A;
                }
            }

            _frameTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var il2cppBytes = new Il2CppStructArray<byte>(rgba);
            _frameTexture.LoadRawTextureData(il2cppBytes);
            _frameTexture.Apply(false, true);
            _frameTexture.wrapMode = TextureWrapMode.Clamp;
            _frameTexture.filterMode = FilterMode.Bilinear;

            Shader shader = Shader.Find("Sprites/Default") ??
                            Shader.Find("Unlit/Transparent") ??
                            Shader.Find("Unlit/Texture");
            if (shader == null)
            {
                OliverBootstrap.LogSource?.LogWarning("[OLIVER] No transparent shader was found for the PNG frame.");
                return;
            }

            _frameMaterial = new Material(shader);
            _frameMaterial.mainTexture = _frameTexture;
            _frameMaterial.color = UnityEngine.Color.white;
            OliverBootstrap.LogSource?.LogInfo("[OLIVER] Royal PNG frame loaded.");
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogWarning($"[OLIVER] Frame PNG could not load: {ex.Message}");
        }
    }
}
