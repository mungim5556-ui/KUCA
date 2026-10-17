using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// B-4 산책 길잡이: 지도 오른쪽 위 길 버튼으로 연다.
/// - 산책 추천: 걸을 시간·취향을 고르면 AI(서버)가 경희스팟 코스를 짠다. 기다리는 동안 파트너가 길을 찾는 연출,
///   받으면 지도 아래에 장소 카드(①②③, 추천 이유)를 보여 준다. 카드를 누르면 팝업 미니맵에
///   지금 위치 → ①②③ 경로를 파란 선과 번호로 그리고, 그 장소의 공지(원문 링크)를 함께 보여 준다.
/// - 장소 공지는 경희스팟 메모판의 공지 탭에 있다 (MemoBoard.Notices). 여기서는 코스 카드의 공지 수와 장소 팝업에만 쓴다.
/// 서버에 API 가 없거나 AI 가 실패하면 기기 안 기본 추천으로 바꿔서 화면이 깨지지 않는다 (HttpRouteService).
/// </summary>
[DefaultExecutionOrder(1000)] // 카메라가 움직인 뒤에 핀 자리를 잡는다
public class RoutePlanner : MonoBehaviour
{
    static readonly Color Ink = new Color(0.11f, 0.11f, 0.12f);
    static readonly Color InkQuiet = new Color(0.11f, 0.11f, 0.12f, 0.55f);
    static readonly Color RouteBlue = new Color(0.16f, 0.42f, 0.95f);
    static readonly Color DoneGreen = new Color(0.24f, 0.68f, 0.38f);
    static readonly Color ChipOff = new Color(0.94f, 0.95f, 0.96f);
    static readonly Color Dim = new Color(0f, 0f, 0f, 0.45f);

    static readonly (int minutes, string label)[] TimeChoices = { (15, "15분"), (30, "30분"), (60, "1시간") };
    static readonly (string id, string label)[] TasteChoices =
        { ("photo", "사진 명소"), ("quiet", "조용한 곳"), ("food", "맛집·카페"), ("unvisited", "안 가 본 곳") };

    CollectController collect;
    KyungHeeSpots spots;
    IRouteService service;
    string serviceUrl;

    RectTransform canvasRect;
    GameObject sheet, recommendPanel, loading, placePopup, routeUi;
    Image[] timeChips, tasteChips;
    RectTransform cardsContent, popupNotices;
    Text routeTitle, popupName, popupReason;
    RawImage loadingPartner;
    Button popupBoardButton;

    int minutes = 30;
    readonly HashSet<string> tastes = new HashSet<string> { "unvisited" };
    int request;

    RouteResult route;
    readonly List<Vector3> points = new List<Vector3>(); // [0] 은 지금 위치, 나머지는 들를 곳
    RawImage miniMap;
    RectTransform miniOverlay;
    Camera miniCam;
    RenderTexture miniTexture;
    readonly List<Image> cardBadges = new List<Image>();
    readonly List<bool> done = new List<bool>();
    readonly Dictionary<string, List<PlaceNotice>> noticesByPlace = new Dictionary<string, List<PlaceNotice>>();
    List<PlaceNotice> allNotices = new List<PlaceNotice>();
    string noticeError;

    public static void Open()
    {
        RoutePlanner p = FindAnyObjectByType<RoutePlanner>();
        if (p == null)
            p = new GameObject("RoutePlanner").AddComponent<RoutePlanner>();
        p.ShowSheet();
    }

    void Awake()
    {
        collect = FindAnyObjectByType<CollectController>();
        spots = FindAnyObjectByType<KyungHeeSpots>();
        Canvas canvas = UIKit.CreateCanvas(transform, "RouteCanvas", 20);
        canvasRect = (RectTransform)canvas.transform;
        BuildRouteUi();
        // 산책 취향(체크리스트)이 있으면 걸을 시간·취향 칩의 기본값으로 쓴다
        WalkPreferences prefs = WalkPreferences.Load();
        if (prefs.IsComplete)
        {
            minutes = prefs.walkMinutes <= 15 ? 15 : prefs.walkMinutes <= 30 ? 30 : 60;
            tastes.Clear();
            foreach (string t in prefs.tastes)
                if (System.Array.Exists(TasteChoices, c => c.id == t))
                    tastes.Add(t);
            if (tastes.Count == 0) tastes.Add("unvisited");
        }
        BuildSheet();
        BuildLoading();
        BuildPlacePopup();
    }

    void EnsureService()
    {
        var board = FindAnyObjectByType<MemoBoard>();
        string url = board != null ? board.ServerUrl : "";
        if (service != null && url == serviceUrl)
            return;
        serviceUrl = url;
        var local = new LocalRouteService(
            () => spots != null ? new List<KyungHeeSpot>(spots.Spots) : new List<KyungHeeSpot>(),
            () => PlayerPosition);
        service = url.Length == 0 ? local : new HttpRouteService(url, local);
        ReloadNotices();
    }

    void ReloadNotices()
    {
        noticeError = null;
        noticesByPlace.Clear();
        StartCoroutine(service.GetNotices(null, (list, error) =>
        {
            noticeError = error;
            allNotices = list ?? new List<PlaceNotice>();
            noticesByPlace.Clear();
            foreach (PlaceNotice n in allNotices)
            {
                if (!noticesByPlace.TryGetValue(n.buildingId ?? "", out var l))
                    noticesByPlace[n.buildingId ?? ""] = l = new List<PlaceNotice>();
                l.Add(n);
            }
            if (route != null) BuildCards(); // 공지 수 표시를 맞춘다
        }));
    }

    Vector3 PlayerPosition => collect != null && collect.campusMap != null && collect.campusMap.player != null
        ? collect.campusMap.player.position : Vector3.zero;

    // ---------- 추천 창 ----------

    void ShowSheet()
    {
        EnsureService();
        if (noticeError != null)
            ReloadNotices(); // 지난번에 못 불러왔으면 다시 시도
        sheet.SetActive(true);
        sheet.transform.SetAsLastSibling();
        UIInputBlocker.SetModal(this, true);
        RefreshChips();
    }

    void HideSheet()
    {
        sheet.SetActive(false);
        UIInputBlocker.SetModal(this, loading.activeSelf || placePopup.activeSelf);
    }

    void RefreshChips()
    {
        for (int i = 0; i < timeChips.Length; i++)
            SetChip(timeChips[i], TimeChoices[i].minutes == minutes);
        for (int i = 0; i < tasteChips.Length; i++)
            SetChip(tasteChips[i], tastes.Contains(TasteChoices[i].id));
    }

    static void SetChip(Image chip, bool on)
    {
        chip.color = on ? RouteBlue : ChipOff;
        chip.GetComponentInChildren<Text>().color = on ? Color.white : Ink;
    }

    void RequestRoute()
    {
        var req = new RouteRequest { minutes = minutes, tastes = new List<string>(tastes) };
        if (collect != null && collect.campusMap != null)
        {
            req.lat = collect.campusMap.CurrentLatitude;
            req.lon = collect.campusMap.CurrentLongitude;
        }
        if (collect != null && collect.Progress != null)
            req.visitedBuildingIds = new List<string>(collect.Progress.visitedSpots);
        if (spots != null)
            foreach (KyungHeeSpot s in spots.Spots)
                if (s != null && s.Building != null)
                    req.candidateBuildingIds.Add(s.Building.buildingId);

        HideSheet();
        ShowLoading(true);
        int id = ++request;
        StartCoroutine(service.Recommend(req, (result, error) =>
        {
            if (id != request)
                return; // 취소함
            ShowLoading(false);
            if (result == null || result.stops == null || result.stops.Count == 0)
            {
                Toast(error ?? "코스를 만들지 못했어요. 잠시 뒤 다시 해 주세요");
                ShowSheet();
                return;
            }
            ShowRoute(result);
            if (result.source != "ai" && serviceUrl.Length > 0)
                Toast("AI 추천을 받지 못해 가까운 경희스팟으로 코스를 짰어요");
        }));
    }

    void ShowLoading(bool on)
    {
        loading.SetActive(on);
        if (on)
        {
            loading.transform.SetAsLastSibling();
            Texture2D partner = null;
            try
            {
                GameProgress.Caught p = collect != null && collect.Progress != null ? collect.Progress.Partner : null;
                if (p != null) partner = MonsterThumbnails.Get(p.speciesId);
                if (partner == null) partner = MonsterThumbnails.Get(CreatureLibrary.AllSpecies.Count > 0 ? CreatureLibrary.AllSpecies[0] : null);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Route] 파트너 그림을 못 만들었어요: " + e.Message); // 그림이 없어도 추천은 계속한다
            }
            loadingPartner.texture = partner;
            loadingPartner.enabled = partner != null;
        }
        UIInputBlocker.SetModal(this, on || sheet.activeSelf || placePopup.activeSelf);
    }

    // ---------- 지도 위 코스 ----------

    void ShowRoute(RouteResult result)
    {
        ClearRoute();
        route = result;
        points.Add(PlayerPosition);
        var kept = new List<RouteStop>();
        foreach (RouteStop s in result.stops)
        {
            if (!TryPlace(s, out Vector3 pos))
                continue; // 지도에 없는 장소는 건너뛴다
            points.Add(pos);
            kept.Add(s);
        }
        route.stops = kept;
        if (kept.Count == 0)
        {
            Toast("추천받은 장소를 지도에서 찾지 못했어요");
            route = null;
            return;
        }
        for (int i = 0; i < kept.Count; i++)
            done.Add(false);
        routeTitle.text = $"<b>{Escape(result.title)}</b>  <size=26><color=#8C8C91>{(result.source == "ai" ? "AI 추천" : "기본 추천")}</color></size>";
        BuildCards();
        routeUi.SetActive(true);
    }

    void ClearRoute()
    {
        points.Clear();
        done.Clear();
        route = null;
        routeUi.SetActive(false);
    }

    bool TryPlace(RouteStop s, out Vector3 pos)
    {
        KyungHeeSpot spot = SpotOf(s.buildingId);
        if (spot != null)
        {
            pos = spot.BuildingBounds.center;
            pos.y = spot.BuildingBounds.max.y;
            return true;
        }
        if ((s.lat != 0 || s.lon != 0) && collect != null && collect.campusMap != null)
        {
            pos = collect.campusMap.GeoToWorld(s.lon, s.lat);
            return true;
        }
        pos = Vector3.zero;
        return false;
    }

    KyungHeeSpot SpotOf(string buildingId)
    {
        if (spots == null) return null;
        foreach (KyungHeeSpot s in spots.Spots)
            if (s != null && s.Building != null && s.Building.buildingId == buildingId)
                return s;
        return null;
    }

    void LateUpdate()
    {
        // 다른 화면(메뉴·메모판·설정·잡기 등)이 열리면 길잡이 창은 닫고, 코스 카드는 그동안 숨긴다.
        bool otherOpen = UIInputBlocker.IsOtherModalOpen(this);
        if (otherOpen)
        {
            if (loading.activeSelf) { request++; ShowLoading(false); }
            if (sheet.activeSelf) HideSheet();
            if (placePopup.activeSelf) ClosePlace();
        }
        bool showCards = route != null && !otherOpen;
        if (routeUi.activeSelf != showCards)
            routeUi.SetActive(showCards);

        if (loading.activeSelf && loadingPartner != null)
            loadingPartner.rectTransform.anchoredPosition = new Vector2(0f, 70f + Mathf.Abs(Mathf.Sin(Time.unscaledTime * 6f)) * 36f);
        if (route == null)
            return;

        // 도착한 곳은 초록 체크로 바꾸고, 다 돌면 축하
        for (int i = 0; i < route.stops.Count; i++)
        {
            if (done[i]) continue;
            KyungHeeSpot spot = SpotOf(route.stops[i].buildingId);
            if (spot == null || !spot.InRange) continue;
            done[i] = true;
            MarkDone(i);
            bool finished = done.TrueForAll(x => x);
            Toast(finished ? "코스 완주! 수고했어요" : $"{route.stops[i].name} 도착!");
            if (finished) Quests.Report("course_done");
        }
    }

    void MarkDone(int i)
    {
        if (i < cardBadges.Count && cardBadges[i] != null)
        {
            cardBadges[i].color = DoneGreen;
            cardBadges[i].GetComponentInChildren<Text>().text = "✓";
        }
    }

    // ---------- 장소 카드 · 장소 공지 ----------

    void BuildCards()
    {
        for (int i = cardsContent.childCount - 1; i >= 0; i--)
            Destroy(cardsContent.GetChild(i).gameObject);
        cardBadges.Clear();
        if (route == null)
            return;
        for (int i = 0; i < route.stops.Count; i++)
        {
            RouteStop s = route.stops[i];
            int index = i;
            Image card = UIKit.Image(cardsContent, "Card", Color.white, raycast: true);
            card.sprite = HudIcons.Rounded;
            card.type = Image.Type.Sliced;
            card.pixelsPerUnitMultiplier = 0.6f;
            UIKit.Size(card, 230f, 600f);
            var b = card.gameObject.AddComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.onClick.AddListener(() => OpenPlace(index));
            card.gameObject.AddComponent<PressScale>();

            Image badge = Circle(card.transform, done.Count > i && done[i] ? DoneGreen : RouteBlue, 64f, done.Count > i && done[i] ? "✓" : (i + 1).ToString());
            Place(badge.rectTransform, new Vector2(0f, 1f), new Vector2(28f, -28f));
            cardBadges.Add(badge);
            Text name = UIKit.Text(card.transform, s.name, 36, TextAnchor.MiddleLeft, Ink);
            name.fontStyle = FontStyle.Bold;
            Box(name.rectTransform, 108f, 28f, 30f, 64f);
            int count = NoticesOf(s.buildingId).Count;
            string meta = $"도보 {s.walkMinutes}분" + (count > 0 ? $"  ·  <color=#D23A4A>공지 {count}</color>" : "");
            Text info = UIKit.Text(card.transform, meta, 27, TextAnchor.MiddleLeft, InkQuiet);
            Box(info.rectTransform, 30f, 104f, 30f, 40f);
            Text reason = UIKit.Text(card.transform, s.reason, 28, TextAnchor.UpperLeft, Ink);
            Box(reason.rectTransform, 30f, 150f, 30f, 70f);
            reason.verticalOverflow = VerticalWrapMode.Truncate;
        }
    }

    List<PlaceNotice> NoticesOf(string buildingId) =>
        buildingId != null && noticesByPlace.TryGetValue(buildingId, out var l) ? l : new List<PlaceNotice>();

    void OpenPlace(int index)
    {
        if (route == null || index < 0 || index >= route.stops.Count)
            return;
        RouteStop s = route.stops[index];
        ShowPlace(s.buildingId, $"<color=#2A6BF2>{index + 1}</color>  {Escape(s.name)}", $"{s.reason}  ·  도보 {s.walkMinutes}분", index);
    }

    void ShowPlace(string buildingId, string name, string reason, int focus)
    {
        popupName.text = name;
        popupReason.text = string.IsNullOrEmpty(reason) ? "" : reason;
        for (int i = popupNotices.childCount - 1; i >= 0; i--)
            Destroy(popupNotices.GetChild(i).gameObject);
        List<PlaceNotice> list = NoticesOf(buildingId);
        if (list.Count == 0)
        {
            Text none = UIKit.Text(popupNotices, "이 장소의 공지는 아직 없어요", 30, TextAnchor.MiddleCenter, InkQuiet);
            UIKit.Size(none, 90f);
        }
        foreach (PlaceNotice n in list)
            NoticeRow(popupNotices, n, false);
        KyungHeeSpot spot = SpotOf(buildingId);
        popupBoardButton.gameObject.SetActive(spot != null);
        popupBoardButton.onClick.RemoveAllListeners();
        popupBoardButton.onClick.AddListener(() =>
        {
            ClosePlace();
            HideSheet();
            var board = FindAnyObjectByType<MemoBoard>();
            if (board != null && spot != null) board.OpenSpot(spot);
        });
        placePopup.SetActive(true);
        placePopup.transform.SetAsLastSibling();
        UIInputBlocker.SetModal(this, true);
        Canvas.ForceUpdateCanvases();
        DrawMiniMap(focus);
    }

    // ---------- 미니맵 ----------

    /// <summary>
    /// 코스 전체(지금 위치 + 들를 곳)가 들어오게 위에서 내려다본 지도를 한 장 찍고,
    /// 그 위에 파란 경로 선과 번호를 그린다. focus 번째 장소는 크게 표시한다. 지도 방향은 지금 화면과 같다.
    /// </summary>
    void DrawMiniMap(int focus)
    {
        for (int i = miniOverlay.childCount - 1; i >= 0; i--)
            Destroy(miniOverlay.GetChild(i).gameObject);
        Camera main = Camera.main;
        if (route == null || points.Count < 2 || main == null)
            return;
        points[0] = PlayerPosition;

        Vector2 size = ((RectTransform)miniMap.transform).rect.size;
        float aspect = size.x / Mathf.Max(1f, size.y);
        Quaternion rot = Quaternion.Euler(90f, main.transform.eulerAngles.y, 0f);
        Vector3 right = rot * Vector3.right, up = rot * Vector3.up;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (Vector3 p in points)
        {
            float x = Vector3.Dot(p, right), y = Vector3.Dot(p, up);
            minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
            minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
        }
        Vector3 center = right * ((minX + maxX) * 0.5f) + up * ((minY + maxY) * 0.5f);
        center.y = 0f;
        float half = Mathf.Max((maxY - minY) * 0.5f, (maxX - minX) * 0.5f / aspect) * 1.3f + 40f;

        if (miniCam == null)
        {
            miniCam = new GameObject("RouteMiniMapCamera").AddComponent<Camera>();
            miniCam.transform.SetParent(transform, false);
            miniCam.enabled = false;
            miniCam.orthographic = true;
            miniCam.clearFlags = CameraClearFlags.SolidColor;
            miniCam.backgroundColor = new Color(0.55f, 0.72f, 0.42f);
        }
        int w = 1024, h = Mathf.Max(64, Mathf.RoundToInt(w / aspect));
        if (miniTexture == null || miniTexture.width != w || miniTexture.height != h)
        {
            if (miniTexture != null) miniTexture.Release();
            miniTexture = new RenderTexture(w, h, 24) { name = "RouteMiniMap", antiAliasing = 2 };
        }
        miniCam.cullingMask = main.cullingMask;
        miniCam.orthographicSize = half;
        miniCam.aspect = aspect;
        miniCam.transform.SetPositionAndRotation(center + Vector3.up * 800f, rot);
        miniCam.nearClipPlane = 1f;
        miniCam.farClipPlane = 2000f;
        miniCam.targetTexture = miniTexture;
        miniCam.Render();
        miniCam.targetTexture = null;
        miniMap.texture = miniTexture;

        // 지도 위 경로: 지금 위치 → ① → ② … 파란 선, 번호 원
        var at = new Vector2[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 vp = miniCam.WorldToViewportPoint(points[i]);
            at[i] = new Vector2((vp.x - 0.5f) * size.x, (vp.y - 0.5f) * size.y);
        }
        for (int i = 0; i + 1 < at.Length; i++)
            Line(miniOverlay, at[i], at[i + 1], i == focus ? 14f : 10f, RouteBlue);
        Image me = Circle(miniOverlay, Ink, 54f, "나");
        me.rectTransform.anchoredPosition = at[0];
        me.GetComponentInChildren<Text>().fontSize = 24;
        for (int i = 1; i < at.Length; i++)
        {
            bool isFocus = i - 1 == focus;
            bool arrived = i - 1 < done.Count && done[i - 1];
            Image c = Circle(miniOverlay, arrived ? DoneGreen : RouteBlue, isFocus ? 84f : 60f, arrived ? "✓" : i.ToString());
            c.rectTransform.anchoredPosition = at[i];
            Outline o = c.gameObject.AddComponent<Outline>();
            o.effectColor = Color.white;
            o.effectDistance = isFocus ? new Vector2(5f, -5f) : new Vector2(3f, -3f);
        }
    }

    static void Line(RectTransform parent, Vector2 a, Vector2 b, float width, Color color)
    {
        Image seg = UIKit.Image(parent, "Line", color);
        RectTransform r = seg.rectTransform;
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
        Vector2 d = b - a;
        r.anchoredPosition = (a + b) * 0.5f;
        r.sizeDelta = new Vector2(d.magnitude, width);
        r.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
    }

    void OnDestroy()
    {
        if (miniTexture != null) miniTexture.Release();
    }

    void ClosePlace()
    {
        placePopup.SetActive(false);
        UIInputBlocker.SetModal(this, sheet.activeSelf || loading.activeSelf);
    }

    /// <summary>공지 한 줄: (장소) 제목 · 요약 · 날짜 · 원문 보기. 누르면 원문을 연다.</summary>
    internal static void NoticeRow(RectTransform parent, PlaceNotice n, bool withPlace)
    {
        Image row = UIKit.Image(parent, "Notice", ChipOff, raycast: true);
        row.sprite = HudIcons.Rounded;
        row.type = Image.Type.Sliced;
        row.pixelsPerUnitMultiplier = 0.8f;
        UIKit.Vertical(row, 8f, new RectOffset(32, 32, 26, 26));
        row.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var b = row.gameObject.AddComponent<Button>();
        b.transition = Selectable.Transition.None;
        string url = n.url;
        b.onClick.AddListener(() => { if (!string.IsNullOrEmpty(url)) Application.OpenURL(url); });
        if (withPlace)
        {
            Text place = UIKit.Text(row.transform, n.buildingName, 26, TextAnchor.UpperLeft, RouteBlue);
            place.fontStyle = FontStyle.Bold;
        }
        Text title = UIKit.Text(row.transform, n.title, 32, TextAnchor.UpperLeft, Ink);
        title.fontStyle = FontStyle.Bold;
        if (!string.IsNullOrEmpty(n.summary))
            UIKit.Text(row.transform, n.summary, 27, TextAnchor.UpperLeft, InkQuiet);
        UIKit.Text(row.transform, $"{n.postedAt}   <color=#2A6BF2>원문 보기 ›</color>", 26, TextAnchor.UpperLeft, InkQuiet);
    }

    // ---------- 화면 만들기 ----------

    void BuildRouteUi()
    {
        routeUi = UIKit.Rect(canvasRect, "RouteCards").gameObject;
        var rr = (RectTransform)routeUi.transform;
        rr.anchorMin = new Vector2(0f, 0f);
        rr.anchorMax = new Vector2(1f, 0f);
        rr.pivot = new Vector2(0.5f, 0f);
        rr.sizeDelta = new Vector2(0f, 330f);
        rr.anchoredPosition = new Vector2(0f, 520f);
        UIInputBlocker.Register(rr);

        // 코스 이름 + 지우기
        Image head = UIKit.Image(rr, "Head", new Color(1f, 1f, 1f, 0.94f), raycast: true);
        head.sprite = HudIcons.Pill;
        head.type = Image.Type.Sliced;
        head.pixelsPerUnitMultiplier = 120f / 80f;
        Place(head.rectTransform, new Vector2(0f, 1f), new Vector2(40f, 0f));
        head.rectTransform.sizeDelta = new Vector2(620f, 80f);
        routeTitle = UIKit.Text(head.transform, "", 32, TextAnchor.MiddleLeft, Ink);
        Box(routeTitle.rectTransform, 30f, 0f, 90f, 80f);
        Image x = UIKit.Image(head.transform, "Clear", new Color(0f, 0f, 0f, 0f), raycast: true);
        Place(x.rectTransform, new Vector2(1f, 0.5f), new Vector2(-8f, 0f));
        x.rectTransform.sizeDelta = new Vector2(76f, 76f);
        x.gameObject.AddComponent<Button>().onClick.AddListener(ClearRoute);
        Image xi = UIKit.Image(x.transform, "Icon", InkQuiet);
        xi.sprite = HudIcons.Close;
        Place(xi.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        xi.rectTransform.sizeDelta = new Vector2(34f, 34f);

        // 가로로 넘기는 장소 카드
        RectTransform area = UIKit.Rect(rr, "Cards");
        area.anchorMin = Vector2.zero;
        area.anchorMax = new Vector2(1f, 0f);
        area.pivot = new Vector2(0.5f, 0f);
        area.sizeDelta = new Vector2(0f, 236f);
        var scroll = area.gameObject.AddComponent<ScrollRect>();
        scroll.vertical = false;
        RectTransform viewport = UIKit.Rect(area, "Viewport");
        UIKit.Stretch(viewport);
        viewport.gameObject.AddComponent<RectMask2D>();
        viewport.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        cardsContent = UIKit.Rect(viewport, "Content");
        cardsContent.anchorMin = new Vector2(0f, 0f);
        cardsContent.anchorMax = new Vector2(0f, 1f);
        cardsContent.pivot = new Vector2(0f, 0.5f);
        cardsContent.offsetMin = cardsContent.offsetMax = Vector2.zero;
        var h = cardsContent.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 24f;
        h.padding = new RectOffset(40, 40, 3, 3);
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = h.childForceExpandHeight = false;
        cardsContent.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewport;
        scroll.content = cardsContent;
        routeUi.SetActive(false);
    }

    void BuildSheet()
    {
        sheet = UIKit.Image(canvasRect, "RouteSheet", Dim, raycast: true).gameObject;
        UIKit.Stretch((RectTransform)sheet.transform);
        sheet.AddComponent<Button>().onClick.AddListener(HideSheet); // 바깥을 누르면 닫힘

        Image panel = UIKit.Image(sheet.transform, "Panel", Color.white, raycast: true);
        panel.sprite = HudIcons.Rounded;
        panel.type = Image.Type.Sliced;
        panel.pixelsPerUnitMultiplier = 0.5f;
        RectTransform pr = panel.rectTransform;
        pr.anchorMin = new Vector2(0f, 0f);
        pr.anchorMax = new Vector2(1f, 0f);
        pr.pivot = new Vector2(0.5f, 0f);
        pr.sizeDelta = new Vector2(0f, 960f);
        pr.anchoredPosition = new Vector2(0f, -40f); // 아래 둥근 모서리는 화면 밖으로
        Swallow(panel.gameObject); // 판을 눌러도 바깥(닫기)으로 전달되지 않게

        Text title = UIKit.Text(pr, "산책 길잡이", 44, TextAnchor.MiddleLeft, Ink);
        title.fontStyle = FontStyle.Bold;
        Box(title.rectTransform, 50f, 46f, 150f, 70f);
        Image close = UIKit.Image(pr, "Close", new Color(0f, 0f, 0f, 0f), raycast: true);
        Place(close.rectTransform, new Vector2(1f, 1f), new Vector2(-30f, -36f));
        close.rectTransform.sizeDelta = new Vector2(84f, 84f);
        close.gameObject.AddComponent<Button>().onClick.AddListener(HideSheet);
        Image ci = UIKit.Image(close.transform, "Icon", Ink);
        ci.sprite = HudIcons.Close;
        Place(ci.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        ci.rectTransform.sizeDelta = new Vector2(40f, 40f);

        // 산책 추천
        recommendPanel = UIKit.Rect(pr, "Recommend").gameObject;
        var rp = (RectTransform)recommendPanel.transform;
        UIKit.StretchBetween(rp, 40f, 150f);
        Text q1 = UIKit.Text(rp, "얼마나 걸을까요?", 36, TextAnchor.MiddleLeft, Ink);
        q1.fontStyle = FontStyle.Bold;
        Box(q1.rectTransform, 50f, 0f, 50f, 60f);
        timeChips = new Image[TimeChoices.Length];
        for (int i = 0; i < TimeChoices.Length; i++)
        {
            int m = TimeChoices[i].minutes;
            Image c = Chip(rp, TimeChoices[i].label, () => { minutes = m; RefreshChips(); }, 32);
            Place(c.rectTransform, new Vector2(0f, 1f), new Vector2(50f + i * 230f, -80f));
            c.rectTransform.sizeDelta = new Vector2(210f, 92f);
            timeChips[i] = c;
        }
        Text q2 = UIKit.Text(rp, "무엇을 하고 싶어요?  <size=28><color=#8C8C91>여러 개 고를 수 있어요</color></size>", 36, TextAnchor.MiddleLeft, Ink);
        q2.fontStyle = FontStyle.Bold;
        Box(q2.rectTransform, 50f, 230f, 50f, 60f);
        tasteChips = new Image[TasteChoices.Length];
        for (int i = 0; i < TasteChoices.Length; i++)
        {
            string id = TasteChoices[i].id;
            Image c = Chip(rp, TasteChoices[i].label, () =>
            {
                if (!tastes.Remove(id)) tastes.Add(id);
                RefreshChips();
            }, 32);
            Place(c.rectTransform, new Vector2(0f, 1f), new Vector2(50f + (i % 2) * 330f, -310f - (i / 2) * 114f));
            c.rectTransform.sizeDelta = new Vector2(310f, 92f);
            tasteChips[i] = c;
        }
        Button go = UIKit.Button(rp, "코스 추천 받기", RequestRoute, RouteBlue, 40);
        Image gi = go.GetComponent<Image>();
        gi.sprite = HudIcons.Pill;
        gi.type = Image.Type.Sliced;
        gi.pixelsPerUnitMultiplier = 120f / 124f;
        go.GetComponentInChildren<Text>().fontStyle = FontStyle.Bold;
        go.gameObject.AddComponent<PressScale>();
        RectTransform gr = (RectTransform)go.transform;
        gr.anchorMin = new Vector2(0f, 0f);
        gr.anchorMax = new Vector2(1f, 0f);
        gr.pivot = new Vector2(0.5f, 0f);
        gr.sizeDelta = new Vector2(-100f, 124f);
        gr.anchoredPosition = new Vector2(0f, 60f);

        sheet.SetActive(false);
    }

    void BuildLoading()
    {
        loading = UIKit.Image(canvasRect, "RouteLoading", Dim, raycast: true).gameObject;
        UIKit.Stretch((RectTransform)loading.transform);
        Image card = UIKit.Image(loading.transform, "Card", Color.white, raycast: true);
        card.sprite = HudIcons.Rounded;
        card.type = Image.Type.Sliced;
        card.pixelsPerUnitMultiplier = 0.5f;
        Place(card.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        card.rectTransform.sizeDelta = new Vector2(760f, 640f);
        var go = new GameObject("Partner", typeof(RectTransform), typeof(RawImage));
        go.transform.SetParent(card.transform, false);
        loadingPartner = go.GetComponent<RawImage>();
        loadingPartner.raycastTarget = false;
        Place(loadingPartner.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 70f));
        loadingPartner.rectTransform.sizeDelta = new Vector2(280f, 280f);
        Text t = UIKit.Text(card.transform, "코스를 짜고 있어요…", 40, TextAnchor.MiddleCenter, Ink);
        t.fontStyle = FontStyle.Bold;
        Place(t.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 190f));
        t.rectTransform.sizeDelta = new Vector2(700f, 60f);
        Text sub = UIKit.Text(card.transform, "파트너가 길을 찾고 있어요", 30, TextAnchor.MiddleCenter, InkQuiet);
        Place(sub.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 140f));
        sub.rectTransform.sizeDelta = new Vector2(700f, 44f);
        Button cancel = UIKit.Button(card.transform, "취소", () => { request++; ShowLoading(false); ShowSheet(); }, new Color(0f, 0f, 0f, 0f), 32);
        cancel.GetComponentInChildren<Text>().color = InkQuiet;
        Place((RectTransform)cancel.transform, new Vector2(0.5f, 0f), new Vector2(0f, 30f));
        ((RectTransform)cancel.transform).sizeDelta = new Vector2(200f, 84f);
        loading.SetActive(false);
    }

    void BuildPlacePopup()
    {
        placePopup = UIKit.Image(canvasRect, "PlacePopup", Dim, raycast: true).gameObject;
        UIKit.Stretch((RectTransform)placePopup.transform);
        placePopup.AddComponent<Button>().onClick.AddListener(ClosePlace);
        Image card = UIKit.Image(placePopup.transform, "Card", Color.white, raycast: true);
        card.sprite = HudIcons.Rounded;
        card.type = Image.Type.Sliced;
        card.pixelsPerUnitMultiplier = 0.5f;
        RectTransform cr = card.rectTransform;
        cr.anchorMin = new Vector2(0.05f, 0.5f);
        cr.anchorMax = new Vector2(0.95f, 0.5f);
        cr.pivot = new Vector2(0.5f, 0.5f);
        cr.sizeDelta = new Vector2(0f, 1560f);
        Swallow(card.gameObject);
        popupName = UIKit.Text(cr, "", 44, TextAnchor.MiddleLeft, Ink);
        popupName.fontStyle = FontStyle.Bold;
        Box(popupName.rectTransform, 50f, 46f, 140f, 64f);
        Image close = UIKit.Image(cr, "Close", new Color(0f, 0f, 0f, 0f), raycast: true);
        Place(close.rectTransform, new Vector2(1f, 1f), new Vector2(-30f, -36f));
        close.rectTransform.sizeDelta = new Vector2(84f, 84f);
        close.gameObject.AddComponent<Button>().onClick.AddListener(ClosePlace);
        Image ci = UIKit.Image(close.transform, "Icon", Ink);
        ci.sprite = HudIcons.Close;
        Place(ci.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        ci.rectTransform.sizeDelta = new Vector2(40f, 40f);
        popupReason = UIKit.Text(cr, "", 30, TextAnchor.UpperLeft, InkQuiet);
        Box(popupReason.rectTransform, 50f, 116f, 50f, 50f);

        // 미니맵 (코스 전체를 위에서 본 지도 + 파란 경로)
        Image frame = UIKit.Image(cr, "MiniMap", new Color(0.55f, 0.72f, 0.42f));
        frame.sprite = HudIcons.Rounded;
        frame.type = Image.Type.Sliced;
        frame.pixelsPerUnitMultiplier = 1f;
        Box(frame.rectTransform, 50f, 186f, 50f, 680f);
        frame.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        var mm = new GameObject("Map", typeof(RectTransform), typeof(RawImage));
        mm.transform.SetParent(frame.transform, false);
        miniMap = mm.GetComponent<RawImage>();
        miniMap.raycastTarget = false;
        UIKit.Stretch(miniMap.rectTransform);
        miniOverlay = UIKit.Rect(frame.transform, "Route");
        UIKit.Stretch(miniOverlay);

        Text head = UIKit.Text(cr, "장소 공지", 32, TextAnchor.MiddleLeft, Ink);
        head.fontStyle = FontStyle.Bold;
        Box(head.rectTransform, 50f, 896f, 50f, 50f);
        RectTransform area = UIKit.Rect(cr, "List");
        UIKit.StretchBetween(area, 190f, 960f);
        popupNotices = UIKit.ScrollList(area, 16f, new RectOffset(50, 50, 0, 20));
        popupBoardButton = UIKit.Button(cr, "이 장소 쪽지판 열기", () => { }, Ink, 34);
        Image bi = popupBoardButton.GetComponent<Image>();
        bi.sprite = HudIcons.Pill;
        bi.type = Image.Type.Sliced;
        bi.pixelsPerUnitMultiplier = 120f / 110f;
        popupBoardButton.GetComponentInChildren<Text>().fontStyle = FontStyle.Bold;
        RectTransform br = (RectTransform)popupBoardButton.transform;
        br.anchorMin = new Vector2(0f, 0f);
        br.anchorMax = new Vector2(1f, 0f);
        br.pivot = new Vector2(0.5f, 0f);
        br.sizeDelta = new Vector2(-100f, 110f);
        br.anchoredPosition = new Vector2(0f, 50f);
        placePopup.SetActive(false);
    }

    // ---------- 작은 도우미 ----------

    static Image Circle(Transform parent, Color color, float size, string label)
    {
        Image c = UIKit.Image(parent, "Badge", color);
        c.sprite = HudIcons.Circle;
        c.rectTransform.anchorMin = c.rectTransform.anchorMax = c.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        c.rectTransform.sizeDelta = new Vector2(size, size);
        Text t = UIKit.Text(c.transform, label, Mathf.RoundToInt(size * 0.48f), TextAnchor.MiddleCenter, Color.white);
        t.fontStyle = FontStyle.Bold;
        UIKit.Stretch(t.rectTransform);
        return c;
    }

    static Image Chip(Transform parent, string label, UnityEngine.Events.UnityAction onClick, int fontSize)
    {
        Button b = UIKit.Button(parent, label, onClick, ChipOff, fontSize);
        Image img = b.GetComponent<Image>();
        img.sprite = HudIcons.Pill;
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = 120f / 92f;
        b.GetComponentInChildren<Text>().color = Ink;
        b.gameObject.AddComponent<PressScale>();
        return img;
    }

    static void Swallow(GameObject go) => go.AddComponent<Button>().transition = Selectable.Transition.None;

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pos)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos;
    }

    /// <summary>부모 왼쪽 위에서 (left, top) 만큼 떨어지고 오른쪽 여백 right, 높이 height 인 칸</summary>
    static void Box(RectTransform rt, float left, float top, float right, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(left, -top - height);
        rt.offsetMax = new Vector2(-right, -top);
    }

    static string Escape(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");

    static void Toast(string message)
    {
        var hud = FindAnyObjectByType<GameHUD>();
        if (hud != null) hud.Toast(message);
    }

    void OnDisable() => UIInputBlocker.SetModal(this, false);
}
