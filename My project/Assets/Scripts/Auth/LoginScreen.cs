using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 로그인 (필수, 또는 체험 모드). 앱이 켜지면 저장된 토큰을 확인하고, 없거나 만료면 로그인 화면을 띄운다.
/// 흐름: (첫 실행) 개인정보 안내 → 카카오 / Apple 로그인 → 경희대 메일 인증 → 닉네임 → 성별 → 산책 취향 체크리스트 → 시작.
/// 체험 모드("로그인 없이 둘러보기")는 서버가 만든 체험 계정으로 메일 인증 · 닉네임을 건너뛰고 성별만 받는다.
/// 이미 끝낸 단계는 건너뛴다 (예: 토큰이 살아 있고 모두 끝났으면 바로 시작).
/// </summary>
public class LoginScreen : MonoBehaviour
{
    static readonly Color Ink = new Color(0.11f, 0.11f, 0.12f);
    static readonly Color InkQuiet = new Color(0.11f, 0.11f, 0.12f, 0.55f);
    static readonly Color Accent = new Color(0.16f, 0.42f, 0.95f);
    static readonly Color KakaoYellow = new Color(1f, 0.9f, 0f);
    static readonly Color Burgundy = new Color(0.55f, 0.13f, 0.20f);
    static readonly Color Field = new Color(0.95f, 0.96f, 0.97f);
    static readonly Color ErrorRed = new Color(0.86f, 0.25f, 0.25f);

    const string PrivacySeenKey = "privacy_seen";

    GameObject root, checking, loginPage, emailPage, nickPage, genderPage, privacyPage;
    Button privacyButton;
    bool privacyOnly; // 설정에서 열었으면 확인을 눌러도 닫기만 한다
    Text checkingText, errorText, emailHint, codeHint;
    InputField emailInput, codeInput, nickInput;
    Button sendButton, verifyButton, nickButton, retryButton;
    GameObject codeRow;
    bool busy;
    float resendAt;

    /// <summary>앱 시작 때 한 번 (GameHUD 가 부른다). 로그아웃한 뒤에도 다시 부른다.</summary>
    public static void Begin()
    {
        LoginScreen s = FindAnyObjectByType<LoginScreen>();
        if (s == null)
            s = new GameObject("LoginScreen").AddComponent<LoginScreen>();
        s.StartFlow();
    }

    /// <summary>설정의 "개인정보 안내": 안내 화면만 띄웠다가 닫는다.</summary>
    public static void ShowPrivacy()
    {
        LoginScreen s = FindAnyObjectByType<LoginScreen>();
        if (s == null)
            s = new GameObject("LoginScreen").AddComponent<LoginScreen>();
        s.privacyOnly = true;
        s.root.SetActive(true);
        UIInputBlocker.SetModal(s, true);
        s.errorText.text = "";
        UIKit.SetLabel(s.privacyButton, "닫기");
        s.Show(s.privacyPage);
    }

    void Awake() => Build();

    void ConfirmPrivacy()
    {
        PlayerPrefs.SetInt(PrivacySeenKey, 1);
        PlayerPrefs.Save();
        if (privacyOnly)
        {
            privacyOnly = false;
            root.SetActive(false);
            UIInputBlocker.SetModal(this, false);
            return;
        }
        StartFlow();
    }

    void StartFlow()
    {
        root.SetActive(true);
        UIInputBlocker.SetModal(this, true);
        errorText.text = "";
        privacyOnly = false;
        if (PlayerPrefs.GetInt(PrivacySeenKey, 0) == 0)
        {
            UIKit.SetLabel(privacyButton, "확인하고 시작");
            Show(privacyPage);
            return;
        }
        if (!AuthService.HasServer)
        {
            // 서버 주소가 없는 가짜 서버 모드(개발용): 계정을 만들 곳이 없어 로그인 없이 진행한다.
            Finish();
            return;
        }
        if (!AuthService.HasToken)
        {
            Show(loginPage);
            return;
        }
        Show(checking);
        checkingText.text = "로그인 정보를 확인하고 있어요…";
        retryButton.gameObject.SetActive(false);
        StartCoroutine(AuthService.FetchMe((account, error, invalid) =>
        {
            if (account != null) { Continue(account); return; }
            if (invalid) { Show(loginPage); return; }
            checkingText.text = error ?? "서버에 연결하지 못했어요";
            retryButton.gameObject.SetActive(true); // 로그인 필수라 다시 시도만 할 수 있다
        }));
    }

    /// <summary>남은 단계로 간다.</summary>
    void Continue(KucaAccount a)
    {
        errorText.text = "";
        if (!a.emailVerified && !a.isGuest)
        {
            Show(emailPage);
            codeRow.SetActive(false);
            emailHint.text = "경희대 학생만 쓸 수 있어요. 학교 메일로 인증번호를 보내 드려요.";
            return;
        }
        if (string.IsNullOrWhiteSpace(a.nickname))
        {
            Show(nickPage);
            nickInput.text = PlayerPrefs.GetString("memo_author", "");
            return;
        }
        PlayerPrefs.SetString("memo_author", a.nickname); // 메모·HUD 에 쓰는 닉네임
        PlayerPrefs.Save();
        if (string.IsNullOrEmpty(a.gender))
        {
            Show(genderPage);
            return;
        }
        if (!PreferencesComplete(a.preferencesJson)) // 없거나, 새 문항(매칭 상대)에 아직 답하지 않음
        {
            WalkPreferences local = WalkPreferences.Load();
            if (local.IsComplete)
            {
                // 예전에 기기에서 답한 체크리스트가 있으면 그대로 올린다.
                StartCoroutine(AuthService.SavePreferences(local, (_, __) => Finish()));
                return;
            }
            root.SetActive(false); // 체크리스트 화면이 위에 뜬다 (저장하면 서버에도 올린다)
            PreferenceSurvey.Open(Finish);
            return;
        }
        Finish();
    }

    static bool PreferencesComplete(string json)
    {
        if (string.IsNullOrEmpty(json))
            return false;
        try { return JsonUtility.FromJson<WalkPreferences>(json)?.IsComplete ?? false; }
        catch (System.ArgumentException) { return false; }
    }

    void Finish()
    {
        root.SetActive(false);
        UIInputBlocker.SetModal(this, false);
        if (AuthService.Current != null && !string.IsNullOrEmpty(AuthService.Current.nickname))
            Toast($"{AuthService.Current.nickname}님, 반가워요!");
    }

    // ---------- 로그인 ----------

    void LoginWith(string provider)
    {
        if (busy) return;
        busy = true;
        errorText.text = "로그인하는 중…";
        System.Action<string, string> afterNative = (token, error) =>
        {
            if (token == null)
            {
                busy = false;
                errorText.text = error == NativeLogin.Cancelled ? "" : error;
                return;
            }
            StartCoroutine(AuthService.Login(provider, token, (account, err) =>
            {
                busy = false;
                if (account == null) { errorText.text = err; return; }
                Continue(account);
            }));
        };
        if (provider == "kakao") NativeLogin.Kakao(afterNative);
        else if (provider == "apple") NativeLogin.Apple(afterNative);
        StopCoroutine(nameof(LoginTimeout));
        StartCoroutine(nameof(LoginTimeout));
    }

    /// <summary>체험 모드: 서버가 체험 계정을 만들어 준다 (메일 인증 없음, 7일 뒤 삭제).</summary>
    void StartGuest()
    {
        if (busy) return;
        busy = true;
        errorText.text = "체험 계정을 만드는 중…";
        StartCoroutine(AuthService.Login("guest", null, (account, err) =>
        {
            busy = false;
            if (account == null) { errorText.text = err; return; }
            errorText.text = "";
            Continue(account);
        }));
    }

    /// <summary>로그인 앱에서 돌아왔는데 답이 없으면 (중간에 닫는 등) 다시 누를 수 있게 한다.</summary>
    IEnumerator LoginTimeout()
    {
        yield return new WaitForSecondsRealtime(60f);
        if (busy && loginPage.activeSelf)
        {
            busy = false;
            errorText.text = "로그인 응답이 없어요. 다시 눌러 주세요.";
        }
    }

    // ---------- 경희대 메일 ----------

    string EmailAddress
    {
        get
        {
            string id = emailInput.text.Trim();
            int at = id.IndexOf('@');
            if (at >= 0) id = id.Substring(0, at); // "@khu.ac.kr" 까지 써도 된다
            return id.Length > 0 ? $"{id}@{AuthConfig.SchoolDomain}" : "";
        }
    }

    void SendCode()
    {
        if (busy) return;
        if (EmailAddress.Length == 0) { errorText.text = "학교 메일 아이디를 써 주세요."; return; }
        if (Time.unscaledTime < resendAt) return;
        busy = true;
        errorText.text = "";
        UIKit.SetLabel(sendButton, "보내는 중…");
        StartCoroutine(AuthService.StartEmail(EmailAddress, (error, devCode) =>
        {
            busy = false;
            UIKit.SetLabel(sendButton, "다시 받기");
            if (error != null) { errorText.text = error; return; }
            resendAt = Time.unscaledTime + 60f;
            codeRow.SetActive(true);
            codeInput.text = devCode ?? "";
            codeHint.text = devCode != null
                ? $"개발용 서버라 번호를 바로 채웠어요 ({devCode})"
                : $"{EmailAddress} 로 보낸 6자리 번호를 넣어 주세요. 메일이 안 오면 스팸함도 확인해 주세요.";
            codeInput.ActivateInputField();
        }));
    }

    void VerifyCode()
    {
        if (busy) return;
        string code = codeInput.text.Trim();
        if (code.Length != 6) { errorText.text = "6자리 번호를 넣어 주세요."; return; }
        busy = true;
        errorText.text = "";
        StartCoroutine(AuthService.VerifyEmail(code, (account, error) =>
        {
            busy = false;
            if (account == null) { errorText.text = error; return; }
            Continue(account);
        }));
    }

    void Update()
    {
        if (sendButton != null && emailPage.activeSelf && codeRow.activeSelf)
        {
            float left = resendAt - Time.unscaledTime;
            sendButton.interactable = left <= 0f && !busy;
            if (!busy) UIKit.SetLabel(sendButton, left > 0f ? $"다시 받기 ({Mathf.CeilToInt(left)}초)" : "다시 받기");
        }
    }

    // ---------- 닉네임 ----------

    void SaveNickname()
    {
        if (busy) return;
        string nick = nickInput.text.Trim();
        if (nick.Length is < 1 or > 20) { errorText.text = "닉네임은 1~20자로 써 주세요."; return; }
        busy = true;
        errorText.text = "";
        StartCoroutine(AuthService.SetNickname(nick, (account, error) =>
        {
            busy = false;
            if (account == null) { errorText.text = error; return; }
            Continue(account);
        }));
    }

    // ---------- 성별 ----------

    void SaveGender(string gender)
    {
        if (busy) return;
        busy = true;
        errorText.text = "";
        StartCoroutine(AuthService.SetGender(gender, (account, error) =>
        {
            busy = false;
            if (account == null) { errorText.text = error; return; }
            Continue(account);
        }));
    }

    // ---------- 화면 ----------

    void Show(GameObject page)
    {
        foreach (GameObject p in new[] { checking, loginPage, emailPage, nickPage, genderPage, privacyPage })
            p.SetActive(p == page);
    }

    static void Toast(string message)
    {
        var hud = FindAnyObjectByType<GameHUD>();
        if (hud != null) hud.Toast(message);
    }

    void OnDisable() => UIInputBlocker.SetModal(this, false);

    void Build()
    {
        Canvas canvas = UIKit.CreateCanvas(transform, "LoginCanvas", 60);
        root = UIKit.Image(canvas.transform, "Login", Color.white, raycast: true).gameObject;
        UIKit.Stretch((RectTransform)root.transform);
        // 위쪽 버건디 띠 (학교 색)
        var bg = new GameObject("Band", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        bg.transform.SetParent(root.transform, false);
        bg.texture = HudIcons.Gradient(new Color(0.62f, 0.16f, 0.24f), new Color(0.36f, 0.07f, 0.13f));
        bg.raycastTarget = false;
        bg.rectTransform.anchorMin = new Vector2(0f, 0.76f); // 위 띠를 짧게: 키보드가 올라와도 입력칸이 가려지지 않게
        bg.rectTransform.anchorMax = Vector2.one;
        bg.rectTransform.offsetMin = bg.rectTransform.offsetMax = Vector2.zero;

        Rect sa = Screen.safeArea;
        RectTransform safe = UIKit.Rect(root.transform, "Safe");
        safe.anchorMin = new Vector2(sa.xMin / Mathf.Max(1, Screen.width), sa.yMin / Mathf.Max(1, Screen.height));
        safe.anchorMax = new Vector2(sa.xMax / Mathf.Max(1, Screen.width), sa.yMax / Mathf.Max(1, Screen.height));
        safe.offsetMin = safe.offsetMax = Vector2.zero;

        Text brand = UIKit.Text(safe, "KUCA", 120, TextAnchor.MiddleCenter, Color.white);
        brand.fontStyle = FontStyle.Bold;
        Box(brand.rectTransform, 0f, 90f, 0f, 150f);
        Text tag = UIKit.Text(safe, "경희대 국제캠퍼스를 함께 걷는 산책 동반자", 34, TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, 0.85f));
        Box(tag.rectTransform, 0f, 245f, 0f, 60f);

        RectTransform panel = UIKit.Rect(safe, "Panel");
        panel.anchorMin = Vector2.zero;
        panel.anchorMax = new Vector2(1f, 0.74f);
        panel.offsetMin = panel.offsetMax = Vector2.zero;

        errorText = UIKit.Text(safe, "", 30, TextAnchor.MiddleCenter, ErrorRed);
        errorText.rectTransform.anchorMin = new Vector2(0f, 0f);
        errorText.rectTransform.anchorMax = new Vector2(1f, 0f);
        errorText.rectTransform.pivot = new Vector2(0.5f, 0f);
        errorText.rectTransform.offsetMin = new Vector2(60f, 40f);
        errorText.rectTransform.offsetMax = new Vector2(-60f, 140f);

        // 확인 중
        checking = UIKit.Rect(panel, "Checking").gameObject;
        UIKit.Stretch((RectTransform)checking.transform);
        checkingText = UIKit.Text(checking.transform, "", 34, TextAnchor.MiddleCenter, InkQuiet);
        Box(checkingText.rectTransform, 60f, 160f, 60f, 120f);
        retryButton = Pill(checking.transform, "다시 시도", Accent, Color.white, StartFlow, 320f);

        // 로그인
        loginPage = UIKit.Rect(panel, "LoginPage").gameObject;
        UIKit.Stretch((RectTransform)loginPage.transform);
        Text welcome = UIKit.Text(loginPage.transform, "시작하기", 52, TextAnchor.MiddleLeft, Ink);
        welcome.fontStyle = FontStyle.Bold;
        Box(welcome.rectTransform, 60f, 90f, 60f, 80f);
        Text sub = UIKit.Text(loginPage.transform, "로그인한 뒤 경희대 메일 인증을 한 번 해요.", 30, TextAnchor.MiddleLeft, InkQuiet);
        Box(sub.rectTransform, 60f, 170f, 60f, 50f);
        Button kakao = Pill(loginPage.transform, "카카오로 시작하기", KakaoYellow, new Color(0.1f, 0.1f, 0.1f), () => LoginWith("kakao"), 280f);
        AddIcon(kakao, HudIcons.Bubble, new Color(0.1f, 0.1f, 0.1f));
        float y = 412f;
        if (AuthConfig.AppleLoginEnabled)
        {
            Pill(loginPage.transform, "Apple로 계속하기", Color.black, Color.white, () => LoginWith("apple"), y);
            y += 132f;
        }
        Button guest = UIKit.Button(loginPage.transform, "로그인 없이 둘러보기", StartGuest, new Color(0f, 0f, 0f, 0f), 34);
        guest.GetComponentInChildren<Text>().color = Ink;
        Box((RectTransform)guest.transform, 60f, y + 10f, 60f, 90f);
        Text guestHint = UIKit.Text(loginPage.transform, "체험 계정은 7일 뒤 지워져요. 나중에 로그인해도 진행은 이어져요.", 26, TextAnchor.MiddleCenter, InkQuiet);
        Box(guestHint.rectTransform, 60f, y + 92f, 60f, 50f);
        y += 170f;
#if UNITY_EDITOR
        // 에디터에는 카카오·Apple 이 없으니 테스트용으로 건너뛸 수 있게 한다 (빌드에는 없음).
        Button skip = UIKit.Button(loginPage.transform, "에디터: 로그인 건너뛰기", Finish, new Color(0f, 0f, 0f, 0f), 30);
        skip.GetComponentInChildren<Text>().color = InkQuiet;
        Box((RectTransform)skip.transform, 60f, y + 10f, 60f, 80f);
#endif

        // 경희대 메일 인증
        emailPage = UIKit.Rect(panel, "EmailPage").gameObject;
        UIKit.Stretch((RectTransform)emailPage.transform);
        Text et = UIKit.Text(emailPage.transform, "경희대 메일 인증", 52, TextAnchor.MiddleLeft, Ink);
        et.fontStyle = FontStyle.Bold;
        Box(et.rectTransform, 60f, 60f, 60f, 80f);
        emailHint = UIKit.Text(emailPage.transform, "", 28, TextAnchor.UpperLeft, InkQuiet);
        Box(emailHint.rectTransform, 60f, 140f, 60f, 80f);
        emailInput = Input(emailPage.transform, "학교 메일 아이디", 220f, 60f, 330f);
        emailInput.contentType = InputField.ContentType.EmailAddress;
        emailInput.characterLimit = 64;
        Text domain = UIKit.Text(emailPage.transform, "@" + AuthConfig.SchoolDomain, 36, TextAnchor.MiddleLeft, Ink);
        domain.rectTransform.anchorMin = domain.rectTransform.anchorMax = new Vector2(1f, 1f);
        domain.rectTransform.pivot = new Vector2(1f, 1f);
        domain.rectTransform.anchoredPosition = new Vector2(-60f, -220f);
        domain.rectTransform.sizeDelta = new Vector2(260f, 110f);
        sendButton = Pill(emailPage.transform, "인증번호 받기", Burgundy, Color.white, SendCode, 350f);
        codeRow = UIKit.Rect(emailPage.transform, "Code").gameObject;
        UIKit.Stretch((RectTransform)codeRow.transform);
        codeInput = Input(codeRow.transform, "6자리 번호", 490f, 60f, 60f);
        codeInput.contentType = InputField.ContentType.IntegerNumber;
        codeInput.characterLimit = 6;
        codeHint = UIKit.Text(codeRow.transform, "", 26, TextAnchor.UpperLeft, InkQuiet);
        Box(codeHint.rectTransform, 60f, 606f, 60f, 70f);
        verifyButton = Pill(codeRow.transform, "확인", Accent, Color.white, VerifyCode, 680f);

        // 닉네임
        nickPage = UIKit.Rect(panel, "NickPage").gameObject;
        UIKit.Stretch((RectTransform)nickPage.transform);
        Text nt = UIKit.Text(nickPage.transform, "닉네임을 정해 주세요", 52, TextAnchor.MiddleLeft, Ink);
        nt.fontStyle = FontStyle.Bold;
        Box(nt.rectTransform, 60f, 60f, 60f, 80f);
        Text nh = UIKit.Text(nickPage.transform, "메모판과 산책메이트에서 보이는 이름이에요 (1~20자).", 28, TextAnchor.UpperLeft, InkQuiet);
        Box(nh.rectTransform, 60f, 140f, 60f, 60f);
        nickInput = Input(nickPage.transform, "닉네임", 220f, 60f, 60f);
        nickInput.characterLimit = 20;
        nickButton = Pill(nickPage.transform, "다음", Accent, Color.white, SaveNickname, 360f);

        // 성별 (필수)
        genderPage = UIKit.Rect(panel, "GenderPage").gameObject;
        UIKit.Stretch((RectTransform)genderPage.transform);
        Text gt = UIKit.Text(genderPage.transform, "성별을 알려 주세요", 52, TextAnchor.MiddleLeft, Ink);
        gt.fontStyle = FontStyle.Bold;
        Box(gt.rectTransform, 60f, 60f, 60f, 80f);
        Text gh = UIKit.Text(genderPage.transform, "상대에게 보이지 않아요. 산책메이트 '동성만' 조건에만 써요.", 28, TextAnchor.UpperLeft, InkQuiet);
        Box(gh.rectTransform, 60f, 140f, 60f, 60f);
        Pill(genderPage.transform, "남자", Field, Ink, () => SaveGender("male"), 240f);
        Pill(genderPage.transform, "여자", Field, Ink, () => SaveGender("female"), 380f);

        BuildPrivacy(panel);

        root.SetActive(false);
    }

    /// <summary>개인정보 안내: 무엇을 왜 받고 언제 지우는지</summary>
    void BuildPrivacy(RectTransform panel)
    {
        privacyPage = UIKit.Rect(panel, "PrivacyPage").gameObject;
        UIKit.Stretch((RectTransform)privacyPage.transform);
        Text pt = UIKit.Text(privacyPage.transform, "개인정보 안내", 52, TextAnchor.MiddleLeft, Ink);
        pt.fontStyle = FontStyle.Bold;
        Box(pt.rectTransform, 60f, 50f, 60f, 80f);
        Text ph = UIKit.Text(privacyPage.transform, "KUCA 가 받는 정보와 쓰는 곳, 보관 기간이에요.", 28, TextAnchor.UpperLeft, InkQuiet);
        Box(ph.rectTransform, 60f, 130f, 60f, 50f);

        (string item, string use, string keep)[] rows =
        {
            ("경희대 메일", "경희대 학생인지 확인", "탈퇴할 때까지"),
            ("성별", "산책메이트 '동성만' 조건에만 (상대에게 안 보임)", "탈퇴할 때까지"),
            ("산책 취향", "산책 추천 · 산책메이트 찾기", "탈퇴할 때까지"),
            ("위치", "건물 단위로만, 산책 추천 · 매칭에", "산책이 끝나면 삭제"),
            ("인상착의", "만나기로 한 상대에게만", "산책이 끝나면 삭제"),
            ("사진 · 메모", "경희스팟 메모판에 공개", "직접 지울 때까지"),
            ("체험 계정", "로그인 없이 둘러보기", "만든 지 7일 뒤 삭제"),
        };
        float top = 200f;
        foreach (var (item, use, keep) in rows)
        {
            Image row = UIKit.Image(privacyPage.transform, item, Field);
            row.sprite = HudIcons.Rounded;
            row.type = Image.Type.Sliced;
            row.pixelsPerUnitMultiplier = 0.8f;
            Box(row.rectTransform, 60f, top, 60f, 116f);
            Text name = UIKit.Text(row.transform, item, 32, TextAnchor.UpperLeft, Ink);
            name.fontStyle = FontStyle.Bold;
            Box(name.rectTransform, 32f, 18f, 32f, 44f);
            Text k = UIKit.Text(row.transform, keep, 26, TextAnchor.UpperRight, Burgundy);
            Box(k.rectTransform, 32f, 22f, 32f, 40f);
            Text u = UIKit.Text(row.transform, use, 26, TextAnchor.UpperLeft, InkQuiet);
            Box(u.rectTransform, 32f, 64f, 32f, 40f);
            top += 128f;
        }
        Text foot = UIKit.Text(privacyPage.transform, "설정 › 회원 탈퇴로 계정과 정보를 언제든 지울 수 있어요.", 26, TextAnchor.UpperLeft, InkQuiet);
        Box(foot.rectTransform, 60f, top + 6f, 60f, 50f);
        privacyButton = Pill(privacyPage.transform, "확인하고 시작", Burgundy, Color.white, ConfirmPrivacy, top + 80f);
    }

    InputField Input(Transform parent, string placeholder, float top, float left, float right)
    {
        InputField f = UIKit.Input(parent, placeholder, 38, false, 64);
        Image bg = f.GetComponent<Image>();
        bg.color = Field;
        bg.sprite = HudIcons.Rounded;
        bg.type = Image.Type.Sliced;
        bg.pixelsPerUnitMultiplier = 0.8f;
        f.textComponent.color = Ink;
        ((Text)f.placeholder).color = InkQuiet;
        ((Text)f.placeholder).fontStyle = FontStyle.Normal;
        Box((RectTransform)f.transform, left, top, right, 110f);
        return f;
    }

    static Button Pill(Transform parent, string label, Color face, Color ink, UnityEngine.Events.UnityAction onClick, float top)
    {
        Button b = UIKit.Button(parent, label, onClick, face, 40);
        Image img = b.GetComponent<Image>();
        img.sprite = HudIcons.Pill;
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = 120f / 116f;
        Text t = b.GetComponentInChildren<Text>();
        t.color = ink;
        t.fontStyle = FontStyle.Bold;
        Box((RectTransform)b.transform, 60f, top, 60f, 116f);
        b.gameObject.AddComponent<PressScale>();
        return b;
    }

    static void AddIcon(Button b, Sprite icon, Color color)
    {
        Image ic = UIKit.Image(b.transform, "Icon", color);
        ic.sprite = icon;
        ic.rectTransform.anchorMin = ic.rectTransform.anchorMax = ic.rectTransform.pivot = new Vector2(0f, 0.5f);
        ic.rectTransform.anchoredPosition = new Vector2(48f, 0f);
        ic.rectTransform.sizeDelta = new Vector2(56f, 56f);
    }

    static void Box(RectTransform rt, float left, float top, float right, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(left, -top - height);
        rt.offsetMax = new Vector2(-right, -top);
    }
}
