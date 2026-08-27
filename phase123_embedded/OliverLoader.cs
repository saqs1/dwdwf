using System;
using System.IO;
using System.Reflection;

public static class OliverLoader
{
    private static bool _loaded;
    public static void Initialize()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            Assembly host = Assembly.GetExecutingAssembly();
            using (Stream stream = host.GetManifestResourceStream("OliverS2EEmbeddedHelper.dll"))
            {
                if (stream == null) return;
                byte[] data = new byte[stream.Length];
                int offset = 0;
                while (offset < data.Length)
                {
                    int read = stream.Read(data, offset, data.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                if (offset != data.Length) return;
                Assembly helper = Assembly.Load(data);
                Type bootstrap = helper.GetType("OliverBootstrap", false);
                MethodInfo init = bootstrap?.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
                init?.Invoke(null, null);
            }
        }
        catch
        {
        }
    }
}
