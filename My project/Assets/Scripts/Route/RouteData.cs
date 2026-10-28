using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;

// 산책 추천 · 장소 공지 (B-4). docs/api.md 의 "산책 추천", "장소 공지" 와 같은 모양 (JsonUtility 로 그대로 읽는다).
// v2 칸 (B-V2) 은 docs/api.md "POST /api/recommendations (v2)". 응답에 없으면 (기기 추천) 앱이 RouteMath 로 채운다.
// 시간대 · 계절 · 날씨는 서버가 정한다. 걸음 수 · 칼로리는 서버 공식의 대략 범위만 보여 준다 (10/27, docs/api.md "건강").

/// <summary>POST /api/recommendations 본문</summary>
[Serializable]
public class RouteRequest
{
    /// <summary>걸을 시간 (분): 15, 30, 60</summary>
    public int minutes;
    /// <summary>"photo"(사진 명소) · "quiet"(조용한 곳) · "food"(맛집·카페) · "unvisited"(처음 가는 곳)</summary>
    public List<string> tastes = new List<string>();
    /// <summary>지금 위치 (GPS)</summary>
    public double lat, lon;
    /// <summary>이미 가 본 경희스팟 건물 ID</summary>
    public List<string> visitedBuildingIds = new List<string>();
    /// <summary>추천 후보 (앱의 경희스팟 건물 ID). 서버는 이 안에서만 고른다</summary>
    public List<string> candidateBuildingIds = new List<string>();

    // ---------- v2 ----------
    /// <summary>걷는 속도 "slow" · "normal" · "fast" (취향 체크리스트)</summary>
    public string pace = "normal";
    /// <summary>식후 걷기 버튼으로 요청함 (가까운 곳 위주, 응답에 healthNote)</summary>
    public bool afterMeal;
    /// <summary>말로 한 산책 바람 (200자 이내, 비우면 칩 선택만). 서버가 해석해 시간 · 취향 · 피할 곳에 반영한다</summary>
    public string wish;
    /// <summary>장소마다 들른 횟수 (이 기기의 산책 기록). 서버가 "안 가 본 곳 · 자주 가던 곳" 을 판단할 때 쓴다</summary>
    public List<VisitCount> visitCounts = new List<VisitCount>();
    /// <summary>(선택) 몸무게 kg — 칼로리 범위를 좁힌다 (HealthProfile, 이 기기에만 저장). 0 = 모름</summary>
    public float weightKg;
}

[Serializable]
public class VisitCount
{
    public string buildingId;
    public int count;
}

/// <summary>서버가 바람을 어떻게 이해했는지 (RouteResult.wish)</summary>
[Serializable]
public class RouteWish
{
    /// <summary>"20분 · 조용한 곳 · 오르막 피하기" 같은 한 줄</summary>
    public string summary;
    /// <summary>"ai"(AI 가 해석) 또는 "fallback"(말 찾기)</summary>
    public string source;
    /// <summary>어떤 산책이 맞는지 판단과 이유 한 문장</summary>
    public string judgment;
}

[Serializable]
public class RouteStop
{
    public string buildingId;
    public string name;
    /// <summary>추천 이유 한 문장</summary>
    public string reason;
    /// <summary>앞 장소(첫 곳은 지금 위치)에서 걸어서 몇 분</summary>
    public int walkMinutes;
    public double lat, lon;
    /// <summary>v2: 앞 장소(첫 곳은 지금 위치)에서 여기까지 걷는 거리 (m)</summary>
    public int distanceM;
}

/// <summary>서버가 추천에 쓴 상황 (시간대 · 계절 · 날씨)</summary>
[Serializable]
public class RouteContext
{
    /// <summary>morning · day · evening · night</summary>
    public string timeOfDay;
    /// <summary>spring · summer · autumn · winter</summary>
    public string season;
    /// <summary>clear · cloudy · rain · snow (모르면 빈 값)</summary>
    public string weather;
}

[Serializable]
public class RouteResult
{
    public string title;
    public List<RouteStop> stops = new List<RouteStop>();
    /// <summary>"ai"(AI 추천) 또는 "fallback"(AI 없이 기본 규칙으로 고름)</summary>
    public string source;

    // ---------- v2 ----------
    /// <summary>추천 ID. 이 코스로 산책을 시작할 때 POST /api/walks 에 넣는다 (기기 추천은 빈 값)</summary>
    public string id;
    public RouteContext context;
    /// <summary>코스 전체 걷는 거리 (m) · 시간 (분)</summary>
    public int totalDistanceM;
    public int totalMinutes;
    /// <summary>식후 걷기 안내 (afterMeal 일 때만, 의료 조언 아님 문구 포함)</summary>
    public string healthNote;
    /// <summary>바람을 보냈을 때만: 어떻게 이해했는지</summary>
    public RouteWish wish;
    /// <summary>코스 전체를 이렇게 짠 이유 한 문장</summary>
    public string why;
    /// <summary>걸음 수 · 칼로리 범위 · 강도 (서버 공식, 기기 추천이면 비어 있음 → steps 0)</summary>
    public HealthEstimate health;
    /// <summary>로그인했을 때만: 이 코스로 이번 주 목표(150분)에 얼마나 다가가는지 (note 가 비면 없음)</summary>
    public CourseGoal goal;
    /// <summary>난이도 하 · 중 · 상 (서버가 거리 · 오르막 장소 수로 정함, 기기 추천이면 비어 있음)</summary>
    public CourseDifficulty difficulty;
}

/// <summary>코스 난이도 (docs/api.md "건강"). level: easy · normal · hard, label: 하 · 중 · 상</summary>
[Serializable]
public class CourseDifficulty
{
    public string level, label;
    /// <summary>가는 길에 힘든 오르막이 있는 장소 수</summary>
    public int hills;

    /// <summary>JsonUtility 는 빠진 객체도 빈 객체로 채우니 글자로 있는지 본다</summary>
    public static bool Has(CourseDifficulty d) => d != null && !string.IsNullOrEmpty(d.label);

    /// <summary>하 초록 · 중 회색 · 상 크림슨 (KucaTheme 색만)</summary>
    public Color Color => level == "easy" ? KucaTheme.PrimaryDeep : level == "hard" ? KucaTheme.Crimson : KucaTheme.InkMuted;

    /// <summary>제목 옆에 붙이는 작은 글자: "  난이도 중" (없으면 빈 글자)</summary>
    public static string Tag(CourseDifficulty d, int size) =>
        Has(d) ? $"  <size={size}><color=#{ColorUtility.ToHtmlStringRGB(d.Color)}>난이도 {d.label}</color></size>" : "";
}

/// <summary>걸음 수 · 칼로리 범위 · 강도 (docs/api.md "건강"). 걸음 센서가 없어 대략값이다</summary>
[Serializable]
public class HealthEstimate
{
    public int steps, kcalMin, kcalMax;
    /// <summary>"light" · "moderate" · "brisk"</summary>
    public string intensity;
    /// <summary>"가벼운 산책" · "보통 걷기" · "운동 되는 걷기"</summary>
    public string intensityLabel;
    /// <summary>true 면 걸음 수가 휴대폰 걸음 센서 값 (산책 끝 응답만), false 면 거리로 어림</summary>
    public bool stepsMeasured;

    /// <summary>JsonUtility 는 빠진 객체도 빈 객체로 채우니 숫자로 있는지 본다</summary>
    public static bool Has(HealthEstimate h) => h != null && h.steps > 0;
}

/// <summary>이번 주 걷기 목표 (월요일 0시부터, WHO 150분)</summary>
[Serializable]
public class WeekGoal
{
    public int weekMinutes, goalMinutes, minutesLeft, weekWalks;
    public static bool Has(WeekGoal w) => w != null && w.goalMinutes > 0;
}

/// <summary>이 코스를 걸으면 이번 주 목표가 어떻게 되는지</summary>
[Serializable]
public class CourseGoal
{
    public int weekMinutes, goalMinutes, minutesLeft;
    public bool completesGoal;
    public string note;
}

/// <summary>학교 공지 중 특정 장소와 관련된 것 (GET /api/notices)</summary>
[Serializable]
public class PlaceNotice
{
    public string id;
    public string buildingId;
    public string buildingName;
    public string title;
    /// <summary>한두 문장 요약</summary>
    public string summary;
    /// <summary>원문 링크</summary>
    public string url;
    /// <summary>게시일 (yyyy-MM-dd)</summary>
    public string postedAt;
}

[Serializable]
public class PlaceNoticeList { public List<PlaceNotice> items = new List<PlaceNotice>(); }

public interface IRouteService
{
    IEnumerator Recommend(RouteRequest request, Action<RouteResult, string> onDone);
    /// <summary>buildingId 가 null 이면 장소와 연결된 모든 공지 (최신순)</summary>
    IEnumerator GetNotices(string buildingId, Action<List<PlaceNotice>, string> onDone);
}
