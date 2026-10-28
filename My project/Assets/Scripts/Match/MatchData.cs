using System;
using System.Collections;
using System.Collections.Generic;

// 실시간 산책 매칭 (B-V3). 앱 안에서 쓰는 모양이다.
// 서버 모양(docs/api.md "매칭")은 HttpMatchService 가 이 모양으로 옮겨 담고, 기기 안 가상 상대(MockMatchService)도 같은 모양을 쓴다.

[Serializable]
public class MatchQueueRequest
{
    /// <summary>30 · 60</summary>
    public int minutes = 30;
    /// <summary>true 면 동성만 (내 성별은 서버 계정에 있음)</summary>
    public bool sameGender;
    /// <summary>지금 있는 건물 ID (GPS 좌표는 보내지 않는다)</summary>
    public string buildingId;
    public List<string> tastes = new List<string>();
}

[Serializable]
public class MatchPartner
{
    public string nickname;
    public List<string> commonTastes = new List<string>();
    /// <summary>chatty · some · quiet</summary>
    public string talkStyle;
    /// <summary>왜 둘을 연결했는지 한 줄 (서버 AI, 실패하면 공통점 틀 문장)</summary>
    public string whyMatched;
}

/// <summary>스몰토크 카드 한 장 (서버 smalltalkCards). topic: 공강 · 학식 · 전공 · 캠퍼스 장소 · 계절 · 날씨 · 취향 · 대화</summary>
[Serializable]
public class TopicCard
{
    public string text;
    public string topic;
    public string emoji;

    public static TopicCard Plain(string text) => new TopicCard { text = text, topic = "대화", emoji = "💬" };
}

[Serializable]
public class CourseStop
{
    public string buildingId;
    public string name;
    public string reason;
    public int walkMinutes;
    public int distanceM;
}

/// <summary>
/// 함께 걸을 코스 (서버 course). 둘 다 "이 코스 좋아요" 를 누르면 confirmed.
/// "다른 코스" 는 changesLeft 번 더 볼 수 있고, 바꾸면 두 사람의 동의가 지워진다.
/// </summary>
[Serializable]
public class MatchCourse
{
    public string recommendationId;
    public string title;
    public string source;
    public List<CourseStop> stops = new List<CourseStop>();
    public int totalDistanceM;
    public int totalMinutes;
    public bool myAgree, partnerAgree, confirmed;
    public int changesLeft;
    /// <summary>대략 칼로리 · 걸음 수 (몸무게는 매칭에서 받지 않아 50~70kg 범위). 예전 서버면 비어 있음</summary>
    public HealthEstimate health;
    /// <summary>난이도 하 · 중 · 상</summary>
    public CourseDifficulty difficulty;
}

[Serializable]
public class MeetSpot
{
    public string buildingId;
    public string name;
    public double lat, lon;
    /// <summary>그 스팟에 서 있는 경희펫 (CreatureLibrary id)</summary>
    public string species;
}

[Serializable]
public class MatchState
{
    public const string Idle = "idle", Waiting = "waiting", Proposed = "proposed", Accepted = "accepted",
        Matched = "matched", Walking = "walking", Ended = "ended", Cancelled = "cancelled", Expired = "expired";

    public string status = Idle;
    public string matchId;
    public int waitSecondsLeft;
    public int decideSecondsLeft;
    public MatchPartner partner;
    public bool partnerAccepted;
    public MeetSpot spot;
    public List<string> smalltalk = new List<string>();
    /// <summary>smalltalk 와 같은 주제에 분류 · 이모지를 붙인 것 (서버가 안 주면 비어 있음)</summary>
    public List<TopicCard> cards = new List<TopicCard>();
    /// <summary>함께 걸을 코스 (meeting · walking). 서버가 만드는 몇 초 동안은 null</summary>
    public MatchCourse course;
    /// <summary>기다리는 동안 나 말고 지금 짝을 찾는 사람 수 (-1 = 모름)</summary>
    public int nowSearching = -1;
    public string myLook;
    public string partnerLook;
    public int noShowSecondsLeft;
    public int minutes;
    public string message;
    /// <summary>둘 다 만남 스팟에 도착해 산책이 시작되면 생기는 서버 산책 ID (산책 완료 API 에 씀)</summary>
    public string walkId;
    /// <summary>만남 스팟 도착 여부 (서버)</summary>
    public bool meArrived, partnerArrived;

    /// <summary>코스가 왔는지 (JsonUtility 는 없는 객체도 빈 객체로 채우므로 ID 로 본다)</summary>
    public bool HasCourse => course != null && !string.IsNullOrEmpty(course.recommendationId);

    /// <summary>대기 · 제안 · 만남 · 산책 중 (화면을 닫아도 계속 지켜볼 상태)</summary>
    public bool Active => status == Waiting || status == Proposed || status == Accepted || status == Matched || status == Walking;
}

/// <summary>
/// 매칭 서버. HTTP 구현(HttpMatchService)과 기기 안 가상 상대(MockMatchService)가 같은 모양을 따른다 (Memo 의 Mock/Http 와 같은 구조).
/// 모든 호출은 코루틴이고, 끝나면 onDone(상태, 에러 메시지). 에러가 없으면 null.
/// </summary>
public interface IMatchService
{
    /// <summary>화면에 작게 보여 줄 이름 (예: "가상 상대")</summary>
    string Description { get; }
    IEnumerator Join(MatchQueueRequest request, Action<MatchState, string> onDone);
    IEnumerator Leave(Action<MatchState, string> onDone);
    IEnumerator Status(Action<MatchState, string> onDone);
    IEnumerator Respond(string matchId, bool accept, Action<MatchState, string> onDone);
    IEnumerator SetLook(string matchId, string look, Action<MatchState, string> onDone);
    /// <summary>다음 스몰토크 주제 카드. onDone(카드, 에러)</summary>
    IEnumerator NextTopic(string matchId, Action<TopicCard, string> onDone);
    /// <summary>"이 코스 좋아요" (둘 다 누르면 확정)</summary>
    IEnumerator AgreeCourse(string matchId, Action<MatchState, string> onDone);
    /// <summary>"다른 코스 보기" (두 사람의 동의가 지워진다)</summary>
    IEnumerator NextCourse(string matchId, Action<MatchState, string> onDone);
    IEnumerator StartWalk(string matchId, Action<MatchState, string> onDone);
    /// <summary>산책 끝내기 → ended (완료 · 리뷰 · 보상은 B-V4 / C-V4)</summary>
    IEnumerator End(string matchId, Action<MatchState, string> onDone);
    IEnumerator Cancel(string matchId, string reason, Action<MatchState, string> onDone);
    /// <summary>reason: rude · noshow · personal · other</summary>
    IEnumerator Report(string matchId, string reason, string detail, bool block, Action<MatchState, string> onDone);
}
