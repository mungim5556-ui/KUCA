using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 경희스팟 메모판. 지도에서 경희스팟(또는 건물)을 탭하면 열린다.
/// - 메모판: 그 장소에 남긴 사진들을 종이 위 스티커처럼 기울여 붙인 콜라주로 보여 준다. 누르면 크게 보고 글을 읽는다.
///   오른쪽 위 작은 원형 버튼으로 새 메모를 쓴다 (경희스팟 근처에서만).
/// - 새 게시물: 사진 + 캡션 + 닉네임 + 위치를 넣어 공유한다 (SNS 새 게시물 화면처럼).
/// serverUrl 이 비어 있으면 기기 안의 가짜 서버(MockMemoService)를 쓴다.
/// </summary>
public partial class MemoBoard : MonoBehaviour
{
    public CollectController collectController;
    public PhotoCaptureScreen photoCapture;
    public KyungHeeSpots spots;

    [Tooltip("켜면 경희스팟이 없는 건물도 탭해서 메모판을 연다 (쓰기는 항상 가능)")]
    public bool openOnBuildingTap = false;
    [Tooltip("켜면 경희스팟 근처(interactRadius)에서만 메모를 쓸 수 있다")]
    public bool requireProximityToWrite = true;

    [Tooltip("메모 서버 기본 주소 (Azure: https://kuca-memo.azurewebsites.net). 비우면 기기 안의 가짜 서버. 앱 설정에서 다른 주소를 저장하면 그쪽을 쓴다")]
    public string serverUrl = DefaultServer;

    /// <summary>배포된 메모 서버 (server/deploy-azure.sh)</summary>
    public const string DefaultServer = "https://kuca-memo.azurewebsites.net";

    /// <summary>앱 설정 화면에서 바꾼 서버 주소. 키가 없으면 기본 주소(serverUrl)를 쓴다</summary>
    public const string ServerUrlKey = "memo_server_url";

    /// <summary>설정에서 따로 정하지 않았을 때 쓰는 주소</summary>
    public string DefaultServerUrl => (serverUrl ?? "").Trim();

    const string AuthorKey = "memo_author";
    const string DeviceKey = "memo_device_id";
    const string MineKey = "memo_mine";

    // 콜라주 (종이·잉크)
    static readonly Color Paper = new Color(0.90f, 0.90f, 0.89f);
    static readonly Color PaperDark = new Color(0.83f, 0.83f, 0.82f);
    static readonly Color Ink = new Color(0.11f, 0.11f, 0.12f);
    static readonly Color InkQuiet = new Color(0.11f, 0.11f, 0.12f, 0.55f);
    static readonly Color NotePaper = new Color(1f, 0.98f, 0.91f);
    // 새 게시물 (흰 화면)
    static readonly Color Line = new Color(0f, 0f, 0f, 0.08f);
    static readonly Color Chip = new Color(0.94f, 0.95f, 0.96f);
    static readonly Color ShareBlue = new Color(0.29f, 0.37f, 0.98f);
    static readonly Color ErrorRed = new Color(0.86f, 0.25f, 0.25f);
    static readonly Color LikeRed = new Color(0.93f, 0.25f, 0.33f);

    IMemoService service;
    string serviceUrl;
    CampusBuildingInfo building;
    readonly List<Texture2D> loadedPhotos = new List<Texture2D>();
    readonly List<(RectTransform holder, MemoData memo)> stickers = new List<(RectTransform, MemoData)>();
    float laidOutWidth;

    GameObject root, listView, writeView, viewer;
    Text title, subtitle, listStatus, writeError, writeHint, locationText;
    RectTransform collage;
    ScrollRect collageScroll;
    Button writeButton, postButton, photoButton, galleryButton, removePhotoButton;
    Image writeButtonFace;
    InputField authorInput, textInput;
    RawImage photoPreview;
    AspectRatioFitter photoFitter;
    GameObject photoEmpty;
    byte[] photoJpg;
    Texture2D photoTexture;
    bool posting;
    KyungHeeSpot currentSpot;
    bool canWrite;

    // 게시물 보기 (인스타그램 게시물처럼: 작성자 · 사진 · 좋아요 · 캡션)
    RawImage viewerPhoto;
    Image viewerAvatar, viewerHeart;
    Text viewerAvatarLetter, viewerAuthor, viewerLikes;
    bool liking;
    LayoutElement viewerPhotoSize;
    Button viewerShare;

    // 작성자 프로필 (이 장소에 그 사람이 남긴 게시물)
    GameObject profile;
    Image profileAvatar;
    Text profileLetter, profileName, profileCount, profileEmpty;
    RectTransform profileGrid;
    readonly List<MemoData> currentMemos = new List<MemoData>();
    AspectRatioFitter viewerFitter;
    Text viewerText;
    Button viewerDelete;
    MemoData viewerMemo;

    public static string DeviceId
    {
        get
        {
            string id = PlayerPrefs.GetString(DeviceKey, "");
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString();
                PlayerPrefs.SetString(DeviceKey, id);
            }
            return id;
        }
    }

    void Awake()
    {
        EnsureService();
        Canvas canvas = UIKit.CreateCanvas(transform, "MemoCanvas", 30);
        BuildUI(canvas.transform);
        if (photoCapture != null)
            photoCapture.Build(canvas.transform);
    }

    void OnEnable()
    {
        if (collectController != null)
        {
            collectController.SpotTapped += OpenSpot;
            collectController.BuildingTapped += OnBuildingTapped;
        }
    }

    void OnBuildingTapped(CampusBuildingInfo info)
    {
        if (openOnBuildingTap)
            Open(info);
    }

    public void OpenSpot(KyungHeeSpot spot)
    {
        currentSpot = spot;
        OpenBoard(spot.Building);
    }

    public void Open(CampusBuildingInfo target)
    {
        currentSpot = null;
        OpenBoard(target);
    }

    void OnDisable()
    {
        if (collectController != null)
        {
            collectController.SpotTapped -= OpenSpot;
            collectController.BuildingTapped -= OnBuildingTapped;
        }
        UIInputBlocker.SetModal(this, false);
    }

    /// <summary>쓸 서버 주소: 설정에서 저장한 값이 있으면 그것, 없으면 인스펙터의 serverUrl</summary>
    /// <summary>메모 서버 주소 (산책 추천·공지도 같은 서버를 쓴다). 비어 있으면 기기 안 가짜 서버</summary>
    public string ServerUrl => CurrentServerUrl;

    string CurrentServerUrl => (PlayerPrefs.HasKey(ServerUrlKey) ? PlayerPrefs.GetString(ServerUrlKey) : serverUrl ?? "").Trim();

    /// <summary>설정에서 서버 주소가 바뀌었으면 새 주소로 다시 만든다.</summary>
    void EnsureService()
    {
        string url = CurrentServerUrl;
        if (service != null && url == serviceUrl)
            return;
        serviceUrl = url;
        service = url.Length == 0 ? new MockMemoService() : new HttpMemoService(url);
    }

    void OpenBoard(CampusBuildingInfo target)
    {
        EnsureService();
        building = target;
        locked = !IsVisited(target);
        lockedCount = 0;
        album.SetActive(false);
        albumMode = false;
        root.SetActive(true);
        UIInputBlocker.SetModal(this, true);
        title.text = target.DisplayName;
        subtitle.text = service.Description;
        ShowList();
        SetBoardTab(0);
    }

    public void Close()
    {
        root.SetActive(false);
        viewer.SetActive(false);
        profile.SetActive(false);
        album.SetActive(false);
        albumMode = false;
        CloseShare();
        UIInputBlocker.SetModal(this, false);
        ClearList();
    }

    // ---------- 콜라주 ----------

    void ShowList()
    {
        listView.SetActive(true);
        writeView.SetActive(false);
        viewer.SetActive(false);
        UpdateWriteState();
        Reload();
    }

    void Reload()
    {
        ClearList();
        listStatus.text = "불러오는 중…";
        listStatus.gameObject.SetActive(true);
        string requested = building.buildingId;
        StartCoroutine(service.GetMemos(requested, DeviceId, (memos, error) =>
        {
            if (building == null || building.buildingId != requested || !root.activeSelf)
                return;
            if (error != null) { listStatus.text = error; return; }
            if (memos.Count == 0)
            {
                listStatus.text = "아직 남겨진 사진이 없어요\n오른쪽 위 메모 버튼으로 첫 사진을 붙여 보세요";
                return;
            }
            listStatus.gameObject.SetActive(false);
            lockedCount = locked ? memos.Count : 0;
            currentMemos.Clear();
            currentMemos.AddRange(memos);
            LayoutCollage(memos);
        }));
    }

    void ClearList()
    {
        for (int i = collage.childCount - 1; i >= 0; i--)
            Destroy(collage.GetChild(i).gameObject);
        stickers.Clear();
        foreach (Texture2D t in loadedPhotos)
            if (t != null)
                Destroy(t);
        loadedPhotos.Clear();
    }

    /// <summary>
    /// 두 줄로 쌓되, 크기·기울기·좌우 위치를 메모마다 조금씩 다르게 해서 종이 위에 붙인 스티커처럼 보이게 한다.
    /// 값은 메모 id 로 정하므로 열 때마다 같은 자리에 붙는다. 화면 너비가 바뀌면 Update 에서 다시 배치한다.
    /// </summary>
    /// <summary>
    /// 대표 사진 (B-5): 최근 7일 동안 올라온 사진 중 좋아요가 가장 많은 것 (같으면 최신).
    /// 좋아요가 하나도 없으면 대표 사진을 두지 않는다. 검토 대기 사진은 제외.
    /// </summary>
    static MemoData PickRepresentative(List<MemoData> memos)
    {
        DateTime since = DateTime.Now.AddDays(-7);
        MemoData best = null;
        foreach (MemoData m in memos)
        {
            if (!m.HasPhoto || m.IsPending || m.likeCount <= 0 || m.CreatedAtLocal < since)
                continue;
            if (best == null || m.likeCount > best.likeCount ||
                (m.likeCount == best.likeCount && m.CreatedAtLocal > best.CreatedAtLocal))
                best = m;
        }
        return best;
    }

    string representativeId;

    void LayoutCollage(List<MemoData> memos)
    {
        stickers.Clear();
        MemoData rep = PickRepresentative(memos);
        representativeId = rep?.id;
        if (rep != null)
        {
            memos = new List<MemoData>(memos);
            memos.Remove(rep);
            memos.Insert(0, rep); // 대표 사진은 맨 위에 크게
        }
        foreach (MemoData m in memos)
            stickers.Add((Sticker(m), m));
        Relayout();
    }

    void Relayout()
    {
        Canvas.ForceUpdateCanvases();
        float width = ((RectTransform)collageScroll.viewport).rect.width;
        laidOutWidth = width;
        const float pad = 40f, gap = 22f, overlap = 34f, border = 14f;
        float colW = (width - pad * 2f - gap) / 2f;
        float[] heights = { 24f, 120f }; // 오른쪽 줄은 조금 내려서 시작
        float[] aspects = { 0.78f, 1f, 1.32f, 0.88f };

        foreach (var (holder, m) in stickers)
        {
            if (holder == null)
                continue;
            if (m.id == representativeId)
            {
                // 대표 사진: 두 줄을 가로질러 맨 위에 크게
                float rw = width - pad * 2f - border * 2f, rh = rw * 0.72f;
                holder.anchoredPosition = new Vector2(width * 0.5f, -(70f + rh * 0.5f + border));
                holder.sizeDelta = new Vector2(rw, rh) + Vector2.one * border * 2f;
                holder.localRotation = Quaternion.Euler(0f, 0f, -1.5f);
                float below = 70f + rh + border * 2f + 30f;
                heights[0] = below;
                heights[1] = below + 60f;
                continue;
            }
            var rng = new System.Random(StableHash(m.id));
            int col = heights[0] <= heights[1] ? 0 : 1;
            float aspect = m.HasPhoto ? aspects[rng.Next(aspects.Length)] : 1f;
            // 크기를 들쭉날쭉하게 (줄 너비의 62~88%)
            float w = (colW - border * 2f) * (0.66f + (float)rng.NextDouble() * 0.28f);
            float h = w / aspect;
            float slack = colW - w - border * 2f; // 테두리까지 줄 너비 안에 들어가게
            float x = pad + col * (colW + gap) + w * 0.5f + border + slack * (float)rng.NextDouble();
            float y = heights[col] + h * 0.5f + border;
            float angle = ((float)rng.NextDouble() - 0.5f) * 14f;
            holder.anchoredPosition = new Vector2(x, -y);
            holder.sizeDelta = new Vector2(w, h) + Vector2.one * border * 2f;
            holder.localRotation = Quaternion.Euler(0f, 0f, angle);
            heights[col] += h + border * 2f - overlap + (float)rng.NextDouble() * 24f;
        }
        collage.sizeDelta = new Vector2(collage.sizeDelta.x, Mathf.Max(heights[0], heights[1]) + 220f);
    }

    /// <summary>스티커 하나를 만든다 (자리와 크기는 Relayout 이 정한다).</summary>
    RectTransform Sticker(MemoData m)
    {
        const float border = 14f;
        var holder = new GameObject(m.id, typeof(RectTransform)).GetComponent<RectTransform>();
        holder.SetParent(collage, false);
        holder.anchorMin = holder.anchorMax = new Vector2(0f, 1f);
        holder.pivot = new Vector2(0.5f, 0.5f);

        // 종이 그림자 + 흰 테두리 (누르면 크게 보기)
        Image shadow = UIKit.Image(holder, "Shadow", new Color(0f, 0f, 0f, 0.22f));
        shadow.sprite = HudIcons.TornPaper;
        shadow.type = Image.Type.Sliced;
        UIKit.Stretch(shadow.rectTransform);
        shadow.rectTransform.anchoredPosition = new Vector2(7f, -10f);
        Image paper = UIKit.Image(holder, "Paper", m.HasPhoto ? Color.white : NotePaper, raycast: true);
        paper.sprite = HudIcons.TornPaper;
        paper.type = Image.Type.Sliced;
        UIKit.Stretch(paper.rectTransform);
        var b = paper.gameObject.AddComponent<Button>();
        b.transition = Selectable.Transition.None;
        bool hide = locked; // 가 보지 않은 스팟: 흐리게만 보이고 열리지 않는다 (B-2)
        b.onClick.AddListener(() =>
        {
            if (hide) ShowToast("이 경희스팟에 가 보면 쪽지가 열려요");
            else OpenViewer(m);
        });
        paper.gameObject.AddComponent<PressScale>();

        if (!m.HasPhoto)
        {
            // 사진이 없는 예전 메모는 쪽지처럼 (가려졌으면 글 대신 자물쇠)
            if (hide)
            {
                LockMark(paper.transform);
                return holder;
            }
            Text t = UIKit.Text(paper.transform, Clip(m.text, 60), 32, TextAnchor.UpperLeft, Ink);
            UIKit.Stretch(t.rectTransform, border + 18f, border + 18f);
            t.verticalOverflow = VerticalWrapMode.Truncate;
            return holder;
        }

        // 사진 (기울어진 채로 잘리도록 스텐실 마스크)
        Image frame = UIKit.Image(paper.transform, "Frame", Color.white);
        frame.sprite = HudIcons.Rounded;
        frame.type = Image.Type.Sliced;
        frame.pixelsPerUnitMultiplier = 4f;
        UIKit.Stretch(frame.rectTransform, border, border);
        frame.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        Image empty = UIKit.Image(frame.transform, "Loading", PaperDark);
        UIKit.Stretch(empty.rectTransform);
        var go = new GameObject("Photo", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
        go.transform.SetParent(frame.transform, false);
        var raw = go.GetComponent<RawImage>();
        raw.raycastTarget = false;
        raw.enabled = false;
        var fit = go.GetComponent<AspectRatioFitter>();
        fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;

        if (hide)
            LockMark(paper.transform);
        if (m.id == representativeId)
        {
            // "★ 이번 주 대표 사진" 띠
            Image crown = UIKit.Image(holder, "Representative", new Color(0.96f, 0.72f, 0.18f));
            crown.sprite = HudIcons.Pill;
            crown.type = Image.Type.Sliced;
            crown.pixelsPerUnitMultiplier = 120f / 64f;
            Place(crown.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, 30f));
            crown.rectTransform.sizeDelta = new Vector2(360f, 64f);
            Text ct = UIKit.Text(crown.transform, $"★ 이번 주 대표 사진  ·  ♥ {m.likeCount}", 28, TextAnchor.MiddleCenter, Color.white);
            ct.fontStyle = FontStyle.Bold;
            UIKit.Stretch(ct.rectTransform);
        }
        if (m.IsPending)
        {
            // 검토 대기: 나에게만 보이는 사진이라는 표시
            Image tag = UIKit.Image(paper.transform, "Pending", new Color(0.88f, 0.54f, 0.12f));
            tag.sprite = HudIcons.Pill;
            tag.type = Image.Type.Sliced;
            tag.pixelsPerUnitMultiplier = 120f / 48f;
            Place(tag.rectTransform, new Vector2(0f, 1f), new Vector2(border + 10f, -border - 10f));
            tag.rectTransform.sizeDelta = new Vector2(130f, 48f);
            Text tt = UIKit.Text(tag.transform, "검토 중", 24, TextAnchor.MiddleCenter, Color.white);
            tt.fontStyle = FontStyle.Bold;
            UIKit.Stretch(tt.rectTransform);
        }
        StartCoroutine(service.LoadPhoto(m, (tex, error) =>
        {
            if (raw == null || tex == null)
                return;
            // 내려받은 사진만 나중에 지운다 (예시 사진은 에셋이라 지우면 안 됨)
            bool asset = m.photoUrl.StartsWith("resource://");
            if (hide)
            {
                Texture2D blurred = Blurred(tex);
                if (!asset) Destroy(tex);
                tex = blurred;
                loadedPhotos.Add(tex);
                raw.color = new Color(0.85f, 0.85f, 0.85f);
            }
            else if (!asset) loadedPhotos.Add(tex);
            raw.texture = tex;
            raw.enabled = true;
            fit.aspectRatio = (float)tex.width / Mathf.Max(1, tex.height);
            empty.enabled = false;
        }));
        return holder;
    }

    // ---------- 크게 보기 ----------

    void OpenViewer(MemoData m)
    {
        viewerMemo = m;
        viewerPhoto.texture = null;
        viewerPhoto.enabled = false;
        SetAvatar(viewerAvatar, viewerAvatarLetter, m.author);
        string when = m.IsPending ? "<color=#E08A1E>검토 대기 · 나에게만 보여요</color>" : $"<color=#8C8C91>{FormatTime(m.CreatedAtLocal)}</color>";
        viewerAuthor.text = $"<b>{Escape(m.author)}</b>\n<size=26>{when}</size>";
        viewerText.text = $"<b>{Escape(m.author)}</b>  {Escape(m.text)}";
        viewerDelete.gameObject.SetActive(IsMine(m.id));
        viewerShare.gameObject.SetActive(albumMode && IsMine(m.id) && m.HasPhoto && !m.IsPending);
        RefreshLike();
        viewer.SetActive(true);
        viewer.transform.SetAsLastSibling();
        Canvas.ForceUpdateCanvases();
        viewerPhotoSize.preferredHeight = viewerPhotoSize.minHeight = ((RectTransform)viewerPhotoSize.transform).rect.width;
        if (m.HasPhoto)
            StartCoroutine(service.LoadPhoto(m, (tex, error) =>
            {
                if (tex == null || viewerMemo != m)
                    return;
                if (!m.photoUrl.StartsWith("resource://")) loadedPhotos.Add(tex);
                viewerPhoto.texture = tex;
                viewerPhoto.enabled = true;
                viewerFitter.aspectRatio = (float)tex.width / Mathf.Max(1, tex.height);
            }));
    }

    void RefreshLike()
    {
        if (viewerMemo == null)
            return;
        viewerHeart.sprite = viewerMemo.likedByMe ? HudIcons.Heart : HudIcons.HeartOutline;
        viewerHeart.color = viewerMemo.likedByMe ? LikeRed : Ink;
        viewerLikes.text = viewerMemo.likeCount > 0 ? $"좋아요 {viewerMemo.likeCount:N0}개" : "가장 먼저 좋아요를 눌러 보세요";
    }

    /// <summary>하트: 화면을 먼저 바꾸고(빠른 반응) 서버 응답으로 맞춘다. 실패하면 되돌린다.</summary>
    void ToggleLike()
    {
        MemoData m = viewerMemo;
        if (m == null || liking)
            return;
        if (m.IsPending)
        {
            ShowToast("검토가 끝나 공개되면 좋아요를 받을 수 있어요");
            return;
        }
        bool like = !m.likedByMe;
        int before = m.likeCount;
        m.likedByMe = like;
        m.likeCount = Mathf.Max(0, before + (like ? 1 : -1));
        RefreshLike();
        viewerHeart.transform.localScale = Vector3.one * 1.35f; // PressScale 이 원래 크기로 되돌리며 톡 튄다
        liking = true;
        StartCoroutine(service.SetLike(m.id, DeviceId, like, (count, likedByMe, error) =>
        {
            liking = false;
            if (error != null)
            {
                m.likedByMe = !like;
                m.likeCount = before;
                ShowToast(error);
            }
            else
            {
                m.likeCount = count;
                m.likedByMe = likedByMe;
                if (like && !IsMine(m.id))
                    Quests.Report("like_other", 1, m.id); // 같은 사진은 한 번만 센다
            }
            if (viewerMemo == m)
                RefreshLike();
        }));
    }

    // ---------- 작성자 프로필 ----------

    void OpenProfile(string author)
    {
        SetAvatar(profileAvatar, profileLetter, author);
        profileName.text = author;
        profile.SetActive(true);
        ClearGrid(profileGrid);
        var posts = currentMemos.FindAll(x => x.author == author && x.HasPhoto);
        string where = albumMode ? "게시물" : "이 장소의 게시물";
        profileCount.text = $"<b>{posts.Count}</b>\n<size=28>{where}</size>";
        profileEmpty.text = posts.Count == 0 ? (albumMode ? "올린 사진이 없어요" : "이 장소에 남긴 사진이 없어요") : "";
        foreach (MemoData m in posts)
        {
            MemoData memo = m;
            AddGridPhoto(profileGrid, m, () =>
            {
                profile.SetActive(false);
                OpenViewer(memo);
            });
        }
        profile.SetActive(true);
        profile.transform.SetAsLastSibling();
    }

    /// <summary>사진 격자를 비우고, 칸 크기를 지금 너비의 1/3 로 맞춘다 (격자가 켜진 상태에서 부른다).</summary>
    static void ClearGrid(RectTransform grid)
    {
        for (int i = grid.childCount - 1; i >= 0; i--)
        {
            Transform old = grid.GetChild(i);
            old.SetParent(null, false); // Destroy 는 프레임 끝이라, 격자에서 먼저 뺀다
            Destroy(old.gameObject);
        }
        Canvas.ForceUpdateCanvases();
        var g = grid.GetComponent<GridLayoutGroup>();
        float side = Mathf.Floor((grid.rect.width - g.spacing.x * 2f) / 3f);
        g.cellSize = new Vector2(side, side);
    }

    /// <summary>격자에 정사각형 사진 칸 하나 (누르면 onClick)</summary>
    Image AddGridPhoto(RectTransform grid, MemoData m, UnityEngine.Events.UnityAction onClick)
    {
        Image cell = UIKit.Image(grid, m.id, PaperDark, raycast: true);
        cell.gameObject.AddComponent<RectMask2D>();
        var b = cell.gameObject.AddComponent<Button>();
        b.transition = Selectable.Transition.None;
        b.onClick.AddListener(onClick);
        var go = new GameObject("Photo", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
        go.transform.SetParent(cell.transform, false);
        var raw = go.GetComponent<RawImage>();
        raw.raycastTarget = false;
        raw.enabled = false;
        var fit = go.GetComponent<AspectRatioFitter>();
        fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        StartCoroutine(service.LoadPhoto(m, (tex, error) =>
        {
            if (raw == null || tex == null)
                return;
            if (!m.photoUrl.StartsWith("resource://")) loadedPhotos.Add(tex);
            raw.texture = tex;
            raw.enabled = true;
            fit.aspectRatio = (float)tex.width / Mathf.Max(1, tex.height);
        }));
        return cell;
    }

    /// <summary>area 를 채우는 세로 스크롤 3열 사진 격자 (돌려주는 것은 칸이 들어갈 Content)</summary>
    static RectTransform BuildGrid(RectTransform area)
    {
        var scroll = area.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        RectTransform viewport = UIKit.Rect(area, "Viewport");
        UIKit.Stretch(viewport);
        viewport.gameObject.AddComponent<RectMask2D>();
        viewport.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        RectTransform content = UIKit.Rect(viewport, "Content");
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.offsetMin = content.offsetMax = Vector2.zero;
        var g = content.gameObject.AddComponent<GridLayoutGroup>();
        g.cellSize = new Vector2(330f, 330f);
        g.spacing = new Vector2(6f, 6f);
        g.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        g.constraintCount = 3;
        g.childAlignment = TextAnchor.UpperLeft;
        content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewport;
        scroll.content = content;
        return content;
    }

    static readonly Color[] AvatarColors =
    {
        new Color(0.91f, 0.45f, 0.42f), new Color(0.98f, 0.66f, 0.30f), new Color(0.40f, 0.72f, 0.45f),
        new Color(0.33f, 0.62f, 0.90f), new Color(0.60f, 0.47f, 0.86f), new Color(0.86f, 0.44f, 0.66f),
    };

    /// <summary>프로필 사진 대신 닉네임 첫 글자와, 닉네임마다 정해진 색의 원</summary>
    static void SetAvatar(Image circle, Text letter, string author)
    {
        string name = string.IsNullOrEmpty(author) ? "?" : author.Trim();
        circle.color = AvatarColors[(StableHash(name) & int.MaxValue) % AvatarColors.Length];
        letter.text = name.Length > 0 ? name.Substring(0, 1) : "?";
    }

    /// <summary>리치 텍스트로 넣을 사용자 글자에서 태그 기호를 무력화한다.</summary>
    static string Escape(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");

    static string FormatTime(DateTime t)
    {
        if (t == DateTime.MinValue) return "";
        TimeSpan ago = DateTime.Now - t;
        if (ago.TotalMinutes < 1) return "방금";
        if (ago.TotalHours < 1) return $"{(int)ago.TotalMinutes}분 전";
        if (ago.TotalDays < 1) return $"{(int)ago.TotalHours}시간 전";
        return t.ToString("M월 d일 HH:mm");
    }

    void Delete(MemoData m)
    {
        StartCoroutine(service.DeleteMemo(m.id, DeviceId, (ok, error) =>
        {
            // 이미 서버에서 지워진 메모면 지운 것으로 본다
            if (!ok && error != MemoData.NotFound) { ShowToast(error); return; }
            SetMine(m.id, false);
            viewer.SetActive(false);
            if (albumMode) ReloadAlbum();
            else Reload();
        }));
    }

    // ---------- 새 게시물 ----------

    /// <summary>경희스팟 근처에 있는지. 스팟이 아닌 건물에서 열었거나 거리 제한을 껐으면 항상 true.</summary>
    bool CanWrite(out float distance)
    {
        distance = 0f;
        if (!requireProximityToWrite || currentSpot == null || spots == null || collectController == null || collectController.campusMap == null)
            return true;
        distance = currentSpot.DistanceFrom(collectController.campusMap.player.position);
        return distance <= spots.interactRadius;
    }

    void Update()
    {
        if (toast != null && toast.activeSelf && Time.unscaledTime > toastUntil)
            toast.SetActive(false);
        if (root == null || !root.activeSelf || !listView.activeSelf)
            return;
        if (stickers.Count > 0 && Mathf.Abs(((RectTransform)collageScroll.viewport).rect.width - laidOutWidth) > 1f)
            Relayout();
        // 메모판을 연 채로 걸어서 스팟에 들어오면 (방문 기록이 생기면) 바로 연다.
        if (locked && building != null && IsVisited(building))
        {
            locked = false;
            ShowToast("쪽지가 열렸어요!");
            Reload();
        }
        // 걸어서 가까워지면 바로 쓰기 버튼이 켜지도록 매 프레임 확인한다.
        UpdateWriteState();
    }

    void UpdateWriteState()
    {
        canWrite = CanWrite(out float d);
        writeButtonFace.color = canWrite ? Ink : new Color(Ink.r, Ink.g, Ink.b, 0.35f);
        writeHint.transform.parent.gameObject.SetActive((!canWrite || locked) && boardTab == 0);
        if (locked)
            writeHint.text = lockedCount > 0
                ? $"아직 가 보지 않은 곳이에요  ·  {SpotDistance():F0}m 더 가면 쪽지 {lockedCount}개가 열려요"
                : $"아직 가 보지 않은 곳이에요  ·  경희스팟까지 {SpotDistance():F0}m";
        else if (!canWrite)
            writeHint.text = $"경희스팟까지 {d:F0}m  ·  {spots.interactRadius:F0}m 안에서 사진을 남길 수 있어요";
    }

    void OnWriteButton()
    {
        if (!canWrite)
        {
            ShowToast("경희스팟 가까이 가야 사진을 남길 수 있어요");
            return;
        }
        ShowWrite();
    }

    void ShowWrite()
    {
        listView.SetActive(false);
        viewer.SetActive(false);
        writeView.SetActive(true);
        authorInput.text = PlayerPrefs.GetString(AuthorKey, "");
        textInput.text = "";
        writeError.text = "";
        locationText.text = building != null ? building.DisplayName : "";
        SetPhoto(null, null);
    }

    void TakePhoto()
    {
        if (photoCapture == null) return;
        photoCapture.Open((jpg, tex) => SetPhoto(jpg, tex));
    }

    /// <summary>폰 앨범에서 사진을 고른다 (NativeGallery). 긴 변을 1280px 로 줄여 JPEG 로 올린다.</summary>
    void PickFromGallery()
    {
        NativeGallery.GetImageFromGallery(path =>
        {
            if (string.IsNullOrEmpty(path))
                return; // 고르지 않고 닫음
            Texture2D tex = NativeGallery.LoadImageAtPath(path, 1280, markTextureNonReadable: false, generateMipmaps: false);
            if (tex == null)
            {
                writeError.text = "사진을 불러오지 못했어요. 다른 사진을 골라 주세요.";
                return;
            }
            // 플러그인의 크기 제한이 적용되지 않는 경우(에디터 등)도 있어 한 번 더 줄인다.
            tex = PhotoCaptureScreen.Downscale(tex, 1280);
            SetPhoto(tex.EncodeToJPG(80), tex);
        }, "메모에 올릴 사진 고르기", "image/*");
    }

    void SetPhoto(byte[] jpg, Texture2D tex)
    {
        if (photoTexture != null && photoTexture != tex)
            Destroy(photoTexture);
        photoJpg = jpg;
        photoTexture = tex;
        photoPreview.texture = tex;
        photoPreview.enabled = tex != null;
        if (tex != null && tex.height > 0)
            photoFitter.aspectRatio = (float)tex.width / tex.height;
        photoEmpty.SetActive(tex == null);
        UIKit.SetLabel(photoButton, tex != null ? "다시 찍기" : "사진 찍기");
        removePhotoButton.gameObject.SetActive(tex != null);
        if (tex != null)
            writeError.text = "";
    }

    void Post()
    {
        if (posting) return;
        string author = authorInput.text.Trim();
        string text = textInput.text.Trim();
        if (photoJpg == null) { writeError.text = "사진을 추가해 주세요."; return; }
        if (text.Length == 0) { writeError.text = "캡션을 써 주세요."; return; }
        if (author.Length == 0) { writeError.text = "닉네임을 써 주세요."; return; }

        posting = true;
        UIKit.SetLabel(postButton, "공유하는 중…");
        writeError.text = "";
        PlayerPrefs.SetString(AuthorKey, author);
        var memo = new NewMemo { author = author, text = text, deviceId = DeviceId, photoJpg = photoJpg };
        StartCoroutine(service.CreateMemo(building.buildingId, memo, (created, error) =>
        {
            posting = false;
            UIKit.SetLabel(postButton, "공유하기");
            if (created == null) { writeError.text = error; return; }
            SetMine(created.id, true);
            Texture2D shot = photoTexture;
            photoTexture = null; // 공유 창이 쓰고 지운다
            SetPhoto(null, null);
            ShowList();
            if (created.IsPending)
            {
                // 서버 내용 검사에서 애매하다고 본 글: 검토가 끝날 때까지 나에게만 보인다.
                if (shot != null) Destroy(shot);
                ShowToast("검토 후 공개돼요 · 그때까지는 나에게만 보여요");
                return;
            }
            ShowToast("사진을 붙였어요!");
            OpenShare(created, shot, true);
        }));
    }

    /// <summary>안내 문구. 메모판이 열려 있으면 HUD 토스트가 메모판 뒤에 가려지므로 메모판 위에 띄운다.</summary>
    void ShowToast(string message)
    {
        if (string.IsNullOrEmpty(message))
            return;
        Debug.Log("[MemoBoard] " + message);
        if (root != null && root.activeSelf && toast != null)
        {
            toastLabel.text = message;
            toast.SetActive(true);
            toast.transform.SetAsLastSibling();
            toastUntil = Time.unscaledTime + 3.2f;
            return;
        }
        var hud = FindAnyObjectByType<GameHUD>();
        if (hud != null) hud.Toast(message);
    }

    GameObject toast;
    Text toastLabel;
    float toastUntil;

    void BuildToast(Transform canvas)
    {
        Image bg = UIKit.Image(canvas, "MemoToast", new Color(Ink.r, Ink.g, Ink.b, 0.88f));
        bg.sprite = HudIcons.Pill;
        bg.type = Image.Type.Sliced;
        bg.pixelsPerUnitMultiplier = 120f / 96f;
        RectTransform r = bg.rectTransform;
        r.anchorMin = new Vector2(0.06f, 0f);
        r.anchorMax = new Vector2(0.94f, 0f);
        r.pivot = new Vector2(0.5f, 0f);
        r.sizeDelta = new Vector2(0f, 120f);
        r.anchoredPosition = new Vector2(0f, 200f);
        toastLabel = UIKit.Text(r, "", 32, TextAnchor.MiddleCenter, Color.white);
        UIKit.Stretch(toastLabel.rectTransform, 40f, 8f);
        toastLabel.resizeTextForBestFit = true;
        toastLabel.resizeTextMinSize = 20;
        toastLabel.resizeTextMaxSize = 32;
        toast = bg.gameObject;
        toast.SetActive(false);
    }

    // ---------- 내 메모 기록 (서버는 작성 기기를 알려 주지 않으므로 기기에 따로 적어 둔다) ----------

    [Serializable] class IdList { public List<string> ids = new List<string>(); }

    static IdList Mine => JsonUtility.FromJson<IdList>(PlayerPrefs.GetString(MineKey, "{}")) ?? new IdList();

    static bool IsMine(string id) => Mine.ids.Contains(id);

    static void SetMine(string id, bool mine)
    {
        IdList list = Mine;
        list.ids.Remove(id);
        if (mine) list.ids.Add(id);
        PlayerPrefs.SetString(MineKey, JsonUtility.ToJson(list));
        PlayerPrefs.Save();
    }

    static int StableHash(string s)
    {
        int h = 17;
        if (s != null)
            foreach (char c in s)
                h = h * 31 + c;
        return h;
    }

    static string Clip(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    // ---------- 화면 만들기 ----------

    void BuildUI(Transform canvas)
    {
        root = UIKit.Image(canvas, "MemoBoard", Paper, raycast: true).gameObject;
        UIKit.Stretch((RectTransform)root.transform);
        Rect sa = Screen.safeArea;
        Vector2 safeMin = new Vector2(sa.xMin / Mathf.Max(1, Screen.width), sa.yMin / Mathf.Max(1, Screen.height));
        Vector2 safeMax = new Vector2(sa.xMax / Mathf.Max(1, Screen.width), sa.yMax / Mathf.Max(1, Screen.height));

        BuildCollageView(safeMin, safeMax);
        BuildWriteView(safeMin, safeMax);
        BuildViewer();
        BuildProfile();
        BuildAlbum();
        BuildShare();
        BuildToast(canvas);
        root.SetActive(false);
    }

    RectTransform SafeRect(Transform parent, string name, Vector2 min, Vector2 max)
    {
        RectTransform r = UIKit.Rect(parent, name);
        r.anchorMin = min;
        r.anchorMax = max;
        r.offsetMin = r.offsetMax = Vector2.zero;
        return r;
    }

    void BuildCollageView(Vector2 safeMin, Vector2 safeMax)
    {
        // 종이 배경 (위는 밝고 아래는 살짝 어둡게)
        var bg = new GameObject("PaperBg", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        bg.transform.SetParent(root.transform, false);
        bg.texture = HudIcons.Gradient(new Color(0.93f, 0.93f, 0.92f), new Color(0.84f, 0.84f, 0.83f));
        bg.raycastTarget = false;
        UIKit.Stretch(bg.rectTransform);

        listView = UIKit.Rect(root.transform, "Collage").gameObject;
        UIKit.Stretch((RectTransform)listView.transform);
        RectTransform safe = SafeRect(listView.transform, "Safe", safeMin, safeMax);

        // 콜라주 (자유 배치라 레이아웃 그룹 없이 내용 높이를 직접 정한다)
        RectTransform area = UIKit.Rect(safe, "Area");
        UIKit.StretchBetween(area, 0f, BoardTop);
        photoArea = area.gameObject;
        collageScroll = area.gameObject.AddComponent<ScrollRect>();
        collageScroll.horizontal = false;
        collageScroll.scrollSensitivity = 30f;
        RectTransform viewport = UIKit.Rect(area, "Viewport");
        UIKit.Stretch(viewport);
        viewport.gameObject.AddComponent<RectMask2D>();
        viewport.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        collage = UIKit.Rect(viewport, "Content");
        collage.anchorMin = new Vector2(0f, 1f);
        collage.anchorMax = new Vector2(1f, 1f);
        collage.pivot = new Vector2(0.5f, 1f);
        collage.offsetMin = collage.offsetMax = Vector2.zero;
        collage.sizeDelta = new Vector2(0f, 400f);
        collageScroll.viewport = viewport;
        collageScroll.content = collage;

        listStatus = UIKit.Text(area, "", 38, TextAnchor.MiddleCenter, InkQuiet);
        UIKit.Stretch(listStatus.rectTransform, 80f, 300f);

        // 위: 닫기 · 장소 이름 · 쓰기
        Button close = RoundButton(safe, "Close", HudIcons.Close, 96f, Color.white, Ink, Close);
        Place((RectTransform)close.transform, new Vector2(0f, 1f), new Vector2(32f, -40f));
        title = UIKit.Text(safe, "", 50, TextAnchor.MiddleCenter, Ink);
        title.fontStyle = FontStyle.Bold;
        TopBox(title.rectTransform, 40f, 70f, 150f);
        subtitle = UIKit.Text(safe, "", 26, TextAnchor.MiddleCenter, InkQuiet);
        TopBox(subtitle.rectTransform, 112f, 40f, 150f);
        BuildBoardTabs(safe);
        writeButton = RoundButton(safe, "Write", HudIcons.MemoWrite, 96f, Ink, Color.white, OnWriteButton);
        writeButtonFace = writeButton.transform.Find("Face").GetComponent<Image>();
        Place((RectTransform)writeButton.transform, new Vector2(1f, 1f), new Vector2(-32f, -40f));

        // 아래: 멀리 있을 때만 거리 안내
        Image hintPill = UIKit.Image(safe, "Hint", new Color(Ink.r, Ink.g, Ink.b, 0.82f));
        hintPill.sprite = HudIcons.Pill;
        hintPill.type = Image.Type.Sliced;
        hintPill.pixelsPerUnitMultiplier = 120f / 84f;
        Place(hintPill.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 40f));
        hintPill.rectTransform.sizeDelta = new Vector2(900f, 84f);
        writeHint = UIKit.Text(hintPill.transform, "", 28, TextAnchor.MiddleCenter, Color.white);
        UIKit.Stretch(writeHint.rectTransform, 24f, 0f);
    }

    void BuildViewer()
    {
        viewer = UIKit.Image(root.transform, "Post", Color.white, raycast: true).gameObject;
        UIKit.Stretch((RectTransform)viewer.transform);
        Rect sa = Screen.safeArea;
        RectTransform safe = SafeRect(viewer.transform, "Safe",
            new Vector2(sa.xMin / Mathf.Max(1, Screen.width), sa.yMin / Mathf.Max(1, Screen.height)),
            new Vector2(sa.xMax / Mathf.Max(1, Screen.width), sa.yMax / Mathf.Max(1, Screen.height)));

        // 위: 뒤로 · 장소 이름
        Button back = RoundButton(safe, "Back", HudIcons.Chevron, 88f, Color.white, Ink, () => viewer.SetActive(false));
        Place((RectTransform)back.transform, new Vector2(0f, 1f), new Vector2(32f, -30f));
        back.transform.Find("Face/Icon").localRotation = Quaternion.Euler(0f, 0f, 180f);
        back.transform.Find("Shadow").GetComponent<Image>().color = Line;
        Text head = UIKit.Text(safe, "게시물", 38, TextAnchor.MiddleCenter, Ink);
        head.fontStyle = FontStyle.Bold;
        TopBox(head.rectTransform, 30f, 88f, 140f);
        Image hr = UIKit.Image(safe, "Line", Line);
        TopBox(hr.rectTransform, 140f, 2f, 0f);

        RectTransform area = UIKit.Rect(safe, "Body");
        UIKit.StretchBetween(area, 0f, 142f);
        RectTransform body = UIKit.ScrollList(area, 0f, new RectOffset(0, 0, 0, 80));

        // 작성자 줄 (누르면 프로필)
        Image authorRow = UIKit.Image(body, "Author", new Color(0f, 0f, 0f, 0f), raycast: true);
        UIKit.Size(authorRow, 130f);
        var ab = authorRow.gameObject.AddComponent<Button>();
        ab.transition = Selectable.Transition.None;
        ab.onClick.AddListener(() => { if (viewerMemo != null) OpenProfile(viewerMemo.author); });
        viewerAvatar = Avatar(authorRow.transform, 84f, out viewerAvatarLetter);
        Place(viewerAvatar.rectTransform, new Vector2(0f, 0.5f), new Vector2(36f, 0f));
        viewerAuthor = UIKit.Text(authorRow.transform, "", 34, TextAnchor.MiddleLeft, Ink);
        viewerAuthor.rectTransform.anchorMin = Vector2.zero;
        viewerAuthor.rectTransform.anchorMax = Vector2.one;
        viewerAuthor.rectTransform.offsetMin = new Vector2(140f, 0f);
        viewerAuthor.rectTransform.offsetMax = new Vector2(-200f, 0f);
        viewerDelete = UIKit.Button(authorRow.transform, "삭제", () => { if (viewerMemo != null) Delete(viewerMemo); }, new Color(0f, 0f, 0f, 0f), 30);
        viewerDelete.GetComponentInChildren<Text>().color = ErrorRed;
        Place((RectTransform)viewerDelete.transform, new Vector2(1f, 0.5f), new Vector2(-24f, 0f));
        ((RectTransform)viewerDelete.transform).sizeDelta = new Vector2(150f, 80f);

        // 사진 (가로 꽉, 정사각형으로 잘라 보이기. 높이는 열 때 화면 너비에 맞춘다)
        RectTransform gap = UIKit.Rect(body, "Gap");
        UIKit.Size(gap, 20f);
        RectTransform photoRow = UIKit.Rect(body, "Photo");
        viewerPhotoSize = UIKit.Size(photoRow, 1000f);
        photoRow.gameObject.AddComponent<RectMask2D>();
        Image photoBg = UIKit.Image(photoRow, "Bg", PaperDark);
        UIKit.Stretch(photoBg.rectTransform);
        var go = new GameObject("Image", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
        go.transform.SetParent(photoRow, false);
        viewerPhoto = go.GetComponent<RawImage>();
        viewerPhoto.raycastTarget = false;
        viewerFitter = go.GetComponent<AspectRatioFitter>();
        viewerFitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;

        // 왼쪽 아래 하트
        RectTransform actions = UIKit.Rect(body, "Actions");
        UIKit.Size(actions, 110f);
        Image hit = UIKit.Image(actions, "Like", new Color(0f, 0f, 0f, 0f), raycast: true);
        Place(hit.rectTransform, new Vector2(0f, 0.5f), new Vector2(18f, 0f));
        hit.rectTransform.sizeDelta = new Vector2(100f, 100f);
        var lb = hit.gameObject.AddComponent<Button>();
        lb.transition = Selectable.Transition.None;
        lb.onClick.AddListener(ToggleLike);
        viewerHeart = UIKit.Image(hit.transform, "Heart", Ink);
        viewerHeart.sprite = HudIcons.HeartOutline;
        Place(viewerHeart.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        viewerHeart.rectTransform.sizeDelta = new Vector2(68f, 68f);
        viewerHeart.gameObject.AddComponent<PressScale>();
        // 사진첩에서 연 내 게시물은 하트 옆에 공유
        Image shareHit = UIKit.Image(actions, "Share", new Color(0f, 0f, 0f, 0f), raycast: true);
        Place(shareHit.rectTransform, new Vector2(0f, 0.5f), new Vector2(122f, 0f));
        shareHit.rectTransform.sizeDelta = new Vector2(100f, 100f);
        viewerShare = shareHit.gameObject.AddComponent<Button>();
        viewerShare.transition = Selectable.Transition.None;
        viewerShare.onClick.AddListener(() =>
        {
            if (viewerMemo != null && viewerPhoto.texture is Texture2D shown)
                OpenShare(viewerMemo, shown, false);
            else
                ShowToast("사진을 불러온 뒤에 공유할 수 있어요");
        });
        Image shareIcon = UIKit.Image(shareHit.transform, "Icon", Ink);
        shareIcon.sprite = HudIcons.Send;
        Place(shareIcon.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        shareIcon.rectTransform.sizeDelta = new Vector2(64f, 64f);
        shareIcon.gameObject.AddComponent<PressScale>();

        // 좋아요 수 · 캡션
        RectTransform likesRow = UIKit.Rect(body, "Likes");
        UIKit.Size(likesRow, 56f);
        viewerLikes = UIKit.Text(likesRow, "", 32, TextAnchor.MiddleLeft, Ink);
        viewerLikes.fontStyle = FontStyle.Bold;
        UIKit.Stretch(viewerLikes.rectTransform, 36f, 0f);
        RectTransform captionRow = UIKit.Rect(body, "Caption");
        UIKit.Vertical(captionRow, 0f, new RectOffset(36, 36, 8, 8));
        viewerText = UIKit.Text(captionRow, "", 34, TextAnchor.UpperLeft, Ink);

        viewer.SetActive(false);
    }

    void BuildProfile()
    {
        profile = UIKit.Image(root.transform, "Profile", Color.white, raycast: true).gameObject;
        UIKit.Stretch((RectTransform)profile.transform);
        Rect sa = Screen.safeArea;
        RectTransform safe = SafeRect(profile.transform, "Safe",
            new Vector2(sa.xMin / Mathf.Max(1, Screen.width), sa.yMin / Mathf.Max(1, Screen.height)),
            new Vector2(sa.xMax / Mathf.Max(1, Screen.width), sa.yMax / Mathf.Max(1, Screen.height)));

        Button back = RoundButton(safe, "Back", HudIcons.Chevron, 88f, Color.white, Ink, () => profile.SetActive(false));
        Place((RectTransform)back.transform, new Vector2(0f, 1f), new Vector2(32f, -30f));
        back.transform.Find("Face/Icon").localRotation = Quaternion.Euler(0f, 0f, 180f);
        back.transform.Find("Shadow").GetComponent<Image>().color = Line;
        Text head = UIKit.Text(safe, "프로필", 38, TextAnchor.MiddleCenter, Ink);
        head.fontStyle = FontStyle.Bold;
        TopBox(head.rectTransform, 30f, 88f, 140f);
        Image hr = UIKit.Image(safe, "Line", Line);
        TopBox(hr.rectTransform, 140f, 2f, 0f);

        // 프로필 사진 · 닉네임 · 게시물 수
        profileAvatar = Avatar(safe, 190f, out profileLetter);
        Place(profileAvatar.rectTransform, new Vector2(0f, 1f), new Vector2(50f, -190f));
        profileLetter.fontSize = 90;
        profileName = UIKit.Text(safe, "", 44, TextAnchor.MiddleLeft, Ink);
        profileName.fontStyle = FontStyle.Bold;
        TopBox(profileName.rectTransform, 410f, 70f, 0f);
        profileName.rectTransform.offsetMin = new Vector2(56f, profileName.rectTransform.offsetMin.y);
        profileCount = UIKit.Text(safe, "", 40, TextAnchor.MiddleCenter, Ink);
        Place(profileCount.rectTransform, new Vector2(0f, 1f), new Vector2(330f, -220f));
        profileCount.rectTransform.sizeDelta = new Vector2(320f, 130f);
        Image hr2 = UIKit.Image(safe, "Line2", Line);
        TopBox(hr2.rectTransform, 500f, 2f, 0f);

        // 사진 3열 격자
        RectTransform area = UIKit.Rect(safe, "Grid");
        UIKit.StretchBetween(area, 0f, 504f);
        profileGrid = BuildGrid(area);
        profileEmpty = UIKit.Text(area, "", 34, TextAnchor.UpperCenter, InkQuiet);
        UIKit.Stretch(profileEmpty.rectTransform, 40f, 80f);

        profile.SetActive(false);
    }

    /// <summary>색 원 + 가운데 첫 글자</summary>
    static Image Avatar(Transform parent, float size, out Text letter)
    {
        Image circle = UIKit.Image(parent, "Avatar", AvatarColors[0]);
        circle.sprite = HudIcons.Circle;
        circle.rectTransform.sizeDelta = new Vector2(size, size);
        letter = UIKit.Text(circle.transform, "", Mathf.RoundToInt(size * 0.45f), TextAnchor.MiddleCenter, Color.white);
        letter.fontStyle = FontStyle.Bold;
        UIKit.Stretch(letter.rectTransform);
        return circle;
    }

    void BuildWriteView(Vector2 safeMin, Vector2 safeMax)
    {
        writeView = UIKit.Image(root.transform, "NewPost", Color.white, raycast: true).gameObject;
        UIKit.Stretch((RectTransform)writeView.transform);
        RectTransform safe = SafeRect(writeView.transform, "Safe", safeMin, safeMax);

        // 위: 뒤로 · 새 게시물
        Button back = RoundButton(safe, "Back", HudIcons.Chevron, 88f, Color.white, Ink, ShowList);
        Place((RectTransform)back.transform, new Vector2(0f, 1f), new Vector2(32f, -30f));
        back.transform.Find("Face/Icon").localRotation = Quaternion.Euler(0f, 0f, 180f);
        var ring = back.transform.Find("Shadow").GetComponent<Image>();
        ring.color = Line;
        Text head = UIKit.Text(safe, "새 게시물", 40, TextAnchor.MiddleCenter, Ink);
        head.fontStyle = FontStyle.Bold;
        TopBox(head.rectTransform, 30f, 88f, 140f);
        Image hr = UIKit.Image(safe, "Line", Line);
        TopBox(hr.rectTransform, 140f, 2f, 0f);

        // 내용 (스크롤)
        RectTransform area = UIKit.Rect(safe, "Form");
        UIKit.StretchBetween(area, 190f, 142f);
        RectTransform form = UIKit.ScrollList(area, 0f, new RectOffset(0, 0, 30, 40));

        // 사진 미리보기
        RectTransform photoRow = UIKit.Rect(form, "PhotoRow");
        UIKit.Size(photoRow, 480f);
        Image frame = UIKit.Image(photoRow, "Frame", Chip, raycast: true);
        frame.sprite = HudIcons.Rounded;
        frame.type = Image.Type.Sliced;
        frame.pixelsPerUnitMultiplier = 1.2f;
        Place(frame.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        frame.rectTransform.sizeDelta = new Vector2(620f, 460f);
        frame.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        var tap = frame.gameObject.AddComponent<Button>();
        tap.transition = Selectable.Transition.None;
        tap.onClick.AddListener(() => { if (photoTexture == null) TakePhoto(); });
        var pv = new GameObject("Photo", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
        pv.transform.SetParent(frame.transform, false);
        photoPreview = pv.GetComponent<RawImage>();
        photoPreview.raycastTarget = false;
        photoFitter = pv.GetComponent<AspectRatioFitter>();
        photoFitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        photoEmpty = UIKit.Rect(frame.transform, "Empty").gameObject;
        UIKit.Stretch((RectTransform)photoEmpty.transform);
        Image cam = UIKit.Image(photoEmpty.transform, "Icon", InkQuiet);
        cam.sprite = HudIcons.Camera;
        Place(cam.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 30f));
        cam.rectTransform.sizeDelta = new Vector2(110f, 110f);
        Text camHint = UIKit.Text(photoEmpty.transform, "눌러서 사진 찍기", 30, TextAnchor.MiddleCenter, InkQuiet);
        Place(camHint.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -70f));
        camHint.rectTransform.sizeDelta = new Vector2(500f, 50f);

        // 캡션
        textInput = UIKit.Input(form, "캡션 추가...", 38, true, 500);
        textInput.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        textInput.textComponent.color = Ink;
        ((Text)textInput.placeholder).color = InkQuiet;
        ((Text)textInput.placeholder).fontStyle = FontStyle.Normal;
        UIKit.Size(textInput, 260f);
        UIKit.Stretch(textInput.textComponent.rectTransform, 40f, 20f);
        UIKit.Stretch(((Text)textInput.placeholder).rectTransform, 40f, 20f);

        // 사진 칩
        RectTransform chips = UIKit.Rect(form, "Chips");
        UIKit.Size(chips, 110f);
        var ch = chips.gameObject.AddComponent<HorizontalLayoutGroup>();
        ch.spacing = 16f;
        ch.padding = new RectOffset(40, 40, 10, 10);
        ch.childControlWidth = ch.childControlHeight = true;
        ch.childForceExpandWidth = false;
        ch.childForceExpandHeight = true;
        photoButton = ChipButton(chips, HudIcons.Camera, "사진 찍기", 250f, TakePhoto);
        galleryButton = ChipButton(chips, HudIcons.Scrapbook, "앨범에서 고르기", 320f, PickFromGallery);
        removePhotoButton = ChipButton(chips, HudIcons.Close, "빼기", 160f, () => SetPhoto(null, null));

        // 닉네임 줄
        RectTransform nameRow = Row(form, HudIcons.Person, "닉네임");
        authorInput = UIKit.Input(nameRow, "이름 입력", 34, false, 20);
        authorInput.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        authorInput.textComponent.color = Ink;
        authorInput.textComponent.alignment = TextAnchor.MiddleRight;
        ((Text)authorInput.placeholder).color = InkQuiet;
        ((Text)authorInput.placeholder).alignment = TextAnchor.MiddleRight;
        ((Text)authorInput.placeholder).fontStyle = FontStyle.Normal;
        RectTransform ar = (RectTransform)authorInput.transform;
        ar.anchorMin = new Vector2(0.4f, 0f);
        ar.anchorMax = new Vector2(1f, 1f);
        ar.offsetMin = Vector2.zero;
        ar.offsetMax = new Vector2(-30f, 0f);

        // 위치 줄 + 칩
        RectTransform locRow = Row(form, HudIcons.Pin, "위치");
        locationText = UIKit.Text(locRow, "", 32, TextAnchor.MiddleRight, InkQuiet);
        locationText.rectTransform.anchorMin = new Vector2(0.35f, 0f);
        locationText.rectTransform.anchorMax = new Vector2(1f, 1f);
        locationText.rectTransform.offsetMin = Vector2.zero;
        locationText.rectTransform.offsetMax = new Vector2(-40f, 0f);
        RectTransform locChips = UIKit.Rect(form, "LocationChips");
        UIKit.Size(locChips, 90f);
        var lh = locChips.gameObject.AddComponent<HorizontalLayoutGroup>();
        lh.spacing = 14f;
        lh.padding = new RectOffset(40, 40, 6, 6);
        lh.childControlWidth = lh.childControlHeight = true;
        lh.childForceExpandWidth = false;
        lh.childForceExpandHeight = true;
        StaticChip(locChips, "경희대학교 국제캠퍼스", 360f);
        StaticChip(locChips, "수원", 120f);
        RectTransform infoRow = UIKit.Rect(form, "Info");
        UIKit.Size(infoRow, 90f);
        Text info = UIKit.Text(infoRow, "이 사진은 이 장소를 찾은 다른 사람들도 볼 수 있어요. 메모판에 콜라주로 붙어요.", 26, TextAnchor.UpperLeft, InkQuiet);
        UIKit.Stretch(info.rectTransform, 40f, 8f);

        writeError = UIKit.Text(form, "", 32, TextAnchor.MiddleCenter, ErrorRed);
        UIKit.Size(writeError, 70f);

        // 아래: 공유하기
        postButton = UIKit.Button(safe, "공유하기", Post, ShareBlue, 40);
        var pimg = postButton.GetComponent<Image>();
        pimg.sprite = HudIcons.Pill;
        pimg.type = Image.Type.Sliced;
        pimg.pixelsPerUnitMultiplier = 120f / 70f;
        postButton.GetComponentInChildren<Text>().fontStyle = FontStyle.Bold;
        RectTransform pr = (RectTransform)postButton.transform;
        pr.anchorMin = new Vector2(0f, 0f);
        pr.anchorMax = new Vector2(1f, 0f);
        pr.pivot = new Vector2(0.5f, 0f);
        pr.offsetMin = new Vector2(40f, 40f);
        pr.offsetMax = new Vector2(-40f, 150f);

        writeView.SetActive(false);
    }

    /// <summary>아이콘 · 이름 · (오른쪽 내용) 이 있는 설정 줄. 아래에 옅은 선.</summary>
    RectTransform Row(RectTransform form, Sprite icon, string label)
    {
        RectTransform row = UIKit.Rect(form, label);
        UIKit.Size(row, 110f);
        Image ic = UIKit.Image(row, "Icon", Ink);
        ic.sprite = icon;
        Place(ic.rectTransform, new Vector2(0f, 0.5f), new Vector2(40f, 0f));
        ic.rectTransform.sizeDelta = new Vector2(52f, 52f);
        Text t = UIKit.Text(row, label, 36, TextAnchor.MiddleLeft, Ink);
        t.rectTransform.anchorMin = new Vector2(0f, 0f);
        t.rectTransform.anchorMax = new Vector2(0.4f, 1f);
        t.rectTransform.offsetMin = new Vector2(116f, 0f);
        t.rectTransform.offsetMax = Vector2.zero;
        return row;
    }

    Button ChipButton(Transform parent, Sprite icon, string label, float width, UnityEngine.Events.UnityAction onClick)
    {
        Button b = UIKit.Button(parent, label, onClick, Chip, 30);
        Image img = b.GetComponent<Image>();
        img.sprite = HudIcons.Pill;
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = 120f / 90f;
        UIKit.Size(b, -1f, width);
        Text t = b.GetComponentInChildren<Text>();
        t.color = Ink;
        t.rectTransform.offsetMin = new Vector2(64f, 0f);
        Image ic = UIKit.Image(b.transform, "Icon", Ink);
        ic.sprite = icon;
        Place(ic.rectTransform, new Vector2(0f, 0.5f), new Vector2(26f, 0f));
        ic.rectTransform.sizeDelta = new Vector2(40f, 40f);
        return b;
    }

    static void StaticChip(Transform parent, string label, float width)
    {
        Image img = UIKit.Image(parent, label, Chip);
        img.sprite = HudIcons.Pill;
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = 120f / 78f;
        UIKit.Size(img, -1f, width);
        Text t = UIKit.Text(img.transform, label, 28, TextAnchor.MiddleCenter, Ink);
        UIKit.Stretch(t.rectTransform, 16f, 0f);
    }

    /// <summary>그림자 + 둥근 얼굴(이름 "Face") + 아이콘(이름 "Icon") 인 원형 버튼</summary>
    static Button RoundButton(Transform parent, string name, Sprite icon, float size, Color face, Color iconColor, UnityEngine.Events.UnityAction onClick)
    {
        Image hit = UIKit.Image(parent, name, new Color(0f, 0f, 0f, 0f), raycast: true);
        hit.rectTransform.sizeDelta = new Vector2(size, size);
        var b = hit.gameObject.AddComponent<Button>();
        b.transition = Selectable.Transition.None;
        b.onClick.AddListener(onClick);
        hit.gameObject.AddComponent<PressScale>();
        Image shadow = UIKit.Image(hit.transform, "Shadow", new Color(0f, 0f, 0f, 0.18f));
        shadow.sprite = HudIcons.Circle;
        Place(shadow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -4f));
        shadow.rectTransform.sizeDelta = new Vector2(size + 4f, size + 4f);
        Image f = UIKit.Image(hit.transform, "Face", face);
        f.sprite = HudIcons.Circle;
        Place(f.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        f.rectTransform.sizeDelta = new Vector2(size, size);
        Image ic = UIKit.Image(f.transform, "Icon", iconColor);
        ic.sprite = icon;
        Place(ic.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        ic.rectTransform.sizeDelta = new Vector2(size * 0.48f, size * 0.48f);
        return b;
    }

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pos)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos;
    }

    /// <summary>위에서 top 만큼 내려온 높이 height 인 칸 (좌우 여백 side)</summary>
    static void TopBox(RectTransform rt, float top, float height, float side)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(side, -top - height);
        rt.offsetMax = new Vector2(-side, -top);
    }
}
