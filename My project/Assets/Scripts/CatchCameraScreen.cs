using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

/// <summary>
/// 경희몬 잡기 화면. 경희몬(동물 피규어)은 늘 화면 가운데에 서서 통통 뛴다.
/// - AR 켬: 뒤에 후면 카메라 영상이 그대로 보인다 (바닥을 찾지 않으니 경사지에서도 바로 나온다).
/// - AR 끔: 잔디밭과 나무 두 그루가 있는 배경에 경희몬이 서 있다.
/// 오른쪽 위 스위치로 바꾸고, 고른 값은 기억한다. 화면을 좌우로 끌면 경희몬을 돌려 볼 수 있다.
/// 셔터를 누르면 사진을 찍고, 그 사진과 함께 경희몬을 얻는다 (사진은 스크랩북에 저장).
/// </summary>
public class CatchCameraScreen : MonoBehaviour
{
    [Tooltip("에디터에서도 컴퓨터 웹캠을 켤지 (끄면 대신 배경 그림을 보여 준다)")]
    public bool useWebcamInEditor = false;
    [Tooltip("경희몬 키 (받침 포함, m)")]
    public float monsterHeight = 1.1f;
    public float cameraFov = 45f;
    [Tooltip("저장할 사진의 긴 변 최대 픽셀")]
    public int maxPhotoSize = 1600;
    [Range(30, 100)] public int jpgQuality = 85;

    const string ArPrefKey = "catch_ar_on";
    static readonly Vector3 StagePos = new Vector3(0f, -30000f, 0f);
    static readonly Color Navy = new Color(0.10f, 0.22f, 0.52f);

    public static bool IsOpen { get; private set; }

    Canvas canvas;
    GameObject root;
    RawImage camView;
    AspectRatioFitter camFitter;
    RawImage cameraPlaceholder;   // AR 켬인데 카메라를 못 쓸 때
    RawImage skyBg;               // AR 끔 배경 하늘
    RawImage stageView;           // 3D 렌더 (투명 배경)
    CanvasGroup controls;
    Text title, hint, note;
    Text arLabel;
    Image arKnob, arTrack;
    Image flash;
    float flashAlpha;
    GameObject resultCard;
    RawImage resultPhoto;
    AspectRatioFitter resultFitter;
    Text resultTitle, resultSub;
    Button albumButton;

    WebCamTexture webcam;
    Camera stageCam;
    RenderTexture stageTexture;
    GameObject stage;
    GameObject scenery;           // 잔디밭·나무 (AR 끔에서만)
    Transform monster;
    Transform shadow;
    float phase;
    float spin;                   // 끌어서 돌린 각도
    bool arOn;

    Collectible target;
    string speciesId;
    string displayName;
    CollectibleType type;
    CollectController owner;
    bool busy;
    Texture2D lastPhoto;
    string lastPhotoPath;

    bool ArPreference
    {
        get => PlayerPrefs.GetInt(ArPrefKey, 1) == 1;
        set { PlayerPrefs.SetInt(ArPrefKey, value ? 1 : 0); PlayerPrefs.Save(); }
    }

    /// <summary>target 을 잡는 카메라 화면을 연다.</summary>
    public void Open(Collectible target, CollectController owner)
    {
        if (IsOpen || target == null || !CreatureLibrary.TryGetMesh(target.SpeciesId, out Mesh body))
            return;
        if (root == null)
            Build();
        this.target = target;
        this.owner = owner;
        type = target.Type;
        speciesId = target.SpeciesId;
        displayName = target.DisplayName;
        IsOpen = true;
        busy = false;
        spin = 0f;
        phase = 0f;
        UIInputBlocker.SetModal(this, true);
        root.SetActive(true);
        resultCard.SetActive(false);
        controls.alpha = 1f;
        controls.blocksRaycasts = true;
        title.text = $"야생의 {displayName}{Josa(displayName, "이", "가")} 나타났다!";
        hint.text = "셔터를 눌러 함께 찍어 보세요";
        note.text = "";

        BuildStage(body);
        SetAr(ArPreference);
    }

    public void Close()
    {
        StopAllCoroutines();
        StopWebcam();
        if (stage != null)
            Destroy(stage);
        stage = null;
        scenery = null;
        if (stageTexture != null)
        {
            stageTexture.Release();
            Destroy(stageTexture);
            stageTexture = null;
        }
        if (lastPhoto != null)
            Destroy(lastPhoto);
        lastPhoto = null;
        if (root != null)
            root.SetActive(false);
        target = null;
        IsOpen = false;
        UIInputBlocker.SetModal(this, false);
    }

    void OnDisable()
    {
        if (IsOpen)
            Close();
    }

    // ---------- AR 켬 / 끔 ----------

    void ToggleAr()
    {
        if (busy)
            return;
        ArPreference = !arOn;
        SetAr(!arOn);
    }

    void SetAr(bool on)
    {
        arOn = on;
        scenery.SetActive(!on);
        skyBg.enabled = !on;
        note.text = "";
        if (on)
            StartCoroutine(StartCamera());
        else
        {
            StopAllCoroutines();
            StopWebcam();
            camView.enabled = false;
            cameraPlaceholder.enabled = false;
        }
        // 스위치 모양
        arLabel.text = on ? "AR 켬" : "AR 끔";
        arTrack.color = on ? new Color(0.30f, 0.78f, 0.55f) : new Color(1f, 1f, 1f, 0.25f);
        Place(arKnob.rectTransform, new Vector2(on ? 1f : 0f, 0.5f), new Vector2(on ? -6f : 6f, 0f));
        arKnob.rectTransform.pivot = new Vector2(on ? 1f : 0f, 0.5f);
    }

    void StopWebcam()
    {
        if (webcam == null)
            return;
        webcam.Stop();
        Destroy(webcam);
        webcam = null;
    }

    IEnumerator StartCamera()
    {
        camView.enabled = false;
        cameraPlaceholder.enabled = true;
        if (Application.isEditor && !useWebcamInEditor)
        {
            note.text = "에디터에서는 카메라 대신 배경 그림을 보여 줘요";
            yield break;
        }
#if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Permission.RequestUserPermission(Permission.Camera);
            float wait = 10f;
            while (!Permission.HasUserAuthorizedPermission(Permission.Camera) && wait > 0f) { wait -= Time.unscaledDeltaTime; yield return null; }
        }
#elif UNITY_IOS
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
#endif
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam) || WebCamTexture.devices.Length == 0)
        {
            note.text = "카메라를 쓸 수 없어요. 설정에서 KUCA의 카메라 권한을 켜거나 AR 을 꺼 주세요";
            yield break;
        }
        string device = WebCamTexture.devices[0].name;
        foreach (WebCamDevice d in WebCamTexture.devices)
            if (!d.isFrontFacing) { device = d.name; break; }
        webcam = new WebCamTexture(device, 1920, 1080, 30);
        webcam.Play();
        float timeout = 5f;
        while (webcam.width <= 16 && timeout > 0f) { timeout -= Time.unscaledDeltaTime; yield return null; }
        camView.texture = webcam;
        camView.enabled = true;
        cameraPlaceholder.enabled = false;
    }

    // ---------- 화면 ----------

    void Build()
    {
        canvas = UIKit.CreateCanvas(transform, "CatchCanvas", 25);
        root = UIKit.Image(canvas.transform, "CatchScreen", Color.black, raycast: true).gameObject;
        UIKit.Stretch((RectTransform)root.transform);

        // AR 켬인데 카메라가 없을 때 (에디터 등) 보여 줄 배경
        cameraPlaceholder = FullImage("CameraPlaceholder",
            HudIcons.Gradient(new Color(0.62f, 0.82f, 1f), new Color(0.42f, 0.62f, 0.38f)));

        // 카메라 영상 (화면을 꽉 채움)
        RectTransform camArea = UIKit.Rect(root.transform, "CameraArea");
        UIKit.Stretch(camArea);
        camArea.gameObject.AddComponent<RectMask2D>();
        var cv = new GameObject("Camera", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
        cv.transform.SetParent(camArea, false);
        camView = cv.GetComponent<RawImage>();
        camView.raycastTarget = false;
        camFitter = cv.GetComponent<AspectRatioFitter>();
        camFitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        camView.enabled = false;

        // AR 끔: 위는 맑은 하늘, 지평선 쪽은 밝게 (잔디밭은 3D 로 그린다)
        skyBg = FullImage("Sky", VerticalGradient(new Color(0.38f, 0.64f, 0.96f), new Color(0.86f, 0.94f, 1f)));

        // 3D 경희몬 (+ AR 끔이면 잔디밭과 나무), 투명 배경 렌더 텍스처
        stageView = new GameObject("Stage", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        stageView.transform.SetParent(root.transform, false);
        stageView.raycastTarget = false;
        UIKit.Stretch(stageView.rectTransform);

        // 좌우로 끌면 경희몬을 돌려 본다
        Image dragArea = UIKit.Image(root.transform, "Drag", new Color(0f, 0f, 0f, 0f), raycast: true);
        UIKit.Stretch(dragArea.rectTransform);
        dragArea.gameObject.AddComponent<CatchDragArea>().onDrag = d => spin -= d.x * 0.4f;

        // 버튼·글자 (사진에는 안 찍힘)
        RectTransform ui = UIKit.Rect(root.transform, "Controls");
        UIKit.Stretch(ui);
        controls = ui.gameObject.AddComponent<CanvasGroup>();
        Rect sa = Screen.safeArea;
        RectTransform safe = UIKit.Rect(ui, "Safe");
        safe.anchorMin = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
        safe.anchorMax = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);
        safe.offsetMin = safe.offsetMax = Vector2.zero;

        Image titleBg = UIKit.Image(safe, "Title", new Color(0.05f, 0.08f, 0.18f, 0.6f));
        titleBg.sprite = HudIcons.Pill;
        titleBg.type = Image.Type.Sliced;
        titleBg.pixelsPerUnitMultiplier = 120f / 100f;
        RectTransform tr = titleBg.rectTransform;
        tr.anchorMin = tr.anchorMax = tr.pivot = new Vector2(0.5f, 1f);
        tr.anchoredPosition = new Vector2(0f, -40f);
        tr.sizeDelta = new Vector2(820f, 100f);
        title = UIKit.Text(titleBg.transform, "", 38, TextAnchor.MiddleCenter);
        title.fontStyle = FontStyle.Bold;
        UIKit.Stretch(title.rectTransform, 24f, 0f);

        note = UIKit.Text(safe, "", 28, TextAnchor.MiddleCenter, new Color(1f, 1f, 1f, 0.9f));
        Shadowed(note);
        RectTransform nr = note.rectTransform;
        nr.anchorMin = new Vector2(0f, 1f);
        nr.anchorMax = new Vector2(1f, 1f);
        nr.pivot = new Vector2(0.5f, 1f);
        nr.offsetMin = new Vector2(180f, -230f);
        nr.offsetMax = new Vector2(-180f, -150f);

        Button close = RoundIconButton(safe, "Close", HudIcons.Close, 110f, Close);
        Place((RectTransform)close.transform, new Vector2(0f, 1f), new Vector2(40f, -170f));

        BuildArSwitch(safe);

        hint = UIKit.Text(safe, "", 36, TextAnchor.MiddleCenter);
        hint.fontStyle = FontStyle.Bold;
        Shadowed(hint);
        RectTransform hr = hint.rectTransform;
        hr.anchorMin = new Vector2(0f, 0f);
        hr.anchorMax = new Vector2(1f, 0f);
        hr.offsetMin = new Vector2(40f, 290f);
        hr.offsetMax = new Vector2(-40f, 370f);

        // 셔터
        Button shutter = UIKit.Image(safe, "Shutter", new Color(0f, 0f, 0f, 0f), raycast: true).gameObject.AddComponent<Button>();
        shutter.transition = Selectable.Transition.None;
        shutter.onClick.AddListener(Shoot);
        shutter.gameObject.AddComponent<PressScale>();
        RectTransform sr = (RectTransform)shutter.transform;
        Place(sr, new Vector2(0.5f, 0f), new Vector2(0f, 80f));
        sr.sizeDelta = new Vector2(190f, 190f);
        Image ring = Circle(sr, "Ring", Color.white, 190f);
        Place(ring.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        Image gap = Circle(ring.transform, "Gap", new Color(0f, 0f, 0f, 0.35f), 166f);
        Place(gap.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        Image core = Circle(gap.transform, "Core", new Color(0.96f, 0.79f, 0.45f), 148f);
        Place(core.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);

        // 셔터 번쩍임
        flash = UIKit.Image(root.transform, "Flash", new Color(1f, 1f, 1f, 0f));
        UIKit.Stretch(flash.rectTransform);

        BuildResultCard();
        root.SetActive(false);
    }

    /// <summary>오른쪽 위 AR 켬/끔 스위치</summary>
    void BuildArSwitch(RectTransform safe)
    {
        Image bg = UIKit.Image(safe, "ArSwitch", new Color(0.05f, 0.08f, 0.18f, 0.55f), raycast: true);
        bg.sprite = HudIcons.Pill;
        bg.type = Image.Type.Sliced;
        bg.pixelsPerUnitMultiplier = 120f / 110f;
        Place(bg.rectTransform, new Vector2(1f, 1f), new Vector2(-40f, -170f));
        bg.rectTransform.sizeDelta = new Vector2(250f, 110f);
        var b = bg.gameObject.AddComponent<Button>();
        b.transition = Selectable.Transition.None;
        b.onClick.AddListener(ToggleAr);
        bg.gameObject.AddComponent<PressScale>();

        arLabel = UIKit.Text(bg.transform, "AR 켬", 32, TextAnchor.MiddleLeft);
        arLabel.fontStyle = FontStyle.Bold;
        arLabel.rectTransform.anchorMin = Vector2.zero;
        arLabel.rectTransform.anchorMax = Vector2.one;
        arLabel.rectTransform.offsetMin = new Vector2(28f, 0f);
        arLabel.rectTransform.offsetMax = new Vector2(-110f, 0f);

        arTrack = UIKit.Image(bg.transform, "Track", Color.white);
        arTrack.sprite = HudIcons.Pill;
        arTrack.type = Image.Type.Sliced;
        arTrack.pixelsPerUnitMultiplier = 120f / 50f;
        Place(arTrack.rectTransform, new Vector2(1f, 0.5f), new Vector2(-24f, 0f));
        arTrack.rectTransform.sizeDelta = new Vector2(84f, 50f);
        arKnob = Circle(arTrack.transform, "Knob", Color.white, 38f);
    }

    RawImage FullImage(string name, Texture tex)
    {
        var img = new GameObject(name, typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        img.transform.SetParent(root.transform, false);
        img.texture = tex;
        img.raycastTarget = false;
        UIKit.Stretch(img.rectTransform);
        return img;
    }

    void BuildResultCard()
    {
        Image dim = UIKit.Image(root.transform, "Result", new Color(0.03f, 0.05f, 0.12f, 0.7f), raycast: true);
        UIKit.Stretch(dim.rectTransform);
        resultCard = dim.gameObject;

        Image card = UIKit.Image(dim.transform, "Card", new Color(0.98f, 0.99f, 1f));
        card.sprite = HudIcons.Pill;
        card.type = Image.Type.Sliced;
        card.pixelsPerUnitMultiplier = 1.4f;
        Place(card.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        card.rectTransform.sizeDelta = new Vector2(900f, 1400f);

        RectTransform frame = UIKit.Rect(card.transform, "PhotoFrame");
        frame.anchorMin = new Vector2(0f, 1f);
        frame.anchorMax = new Vector2(1f, 1f);
        frame.pivot = new Vector2(0.5f, 1f);
        frame.offsetMin = new Vector2(50f, -900f);
        frame.offsetMax = new Vector2(-50f, -50f);
        frame.gameObject.AddComponent<RectMask2D>();
        var photo = new GameObject("Photo", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
        photo.transform.SetParent(frame, false);
        resultPhoto = photo.GetComponent<RawImage>();
        resultPhoto.raycastTarget = false;
        resultFitter = photo.GetComponent<AspectRatioFitter>();
        resultFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;

        resultTitle = UIKit.Text(card.transform, "", 50, TextAnchor.MiddleCenter, Navy);
        resultTitle.fontStyle = FontStyle.Bold;
        TopBox(resultTitle.rectTransform, 930f, 80f);
        resultSub = UIKit.Text(card.transform, "", 32, TextAnchor.MiddleCenter, new Color(0.4f, 0.45f, 0.5f));
        TopBox(resultSub.rectTransform, 1010f, 110f);

        albumButton = PillButton(card.transform, "앨범에도 저장", new Color(0.88f, 0.92f, 0.96f), Navy, SaveToAlbum);
        Place((RectTransform)albumButton.transform, new Vector2(0.5f, 0f), new Vector2(-190f, 50f));
        ((RectTransform)albumButton.transform).sizeDelta = new Vector2(360f, 116f);
        Button ok = PillButton(card.transform, "확인", Navy, Color.white, Close);
        Place((RectTransform)ok.transform, new Vector2(0.5f, 0f), new Vector2(200f, 50f));
        ((RectTransform)ok.transform).sizeDelta = new Vector2(320f, 116f);
    }

    // ---------- 3D 무대 ----------

    /// <summary>
    /// 경희몬은 원점(발밑)에 서고, 카메라는 앞쪽 조금 위에서 내려다본다 → 늘 화면 가운데.
    /// AR 끔 배경(잔디밭·나무)은 scenery 아래에 두고 켬/끔에 따라 보이거나 숨긴다.
    /// </summary>
    void BuildStage(Mesh body)
    {
        int layer = MonsterThumbnails.StageLayer;
        stage = new GameObject("CatchStage");
        stage.transform.position = StagePos;

        var camGo = new GameObject("CatchCamera", typeof(Camera));
        camGo.transform.SetParent(stage.transform, false);
        stageCam = camGo.GetComponent<Camera>();
        stageCam.clearFlags = CameraClearFlags.SolidColor;
        stageCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        stageCam.cullingMask = 1 << layer;
        stageCam.fieldOfView = cameraFov;
        stageCam.nearClipPlane = 0.05f;
        stageCam.farClipPlane = 200f;
        float h = monsterHeight;
        // 경희몬이 화면 높이의 약 42%, 가로의 약 60% 를 넘지 않는 거리 (세로 폰 화면은 가로가 좁다)
        float tanV = Mathf.Tan(cameraFov * 0.5f * Mathf.Deg2Rad);
        float aspect = (float)Mathf.Max(1, Screen.width) / Mathf.Max(1, Screen.height);
        float fitHeight = h * 0.5f / tanV / 0.42f;
        float fitWidth = h * 0.95f * 0.5f / (tanV * aspect) / 0.6f;   // 피규어(받침 포함) 폭은 키와 비슷하다
        float dist = Mathf.Max(fitHeight, fitWidth);
        camGo.transform.localPosition = new Vector3(0f, h * 0.95f, -dist);
        camGo.transform.LookAt(stage.transform.TransformPoint(0f, h * 0.62f, 0f));
        EnsureStageTexture();

        var lightGo = new GameObject("CatchLight", typeof(Light));
        lightGo.transform.SetParent(stage.transform, false);
        lightGo.transform.rotation = Quaternion.Euler(42f, -30f, 0f);
        var sun = lightGo.GetComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.3f;
        sun.shadows = LightShadows.None;
        sun.cullingMask = 1 << layer;

        monster = BuildCreature(body, h, layer);

        // 발밑 그림자 (AR 켬에서 공중에 뜬 느낌을 줄인다)
        var sh = new GameObject("Shadow", typeof(SpriteRenderer));
        sh.layer = layer;
        sh.transform.SetParent(stage.transform, false);
        var sr = sh.GetComponent<SpriteRenderer>();
        sr.sprite = HudIcons.SoftDisk;
        sr.color = new Color(0f, 0f, 0f, 0.4f);
        float w = h * 1.1f / (HudIcons.SoftDisk.rect.width / HudIcons.SoftDisk.pixelsPerUnit);
        sh.transform.localScale = new Vector3(w, w, 1f);
        sh.transform.localPosition = new Vector3(0f, 0.004f, 0f);
        sh.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        shadow = sh.transform;

        scenery = BuildScenery(layer);
    }

    /// <summary>화면과 같은 비율의 렌더 텍스처. 화면 크기·방향이 바뀌면 다시 만든다.</summary>
    void EnsureStageTexture()
    {
        int sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
        int th = Mathf.Clamp(sh, 256, 2400);
        int tw = Mathf.Clamp(Mathf.RoundToInt(th * (float)sw / sh), 64, 4096);
        if (stageTexture != null && stageTexture.width == tw && stageTexture.height == th)
            return;
        if (stageTexture != null)
        {
            stageTexture.Release();
            Destroy(stageTexture);
        }
        stageTexture = new RenderTexture(tw, th, 24, RenderTextureFormat.ARGB32) { name = "CatchView", antiAliasing = 4 };
        stageCam.targetTexture = stageTexture;
        stageView.texture = stageTexture;
    }

    /// <summary>동물 피규어를 키 height 로 세운다. 피벗은 발밑(받침 바닥 가운데).</summary>
    Transform BuildCreature(Mesh body, float height, int layer)
    {
        var pivot = new GameObject("Monster_" + speciesId).transform;
        pivot.SetParent(stage.transform, false);
        Bounds b = body.bounds;
        float k = height / Mathf.Max(b.size.y, 1e-4f);
        var go = new GameObject("Figure", typeof(MeshFilter), typeof(MeshRenderer));
        go.layer = layer;
        go.transform.SetParent(pivot, false);
        go.transform.localScale = Vector3.one * k;
        go.transform.localPosition = new Vector3(-b.center.x, -b.min.y, -b.center.z) * k;
        go.GetComponent<MeshFilter>().sharedMesh = body;
        go.GetComponent<MeshRenderer>().sharedMaterial = CreatureLibrary.Material;
        if (CreatureLibrary.TryGetGlass(speciesId, out Mesh glass) && CreatureLibrary.GlassMaterial != null)
        {
            var g = new GameObject("Glass", typeof(MeshFilter), typeof(MeshRenderer));
            g.layer = layer;
            g.transform.SetParent(go.transform, false);
            g.GetComponent<MeshFilter>().sharedMesh = glass;
            g.GetComponent<MeshRenderer>().sharedMaterial = CreatureLibrary.GlassMaterial;
        }
        return pivot;
    }

    // ---------- AR 끔 배경: 잔디밭 + 나무 두 그루 ----------

    static readonly Color Grass = new Color(0.47f, 0.76f, 0.36f);
    static readonly Color GrassFar = new Color(0.62f, 0.84f, 0.48f);
    static readonly Color Trunk = new Color(0.52f, 0.36f, 0.24f);
    static readonly Color Leaf = new Color(0.30f, 0.62f, 0.32f);
    static readonly Color LeafLight = new Color(0.42f, 0.74f, 0.38f);

    GameObject BuildScenery(int layer)
    {
        var sceneryRoot = new GameObject("Scenery");
        sceneryRoot.transform.SetParent(stage.transform, false);

        // 잔디밭: 가까운 쪽은 진하고 지평선 쪽은 옅은 큰 원판
        AddMesh(sceneryRoot.transform, "Ground", DiscMesh(80f, 48, Grass, GrassFar), Vector3.zero, Vector3.one, layer);

        // 나무 두 그루 (경희몬 뒤 왼쪽·오른쪽, 지도 나무처럼 각진 원뿔 잎).
        // 세로 폰 화면은 가로로 좁으므로(화각 약 ±11°) 경희몬 가까이 둔다.
        AddTree(sceneryRoot.transform, new Vector3(-0.95f, 0f, 2.6f), 0.95f, layer);
        AddTree(sceneryRoot.transform, new Vector3(1.25f, 0f, 4.4f), 1.2f, layer);
        return sceneryRoot;
    }

    void AddTree(Transform parent, Vector3 pos, float scale, int layer)
    {
        var tree = new GameObject("Tree").transform;
        tree.SetParent(parent, false);
        tree.localPosition = pos;
        tree.localScale = Vector3.one * scale;
        AddMesh(tree, "Trunk", ConeMesh(0.13f, 0.11f, 0.7f, 7, Trunk, Trunk), Vector3.zero, Vector3.one, layer);
        AddMesh(tree, "Leaves1", ConeMesh(0.75f, 0f, 1.1f, 8, Leaf, LeafLight), new Vector3(0f, 0.55f, 0f), Vector3.one, layer);
        AddMesh(tree, "Leaves2", ConeMesh(0.58f, 0f, 0.95f, 8, Leaf, LeafLight), new Vector3(0f, 1.15f, 0f), Vector3.one, layer);
        AddMesh(tree, "Leaves3", ConeMesh(0.40f, 0f, 0.8f, 8, Leaf, LeafLight), new Vector3(0f, 1.7f, 0f), Vector3.one, layer);
    }

    void AddMesh(Transform parent, string name, Mesh mesh, Vector3 pos, Vector3 scale, int layer)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        // 동물 피규어와 같은 정점 색 재질 → 같은 토이 느낌, 빌드에 셰이더가 빠질 걱정도 없다
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = CreatureLibrary.Material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    /// <summary>가운데 색 → 가장자리 색으로 바뀌는 원판 (y = 0, 위를 봄)</summary>
    static Mesh DiscMesh(float radius, int segments, Color center, Color edge)
    {
        var verts = new List<Vector3> { Vector3.zero };
        var cols = new List<Color> { center };
        var tris = new List<int>();
        for (int i = 0; i <= segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            verts.Add(new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
            cols.Add(edge);
        }
        for (int i = 1; i <= segments; i++)
        {
            tris.Add(0); tris.Add(i + 1); tris.Add(i);
        }
        var m = new Mesh { name = "Disc" };
        m.SetVertices(verts);
        m.SetColors(cols);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        return m;
    }

    /// <summary>각진 원뿔(아래 반지름 r0, 위 반지름 r1). 면마다 법선을 따로 둬서 로우폴리 느낌</summary>
    static Mesh ConeMesh(float r0, float r1, float height, int sides, Color bottom, Color top)
    {
        var verts = new List<Vector3>();
        var cols = new List<Color>();
        var tris = new List<int>();
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
            Vector3 b0 = new Vector3(Mathf.Cos(a0) * r0, 0f, Mathf.Sin(a0) * r0);
            Vector3 b1 = new Vector3(Mathf.Cos(a1) * r0, 0f, Mathf.Sin(a1) * r0);
            Vector3 t0 = new Vector3(Mathf.Cos(a0) * r1, height, Mathf.Sin(a0) * r1);
            Vector3 t1 = new Vector3(Mathf.Cos(a1) * r1, height, Mathf.Sin(a1) * r1);
            // 옆면 (사각형 = 삼각형 2개, 위쪽이 원뿔 꼭지면 하나는 면적 0)
            int s = verts.Count;
            verts.Add(b0); verts.Add(b1); verts.Add(t1); verts.Add(t0);
            cols.Add(bottom); cols.Add(bottom); cols.Add(top); cols.Add(top);
            tris.Add(s); tris.Add(s + 3); tris.Add(s + 2);
            tris.Add(s); tris.Add(s + 2); tris.Add(s + 1);
            // 밑면
            int c = verts.Count;
            verts.Add(Vector3.zero); verts.Add(b0); verts.Add(b1);
            cols.Add(bottom); cols.Add(bottom); cols.Add(bottom);
            tris.Add(c); tris.Add(c + 1); tris.Add(c + 2);
        }
        var m = new Mesh { name = "Cone" };
        m.SetVertices(verts);
        m.SetColors(cols);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        return m;
    }

    static Texture2D VerticalGradient(Color top, Color bottom)
    {
        const int n = 64;
        var tex = new Texture2D(1, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Sky" };
        for (int j = 0; j < n; j++)
            tex.SetPixel(0, j, Color.Lerp(bottom, top, Mathf.SmoothStep(0f, 1f, j / (float)(n - 1))));
        tex.Apply(false, true);
        return tex;
    }

    // ---------- 매 프레임 ----------

    void Update()
    {
        if (!IsOpen || monster == null)
            return;
        EnsureStageTexture();

        if (arOn && webcam != null && webcam.isPlaying && webcam.width > 16)
            PhotoCaptureScreen.ShowCameraPreview(camView, camFitter, webcam, fill: true);

        // 가운데에서 통통 뛰며 카메라(-Z) 쪽을 보고, 살짝 몸을 흔든다. 끌면 그만큼 돈다.
        phase += Time.deltaTime;
        float jump = Mathf.Max(0f, Mathf.Sin(phase * 2.4f));
        float hop = jump * jump * monsterHeight * 0.12f;
        float squash = 1f + 0.05f * Mathf.Sin(phase * 4.8f);
        monster.localPosition = new Vector3(0f, hop, 0f);
        monster.localScale = new Vector3(1f / Mathf.Sqrt(squash), squash, 1f / Mathf.Sqrt(squash));
        monster.localRotation = Quaternion.Euler(0f, 180f + spin + Mathf.Sin(phase * 0.9f) * 12f, 0f);
        // 높이 뛸수록 그림자가 옅어진다.
        float lift = hop / (monsterHeight * 0.12f + 1e-4f);
        shadow.GetComponent<SpriteRenderer>().color = new Color(0f, 0f, 0f, 0.4f * (1f - 0.4f * lift));
        spin = Mathf.Lerp(spin, 0f, 1f - Mathf.Exp(-0.8f * Time.deltaTime)); // 놓으면 천천히 정면으로

        if (flashAlpha > 0f)
        {
            flashAlpha = Mathf.MoveTowards(flashAlpha, 0f, Time.unscaledDeltaTime * 2.5f);
            flash.color = new Color(1f, 1f, 1f, flashAlpha);
        }
    }

    // ---------- 찍기 ----------

    void Shoot()
    {
        if (busy)
            return;
        StartCoroutine(Capture());
    }

    IEnumerator Capture()
    {
        busy = true;
        controls.alpha = 0f;
        controls.blocksRaycasts = false;
        yield return new WaitForEndOfFrame();
        Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
        controls.alpha = 1f;
        flashAlpha = 1f;
        flash.color = Color.white;

        shot = PhotoCaptureScreen.Downscale(shot, maxPhotoSize);
        string file = $"{DateTime.Now:yyyyMMdd_HHmmss}_{speciesId}.jpg";
        string path = Path.Combine(GameProgress.ScrapbookDir, file);
        try
        {
            Directory.CreateDirectory(GameProgress.ScrapbookDir);
            File.WriteAllBytes(path, shot.EncodeToJPG(jpgQuality));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CatchCamera] 사진을 저장하지 못했습니다: {e.Message}");
            file = "";
        }

        GameProgress.Caught caught = owner != null ? owner.CompleteCatch(type, target, file, speciesId) : null;
        target = null;
        lastPhoto = shot;
        lastPhotoPath = string.IsNullOrEmpty(file) ? null : path;

        yield return new WaitForSecondsRealtime(0.35f);
        ShowResult(caught);
    }

    void ShowResult(GameProgress.Caught caught)
    {
        monster.gameObject.SetActive(false);
        shadow.gameObject.SetActive(false);
        resultPhoto.texture = lastPhoto;
        resultFitter.aspectRatio = lastPhoto != null && lastPhoto.height > 0 ? (float)lastPhoto.width / lastPhoto.height : 0.5625f;
        resultTitle.text = $"{displayName}{Josa(displayName, "을", "를")} 잡았다!";
        resultSub.text = caught != null
            ? $"쿠옹력 {caught.cp}  ·  +{type.points} XP\n사진은 프로필 › 스크랩북에 저장됐어요"
            : "사진을 저장했어요";
        albumButton.interactable = lastPhotoPath != null;
        UIKit.SetLabel(albumButton, "앨범에도 저장");
        resultCard.SetActive(true);
        resultCard.transform.SetAsLastSibling();
        controls.blocksRaycasts = false;
    }

    void SaveToAlbum()
    {
        if (lastPhotoPath == null)
            return;
        NativeGallery.SaveImageToGallery(lastPhotoPath, "KUCA", Path.GetFileName(lastPhotoPath), (success, _) =>
        {
            UIKit.SetLabel(albumButton, success ? "앨범에 저장됨" : "저장하지 못했어요");
            albumButton.interactable = !success;
        });
    }

    // ---------- 도구 ----------

    /// <summary>받침이 있으면 withFinal(이/을), 없으면 withoutFinal(가/를)</summary>
    public static string Josa(string word, string withFinal, string withoutFinal)
    {
        if (string.IsNullOrEmpty(word))
            return withFinal;
        char c = word[word.Length - 1];
        bool hasFinal = c >= 0xAC00 && c <= 0xD7A3 && (c - 0xAC00) % 28 != 0;
        return hasFinal ? withFinal : withoutFinal;
    }

    static void Shadowed(Text t)
    {
        var o = t.gameObject.AddComponent<Outline>();
        o.effectColor = new Color(0f, 0f, 0f, 0.6f);
        o.effectDistance = new Vector2(2f, -2f);
    }

    static Button RoundIconButton(Transform parent, string name, Sprite icon, float size, UnityEngine.Events.UnityAction onClick)
    {
        Image bg = UIKit.Image(parent, name, new Color(0.05f, 0.08f, 0.18f, 0.55f), raycast: true);
        bg.sprite = HudIcons.Circle;
        bg.rectTransform.sizeDelta = new Vector2(size, size);
        var b = bg.gameObject.AddComponent<Button>();
        b.transition = Selectable.Transition.None;
        b.onClick.AddListener(onClick);
        bg.gameObject.AddComponent<PressScale>();
        Image ic = UIKit.Image(bg.transform, "Icon", Color.white);
        ic.sprite = icon;
        UIKit.Stretch(ic.rectTransform, size * 0.25f, size * 0.25f);
        return b;
    }

    static Button PillButton(Transform parent, string label, Color bg, Color fg, UnityEngine.Events.UnityAction onClick)
    {
        Button b = UIKit.Button(parent, label, onClick, bg, 40);
        Image img = b.GetComponent<Image>();
        img.sprite = HudIcons.Pill;
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = 1.1f;
        b.targetGraphic = img;
        Text t = b.GetComponentInChildren<Text>();
        t.color = fg;
        t.fontStyle = FontStyle.Bold;
        return b;
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

    static void TopBox(RectTransform rt, float top, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(30f, -top - height);
        rt.offsetMax = new Vector2(-30f, -top);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => IsOpen = false;
}

/// <summary>화면을 끈 만큼(픽셀) 알려 준다.</summary>
public class CatchDragArea : MonoBehaviour, IDragHandler
{
    public Action<Vector2> onDrag;
    public void OnDrag(PointerEventData e) => onDrag?.Invoke(e.delta);
}
