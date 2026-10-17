using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KucaMemoServer.Services;

/// <summary>앱이 보낸 카카오 액세스 토큰이 진짜인지 카카오에 물어 사용자 id 를 얻는다.</summary>
public interface IKakaoVerifier
{
    bool IsConfigured { get; }
    /// <summary>카카오 사용자 id (실패하면 null)</summary>
    Task<string?> VerifyAsync(string accessToken, CancellationToken ct);
}

/// <summary>Sign in with Apple 의 identityToken(JWT)을 검증해 사용자 id(sub)를 얻는다.</summary>
public interface IAppleVerifier
{
    Task<string?> VerifyAsync(string identityToken, CancellationToken ct);
}

/// <summary>
/// GET https://kapi.kakao.com/v1/user/access_token_info 로 토큰을 확인한다.
/// 다른 앱에서 받은 토큰으로 로그인하지 못하게 응답의 app_id 가 설정 Kakao:AppId(콘솔의 "앱 ID" 숫자)와 같아야 한다.
/// </summary>
public class KakaoVerifier(HttpClient http, IConfiguration config, ILogger<KakaoVerifier> log) : IKakaoVerifier
{
    string AppId => config["Kakao:AppId"] ?? "";
    public bool IsConfigured => AppId.Length > 0;

    public async Task<string?> VerifyAsync(string accessToken, CancellationToken ct)
    {
        if (!IsConfigured)
            return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://kapi.kakao.com/v1/user/access_token_info");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await http.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("카카오 토큰 확인 실패 {Status}: {Body}", (int)res.StatusCode, body.Length > 200 ? body[..200] : body);
                return null;
            }
            using JsonDocument doc = JsonDocument.Parse(body);
            string appId = doc.RootElement.GetProperty("app_id").GetRawText();
            if (appId != AppId)
            {
                log.LogWarning("카카오 토큰의 앱이 다름 ({AppId})", appId);
                return null;
            }
            return doc.RootElement.GetProperty("id").GetRawText();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            log.LogWarning("카카오 토큰 확인 오류: {Message}", e.Message);
            return null;
        }
    }
}

/// <summary>
/// Apple 공개키(https://appleid.apple.com/auth/keys)로 RS256 서명을 확인하고
/// iss = https://appleid.apple.com, aud = 설정 Apple:BundleId(기본 com.mungim.kuca), 만료 전인지 본다.
/// </summary>
public class AppleVerifier(HttpClient http, IConfiguration config, ILogger<AppleVerifier> log) : IAppleVerifier
{
    string BundleId => config["Apple:BundleId"] is { Length: > 0 } b ? b : "com.mungim.kuca";
    static JsonElement? keys;
    static DateTime keysFetched;

    public async Task<string?> VerifyAsync(string identityToken, CancellationToken ct)
    {
        try
        {
            string[] parts = identityToken.Split('.');
            if (parts.Length != 3)
                return null;
            using JsonDocument header = JsonDocument.Parse(B64(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256")
                return null;
            string kid = header.RootElement.GetProperty("kid").GetString() ?? "";

            JsonElement? key = await FindKeyAsync(kid, ct);
            if (key is not { } k)
                return null;
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = B64(k.GetProperty("n").GetString()!), Exponent = B64(k.GetProperty("e").GetString()!) });
            bool ok = rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!ok)
                return null;

            using JsonDocument payload = JsonDocument.Parse(B64(parts[1]));
            JsonElement p = payload.RootElement;
            if (p.GetProperty("iss").GetString() != "https://appleid.apple.com" || p.GetProperty("aud").GetString() != BundleId)
                return null;
            if (DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("exp").GetInt64()) < DateTimeOffset.UtcNow)
                return null;
            return p.GetProperty("sub").GetString();
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or CryptographicException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Apple 토큰 확인 오류: {Message}", e.Message);
            return null;
        }
    }

    async Task<JsonElement?> FindKeyAsync(string kid, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (keys == null || attempt == 1 || DateTime.UtcNow - keysFetched > TimeSpan.FromHours(12))
            {
                string json = await http.GetStringAsync("https://appleid.apple.com/auth/keys", ct);
                keys = JsonDocument.Parse(json).RootElement.GetProperty("keys").Clone();
                keysFetched = DateTime.UtcNow;
            }
            foreach (JsonElement k in keys.Value.EnumerateArray())
                if (k.GetProperty("kid").GetString() == kid)
                    return k;
        }
        return null; // 키가 바뀌었으면 한 번 새로 받아 보고, 그래도 없으면 실패
    }

    static byte[] B64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
