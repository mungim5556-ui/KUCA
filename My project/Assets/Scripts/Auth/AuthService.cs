using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>KUCA 서버 계정 (docs/api.md 의 "로그인 · 계정" Account)</summary>
[Serializable]
public class KucaAccount
{
    public string id;
    public string provider;
    public string nickname;
    public string khuEmail;
    public bool emailVerified;
    public string preferencesJson;
    public string createdAt;
    /// <summary>"male" · "female" (없으면 빈 값). 매칭 "동성만" 조건에만 쓰고 다른 사람에게 보이지 않는다</summary>
    public string gender;
    /// <summary>로그인 없이 둘러보는 체험 계정 (메일 인증 없음, 7일 뒤 삭제)</summary>
    public bool isGuest;
    public bool ready;
}

/// <summary>
/// 로그인 · 계정 서버 통신. 로그인 토큰은 기기에 저장한다 (PlayerPrefs "auth_token").
/// 서버 주소는 메모 서버와 같다 (MemoBoard.ServerUrl).
/// </summary>
public static class AuthService
{
    const string TokenKey = "auth_token";
    const int Timeout = 40; // 무료 요금제 서버가 깨어나는 시간까지

    [Serializable] class LoginBody { public string token; public KucaAccount user; }
    [Serializable] class ErrorBody { public string error; public string devCode; }

    public static string Token => PlayerPrefs.GetString(TokenKey, "");
    public static bool HasToken => Token.Length > 0;

    /// <summary>지금 로그인한 계정 (앱이 켜져 있는 동안)</summary>
    public static KucaAccount Current { get; private set; }

    static string BaseUrl
    {
        get
        {
            var board = UnityEngine.Object.FindAnyObjectByType<MemoBoard>();
            string url = board != null ? board.ServerUrl : "";
            return url.TrimEnd('/');
        }
    }

    public static bool HasServer => BaseUrl.Length > 0;

    static void SaveToken(string token)
    {
        PlayerPrefs.SetString(TokenKey, token ?? "");
        PlayerPrefs.Save();
    }

    public static void ForgetToken()
    {
        SaveToken("");
        Current = null;
    }

    static UnityWebRequest Request(string method, string path, string json = null)
    {
        var req = new UnityWebRequest(BaseUrl + path, method, new DownloadHandlerBuffer(),
            json != null ? new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)) : null);
        if (json != null) req.SetRequestHeader("Content-Type", "application/json");
        if (HasToken) req.SetRequestHeader("Authorization", "Bearer " + Token);
        req.timeout = Timeout;
        return req;
    }

    static string ErrorOf(UnityWebRequest req)
    {
        if (req.result == UnityWebRequest.Result.ConnectionError)
            return "서버에 연결할 수 없어요. 인터넷 연결을 확인해 주세요.";
        try
        {
            var e = JsonUtility.FromJson<ErrorBody>(req.downloadHandler?.text ?? "");
            if (!string.IsNullOrEmpty(e?.error)) return e.error;
        }
        catch (ArgumentException) { }
        return $"서버 오류 ({req.responseCode})";
    }

    /// <summary>저장된 토큰으로 내 계정을 받는다. done(계정, 에러, 토큰이 무효인지)</summary>
    public static IEnumerator FetchMe(Action<KucaAccount, string, bool> done)
    {
        using var req = Request("GET", "/api/me");
        yield return req.SendWebRequest();
        if (req.responseCode == 401) { ForgetToken(); done(null, null, true); yield break; }
        if (req.result != UnityWebRequest.Result.Success) { done(null, ErrorOf(req), false); yield break; }
        Current = JsonUtility.FromJson<KucaAccount>(req.downloadHandler.text);
        done(Current, null, false);
    }

    /// <summary>provider: "kakao" · "apple" · "dev" · "guest"(체험 모드, providerToken 없음)</summary>
    public static IEnumerator Login(string provider, string providerToken, Action<KucaAccount, string> done)
    {
        string json = provider switch
        {
            "kakao" => JsonUtility.ToJson(new KakaoBody { accessToken = providerToken }),
            "apple" => JsonUtility.ToJson(new AppleBody { identityToken = providerToken }),
            "guest" => "{}",
            _ => JsonUtility.ToJson(new DevBody { name = providerToken }),
        };
        SaveToken("");
        using var req = Request("POST", "/api/auth/" + provider, json);
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) { done(null, ErrorOf(req)); yield break; }
        var body = JsonUtility.FromJson<LoginBody>(req.downloadHandler.text);
        SaveToken(body.token);
        Current = body.user;
        done(Current, null);
    }

    [Serializable] class KakaoBody { public string accessToken; }
    [Serializable] class AppleBody { public string identityToken; }
    [Serializable] class DevBody { public string name; }
    [Serializable] class EmailBody { public string email; }
    [Serializable] class CodeBody { public string code; }
    [Serializable] class NickBody { public string nickname; }
    [Serializable] class GenderBody { public string gender; }

    /// <summary>경희대 메일로 인증번호를 보낸다. done(에러, 개발용 번호)</summary>
    public static IEnumerator StartEmail(string email, Action<string, string> done)
    {
        using var req = Request("POST", "/api/me/email/start", JsonUtility.ToJson(new EmailBody { email = email }));
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) { done(ErrorOf(req), null); yield break; }
        string dev = null;
        try { dev = JsonUtility.FromJson<ErrorBody>(req.downloadHandler.text)?.devCode; } catch (ArgumentException) { }
        done(null, string.IsNullOrEmpty(dev) ? null : dev);
    }

    public static IEnumerator VerifyEmail(string code, Action<KucaAccount, string> done) =>
        SendForAccount("POST", "/api/me/email/verify", JsonUtility.ToJson(new CodeBody { code = code }), done);

    public static IEnumerator SetNickname(string nickname, Action<KucaAccount, string> done) =>
        SendForAccount("PUT", "/api/me/nickname", JsonUtility.ToJson(new NickBody { nickname = nickname }), done);

    /// <summary>gender: "male" · "female"</summary>
    public static IEnumerator SetGender(string gender, Action<KucaAccount, string> done) =>
        SendForAccount("PUT", "/api/me/gender", JsonUtility.ToJson(new GenderBody { gender = gender }), done);

    public static IEnumerator SavePreferences(WalkPreferences prefs, Action<KucaAccount, string> done) =>
        SendForAccount("PUT", "/api/me/preferences", prefs.ToJson(), done);

    static IEnumerator SendForAccount(string method, string path, string json, Action<KucaAccount, string> done)
    {
        using var req = Request(method, path, json);
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) { done(null, ErrorOf(req)); yield break; }
        Current = JsonUtility.FromJson<KucaAccount>(req.downloadHandler.text);
        done(Current, null);
    }

    /// <summary>회원 탈퇴: 서버에서 계정을 지우고 기기의 토큰도 지운다. done(에러 또는 null)</summary>
    public static IEnumerator DeleteAccount(Action<string> done)
    {
        using var req = Request("DELETE", "/api/me");
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success && req.responseCode != 401) { done(ErrorOf(req)); yield break; }
        ForgetToken();
        done(null);
    }

    public static IEnumerator Logout(Action done)
    {
        if (HasToken && HasServer)
        {
            using var req = Request("POST", "/api/auth/logout");
            yield return req.SendWebRequest(); // 실패해도 기기에서는 로그아웃한다
        }
        ForgetToken();
        done?.Invoke();
    }
}
