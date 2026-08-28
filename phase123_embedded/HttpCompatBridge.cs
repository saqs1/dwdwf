using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

internal static class OliverHttpCompatBridge
{
    private const string PublicPrefix = "http://127.0.0.1:55001/";
    private const string InternalBase = "http://127.0.0.1:55101";

    private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    private static HttpListener _listener;
    private static CancellationTokenSource _cts;
    private static bool _started;

    internal static void Start()
    {
        if (_started) return;

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(PublicPrefix);
            _listener.Start();
            _started = true;

            OliverBootstrap.LogSource?.LogInfo("[OLIVER HTTP] Compatibility bridge ACTIVE on 55001 -> internal S2E 55101.");
            OliverBootstrap.LogSource?.LogInfo("[OLIVER HTTP] Query parameters such as name/avatarUrl are preserved and normalized automatically.");
            _ = Task.Run(() => ListenLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogError($"[OLIVER HTTP] Could not bind public port 55001: {ex}");
        }
    }

    internal static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        _cts = null;
        _started = false;
    }

    private static async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OliverBootstrap.LogSource?.LogWarning($"[OLIVER HTTP] Listener retry: {ex.Message}");
                await Task.Delay(100, token).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleRequest(context), token);
        }
    }

    private static async Task HandleRequest(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        try
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Superdupertoken";

            if (string.Equals(request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            string incomingBody = string.Empty;
            if (request.HasEntityBody)
            {
                using StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
                incomingBody = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            JsonObject body = ParseObjectOrEmpty(incomingBody);
            CopyQueryIntoJson(request, body);
            NormalizeAliases(body);

            string route = request.Url?.AbsolutePath ?? "/";
            string normalizedBody = body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

            string name = GetString(body, "name");
            string avatar = GetString(body, "avatarUrl");
            string head = GetString(body, "headAvatarUrl");
            if (IsSpawnRoute(route))
            {
                OliverBootstrap.LogSource?.LogInfo(
                    $"[OLIVER HTTP] {route} received: name={(string.IsNullOrWhiteSpace(name) ? "NO" : "YES")}, avatar={(string.IsNullOrWhiteSpace(avatar) ? "NO" : "YES")}, head={(string.IsNullOrWhiteSpace(head) ? "NO" : "YES")}.");
            }

            using HttpRequestMessage forward = new HttpRequestMessage(HttpMethod.Post, InternalBase + route);
            forward.Content = new StringContent(normalizedBody, Encoding.UTF8, "application/json");

            HttpResponseMessage internalResponse = null;
            Exception lastError = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    internalResponse = await Client.SendAsync(forward.Clone()).ConfigureAwait(false);
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }

            if (internalResponse == null)
            {
                throw new InvalidOperationException("Internal S2E listener on 55101 did not respond.", lastError);
            }

            string result = await internalResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            byte[] bytes = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(result) ? "OTVET" : result);
            response.StatusCode = (int)internalResponse.StatusCode;
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            response.Close();
            internalResponse.Dispose();
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogError($"[OLIVER HTTP] Request forwarding failed: {ex}");
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes("OLIVER_HTTP_ERROR");
                response.StatusCode = 502;
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                response.Close();
            }
            catch { }
        }
    }

    private static JsonObject ParseObjectOrEmpty(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                JsonNode node = JsonNode.Parse(text);
                if (node is JsonObject obj) return obj;
            }
            catch { }
        }
        return new JsonObject();
    }

    private static void CopyQueryIntoJson(HttpListenerRequest request, JsonObject body)
    {
        try
        {
            foreach (string key in request.QueryString.AllKeys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                string value = request.QueryString[key];
                if (value == null) continue;
                body[key] = value;
            }
        }
        catch { }
    }

    private static void NormalizeAliases(JsonObject body)
    {
        SetCanonical(body, "name", new[] { "name", "nickname", "userName", "username", "displayName", "uniqueId", "user" });
        SetCanonical(body, "avatarUrl", new[] { "avatarUrl", "avatarURL", "profilePictureUrl", "profileImageUrl", "avatar", "imageUrl", "pictureUrl", "photoUrl", "userAvatar" });
        SetCanonical(body, "headAvatarUrl", new[] { "headAvatarUrl", "headUrl", "headAvatar", "headImageUrl" });
    }

    private static void SetCanonical(JsonObject body, string canonical, IEnumerable<string> aliases)
    {
        string existing = GetStringCaseInsensitive(body, canonical);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            body[canonical] = existing;
            return;
        }

        foreach (string alias in aliases)
        {
            string value = GetStringCaseInsensitive(body, alias);
            if (!string.IsNullOrWhiteSpace(value))
            {
                body[canonical] = value;
                return;
            }
        }
    }

    private static string GetStringCaseInsensitive(JsonObject body, string key)
    {
        foreach (KeyValuePair<string, JsonNode> kv in body)
        {
            if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (kv.Value == null) return null;
                if (kv.Value is JsonValue value && value.TryGetValue<string>(out string text)) return text;
                return kv.Value.ToString();
            }
            catch { return null; }
        }
        return null;
    }

    private static string GetString(JsonObject body, string key) => GetStringCaseInsensitive(body, key);

    private static bool IsSpawnRoute(string route)
    {
        if (string.IsNullOrEmpty(route)) return false;
        return route.EndsWith("/spawncustomer", StringComparison.OrdinalIgnoreCase) ||
               route.EndsWith("/spawnshoplifter", StringComparison.OrdinalIgnoreCase) ||
               route.EndsWith("/spawnnpc", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage Clone(this HttpRequestMessage request)
    {
        HttpRequestMessage clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content != null)
        {
            string content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, "application/json");
        }
        return clone;
    }
}
