using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 게임 HUD.
/// - 왼쪽 위: 캐릭터 얼굴(빛나는 고리), 레벨, 다음 레벨까지 남은 XP, 닉네임. 누르면 프로필.
/// - 오른쪽 위: 원형 버튼 줄 (나침반=북쪽 보기, 도구, 꾸미기, 설정).
/// - 왼쪽 아래: 공지사항 카드 (최근 공지 제목, 안 읽은 수).
/// - 가운데 아래: 경희 로고 버튼 (몬스터볼 자리) → 메인 메뉴 (도감 · 경희몬 · 도구).
/// - 오른쪽 아래: 주변에 있는 수집 대상 6칸.
/// 캔버스와 화면은 실행 시 코드로 만든다. 경험치는 GameProgress.score 를 그대로 쓴다.
/// </summary>
public class GameHUD : MonoBehaviour
{
    public float toastDuration = 2.5f;

    [Header("Content")]
    [Tooltip("가운데 도감 버튼에 쓸 경희 로고")]
    public Texture2D logo;
    [Tooltip("공지사항 목록 JSON ({\"notices\":[{id,date,title,body}]})")]
    public TextAsset noticesJson;
    [Tooltip("프로필 얼굴을 찍을 캐릭터. 비우면 씬에서 찾는다.")]
    public ModularCharacter character;
    [Tooltip("프로필을 눌렀을 때 열 캐릭터 꾸미기. 비우면 씬에서 찾는다.")]
    public CharacterCustomizer customizer;
    [Tooltip("닉네임을 정하지 않았을 때 보여 줄 이름")]
    public string defaultNickname = "경희 탐험가";
    [Tooltip("주변 칸에 보여 줄 거리 (m)")]
    public float nearbyRadius = 250f;

    [Header("Portrait")]
    [Tooltip("얼굴을 밝히는 보조광 세기 (밤 장면에서도 얼굴이 보이도록)")]
    public float portraitLightIntensity = 1.5f;
    [Tooltip("얼굴 카메라가 바라보는 높이 (캐릭터 로컬 단위, 키 약 1.7)")]
    public float portraitLookHeight = 1.6f;
    public float portraitDistance = 2.0f;
    public float portraitFov = 22f;

    static readonly Color Navy = new Color(0.11f, 0.16f, 0.39f, 1f);
    static readonly Color Crimson = new Color(0.86f, 0.22f, 0.25f, 1f);
    static readonly Color Gold = new Color(0.85f, 0.72f, 0.45f, 1f);
    static readonly Color Glow = new Color(0.45f, 0.80f, 1f, 1f);
    static readonly Color GlassFill = new Color(0.06f, 0.10f, 0.20f, 0.78f);
    static readonly Color GlassEdge = new Color(0.55f, 0.82f, 1f, 0.55f);
    static readonly Color XpPink = new Color(0.93f, 0.45f, 0.62f, 1f);
    static readonly Color PortraitSky = new Color(0.62f, 0.80f, 0.96f, 1f);

    const float Margin = 36f;
    // 아래쪽 줄은 가로가 좁은 폰(iPhone 등, 약 980 단위)에서도 겹치지 않게 작게 잡는다.
    const float BottomSide = 22f;
    const float BottomGap = 28f;
    const string NicknameKey = "memo_author"; // MemoBoard 와 같은 키
    const string ReadNoticesKey = "notice_read_ids";

    Canvas canvas;
    RectTransform safe;
    Rect appliedSafeArea;
    RectTransform hud;
    Text toastText;
    Image toastBg;
    float toastTimer;

    // 프로필
    Text levelText, nextText, nicknameText;
    RectTransform xpFill;
    RawImage portraitImage;
    CanvasGroup xpPopup;
    Text xpPopupText;
    float xpPopupTimer;

    // 버튼
    RectTransform compassNeedle;
    RectTransform logoGlow;
    Text noticeTitle;
    GameObject noticeBadge;
    Text noticeBadgeText;
    readonly List<RawImage> nearbyIcons = new List<RawImage>();
    float nearbyTimer;

    // 전체 화면
    GameObject noticeScreen, settingsScreen;
    RectTransform noticeList, settingsList;
    InputField nicknameInput;
    Button logoutButton, deleteAccountButton;
    float deleteConfirmUntil; // 회원 탈퇴는 4초 안에 한 번 더 눌러야 한다
    InputField serverInput;
    Text serverStatus;

    Camera portraitCam;
    Light portraitLight;
    RenderTexture portraitTexture;

    KhuMenus menus;
    CameraFollow cameraFollow;
    CollectController collect;

    GameProgress progress;
    IList<CollectibleType> types;
    int lastScore = -1;
    NoticeData notices;
    HashSet<string> readNotices;

    [Serializable]
    public class Notice
    {
        public string id;
        public string date;
        public string title;
        public string body;
    }

    [Serializable]
    class NoticeData { public List<Notice> notices = new List<Notice>(); }

    [Serializable]
    class IdList { public List<string> ids = new List<string>(); }

    void Awake()
    {
        canvas = UIKit.CreateCanvas(transform, "HUDCanvas", 10);
        safe = UIKit.Rect(canvas.transform, "SafeArea");
        UIKit.Stretch(safe);
        ApplySafeArea();

        hud = UIKit.Rect(safe, "HUD");
        UIKit.Stretch(hud);

        LoadNotices();
        BuildProfile();
        BuildSideButtons();
        BuildNoticeCard();
        BuildLogoButton();
        BuildNearby();

        toastBg = UIKit.Image(safe, "Toast", new Color(0.04f, 0.07f, 0.15f, 0.85f));
        toastBg.sprite = HudIcons.Pill;
        toastBg.type = Image.Type.Sliced;
        toastBg.pixelsPerUnitMultiplier = 1.2f;
        RectTransform tr = toastBg.rectTransform;
        tr.anchorMin = tr.anchorMax = tr.pivot = new Vector2(0.5f, 0f);
        tr.anchoredPosition = new Vector2(0f, 420f);
        tr.sizeDelta = new Vector2(940f, 104f);
        toastText = UIKit.Text(toastBg.transform, "", 38, TextAnchor.MiddleCenter);
        UIKit.Stretch(toastText.rectTransform, 30f, 10f);
        toastBg.gameObject.SetActive(false);

        noticeScreen = BuildScreen("NoticeScreen", "공지사항", CloseNotices, out _, out noticeList);
        settingsScreen = BuildScreen("SettingsScreen", "설정", CloseSettings, out _, out settingsList);
        BuildSettings();
        RefreshNotices();

        menus = gameObject.AddComponent<KhuMenus>();
        menus.Build(this, canvas.transform, safe);
    }

    void Start()
    {
        // 로그인 (필수): 카카오/Apple → 경희대 메일 인증 → 닉네임 → 산책 취향 체크리스트
        LoginScreen.Begin();
        if (character == null)
            character = FindAnyObjectByType<ModularCharacter>();
        if (customizer == null)
            customizer = FindAnyObjectByType<CharacterCustomizer>();
        cameraFollow = FindAnyObjectByType<CameraFollow>();
        collect = FindAnyObjectByType<CollectController>();
        BuildPortraitCamera();
    }

    void OnDestroy()
    {
        UIInputBlocker.SetModal(noticeScreen, false);
        UIInputBlocker.SetModal(settingsScreen, false);
        if (portraitTexture != null)
            portraitTexture.Release();
    }

    void Update()
    {
        ApplySafeArea();

        if (toastTimer > 0f)
        {
            toastTimer -= Time.unscaledDeltaTime;
            if (toastTimer <= 0f)
                toastBg.gameObject.SetActive(false);
        }

        if (xpPopupTimer > 0f)
        {
            xpPopupTimer -= Time.unscaledDeltaTime;
            xpPopup.alpha = Mathf.Clamp01(xpPopupTimer / 0.4f);
            if (xpPopupTimer <= 0f)
                xpPopup.gameObject.SetActive(false);
        }

        // 꾸미기·메모판·도감 같은 전체 화면이 열려 있으면 HUD 를 숨긴다.
        bool visible = !UIInputBlocker.IsModalOpen;
        if (hud.gameObject.activeSelf != visible)
            hud.gameObject.SetActive(visible);
        if (portraitCam != null)
            portraitCam.enabled = portraitLight.enabled = visible;
        if (!visible)
            return;

        nicknameText.text = Nickname;

        if (compassNeedle != null && cameraFollow != null)
            compassNeedle.localRotation = Quaternion.Euler(0f, 0f, cameraFollow.yaw);

        // 로고 뒤 빛 고리가 천천히 숨 쉬듯 커졌다 작아진다.
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 2.2f);
        logoGlow.localScale = Vector3.one * (1f + 0.05f * pulse);
        logoGlow.GetComponent<Image>().color = new Color(Glow.r, Glow.g, Glow.b, 0.65f + 0.35f * pulse);

        nearbyTimer -= Time.unscaledDeltaTime;
        if (nearbyTimer <= 0f)
        {
            nearbyTimer = 0.5f;
            RefreshNearby();
        }
    }

    void LateUpdate()
    {
        if (portraitCam == null || !portraitCam.enabled || character == null)
            return;
        // 캐릭터 정면에서 얼굴 높이를 바라본다 (캐릭터가 돌아도 늘 얼굴이 보인다).
        Transform c = character.transform;
        Vector3 look = c.TransformPoint(0f, portraitLookHeight, 0f);
        Vector3 pos = c.TransformPoint(0f, portraitLookHeight + 0.1f, portraitDistance);
        portraitCam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(look - pos, Vector3.up));
        portraitLight.transform.position = c.TransformPoint(0.4f, portraitLookHeight + 0.5f, portraitDistance * 0.8f);
    }

    public void Toast(string message)
    {
        if (toastText == null)
            return;
        toastText.text = message;
        toastBg.gameObject.SetActive(true);
        toastTimer = toastDuration;
    }

    public void ShowProgress(GameProgress progress, IList<CollectibleType> types)
    {
        if (progress == null || levelText == null)
            return;
        this.progress = progress;
        this.types = types;

        LevelOf(progress.score, out int level, out int xpInLevel, out int xpNeeded);
        levelText.text = $"<size=30>LV</size> {level}";
        nextText.text = $"다음 레벨까지 {xpNeeded - xpInLevel} XP";
        xpFill.anchorMax = new Vector2(xpNeeded > 0 ? (float)xpInLevel / xpNeeded : 0f, 1f);

        if (lastScore >= 0 && progress.score > lastScore)
            ShowXpPopup(progress.score - lastScore);
        lastScore = progress.score;

        ShowEgg(progress);
        if (menus != null)
            menus.OnProgressChanged(progress, types);
    }

    // ---------- 걷기 보상: 알 게이지 (닉네임 아래) ----------

    Text eggText;
    RectTransform eggFill;

    /// <summary>품은 알이 얼마나 자랐는지 (1km 마다 부화, 하루 3번)</summary>
    public void ShowEgg(GameProgress progress)
    {
        if (progress == null || eggText == null)
            return;
        bool doneToday = progress.HatchedToday >= GameProgress.EggsPerDay;
        float m = progress.eggMeters;
        eggText.text = doneToday
            ? $"오늘 알 {GameProgress.EggsPerDay}개 다 깼어요"
            : $"{m:N0} / {GameProgress.EggMeters:N0}m";
        eggFill.anchorMax = new Vector2(doneToday ? 1f : Mathf.Clamp01(m / GameProgress.EggMeters), 1f);
        eggFill.GetComponent<Image>().color = doneToday ? new Color(1f, 1f, 1f, 0.35f) : new Color(1f, 0.85f, 0.4f);
    }

    /// <summary>알이 깨어나는 연출 (KhuMenus 가 부른다)</summary>
    public void ShowHatch(string species, string tier, int hatchedToday) => HatchScreen.Show(species, tier, hatchedToday);

    /// <summary>레벨 n 에서 n+1 로 가려면 100 + (n-1)*50 XP 가 필요하다.</summary>
    public static void LevelOf(int score, out int level, out int xpInLevel, out int xpNeeded)
    {
        level = 1;
        xpInLevel = Mathf.Max(0, score);
        xpNeeded = 100;
        while (xpInLevel >= xpNeeded)
        {
            xpInLevel -= xpNeeded;
            level++;
            xpNeeded = 100 + (level - 1) * 50;
        }
    }

    public string Nickname
    {
        get
        {
            string n = PlayerPrefs.GetString(NicknameKey, "").Trim();
            return n.Length > 0 ? n : defaultNickname;
        }
    }

    /// <summary>노치·홈 표시줄을 피하도록 Screen.safeArea 에 맞춘다.</summary>
    void ApplySafeArea()
    {
        Rect area = Screen.safeArea;
        if (area == appliedSafeArea || Screen.width <= 0 || Screen.height <= 0)
            return;
        appliedSafeArea = area;
        safe.anchorMin = new Vector2(area.xMin / Screen.width, area.yMin / Screen.height);
        safe.anchorMax = new Vector2(area.xMax / Screen.width, area.yMax / Screen.height);
        safe.offsetMin = safe.offsetMax = Vector2.zero;
    }

    // ---------- 왼쪽 위: 프로필 ----------

    void BuildProfile()
    {
        const float avatar = 176f;
        RectTransform root = UIKit.Rect(hud, "Profile");
        Place(root, new Vector2(0f, 1f), new Vector2(Margin, -Margin));
        root.sizeDelta = new Vector2(560f, 280f);
        Vector2 center = new Vector2(avatar * 0.5f + 8f, -avatar * 0.5f - 8f);

        // 레벨 카드 (얼굴 뒤에서 오른쪽으로 뻗는 유리 알약)
        Image card = Glass(root, "LevelCard", new Vector2(430f, 128f), 64f);
        Place(card.rectTransform.parent as RectTransform, new Vector2(0f, 1f), new Vector2(center.x, center.y + 64f));
        levelText = UIKit.Text(card.transform, "<size=30>LV</size> 1", 58, TextAnchor.MiddleLeft);
        levelText.fontStyle = FontStyle.Bold;
        levelText.horizontalOverflow = HorizontalWrapMode.Overflow;
        SetBox(levelText.rectTransform, 124f, 0.42f, 1f, 24f);
        nextText = UIKit.Text(card.transform, "", 26, TextAnchor.UpperLeft, new Color(0.8f, 0.9f, 1f, 0.85f));
        nextText.horizontalOverflow = HorizontalWrapMode.Overflow;
        SetBox(nextText.rectTransform, 126f, 0.14f, 0.42f, 24f);
        Image bar = UIKit.Image(card.transform, "XpBar", new Color(1f, 1f, 1f, 0.15f));
        bar.sprite = HudIcons.Pill;
        bar.type = Image.Type.Sliced;
        bar.pixelsPerUnitMultiplier = 12f;
        RectTransform br = bar.rectTransform;
        br.anchorMin = new Vector2(0f, 0f);
        br.anchorMax = new Vector2(1f, 0f);
        br.offsetMin = new Vector2(126f, 14f);
        br.offsetMax = new Vector2(-34f, 24f);
        Image fill = UIKit.Image(bar.transform, "Fill", Glow);
        fill.sprite = HudIcons.Pill;
        fill.type = Image.Type.Sliced;
        fill.pixelsPerUnitMultiplier = 12f;
        xpFill = fill.rectTransform;
        xpFill.anchorMin = Vector2.zero;
        xpFill.anchorMax = new Vector2(0f, 1f);
        xpFill.offsetMin = xpFill.offsetMax = Vector2.zero;

        // 얼굴: 빛 고리 + 남색 테두리 + 둥근 사진. 누르면 꾸미기.
        Button face = InvisibleButton(root, "Avatar", () => menus.OpenProfile(), avatar);
        Place((RectTransform)face.transform, new Vector2(0f, 1f), center);
        RectTransform ft = (RectTransform)face.transform;
        ft.pivot = new Vector2(0.5f, 0.5f);
        UIInputBlocker.Register(ft);
        Image glow = Circle(ft, "Glow", Glow, avatar * 1.42f);
        glow.sprite = HudIcons.GlowRing;
        Place(glow.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        Image rim = Circle(ft, "Rim", Navy, avatar - 6f);
        Place(rim.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        Image mask = Circle(rim.transform, "Mask", PortraitSky, avatar - 22f);
        Place(mask.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        mask.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        portraitImage = new GameObject("Portrait", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        portraitImage.transform.SetParent(mask.transform, false);
        portraitImage.raycastTarget = false;
        UIKit.Stretch(portraitImage.rectTransform);
        portraitImage.enabled = false;

        // 닉네임 알약 (얼굴 아래)
        Image nick = Glass(root, "Nickname", new Vector2(230f, 64f), 32f);
        Place(nick.rectTransform.parent as RectTransform, new Vector2(0f, 1f), new Vector2(center.x - 115f, -avatar - 26f));
        nicknameText = UIKit.Text(nick.transform, defaultNickname, 32, TextAnchor.MiddleCenter);
        nicknameText.fontStyle = FontStyle.Bold;
        UIKit.Stretch(nicknameText.rectTransform, 12f, 0f);

        // 알 게이지 (닉네임 아래): 걸은 만큼 차고, 1km 가 되면 알이 깨어난다
        Image eggPill = Glass(root, "EggGauge", new Vector2(300f, 60f), 30f);
        Place(eggPill.rectTransform.parent as RectTransform, new Vector2(0f, 1f), new Vector2(center.x - 115f, -avatar - 26f - 64f - 12f));
        Image eggIcon = UIKit.Image(eggPill.transform, "Egg", new Color(1f, 0.95f, 0.84f));
        eggIcon.sprite = HudIcons.Egg;
        eggIcon.rectTransform.anchorMin = eggIcon.rectTransform.anchorMax = eggIcon.rectTransform.pivot = new Vector2(0f, 0.5f);
        eggIcon.rectTransform.anchoredPosition = new Vector2(14f, 2f);
        eggIcon.rectTransform.sizeDelta = new Vector2(40f, 40f);
        eggText = UIKit.Text(eggPill.transform, "", 26, TextAnchor.MiddleLeft);
        eggText.fontStyle = FontStyle.Bold;
        eggText.horizontalOverflow = HorizontalWrapMode.Overflow;
        eggText.rectTransform.anchorMin = new Vector2(0f, 0.25f);
        eggText.rectTransform.anchorMax = new Vector2(1f, 1f);
        eggText.rectTransform.offsetMin = new Vector2(62f, 0f);
        eggText.rectTransform.offsetMax = new Vector2(-12f, 0f);
        Image eggBar = UIKit.Image(eggPill.transform, "Bar", new Color(1f, 1f, 1f, 0.15f));
        eggBar.sprite = HudIcons.Pill;
        eggBar.type = Image.Type.Sliced;
        eggBar.pixelsPerUnitMultiplier = 12f;
        eggBar.rectTransform.anchorMin = new Vector2(0f, 0f);
        eggBar.rectTransform.anchorMax = new Vector2(1f, 0f);
        eggBar.rectTransform.offsetMin = new Vector2(62f, 9f);
        eggBar.rectTransform.offsetMax = new Vector2(-18f, 17f);
        Image eggBarFill = UIKit.Image(eggBar.transform, "Fill", new Color(1f, 0.85f, 0.4f));
        eggBarFill.sprite = HudIcons.Pill;
        eggBarFill.type = Image.Type.Sliced;
        eggBarFill.pixelsPerUnitMultiplier = 12f;
        eggFill = eggBarFill.rectTransform;
        eggFill.anchorMin = Vector2.zero;
        eggFill.anchorMax = new Vector2(0f, 1f);
        eggFill.offsetMin = eggFill.offsetMax = Vector2.zero;

        // "+N XP" (레벨 카드 아래 오른쪽에 잠깐)
        Image pill = UIKit.Image(root, "XpPopup", XpPink);
        pill.sprite = HudIcons.Pill;
        pill.type = Image.Type.Sliced;
        pill.pixelsPerUnitMultiplier = 120f / 64f;
        RectTransform pr = pill.rectTransform;
        Place(pr, new Vector2(0f, 1f), new Vector2(center.x + 170f, -avatar - 26f));
        pr.sizeDelta = new Vector2(190f, 64f);
        xpPopupText = UIKit.Text(pill.transform, "", 38, TextAnchor.MiddleCenter);
        xpPopupText.fontStyle = FontStyle.Bold;
        UIKit.Stretch(xpPopupText.rectTransform);
        xpPopup = pill.gameObject.AddComponent<CanvasGroup>();
        xpPopup.blocksRaycasts = false;
        pill.gameObject.SetActive(false);
    }

    // ---------- 오른쪽 위: 원형 버튼 줄 ----------

    void BuildSideButtons()
    {
        const float size = 120f, gap = 26f;
        var buttons = new (string name, Sprite icon, UnityEngine.Events.UnityAction action)[]
        {
            ("Route", HudIcons.Route, RoutePlanner.Open), // 산책 추천·장소 공지 (북쪽 맞추기는 지도 두 번 탭)
            ("Items", HudIcons.Bag, () => menus.OpenCollection(KhuMenus.Tab.Items)),
            ("Album", HudIcons.Photos, OpenAlbum),
            ("Settings", HudIcons.Gear, OpenSettings),
        };
        for (int i = 0; i < buttons.Length; i++)
        {
            RectTransform face = RoundButton(hud, buttons[i].name, size, buttons[i].action);
            Place(face, new Vector2(1f, 1f), new Vector2(-Margin, -Margin - i * (size + gap)));
            if (buttons[i].name == "Album")
            {
                // 내 게시물에 새 좋아요가 오면 빨간 점
                albumBadge = Circle(face, "Badge", new Color(0.93f, 0.25f, 0.33f), 34f);
                Place(albumBadge.rectTransform, new Vector2(1f, 1f), new Vector2(-4f, -4f));
                albumBadge.gameObject.SetActive(false);
            }
            Image icon;
            if (buttons[i].icon != null)
            {
                icon = UIKit.Image(face.Find("Face"), "Icon", Navy);
                icon.sprite = buttons[i].icon;
                icon.rectTransform.sizeDelta = new Vector2(size * 0.52f, size * 0.52f);
            }
            else
            {
                icon = UIKit.Image(face.Find("Face"), "Needle", Color.white);
                icon.sprite = HudIcons.Needle;
                icon.rectTransform.sizeDelta = new Vector2(size * 0.7f, size * 0.7f);
                compassNeedle = icon.rectTransform;
            }
            Place(icon.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        }
    }

    // ---------- 왼쪽 아래: 공지 카드 ----------

    void BuildNoticeCard()
    {
        Button button = InvisibleButton(hud, "NoticeCard", OpenNotices, 0f);
        RectTransform root = (RectTransform)button.transform;
        Place(root, new Vector2(0f, 0f), new Vector2(BottomSide, BottomGap));
        root.sizeDelta = new Vector2(300f, 116f);
        UIInputBlocker.Register(root);

        Image card = Glass(root, "Card", root.sizeDelta, 58f);
        UIKit.Stretch(card.rectTransform.parent as RectTransform);

        Image hex = UIKit.Image(card.transform, "Emblem", Color.white);
        hex.sprite = HudIcons.Hexagon;
        Place(hex.rectTransform, new Vector2(0f, 0.5f), new Vector2(10f, 0f));
        hex.rectTransform.sizeDelta = new Vector2(84f, 84f);
        Image bell = UIKit.Image(hex.transform, "Bell", Gold);
        bell.sprite = HudIcons.Bell;
        Place(bell.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        bell.rectTransform.sizeDelta = new Vector2(44f, 44f);

        Image badge = Circle(hex.transform, "Badge", Crimson, 38f);
        Place(badge.rectTransform, new Vector2(1f, 1f), new Vector2(8f, 6f));
        noticeBadgeText = UIKit.Text(badge.transform, "", 22, TextAnchor.MiddleCenter);
        noticeBadgeText.fontStyle = FontStyle.Bold;
        UIKit.Stretch(noticeBadgeText.rectTransform);
        noticeBadge = badge.gameObject;

        Text title = UIKit.Text(card.transform, "공지사항", 32, TextAnchor.LowerLeft);
        title.fontStyle = FontStyle.Bold;
        SetBox(title.rectTransform, 106f, 0.5f, 0.92f, 40f);
        noticeTitle = UIKit.Text(card.transform, "", 23, TextAnchor.UpperLeft, new Color(0.8f, 0.9f, 1f, 0.85f));
        noticeTitle.horizontalOverflow = HorizontalWrapMode.Overflow;
        SetBox(noticeTitle.rectTransform, 108f, 0.1f, 0.48f, 40f);

        Image chevron = UIKit.Image(card.transform, "Chevron", new Color(1f, 1f, 1f, 0.85f));
        chevron.sprite = HudIcons.Chevron;
        Place(chevron.rectTransform, new Vector2(1f, 0.5f), new Vector2(-12f, 0f));
        chevron.rectTransform.sizeDelta = new Vector2(30f, 30f);
    }

    // ---------- 가운데 아래: 경희 로고 ----------

    void BuildLogoButton()
    {
        const float size = 188f;
        Button button = InvisibleButton(hud, "LogoButton", () => menus.OpenMainMenu(), size);
        RectTransform root = (RectTransform)button.transform;
        Place(root, new Vector2(0.5f, 0f), new Vector2(0f, BottomGap + 6f));
        UIInputBlocker.Register(root);

        Image glow = Circle(root, "Glow", Glow, size * 1.42f);
        glow.sprite = HudIcons.GlowRing;
        Place(glow.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        logoGlow = glow.rectTransform;
        Image shadow = Circle(root, "Shadow", new Color(0f, 0f, 0f, 0.3f), size);
        Place(shadow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -6f));
        Image ring = Circle(root, "Ring", Gold, size);
        Place(ring.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        Image face = Circle(ring.transform, "Face", Color.white, size - 12f);
        Place(face.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        // 받을 퀘스트 보상이 있으면 빨간 점
        menuBadge = Circle(root, "Badge", new Color(0.93f, 0.25f, 0.33f), 46f);
        Place(menuBadge.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(size * 0.36f, size * 0.36f));
        menuBadge.gameObject.SetActive(false);

        if (logo != null)
        {
            var raw = new GameObject("Logo", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            raw.transform.SetParent(face.transform, false);
            raw.texture = logo;
            raw.raycastTarget = false;
            // 가로로 긴 교표가 원 안에 들어가도록 지름의 80% 폭으로 맞춘다.
            float w = (size - 12f) * 0.8f;
            Place(raw.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 3f));
            raw.rectTransform.sizeDelta = new Vector2(w, w * logo.height / logo.width);
        }
        else
        {
            Text t = UIKit.Text(face.transform, "경희", 60, TextAnchor.MiddleCenter, Crimson);
            t.fontStyle = FontStyle.Bold;
            UIKit.Stretch(t.rectTransform);
        }
    }

    // ---------- 오른쪽 아래: 주변 ----------

    void BuildNearby()
    {
        const float cell = 78f, gap = 9f, pad = 13f;
        Vector2 size = new Vector2(cell * 3f + gap * 2f + pad * 2f, cell * 2f + gap + pad * 2f);
        Button button = InvisibleButton(hud, "Nearby", ShowNearbyToast, 0f);
        RectTransform root = (RectTransform)button.transform;
        Place(root, new Vector2(1f, 0f), new Vector2(-BottomSide, BottomGap));
        root.sizeDelta = size;
        UIInputBlocker.Register(root);

        Image panel = Glass(root, "Panel", size, 30f);
        UIKit.Stretch(panel.rectTransform.parent as RectTransform);
        for (int i = 0; i < 6; i++)
        {
            int col = i % 3, row = i / 3;
            Image slot = UIKit.Image(panel.transform, "Slot" + i, new Color(1f, 1f, 1f, 0.1f));
            slot.sprite = HudIcons.Rounded;
            slot.type = Image.Type.Sliced;
            RectTransform sr = slot.rectTransform;
            sr.anchorMin = sr.anchorMax = sr.pivot = new Vector2(0f, 1f);
            sr.anchoredPosition = new Vector2(pad - 3f + col * (cell + gap), -(pad - 3f) - row * (cell + gap));
            sr.sizeDelta = new Vector2(cell, cell);
            // 동물 피규어 썸네일 (칸보다 살짝 크게 그려 피규어가 꽉 차 보이게)
            var icon = new GameObject("Icon", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            icon.transform.SetParent(slot.transform, false);
            icon.raycastTarget = false;
            Place(icon.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
            icon.rectTransform.sizeDelta = new Vector2(cell * 1.05f, cell * 1.05f);
            icon.gameObject.SetActive(false);
            nearbyIcons.Add(icon);
        }

        Image tag = UIKit.Image(root, "Tag", Navy);
        tag.sprite = HudIcons.Pill;
        tag.type = Image.Type.Sliced;
        tag.pixelsPerUnitMultiplier = 120f / 38f;
        Place(tag.rectTransform, new Vector2(0f, 1f), new Vector2(14f, 20f));
        tag.rectTransform.sizeDelta = new Vector2(92f, 38f);
        Text t = UIKit.Text(tag.transform, "주변", 22, TextAnchor.MiddleCenter);
        t.fontStyle = FontStyle.Bold;
        UIKit.Stretch(t.rectTransform);
    }

    readonly List<Collectible> nearbyBuffer = new List<Collectible>();

    void RefreshNearby()
    {
        nearbyBuffer.Clear();
        if (collect != null && collect.spawner != null && collect.campusMap != null && collect.campusMap.player != null)
        {
            Vector3 p = collect.campusMap.player.position;
            foreach (var c in collect.spawner.Active)
                if (c != null && CollectibleSpawner.HorizontalDistance(c.transform.position, p) <= nearbyRadius)
                    nearbyBuffer.Add(c);
            nearbyBuffer.Sort((a, b) =>
                CollectibleSpawner.HorizontalDistance(a.transform.position, p).CompareTo(CollectibleSpawner.HorizontalDistance(b.transform.position, p)));
        }
        for (int i = 0; i < nearbyIcons.Count; i++)
        {
            RawImage icon = nearbyIcons[i];
            Texture tex = i < nearbyBuffer.Count ? MonsterThumbnails.Get(nearbyBuffer[i].SpeciesId) : null;
            icon.gameObject.SetActive(tex != null);
            icon.texture = tex;
        }
    }

    void ShowNearbyToast()
    {
        if (nearbyBuffer.Count == 0)
        {
            Toast($"{nearbyRadius:F0}m 안에 경희몬이 없어요");
            return;
        }
        Collectible c = nearbyBuffer[0];
        float d = CollectibleSpawner.HorizontalDistance(c.transform.position, collect.campusMap.player.position);
        Toast($"주변 {nearbyBuffer.Count}개 · 가장 가까운 {c.DisplayName} {d:F0}m");
    }

    void BuildPortraitCamera()
    {
        if (character == null)
            return;
        portraitTexture = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32) { name = "HUDPortrait", antiAliasing = 2 };
        var camGo = new GameObject("HUDPortraitCamera", typeof(Camera));
        camGo.transform.SetParent(transform, false);
        portraitCam = camGo.GetComponent<Camera>();
        portraitCam.targetTexture = portraitTexture;
        portraitCam.clearFlags = CameraClearFlags.SolidColor;
        portraitCam.backgroundColor = PortraitSky;
        portraitCam.cullingMask = 1 << character.gameObject.layer;
        portraitCam.fieldOfView = portraitFov;
        portraitCam.nearClipPlane = 1f;
        portraitCam.farClipPlane = 200f;
        portraitImage.texture = portraitTexture;
        portraitImage.enabled = true;

        // 얼굴 앞의 작은 점광원 (캐릭터 레이어만, 범위는 머리 둘레 정도)
        var lightGo = new GameObject("HUDPortraitLight", typeof(Light));
        lightGo.transform.SetParent(transform, false);
        portraitLight = lightGo.GetComponent<Light>();
        portraitLight.type = LightType.Point;
        portraitLight.color = new Color(1f, 0.96f, 0.9f);
        // 점광원은 거리 제곱으로 약해지므로 캐릭터 크기(스케일)만큼 세기를 키운다.
        float scale = character.transform.lossyScale.y;
        float d = portraitDistance * 0.8f * scale;
        portraitLight.intensity = portraitLightIntensity * d * d;
        portraitLight.range = portraitDistance * 2f * scale;
        portraitLight.shadows = LightShadows.None;
        portraitLight.cullingMask = 1 << character.gameObject.layer;
    }

    void ShowXpPopup(int amount)
    {
        xpPopupText.text = $"+{amount} XP";
        xpPopup.gameObject.SetActive(true);
        xpPopup.alpha = 1f;
        xpPopupTimer = 1.8f;
    }

    Image albumBadge, menuBadge;


    /// <summary>가운데 경희 로고 버튼의 "받을 퀘스트 보상 있음" 점</summary>
    public void SetMenuBadge(bool on)
    {
        if (menuBadge == null) return;
        menuBadge.gameObject.SetActive(on);
        menuBadge.transform.SetAsLastSibling();
    }

    /// <summary>사진첩 버튼의 새 반응 점</summary>
    public void SetAlbumBadge(bool on)
    {
        if (albumBadge != null)
        {
            albumBadge.gameObject.SetActive(on);
            albumBadge.transform.SetAsLastSibling();
        }
    }

    /// <summary>내 사진첩 (내가 메모판에 올린 사진들)</summary>
    void OpenAlbum()
    {
        var board = FindAnyObjectByType<MemoBoard>();
        if (board != null) board.OpenAlbum();
    }

    void OpenCustomizer()
    {
        if (customizer != null)
            customizer.SetOpen(true);
    }

    // ---------- 공지사항 ----------

    void LoadNotices()
    {
        notices = new NoticeData();
        if (noticesJson != null)
        {
            try
            {
                notices = JsonUtility.FromJson<NoticeData>(noticesJson.text) ?? new NoticeData();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameHUD] 공지사항을 읽지 못했습니다: {e.Message}");
            }
        }
        var saved = JsonUtility.FromJson<IdList>(PlayerPrefs.GetString(ReadNoticesKey, "{}")) ?? new IdList();
        readNotices = new HashSet<string>(saved.ids);
    }

    void RefreshNotices()
    {
        int unread = 0;
        foreach (var nt in notices.notices)
            if (!readNotices.Contains(nt.id))
                unread++;
        noticeBadge.SetActive(unread > 0);
        noticeBadgeText.text = unread > 9 ? "9+" : unread.ToString();
        noticeTitle.text = notices.notices.Count > 0 ? Ellipsize(notices.notices[0].title, 7) : "새 공지가 없어요";
    }

    void OpenNotices()
    {
        SetScreen(noticeScreen, true, FillNotices);
        foreach (var nt in notices.notices)
            readNotices.Add(nt.id);
        PlayerPrefs.SetString(ReadNoticesKey, JsonUtility.ToJson(new IdList { ids = new List<string>(readNotices) }));
        PlayerPrefs.Save();
        RefreshNotices();
    }

    void CloseNotices() => SetScreen(noticeScreen, false, null);

    void FillNotices()
    {
        Clear(noticeList);
        if (notices.notices.Count == 0)
        {
            UIKit.Size(UIKit.Text(noticeList, "새 공지가 없어요.", 40, TextAnchor.MiddleCenter, UIKit.Quiet), 200f);
            return;
        }
        foreach (var nt in notices.notices)
        {
            bool isNew = !readNotices.Contains(nt.id);
            Image card = Card(noticeList, nt.id);
            UIKit.Vertical(card, 14f, new RectOffset(40, 40, 32, 36));
            UIKit.Text(card.transform, (isNew ? "<color=#FF6B70>NEW</color>  " : "") + nt.date, 30, TextAnchor.MiddleLeft, UIKit.Quiet);
            Text title = UIKit.Text(card.transform, nt.title, 44, TextAnchor.MiddleLeft);
            title.fontStyle = FontStyle.Bold;
            UIKit.Text(card.transform, nt.body, 36, TextAnchor.UpperLeft, new Color(1f, 1f, 1f, 0.85f));
        }
    }

    // ---------- 설정 ----------

    void OpenSettings()
    {
        SetScreen(settingsScreen, true, null);
        nicknameInput.text = PlayerPrefs.GetString(NicknameKey, "");
        RefreshServerStatus();
        bool guest = AuthService.Current != null && AuthService.Current.isGuest;
        UIKit.SetLabel(logoutButton, guest ? "로그인하기 (체험 끝내기)" : "로그아웃");
        deleteAccountButton.gameObject.SetActive(AuthService.HasToken);
        deleteConfirmUntil = 0f;
        UIKit.SetLabel(deleteAccountButton, "회원 탈퇴");
    }

    void DeleteAccount()
    {
        if (Time.unscaledTime > deleteConfirmUntil)
        {
            deleteConfirmUntil = Time.unscaledTime + 4f;
            UIKit.SetLabel(deleteAccountButton, "한 번 더 누르면 계정과 정보가 지워져요");
            return;
        }
        deleteConfirmUntil = 0f;
        StartCoroutine(AuthService.DeleteAccount(error =>
        {
            if (error != null) { Toast("탈퇴하지 못했어요: " + error); return; }
            CloseSettings();
            Toast("계정을 지웠어요");
            LoginScreen.Begin();
        }));
    }

    void CloseSettings() => SetScreen(settingsScreen, false, null);

    void BuildSettings()
    {
        Image nickCard = Card(settingsList, "NicknameCard");
        UIKit.Vertical(nickCard, 18f, new RectOffset(40, 40, 32, 36));
        UIKit.Text(nickCard.transform, "닉네임", 34, TextAnchor.MiddleLeft, UIKit.Quiet);
        RectTransform row = UIKit.Rect(nickCard.transform, "Row");
        UIKit.Size(row, 110f);
        var h = UIKit.Horizontal(row, 20f);
        h.childForceExpandWidth = false;
        nicknameInput = UIKit.Input(row, defaultNickname, 40, false, 12);
        UIKit.Size(nicknameInput, 110f, -1f, 1f);
        Button save = UIKit.Button(row, "저장", SaveNickname, UIKit.Accent, 40);
        UIKit.Size(save, 110f, 200f);
        UIKit.Text(nickCard.transform, "메모를 남길 때도 이 이름이 쓰여요.", 28, TextAnchor.MiddleLeft, UIKit.Quiet);

        // 메모 서버 주소 (비우면 이 기기 안의 가짜 서버)
        Image serverCard = Card(settingsList, "ServerCard");
        UIKit.Vertical(serverCard, 18f, new RectOffset(40, 40, 32, 36));
        UIKit.Text(serverCard.transform, "메모 서버", 34, TextAnchor.MiddleLeft, UIKit.Quiet);
        RectTransform srow = UIKit.Rect(serverCard.transform, "Row");
        UIKit.Size(srow, 110f);
        var sh = UIKit.Horizontal(srow, 20f);
        sh.childForceExpandWidth = false;
        serverInput = UIKit.Input(srow, "비우면 기본 서버", 36, false, 80);
        serverInput.keyboardType = TouchScreenKeyboardType.URL;
        UIKit.Size(serverInput, 110f, -1f, 1f);
        Button check = UIKit.Button(srow, "연결", SaveServer, UIKit.Accent, 38);
        UIKit.Size(check, 110f, 200f);
        serverStatus = UIKit.Text(serverCard.transform, "", 28, TextAnchor.MiddleLeft, UIKit.Quiet);

        Image optCard = Card(settingsList, "OptionsCard");
        UIKit.Vertical(optCard, 18f, new RectOffset(40, 40, 32, 36));
        Button custom = UIKit.Button(optCard.transform, "캐릭터 꾸미기", () => { CloseSettings(); OpenCustomizer(); }, UIKit.ButtonGray, 38);
        UIKit.Size(custom, 110f);
        Button north = UIKit.Button(optCard.transform, "지도 북쪽으로 돌리기", () => { CloseSettings(); if (cameraFollow != null) cameraFollow.ResetNorthUp(); }, UIKit.ButtonGray, 38);
        UIKit.Size(north, 110f);
        Button prefs = UIKit.Button(optCard.transform, "산책 취향 다시 고르기", () => { CloseSettings(); PreferenceSurvey.Open(); }, UIKit.ButtonGray, 38);
        UIKit.Size(prefs, 110f);
        Button privacy = UIKit.Button(optCard.transform, "개인정보 안내", () => { CloseSettings(); LoginScreen.ShowPrivacy(); }, UIKit.ButtonGray, 38);
        UIKit.Size(privacy, 110f);
        // 체험 계정이면 "로그인하기": 로그아웃하고 로그인 화면으로 (게임 진행은 기기에 있어 그대로 이어진다)
        logoutButton = UIKit.Button(optCard.transform, "로그아웃", () =>
        {
            CloseSettings();
            StartCoroutine(AuthService.Logout(LoginScreen.Begin));
        }, UIKit.ButtonGray, 38);
        UIKit.Size(logoutButton, 110f);
        deleteAccountButton = UIKit.Button(optCard.transform, "회원 탈퇴", DeleteAccount, UIKit.ButtonGray, 38);
        deleteAccountButton.GetComponentInChildren<Text>().color = new Color(0.95f, 0.45f, 0.45f);
        UIKit.Size(deleteAccountButton, 110f);

        UIKit.Size(UIKit.Text(settingsList, "KUCA · 경희대 국제캠퍼스를 함께 걷는 산책 동반자", 28, TextAnchor.MiddleCenter, UIKit.Quiet), 80f);
    }

    string DefaultMemoServer
    {
        get
        {
            var board = FindAnyObjectByType<MemoBoard>();
            return board != null ? board.DefaultServerUrl : MemoBoard.DefaultServer;
        }
    }

    void RefreshServerStatus()
    {
        bool custom = PlayerPrefs.HasKey(MemoBoard.ServerUrlKey);
        string url = custom ? PlayerPrefs.GetString(MemoBoard.ServerUrlKey) : DefaultMemoServer;
        serverInput.text = custom ? url : "";
        serverStatus.color = UIKit.Quiet;
        serverStatus.text = custom
            ? $"지금 쓰는 서버: {url}  (비우고 연결을 누르면 기본 서버로)"
            : $"기본 서버를 쓰고 있어요: {url}";
    }

    void SaveServer()
    {
        string url = HttpMemoService.Normalize(serverInput.text);
        if (url.Length == 0)
        {
            // 비우면 기본 서버로 되돌린다.
            PlayerPrefs.DeleteKey(MemoBoard.ServerUrlKey);
            PlayerPrefs.Save();
            RefreshServerStatus();
            Toast("기본 메모 서버를 써요");
            return;
        }
        serverStatus.color = UIKit.Quiet;
        serverStatus.text = "연결 확인 중…";
        StartCoroutine(HttpMemoService.Check(url, (ok, error) =>
        {
            if (!ok)
            {
                serverStatus.color = new Color(1f, 0.55f, 0.55f);
                serverStatus.text = error;
                return;
            }
            PlayerPrefs.SetString(MemoBoard.ServerUrlKey, url);
            PlayerPrefs.Save();
            RefreshServerStatus();
            serverStatus.color = new Color(0.55f, 0.9f, 0.65f);
            serverStatus.text = $"연결됐어요: {url}";
            Toast("메모 서버에 연결했어요");
        }));
    }

    void SaveNickname()
    {
        string nick = nicknameInput.text.Trim();
        PlayerPrefs.SetString(NicknameKey, nick);
        PlayerPrefs.Save();
        Toast("닉네임을 저장했어요");
        if (AuthService.HasToken && nick.Length is >= 1 and <= 20)
            StartCoroutine(AuthService.SetNickname(nick, (_, error) => { if (error != null) Toast(error); })); // 계정에도
    }

    // ---------- 공통 ----------

    void SetScreen(GameObject screen, bool open, Action fill)
    {
        screen.SetActive(open);
        UIInputBlocker.SetModal(screen, open);
        fill?.Invoke();
    }

    /// <summary>제목·요약·닫기 버튼과 스크롤 목록이 있는 전체 화면</summary>
    GameObject BuildScreen(string name, string titleText, UnityEngine.Events.UnityAction onClose, out Text summary, out RectTransform list)
    {
        Image root = UIKit.Image(canvas.transform, name, new Color(0.05f, 0.07f, 0.13f, 0.97f), raycast: true);
        UIKit.Stretch(root.rectTransform);
        RectTransform inner = UIKit.Rect(root.transform, "Inner");
        inner.anchorMin = safe.anchorMin;
        inner.anchorMax = safe.anchorMax;
        inner.offsetMin = inner.offsetMax = Vector2.zero;

        RectTransform head = UIKit.Rect(inner, "Header");
        head.anchorMin = new Vector2(0f, 1f);
        head.anchorMax = new Vector2(1f, 1f);
        head.pivot = new Vector2(0.5f, 1f);
        head.offsetMin = head.offsetMax = Vector2.zero;
        head.sizeDelta = new Vector2(0f, 250f);

        Text title = UIKit.Text(head, titleText, 64, TextAnchor.MiddleLeft);
        title.fontStyle = FontStyle.Bold;
        RectTransform tr = title.rectTransform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = new Vector2(50f, 90f);
        tr.offsetMax = new Vector2(-260f, -40f);

        summary = UIKit.Text(head, "", 32, TextAnchor.UpperLeft, new Color(0.8f, 0.9f, 1f, 0.75f));
        RectTransform smr = summary.rectTransform;
        smr.anchorMin = new Vector2(0f, 0f);
        smr.anchorMax = new Vector2(1f, 0f);
        smr.pivot = new Vector2(0.5f, 0f);
        smr.offsetMin = new Vector2(50f, 30f);
        smr.offsetMax = new Vector2(-50f, 84f);

        Image line = UIKit.Image(head, "Line", GlassEdge);
        RectTransform lr = line.rectTransform;
        lr.anchorMin = new Vector2(0f, 0f);
        lr.anchorMax = new Vector2(1f, 0f);
        lr.offsetMin = new Vector2(36f, 0f);
        lr.offsetMax = new Vector2(-36f, 3f);

        Button close = UIKit.Button(head, "닫기", onClose, new Color(1f, 1f, 1f, 0.14f), 38);
        RectTransform cr = (RectTransform)close.transform;
        Place(cr, new Vector2(1f, 1f), new Vector2(-40f, -50f));
        cr.sizeDelta = new Vector2(170f, 92f);
        var ci = close.GetComponent<Image>();
        ci.sprite = HudIcons.Pill;
        ci.type = Image.Type.Sliced;
        ci.pixelsPerUnitMultiplier = 120f / 92f;

        RectTransform area = UIKit.Rect(inner, "List");
        UIKit.StretchBetween(area, 0f, 250f);
        list = UIKit.ScrollList(area, 24f, new RectOffset(36, 36, 36, 60));

        root.gameObject.SetActive(false);
        return root.gameObject;
    }

    /// <summary>테두리가 은은히 빛나는 유리 패널. 반환값은 안쪽 면(내용을 넣는 곳), 바깥 테두리는 그 부모.</summary>
    static Image Glass(Transform parent, string name, Vector2 size, float radius)
    {
        Image edge = UIKit.Image(parent, name, GlassEdge);
        edge.sprite = HudIcons.Pill;
        edge.type = Image.Type.Sliced;
        edge.pixelsPerUnitMultiplier = 60f / radius;
        edge.rectTransform.sizeDelta = size;
        Image fill = UIKit.Image(edge.transform, "Fill", GlassFill);
        fill.sprite = HudIcons.Pill;
        fill.type = Image.Type.Sliced;
        fill.pixelsPerUnitMultiplier = 60f / Mathf.Max(radius - 3f, 1f);
        UIKit.Stretch(fill.rectTransform, 3f, 3f);
        return fill;
    }

    static Image Card(Transform parent, string name)
    {
        Image card = UIKit.Image(parent, name, new Color(0.12f, 0.16f, 0.27f, 1f));
        card.sprite = HudIcons.Pill;
        card.type = Image.Type.Sliced;
        card.pixelsPerUnitMultiplier = 2f;
        return card;
    }

    /// <summary>그림자 + 흰 원 얼굴(이름 "Face")을 가진 원형 버튼</summary>
    static RectTransform RoundButton(Transform parent, string name, float size, UnityEngine.Events.UnityAction onClick)
    {
        Button button = InvisibleButton(parent, name, onClick, size);
        RectTransform root = (RectTransform)button.transform;
        UIInputBlocker.Register(root);
        Image shadow = Circle(root, "Shadow", new Color(0f, 0f, 0f, 0.28f), size);
        Place(shadow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -5f));
        Image face = Circle(root, "Face", Color.white, size);
        Place(face.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        return root;
    }

    static Button InvisibleButton(Transform parent, string name, UnityEngine.Events.UnityAction onClick, float size)
    {
        Image hit = UIKit.Image(parent, name, new Color(0f, 0f, 0f, 0f), raycast: true);
        if (size > 0f)
            hit.rectTransform.sizeDelta = new Vector2(size, size);
        var button = hit.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(onClick);
        hit.gameObject.AddComponent<PressScale>();
        return button;
    }

    static Image Circle(Transform parent, string name, Color color, float size)
    {
        Image img = UIKit.Image(parent, name, color);
        img.sprite = HudIcons.Circle;
        img.rectTransform.sizeDelta = new Vector2(size, size);
        return img;
    }

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pos)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos;
    }

    /// <summary>왼쪽 여백 left, 세로 범위 [yMin,yMax] (부모 비율), 오른쪽 여백 right 인 칸</summary>
    static void SetBox(RectTransform rt, float left, float yMin, float yMax, float right)
    {
        rt.anchorMin = new Vector2(0f, yMin);
        rt.anchorMax = new Vector2(1f, yMax);
        rt.offsetMin = new Vector2(left, 0f);
        rt.offsetMax = new Vector2(-right, 0f);
    }

    static string Ellipsize(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    static void Clear(RectTransform content)
    {
        for (int i = content.childCount - 1; i >= 0; i--)
            Destroy(content.GetChild(i).gameObject);
    }
}

/// <summary>누르는 동안 살짝 작아지는 버튼 효과</summary>
public class PressScale : MonoBehaviour, UnityEngine.EventSystems.IPointerDownHandler, UnityEngine.EventSystems.IPointerUpHandler, UnityEngine.EventSystems.IPointerExitHandler
{
    float target = 1f;

    public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e) => target = 0.92f;
    public void OnPointerUp(UnityEngine.EventSystems.PointerEventData e) => target = 1f;
    public void OnPointerExit(UnityEngine.EventSystems.PointerEventData e) => target = 1f;

    void Update()
    {
        float s = Mathf.MoveTowards(transform.localScale.x, target, Time.unscaledDeltaTime * 3f);
        transform.localScale = new Vector3(s, s, 1f);
    }
}
