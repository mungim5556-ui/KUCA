using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 산책 길잡이 (지도 오른쪽 위 길 버튼).
/// 1. 조건: 출발지 · 목적지 (경희스팟 목록, 출발 기본은 내 위치) · 걸을 시간 · 취향 · 식후 걷기 → "코스 3개 추천 받기"
///    또는 "자유 산책 시작" (FreeWalk: 코스 없이 걷고 지나간 길을 기록).
/// 2. 코스 고르기: 서버(AI)에 조건을 달리해 세 번 요청 (내 취향 · 가볍게 · 새로운 곳) → 큰 지도에 세 코스를 색 선으로,
///    아래 카드에 거리 · 시간 · 들를 곳 · 이유. 길은 실제 보행로를 따라 (WalkGraph), 거리 · 시간도 길 기준.
/// 3. 걷는 중: 본 지도에 고른 코스를 색 선으로 (경로 표시 토글), 아래에 다음 장소 · 남은 거리. 장소에 닿으면 체크,
///    다 돌거나 80% 이상 걷고 끝내면 완료 화면 (WalkCompleteScreen).
/// 목적지는 서버가 아직 받지 않으므로 앱이 출발 → 목적지 사이에 갈 수 있는 스팟만 후보로 보내고 마지막에 목적지를 붙인다.
/// 서버에 API 가 없거나 AI 가 실패하면 기기 안 기본 추천으로 바꾼다 (HttpRouteService → LocalRouteService).
/// </summary>
[DefaultExecutionOrder(1000)] // 카메라가 움직인 뒤에 표시 자리를 잡는다
public partial class RoutePlanner : MonoBehaviour
{
    static readonly Color Ink = KucaTheme.Ink;
    static readonly Color InkQuiet = KucaTheme.InkMuted;
    static readonly Color RouteBlue = KucaTheme.Primary;
    static readonly Color DoneGreen = KucaTheme.Primary;
    static readonly Color ChipOff = KucaTheme.PrimarySoft;
    static readonly Color Dim = KucaTheme.Scrim;

    static readonly (int minutes, string label)[] TimeChoices = { (15, "15분"), (30, "30분"), (60, "1시간") };
    static readonly (string id, string label)[] TasteChoices =
        { ("photo", "사진 명소"), ("quiet", "조용한 곳"), ("food", "맛집·카페"), ("unvisited", "처음 가는 곳") };

    /// <summary>한 번에 보여 주는 세 코스. 조건을 조금씩 달리해 서버에 따로 요청한다.</summary>
    static readonly Variant[] Variants =
    {
        new Variant("mine", "내 취향 코스", new Color(0.16f, 0.42f, 0.95f)),
        new Variant("light", "가볍게 걷는 코스", new Color(0.56f, 0.30f, 0.92f)),   // 보라 · 분홍: 잔디(초록) · 단풍(주황) 지도에서 잘 보이게
        new Variant("new", "새로운 곳 코스", new Color(0.92f, 0.22f, 0.55f)),
    };

    class Variant
    {
        public readonly string id, label;
        public readonly Color color;
        public Variant(string id, string label, Color color) { this.id = id; this.label = label; this.color = color; }
    }

    /// <summary>받은 코스 하나: 서버 응답 + 지도 위 위치 + 길을 따라 이은 선 + 길 기준 거리 · 시간</summary>
    class Option
    {
        public Variant variant;
        public RouteResult result;
        public int minutes;                                  // 요청한 걸을 시간 (서버 산책 시작에 씀)
        public readonly List<Vector3> stops = new List<Vector3>();     // 들를 곳의 지도 위치 (result.stops 와 같은 순서)
        public List<Vector3> path = new List<Vector3>();      // 출발 → 들를 곳 → 마지막까지 길을 따라
        public List<int> legEnds = new List<int>();          // path 에서 장소마다 닿는 점의 번호 (stops 와 같은 순서)
        public int meters, walkMinutes;
        public bool returns;                                 // 마지막 장소가 출발지로 돌아오기인지 (한 바퀴 코스)
        public bool hasEnd;                                  // 마지막 장소가 고른 도착지 · 돌아오기인지 (모든 코스에 같음)
        public string Key => string.Join(",", result.stops.ConvertAll(s => s.buildingId));

        /// <summary>들르는 곳 (도착지 · 돌아오기 빼고)</summary>
        public HashSet<string> Places
        {
            get
            {
                var set = new HashSet<string>();
                for (int i = 0; i < result.stops.Count - (hasEnd ? 1 : 0); i++)
                    if (result.stops[i].restMinutes == 0)
                        set.Add(result.stops[i].buildingId);
                return set;
            }
        }

        /// <summary>
        /// 같은 코스로 볼지: 순서만 다르거나, 한쪽이 다른 쪽 장소를 모두 포함하면 (A→B→C 와 A→C) 같은 코스.
        /// 사용자 눈에는 같은 길이라 코스 3개 중 하나가 낭비된다.
        /// </summary>
        public bool SameAs(Option other) => SamePlaces(Places, other.Places);
    }

    /// <summary>두 코스가 같은 곳을 도는지: 순서만 다르거나 한쪽이 다른 쪽 장소를 모두 포함하면 같다</summary>
    public static bool SamePlaces(HashSet<string> a, HashSet<string> b) =>
        a.Count <= b.Count ? a.IsSubsetOf(b) : b.IsSubsetOf(a);

    CollectController collect;
    KyungHeeSpots spots;
    IRouteService service;
    string serviceUrl;

    RectTransform canvasRect;
    int minutes = 30;
    readonly HashSet<string> tastes = new HashSet<string> { "unvisited" };
    bool afterMeal;
    KyungHeeSpot startSpot;   // null = 내 위치
    KyungHeeSpot destSpot;    // null = 정하지 않음
    bool roundTrip;           // 출발지로 돌아오기 (destSpot 은 null)
    int request;

    // 코스 고르기 · 걷는 중
    readonly List<Option> options = new List<Option>();
    int chosen;
    Option active;            // 걷고 있는 코스 (null = 코스 없음)
    readonly List<bool> done = new List<bool>();
    CourseGuide guide;   // 걷는 중 지도 위 코스 선 (다음 장소까지 진하게 + 화살표)
    bool showPath = true;
    bool mate;   // 지금 선이 함께 걷기 코스인지 (FollowMate)
    static readonly Variant MateVariant = new Variant("mate", "함께 걷는 코스", Variants[0].color);

    readonly Dictionary<string, List<PlaceNotice>> noticesByPlace = new Dictionary<string, List<PlaceNotice>>();
    List<PlaceNotice> allNotices = new List<PlaceNotice>();

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
        BuildWalkPanel();
        BuildSheet();
        BuildPicker();
        BuildLoading();
        BuildChooser();
        BuildPlacePopup();
        BuildMusicSheet();
    }

    void EnsureService()
    {
        var board = FindAnyObjectByType<MemoBoard>();
        string url = board != null ? board.ServerUrl : "";
        if (service != null && url == serviceUrl)
            return;
        serviceUrl = url;
        var local = new LocalRouteService(() => SpotList, () => StartPosition);
        service = url.Length == 0 ? local : new HttpRouteService(url, local);
        ReloadNotices();
    }

    void ReloadNotices()
    {
        noticesByPlace.Clear();
        StartCoroutine(service.GetNotices(null, (list, error) =>
        {
            allNotices = list ?? new List<PlaceNotice>();
            noticesByPlace.Clear();
            foreach (PlaceNotice n in allNotices)
            {
                if (!noticesByPlace.TryGetValue(n.buildingId ?? "", out var l))
                    noticesByPlace[n.buildingId ?? ""] = l = new List<PlaceNotice>();
                l.Add(n);
            }
            if (active != null) RefreshWalkPanel();
        }));
    }

    CampusMap Map => collect != null ? collect.campusMap : null;

    Vector3 PlayerPosition => Map != null && Map.player != null ? Map.player.position : Vector3.zero;

    List<KyungHeeSpot> SpotList
    {
        get
        {
            var list = new List<KyungHeeSpot>();
            if (spots != null)
                foreach (KyungHeeSpot s in spots.Spots)
                    if (s != null && s.Building != null)
                        list.Add(s);
            list.Sort((a, b) => string.CompareOrdinal(a.Building.buildingName, b.Building.buildingName));
            return list;
        }
    }

    static Vector3 SpotPosition(KyungHeeSpot s)
    {
        Vector3 p = s.BuildingBounds.center;
        p.y = s.BuildingBounds.max.y;
        return p;
    }

    Vector3 StartPosition => startSpot != null ? SpotPosition(startSpot) : PlayerPosition;

    string StartLabel => startSpot != null ? startSpot.Building.buildingName : "내 위치";

    KyungHeeSpot SpotOf(string buildingId)
    {
        if (spots == null || buildingId == null) return null;
        foreach (KyungHeeSpot s in spots.Spots)
            if (s != null && s.Building != null && s.Building.buildingId == buildingId)
                return s;
        return null;
    }

    List<PlaceNotice> NoticesOf(string buildingId) =>
        !string.IsNullOrEmpty(buildingId) && noticesByPlace.TryGetValue(buildingId, out var l) ? l : new List<PlaceNotice>();

    /// <summary>취향 체크리스트의 걷는 속도 (없으면 보통)</summary>
    static string Pace
    {
        get
        {
            string p = WalkPreferences.Load().pace;
            return string.IsNullOrEmpty(p) ? "normal" : p;
        }
    }

    /// <summary>걸을 시간 안에 걸을 수 있는 거리 (m). 서버와 같이 시간의 80% 를 걷고 나머지는 둘러보는 시간</summary>
    /// <summary>코스가 끝나는 곳. 정하지 않았으면 null</summary>
    Vector3? EndPosition => roundTrip ? StartPosition : destSpot != null ? SpotPosition(destSpot) : (Vector3?)null;

    /// <summary>목적지를 출발지와 같은 곳으로 고르면 한 바퀴 코스로 바꾼다</summary>
    void FixRoundTrip()
    {
        if (destSpot != null && destSpot == startSpot)
        {
            destSpot = null;
            roundTrip = true;
        }
    }

    const float ReturnRadius = 30f;   // '내 위치' 로 돌아오기 도착 판정 (m)

    static float Budget(int mins) => mins * 0.8f * RouteMath.MetersPerMinute(Pace);

    // ---------- 추천 요청 (세 코스) ----------

    void RequestRoutes()
    {
        EnsureService();
        HideSheet();
        ShowLoading(true);
        int id = ++request;
        var made = new Option[Variants.Length];
        int left = Variants.Length;
        string lastError = null;
        for (int i = 0; i < Variants.Length; i++)
        {
            int k = i;
            int mins = MinutesFor(Variants[k]);
            StartCoroutine(service.Recommend(RequestFor(Variants[k], mins), (result, error) =>
            {
                if (id != request)
                    return; // 취소함
                made[k] = result != null ? MakeOption(Variants[k], result, mins) : null;
                lastError = error ?? lastError;
                if (--left == 0)
                    CollectOptions(id, made, lastError, true);
            }));
        }
    }

    /// <summary>
    /// 받은 코스를 모은다. 같은 코스 (SameAs) 는 한 번만 보이고, retry 면 겹친 코스만 이미 나온 장소를 빼고 한 번 더 요청한다
    /// (서버가 세 요청에 같은 곳을 골라 코스가 2개 이하로 줄어드는 것을 막는다).
    /// </summary>
    void CollectOptions(int id, Option[] made, string error, bool retry)
    {
        options.Clear();
        var again = new List<int>();
        for (int v = 0; v < made.Length; v++)
        {
            if (made[v] == null) continue;
            Option o = made[v];
            if (options.Exists(x => x.SameAs(o))) again.Add(v);
            else options.Add(o);
        }
        if (retry && again.Count > 0 && options.Count > 0)
        {
            var used = new HashSet<string>();
            foreach (Option o in options)
                foreach (RouteStop st in o.result.stops)
                    used.Add(st.buildingId);
            int left = again.Count;
            foreach (int v in again)
            {
                int k = v;
                int mins = MinutesFor(Variants[k]);
                RouteRequest req = RequestFor(Variants[k], mins);
                req.candidateBuildingIds.RemoveAll(b => used.Contains(b));
                if (req.candidateBuildingIds.Count == 0)
                {
                    made[k] = null;   // 고를 곳이 더 없으면 이 코스는 뺀다
                    if (--left == 0) CollectOptions(id, made, error, false);
                    continue;
                }
                StartCoroutine(service.Recommend(req, (result, e) =>
                {
                    if (id != request)
                        return;
                    made[k] = result != null ? MakeOption(Variants[k], result, mins) : null;
                    if (--left == 0)
                        CollectOptions(id, made, error, false);
                }));
            }
            return;
        }
        ShowLoading(false);
        if (options.Count == 0)
        {
            if (error != null && error.Contains("다시 입력해 주세요"))
                wishHint.text = error;   // 입력칸 바로 아래에 (토스트는 금방 사라져 무엇을 고칠지 놓침)
            else
                Toast(error ?? "코스를 만들지 못했어요. 잠시 뒤 다시 해 주세요");
            ShowSheet();
            return;
        }
        ShowChooser(0);
    }

    int MinutesFor(Variant v) => v.id == "light" ? (minutes >= 60 ? 30 : 15) : minutes;

    RouteRequest RequestFor(Variant v, int mins)
    {
        var req = new RouteRequest { minutes = mins, pace = Pace, afterMeal = afterMeal || v.id == "light", wish = Wish, weightKg = HealthProfile.ForRequest };
        req.tastes = v.id == "new" ? new List<string> { "unvisited" } : new List<string>(tastes);
        if (Map != null)
        {
            CampusMap.Vector2d geo = Map.WorldToGeo(StartPosition);
            req.lon = geo.x;
            req.lat = geo.y;
        }
        if (collect != null && collect.Progress != null)
            req.visitedBuildingIds = new List<string>(collect.Progress.visitedSpots);
        // 몇 번 들렀는지: 이 기기의 산책 기록에서 센다 (안 가 본 곳 · 자주 가던 곳 판단)
        var counts = new Dictionary<string, int>();
        foreach (WalkHistory.Entry e in WalkHistory.All)
            foreach (string id in e.visitedBuildingIds)
                counts[id] = counts.TryGetValue(id, out int n) ? n + 1 : 1;
        foreach (var kv in counts)
            req.visitCounts.Add(new VisitCount { buildingId = kv.Key, count = kv.Value });
        req.candidateBuildingIds = Candidates(mins);
        return req;
    }

    /// <summary>
    /// 추천 후보 스팟. 출발 스팟 · 목적지는 뺀다. 목적지가 있으면 "출발 → 후보 → 목적지" 가 걸을 수 있는 거리 안인 곳만
    /// (직선 × 1.3 ≈ 길). 너무 적으면 범위를 넓힌다.
    /// </summary>
    List<string> Candidates(int mins)
    {
        var ids = new List<string>();
        Vector3 s = StartPosition;
        Vector3? end = EndPosition;
        List<KyungHeeSpot> all = SpotList;
        for (float slack = 1f; slack <= 2.01f && ids.Count < 3; slack += 0.5f)
        {
            ids.Clear();
            float budget = Budget(mins) * slack / 1.3f;
            foreach (KyungHeeSpot spot in all)
            {
                if (spot == startSpot || spot == destSpot)
                    continue;
                if (end != null)
                {
                    // 한 바퀴 코스면 갔다 돌아올 수 있는 곳만 (s → c → s)
                    Vector3 c = SpotPosition(spot);
                    if (Flat(s, c) + Flat(c, end.Value) > budget)
                        continue;
                }
                ids.Add(spot.Building.buildingId);
            }
            if (end == null)
                break;
        }
        return ids;
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

    /// <summary>서버 응답 → 지도 위 코스. 목적지를 붙이고, 길을 따라 잇고, 시간을 넘으면 뒤쪽 장소부터 뺀다.</summary>
    Option MakeOption(Variant v, RouteResult result, int mins)
    {
        if (result.stops == null)
            return null;
        var o = new Option { variant = v, result = result, minutes = mins };
        var kept = new List<RouteStop>();
        foreach (RouteStop s in result.stops)
        {
            if (destSpot != null && s.buildingId == destSpot.Building.buildingId)
                continue; // 목적지는 마지막에 따로 붙인다
            if (startSpot != null && s.buildingId == startSpot.Building.buildingId)
                continue;
            if (!TryPlace(s, out Vector3 pos))
                continue;
            kept.Add(s);
            o.stops.Add(pos);
        }
        if (destSpot != null)
        {
            CampusMap.Vector2d geo = Map != null ? Map.WorldToGeo(SpotPosition(destSpot)) : default;
            kept.Add(new RouteStop
            {
                buildingId = destSpot.Building.buildingId, name = destSpot.Building.buildingName,
                reason = "도착지예요", lat = geo.y, lon = geo.x,
            });
            o.stops.Add(SpotPosition(destSpot));
        }
        else if (roundTrip)
        {
            if (kept.Count == 0)
                return null; // 들를 곳 없이 제자리로 돌아오는 코스는 의미가 없다
            Vector3 home = StartPosition;
            CampusMap.Vector2d geo = Map != null ? Map.WorldToGeo(home) : default;
            kept.Add(new RouteStop
            {
                buildingId = startSpot != null ? startSpot.Building.buildingId : "",
                name = $"{StartLabel}(으)로 돌아오기", reason = "출발한 곳으로 돌아와요", lat = geo.y, lon = geo.x,
            });
            o.stops.Add(home);
            o.returns = true;
        }
        if (kept.Count == 0)
            return null;
        result.stops = kept;
        Trace(o);
        // 말로 시간을 정했으면 (서버도 그 시간으로 짰다) 고른 시간 칩 대신 그 시간에 맞춘다
        if (result.wish != null && result.wish.minutes > 0)
            mins = result.wish.minutes;
        // 시간 안에 못 걸으면 (목적지를 빼고) 뒤쪽 장소부터 줄인다
        bool hasEnd = o.hasEnd = destSpot != null || o.returns;
        int minStops = o.returns ? 2 : 1;   // 한 바퀴 코스는 들를 곳 1 + 돌아오기
        while (o.meters > Budget(mins) * 1.25f && result.stops.Count > minStops)
        {
            int drop = hasEnd ? result.stops.Count - 2 : result.stops.Count - 1;
            if (drop < 0) break;
            result.stops.RemoveAt(drop);
            o.stops.RemoveAt(drop);
            Trace(o);
        }
        AddRest(o, mins);
        return o;
    }

    const int RestMinMinutes = 2, RestMaxMinutes = 5;   // 쉼터에서 쉬는 시간 (분): 고른 시간에 모자라는 만큼, 이 범위에서
    const float RestDetour = 150f;    // 쉼터를 들르려고 더 걸어도 되는 거리 (m). 코스가 고른 시간보다 짧으면 모자란 시간만큼 더
    const float RestFromMeters = 600f; // 이보다 짧은 코스는 쉬지 않는다 (m, 보통 걸음 약 8분)

    /// <summary>
    /// 사용자가 산책길 지도에 표시한 쉼터가 코스에서 가까우면 '잠깐 쉬어가기' 를 한 번 넣는다 (돌아가는 길 150 m 안,
    /// 코스가 고른 시간보다 짧으면 모자란 시간만큼 더 돌아가도 됨 — 사용자 10/29 "근처 쉼터까지 가서 좀 쉬는 느낌으로").
    /// 앞 장소와 다음 장소 사이에서 덜 돌아가는 쉼터 · 자리를 고르고, 쉬는 시간은 고른 시간에 모자라는 만큼 (2 ~ 5분).
    /// </summary>
    void AddRest(Option o, int mins)
    {
        IReadOnlyList<Vector3> rests = WalkGraph.Rests(Map);
        if (rests.Count == 0 || o.meters < RestFromMeters)
            return;
        var way = new List<Vector3> { StartPosition };
        way.AddRange(o.stops);
        float best = Mathf.Max(RestDetour, (mins - o.walkMinutes) * RouteMath.MetersPerMinute(Pace));
        int at = -1;
        Vector3 spot = default;
        for (int i = 0; i + 1 < way.Count; i++)
        {
            float leg = WalkGraph.Length(WalkGraph.Path(Map, way[i], way[i + 1]));
            foreach (Vector3 r in rests)
            {
                if (Flat(r, way[i]) < ReturnRadius || Flat(r, way[i + 1]) < ReturnRadius)
                    continue; // 출발지 · 장소 바로 옆이면 그곳에서 쉬면 된다
                float extra = WalkGraph.Length(WalkGraph.Path(Map, way[i], r)) + WalkGraph.Length(WalkGraph.Path(Map, r, way[i + 1])) - leg;
                if (extra < best) { best = extra; at = i; spot = r; }
            }
        }
        if (at < 0 || o.meters + best > Budget(mins) * 1.25f)
            return;
        int rest = Mathf.Clamp(mins - RouteMath.MinutesFor(o.meters + best, Pace), RestMinMinutes, RestMaxMinutes);
        CampusMap.Vector2d geo = Map != null ? Map.WorldToGeo(spot) : default;
        o.result.stops.Insert(at, new RouteStop
        {
            buildingId = "", name = "잠깐 쉬어가기", reason = $"쉼터에서 {rest}분쯤 숨 돌리고 가요",
            lat = geo.y, lon = geo.x, restMinutes = rest,
        });
        o.stops.Insert(at, spot);
        Trace(o);
    }

    /// <summary>출발 → 들를 곳을 길을 따라 잇고, 장소별 · 전체 거리와 시간을 길 기준으로 다시 센다.</summary>
    void Trace(Option o)
    {
        var way = new List<Vector3> { StartPosition };
        way.AddRange(o.stops);
        o.path = new List<Vector3>();
        o.legEnds.Clear();
        float total = 0f;
        for (int i = 0; i + 1 < way.Count; i++)
        {
            List<Vector3> seg = WalkGraph.Path(Map, way[i], way[i + 1]);
            float m = WalkGraph.Length(seg);
            total += m;
            RouteStop s = o.result.stops[i];
            s.distanceM = Mathf.RoundToInt(m);
            s.walkMinutes = RouteMath.MinutesFor(m, Pace);
            for (int k = o.path.Count == 0 ? 0 : 1; k < seg.Count; k++)
                o.path.Add(seg[k]);
            o.legEnds.Add(o.path.Count - 1);
        }
        o.meters = Mathf.RoundToInt(total);
        o.walkMinutes = RouteMath.MinutesFor(total, Pace);
        foreach (RouteStop s in o.result.stops)
            o.walkMinutes += s.restMinutes;
        o.result.totalDistanceM = o.meters;
        o.result.totalMinutes = o.walkMinutes;
    }

    bool TryPlace(RouteStop s, out Vector3 pos)
    {
        KyungHeeSpot spot = SpotOf(s.buildingId);
        if (spot != null)
        {
            pos = SpotPosition(spot);
            return true;
        }
        if ((s.lat != 0 || s.lon != 0) && Map != null)
        {
            pos = Map.GeoToWorld(s.lon, s.lat);
            return true;
        }
        pos = Vector3.zero;
        return false;
    }

    // ---------- 걷기 시작 · 끝 ----------

    void StartWalk(Option o)
    {
        HideChooser();
        EndRoute(false);
        active = o;
        done.Clear();
        for (int i = 0; i < o.result.stops.Count; i++)
            done.Add(false);
        if (guide == null)
            guide = CourseGuide.Create(transform);
        guide.Show(o.path, o.legEnds, o.variant.color);
        RefreshGuide();
        guide.SetVisible(showPath);
        WalkSession.BeginCourse(o.result.title, o.meters, o.result.stops.Count);
        // 서버에 산책 시작을 알린다 (추천 ID 로 AI · 기본 추천의 수락률을 셈). 실패하면 완료는 기기에서 판정
        StartCoroutine(WalkCompleteScreen.CreateService().Begin(o.result.id, o.minutes, WalkSession.WalkIdSetter()));
        RefreshWalkPanel();
        walkPanel.SetActive(true);
        Toast($"{o.variant.label}로 출발해요");
    }

    /// <summary>
    /// 함께 걷기: 두 사람이 정한 코스를 혼자 걷기처럼 지도 위 선 (다음 장소까지 진하게 + 화살표) 으로 그리고 도착한 곳을 체크한다.
    /// 걷는 중 판 · 산책 기록 · 끝내기는 MatchScreen 이 맡는다. course 가 null 이면 선을 지운다.
    /// </summary>
    public static void FollowMate(MatchCourse course)
    {
        RoutePlanner p = FindAnyObjectByType<RoutePlanner>();
        if (p == null)
        {
            if (course == null) return;
            p = new GameObject("RoutePlanner").AddComponent<RoutePlanner>();
        }
        if (course == null || course.stops == null || course.stops.Count == 0)
        {
            if (p.mate) p.EndRoute(false);
            return;
        }
        if (p.mate && p.active != null && p.active.result.id == course.recommendationId)
            return;   // 이미 그리는 코스
        var result = new RouteResult { title = course.title, id = course.recommendationId, source = course.source };
        var o = new Option { variant = MateVariant, result = result };
        foreach (CourseStop s in course.stops)
        {
            var stop = new RouteStop { buildingId = s.buildingId, name = s.name, reason = s.reason };
            if (!p.TryPlace(stop, out Vector3 pos)) continue;
            result.stops.Add(stop);
            o.stops.Add(pos);
        }
        if (result.stops.Count == 0)
            return;
        p.Trace(o);
        p.HideChooser();
        p.EndRoute(false);
        p.mate = true;
        p.active = o;
        for (int i = 0; i < result.stops.Count; i++)
            p.done.Add(false);
        if (p.guide == null)
            p.guide = CourseGuide.Create(p.transform);
        p.guide.Show(o.path, o.legEnds, o.variant.color);
        p.RefreshGuide();
        p.guide.SetVisible(p.showPath);
    }

    /// <summary>지도 위 선을 내 위치 → 다음 장소 기준으로 다시 그린다</summary>
    void RefreshGuide()
    {
        if (guide != null && active != null)
            guide.Refresh(done.FindIndex(x => !x), PlayerPosition);
    }

    /// <summary>코스 끝내기. finish 면 80% 이상 걸었거나 한 곳이라도 들렀을 때 완료 화면을 띄운다</summary>
    void EndRoute(bool finish)
    {
        if (finish && active != null && WalkSession.Active && WalkSession.Kind == WalkSession.Course)
        {
            var visited = new List<string>();
            for (int i = 0; i < done.Count && i < active.result.stops.Count; i++)
                if (done[i] && !string.IsNullOrEmpty(active.result.stops[i].buildingId)) visited.Add(active.result.stops[i].buildingId);
            if (WalkSession.WalkedMeters >= active.meters * LocalWalkService.CourseRatio || visited.Count > 0)
                WalkCompleteScreen.Show(WalkSession.Finish(visited));
            else
                WalkSession.Abandon();
        }
        active = null;
        mate = false;
        done.Clear();
        if (guide != null) guide.SetVisible(false);
        if (walkPanel != null) walkPanel.SetActive(false);
    }

    void LateUpdate()
    {
        // 다른 화면(메뉴·메모판·설정·잡기 등)이 열리면 길잡이 창은 닫고, 걷는 중 판은 그동안 숨긴다.
        bool otherOpen = UIInputBlocker.IsOtherModalOpen(this);
        if (otherOpen)
        {
            if (loading.activeSelf) { request++; ShowLoading(false); }
            if (sheet.activeSelf) HideSheet();
            if (picker.activeSelf) picker.SetActive(false);
            if (placePopup.activeSelf) ClosePlace();
        }
        bool showPanel = active != null && !mate && !otherOpen && !chooser.activeSelf;
        if (walkPanel.activeSelf != showPanel)
            walkPanel.SetActive(showPanel);
        if (showPanel)
            VirtualWalk.PlacePanel((RectTransform)walkPanel.transform, WalkPanelHeight);
        if (guide != null)
            guide.SetVisible(active != null && showPath);

        if (loading.activeSelf && loadingPartner != null)
            loadingPartner.rectTransform.anchoredPosition = new Vector2(0f, Mathf.Abs(Mathf.Sin(Time.unscaledTime * 6f)) * 36f);
        if (active == null)
            return;

        // 도착한 곳은 체크하고, 다 돌면 축하 + 완료 화면
        for (int i = 0; i < active.result.stops.Count; i++)
        {
            if (done[i]) continue;
            if (active.returns && i == active.result.stops.Count - 1)
            {
                // 돌아오기는 다른 곳을 다 들른 뒤에, 출발한 자리 근처에 오면
                if (!done.GetRange(0, i).TrueForAll(x => x)) continue;
                KyungHeeSpot home = SpotOf(active.result.stops[i].buildingId);
                bool back = home != null ? home.InRange : Flat(PlayerPosition, active.stops[i]) < ReturnRadius;
                if (!back) continue;
            }
            else
            {
                KyungHeeSpot spot = SpotOf(active.result.stops[i].buildingId);
                bool here = spot != null ? spot.InRange
                    : active.result.stops[i].restMinutes > 0 && Flat(PlayerPosition, active.stops[i]) < ReturnRadius;
                if (!here) continue;
            }
            done[i] = true;
            RefreshGuide();
            bool finished = done.TrueForAll(x => x);
            Toast(finished ? "코스 완주! 수고했어요"
                : active.result.stops[i].restMinutes > 0 ? $"쉼터예요. {active.result.stops[i].restMinutes}분쯤 쉬었다 가요"
                : $"{active.result.stops[i].name} 도착!");
            if (finished)
            {
                Quests.Report("course_done");
                if (WalkSession.Active && WalkSession.Kind == WalkSession.Course)
                    WalkCompleteScreen.Show(WalkSession.Finish(active.result.stops.ConvertAll(x => x.buildingId).FindAll(id => !string.IsNullOrEmpty(id))));
                EndRoute(false);
                return;
            }
        }
        if (Time.frameCount % 10 == 0)
            RefreshGuide();
        if (Time.frameCount % 15 == 0)
            RefreshWalkPanel();
    }

    static string Escape(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");

    static void Toast(string message)
    {
        var hud = FindAnyObjectByType<GameHUD>();
        if (hud != null) hud.Toast(message);
    }

    void OnDisable() => UIInputBlocker.SetModal(this, false);

    void OnDestroy()
    {
        if (miniTexture != null) miniTexture.Release();
    }
}
