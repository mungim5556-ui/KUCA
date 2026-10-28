using NUnit.Framework;

/// <summary>서버 MatchState (docs/api.md "매칭") → 앱 MatchState 옮겨 담기 테스트</summary>
public class ServerMatchTests
{
    [Test]
    public void 대기_제안_수락은_앱_상태로()
    {
        Assert.AreEqual(MatchState.Idle, HttpMatchService.Parse("{\"state\":\"idle\"}").status);
        MatchState w = HttpMatchService.Parse("{\"state\":\"waiting\",\"matchId\":\"m_1\",\"waitingUntil\":\"2099-01-01T00:00:00Z\"}");
        Assert.AreEqual(MatchState.Waiting, w.status);
        Assert.Greater(w.waitSecondsLeft, 0);
        Assert.IsNull(w.partner);

        MatchState p = HttpMatchService.Parse("{\"state\":\"proposed\",\"matchId\":\"m_1\",\"partner\":{\"nickname\":\"민지\",\"commonTastes\":[\"photo\"]},\"myAnswer\":null}");
        Assert.AreEqual(MatchState.Proposed, p.status);
        Assert.AreEqual("민지", p.partner.nickname);
        Assert.AreEqual("photo", p.partner.commonTastes[0]);
        Assert.AreEqual(MatchState.Accepted, HttpMatchService.Parse("{\"state\":\"proposed\",\"myAnswer\":\"accepted\",\"partner\":{\"nickname\":\"민지\"}}").status);
    }

    [Test]
    public void 만남과_산책은_스팟_인상착의_산책ID를_옮긴다()
    {
        const string meeting = "{\"state\":\"meeting\",\"matchId\":\"m_1\",\"minutes\":30,\"partner\":{\"nickname\":\"민지\",\"appearance\":\"검은 후드\"}," +
            "\"meetSpot\":{\"buildingId\":\"way-9\",\"name\":\"학생회관\",\"lat\":37.2,\"lon\":127.0,\"meetSpecies\":\"pigeon\"}," +
            "\"meetBy\":\"2099-01-01T00:00:00Z\",\"smalltalk\":[\"a\",\"b\",\"c\"],\"myAppearance\":\"회색 맨투맨\",\"arrived\":{\"me\":true,\"partner\":false}}";
        MatchState m = HttpMatchService.Parse(meeting);
        Assert.AreEqual(MatchState.Matched, m.status);
        Assert.AreEqual("way-9", m.spot.buildingId);
        Assert.AreEqual("pigeon", m.spot.species);
        Assert.AreEqual("검은 후드", m.partnerLook);
        Assert.AreEqual("회색 맨투맨", m.myLook);
        Assert.AreEqual(3, m.smalltalk.Count);
        Assert.IsTrue(m.meArrived);
        Assert.IsFalse(string.IsNullOrEmpty(m.message)); // 나만 도착 → 기다린다는 안내

        MatchState walking = HttpMatchService.Parse("{\"state\":\"walking\",\"matchId\":\"m_1\",\"walkId\":\"w_7\",\"partner\":{\"nickname\":\"민지\"}}");
        Assert.AreEqual(MatchState.Walking, walking.status);
        Assert.AreEqual("w_7", walking.walkId);
    }

    [Test]
    public void 끝난_이유에_따라_완료_취소_시간초과()
    {
        Assert.AreEqual(MatchState.Ended, HttpMatchService.Parse("{\"state\":\"ended\",\"endReason\":\"completed\"}").status);
        Assert.AreEqual(MatchState.Expired, HttpMatchService.Parse("{\"state\":\"ended\",\"endReason\":\"no_partner\"}").status);
        MatchState cancel = HttpMatchService.Parse("{\"state\":\"ended\",\"endReason\":\"cancelled\",\"cancelledBy\":\"partner\"}");
        Assert.AreEqual(MatchState.Cancelled, cancel.status);
        StringAssert.Contains("상대", cancel.message);
        Assert.AreEqual(MatchState.Cancelled, HttpMatchService.Parse("{\"state\":\"ended\",\"endReason\":\"noshow\"}").status);
        Assert.IsNull(HttpMatchService.Parse("not json"));
    }

    [Test]
    public void 대기_인원과_AI_매칭_이유를_옮긴다()
    {
        MatchState w = HttpMatchService.Parse("{\"state\":\"waiting\",\"waitingUntil\":\"2099-01-01T00:00:00Z\",\"nowSearching\":2}");
        Assert.AreEqual(2, w.nowSearching);
        Assert.AreEqual(-1, HttpMatchService.Parse("{\"state\":\"waiting\"}").nowSearching);   // 예전 서버: 모름

        MatchState p = HttpMatchService.Parse("{\"state\":\"proposed\",\"partner\":{\"nickname\":\"민지\",\"whyMatched\":\"둘 다 사진 찍기를 좋아해서 연결했어요\"}}");
        Assert.AreEqual("둘 다 사진 찍기를 좋아해서 연결했어요", p.partner.whyMatched);
    }

    [Test]
    public void 코스와_주제_카드를_옮기고_없으면_비운다()
    {
        const string meeting = "{\"state\":\"meeting\",\"matchId\":\"m_1\",\"partner\":{\"nickname\":\"민지\"}," +
            "\"smalltalk\":[\"학식 뭐 좋아해요?\"],\"smalltalkCards\":[{\"text\":\"학식 뭐 좋아해요?\",\"topic\":\"학식\",\"emoji\":\"🍚\"}]," +
            "\"course\":{\"recommendationId\":\"rec_1\",\"title\":\"가을 30분\",\"source\":\"ai\",\"stops\":[{\"buildingId\":\"way-1\",\"name\":\"중앙도서관\",\"reason\":\"쉬어 가요\",\"walkMinutes\":4,\"distanceM\":300}]," +
            "\"totalDistanceM\":300,\"totalMinutes\":4,\"myAgree\":true,\"partnerAgree\":false,\"confirmed\":false,\"changesLeft\":2}}";
        MatchState m = HttpMatchService.Parse(meeting);
        Assert.IsTrue(m.HasCourse);
        Assert.AreEqual("중앙도서관", m.course.stops[0].name);
        Assert.IsTrue(m.course.myAgree);
        Assert.AreEqual(2, m.course.changesLeft);
        Assert.AreEqual("학식", m.cards[0].topic);

        MatchState old = HttpMatchService.Parse("{\"state\":\"meeting\",\"matchId\":\"m_1\",\"smalltalk\":[\"a\",\"b\"]}");
        Assert.IsFalse(old.HasCourse);                   // 코스가 없으면 (만드는 중 · 예전 서버) null
        Assert.AreEqual(2, old.cards.Count);             // 카드가 없으면 글만으로 만든다
        Assert.AreEqual("대화", old.cards[0].topic);
    }

    [Test]
    public void 함께_걷는_코스에_대략_칼로리와_난이도가_붙는다()
    {
        const string json = "{\"state\":\"meeting\",\"matchId\":\"m_1\",\"partner\":{\"nickname\":\"민지\"}," +
            "\"course\":{\"recommendationId\":\"rec_1\",\"title\":\"조용한 가을산책\",\"stops\":[{\"buildingId\":\"b1\",\"name\":\"공학실험동\"}," +
            "{\"buildingId\":\"b2\",\"name\":\"원예생물공학온실\"},{\"buildingId\":\"b3\",\"name\":\"노천극장\"}],\"totalDistanceM\":1500,\"totalMinutes\":21," +
            "\"health\":{\"steps\":2140,\"kcalMin\":61,\"kcalMax\":86,\"intensity\":\"moderate\",\"intensityLabel\":\"보통 걷기\"}," +
            "\"difficulty\":{\"level\":\"normal\",\"label\":\"중\",\"hills\":0}}}";
        MatchState m = HttpMatchService.Parse(json);
        Assert.AreEqual("3곳  ·  21분  ·  1.5km  ·  약 61~86kcal", MatchScreen.CourseSummary(m.course));
        StringAssert.Contains("난이도 중", CourseDifficulty.Tag(m.course.difficulty, KucaTheme.Label));

        // 예전 서버 (칼로리 · 난이도 없음): 붙이지 않는다
        MatchState old = HttpMatchService.Parse(json.Replace("\"health\"", "\"x1\"").Replace("\"difficulty\"", "\"x2\""));
        Assert.AreEqual("3곳  ·  21분  ·  1.5km", MatchScreen.CourseSummary(old.course));
        Assert.AreEqual("", CourseDifficulty.Tag(old.course.difficulty, KucaTheme.Label));
    }
}
