using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 실시간 산책 매칭 화면 (B-V3). 지도 오른쪽 위 산책메이트 버튼으로 연다.
/// 흐름: 조건 (30/60분 — 함께 걷기는 최소 30분 · 동성만/상관없음) → 대기 (남은 시간 · 취소) → 상대 제안 (닉네임 · 공통 취향, 수락/거절)
///      → 상대 수락 기다림 → 만남 스팟 → 인상착의 → 스몰토크 → 산책 시작 → 산책 중 (끝내기).
/// 어느 단계든 신고 · 차단 · 취소를 할 수 있고, 만나기로 한 뒤 10분 안에 시작하지 않으면 노쇼로 취소된다 (서버 판정).
/// 화면을 닫아도 매칭은 계속되고 (상태를 계속 물어봄), 상대가 나타나면 화면이 다시 열린다.
/// 서버 호출은 IMatchService: 서버에 API 가 없으면 기기 안 가상 상대 (HttpMatchService → MockMatchService).
/// v4 (docs/디자인_v4.md): 당근 "같이 해요" + 토스 단계형 — 단계마다 큰 질문 제목 · 칩 선택 · 맨 위 안전 카드, 아래 고정 버튼.
/// 10/25 피드백 (생동감): 대기 중 원 파동 + "지금 n명이 함께 찾는 중", 찾은 순간 별가루, 제안에 AI 매칭 이유 한 줄,
/// 만남 뒤 함께 걸을 코스를 둘이 고르는 단계 (둘 다 "좋아요" 면 확정 · "다른 코스" 3번), 대화 주제는 분류 태그가 붙은 카드 (MatchEffects.cs).
/// </summary>
public class MatchScreen : MonoBehaviour
{
    static readonly Color Ink = KucaTheme.Ink;
    static readonly Color InkQuiet = KucaTheme.InkMuted;
    static readonly Color Accent = KucaTheme.Primary;
    static readonly Color Danger = KucaTheme.Crimson;

    static readonly Dictionary<string, string> TasteNames = new Dictionary<string, string>
    {
        ["photo"] = "사진 명소", ["quiet"] = "조용한 곳", ["food"] = "맛집·카페", ["unvisited"] = "처음 가는 곳",
        ["partner"] = "파트너와 걷기", ["exercise"] = "운동",
    };

    IMatchService service;
    string serviceUrl;
    MatchState state = new MatchState();
    bool busy;
    float pollAt;
    int minutes = 30;
    bool sameGender;
    int topicIndex;
    TopicCard shownTopic;
    string shownCourseId;

    GameObject root, conditionPage, waitPage, proposePage, acceptWaitPage, meetPage, coursePage, lookPage, talkPage, walkPage, endPage, reportPanel;
    Text waitTime, waitSearching, proposeName, proposeWhy, proposeTastes, proposeTime, meetName, meetInfo, meetNoShow, lookMine, lookPartner,
        courseTitle, courseSummary, courseStatus, courseLoading, talkTopic, talkTopicTag, walkTitle, walkCourse, endTitle, endMessage, serviceLabel;
    GameObject proposeWhyBox;
    RectTransform proposeCard, courseCard, courseStops, talkCard;
    LayoutElement talkTagSize;
    Button courseAgree, courseNext;
    RawImage waitPartner, acceptWaitPartner, meetCreature;
    InputField lookInput;
    Image[] minuteChips, genderChips, reasonChips;
    Image blockChip;
    Button reportButton;
    string reportReason = "rude";
    bool reportBlock = true;

    static readonly (string id, string label)[] Reasons =
        { ("rude", "불쾌한 말 · 행동"), ("noshow", "나오지 않음"), ("personal", "개인정보 요구"), ("other", "기타") };

    public static void Open()
    {
        MatchScreen s = FindAnyObjectByType<MatchScreen>();
        if (s == null)
            s = new GameObject("MatchScreen").AddComponent<MatchScreen>();
        s.Show(true);
    }

    void Awake()
    {
        EnsureService();
        Build();
    }

    void EnsureService()
    {
        var board = FindAnyObjectByType<MemoBoard>();
        string url = board != null ? board.ServerUrl : "";
        if (service != null && url == serviceUrl)
            return;
        serviceUrl = url;
        var mock = new MockMatchService(NearestMeetSpot);
        service = url.Length == 0 ? mock : new HttpMatchService(url, mock);
    }

    void Show(bool open)
    {
        root.SetActive(open);
        UIInputBlocker.SetModal(this, open);
        if (!open)
            return;
        EnsureService();
        root.transform.SetAsLastSibling();
        if (!state.Active)
            ResetCondition();
        Render();
        pollAt = 0f; // 바로 한 번 물어본다
    }

    void Hide()
    {
        Show(false);
        if (state.Active)
            Toast("매칭은 계속 진행돼요. 산책메이트 버튼으로 다시 열 수 있어요");
    }

    void OnDisable() => UIInputBlocker.SetModal(this, false);

    // ---------- 상태 ----------

    void Update()
    {
        if (!state.Active || busy || Time.unscaledTime < pollAt)
            return;
        bool fast = state.status == MatchState.Waiting || state.status == MatchState.Accepted || state.status == MatchState.Matched;
        pollAt = Time.unscaledTime + (fast ? 2f : 5f);
        Call(service.Status(Apply));
    }

    void Call(System.Collections.IEnumerator work)
    {
        busy = true;
        StartCoroutine(Run(work));
    }

    System.Collections.IEnumerator Run(System.Collections.IEnumerator work)
    {
        yield return work;
        busy = false;
    }

    void Apply(MatchState s, string error)
    {
        if (error != null)
        {
            Toast(error);
            return;
        }
        if (s == null)
            return;
        string before = state.status;
        bool wasConfirmed = state.HasCourse && state.course.confirmed;
        state = s;
        // 상대를 찾은 순간 · 코스가 정해진 순간은 별가루로 축하한다 (화면을 닫아 둔 사이여도 다시 열 때 보이게 Render 뒤에)
        bool found = before != s.status && s.status == MatchState.Proposed && before != MatchState.Accepted;
        bool agreed = !wasConfirmed && s.HasCourse && s.course.confirmed && s.status == MatchState.Matched;
        if (before != s.status)
        {
            // 산책 기록 (B-V4): 시작하면 걸은 거리를 재기 시작하고, 끝나면 완료 화면으로
            // 걷는 동안은 정한 코스를 혼자 걷기처럼 지도 위 선으로 보여 준다
            RoutePlanner.FollowMate(s.status == MatchState.Walking && s.HasCourse ? s.course : null);
            if (s.status == MatchState.Walking)
                WalkSession.BeginMate(s.matchId, s.partner?.nickname, s.walkId);
            else if (s.status == MatchState.Ended && WalkSession.Active && WalkSession.Kind == WalkSession.Mate)
            {
                WalkCompleteScreen.Show(WalkSession.Finish(null));
                Again();
                Show(false);
                return;
            }
            else if (!s.Active && WalkSession.Active && WalkSession.Kind == WalkSession.Mate)
                WalkSession.Abandon();
            // 화면을 닫아 둔 사이 상대가 나타나거나 매칭이 끝나면 알려 주고 다시 연다
            if (!root.activeSelf && (s.status == MatchState.Proposed || s.status == MatchState.Matched
                || s.status == MatchState.Cancelled || s.status == MatchState.Expired))
            {
                Toast(s.status == MatchState.Proposed ? "산책메이트를 찾았어요!" : s.status == MatchState.Matched ? "상대도 수락했어요!" : s.message);
                Show(true);
            }
            if (s.status == MatchState.Matched)
                ShowPage(meetPage);
        }
        Render();
        if (found || agreed)
            Celebrate(found ? proposeCard : courseCard);
    }

    /// <summary>별가루를 뿌리고 카드를 살짝 튀게 한다</summary>
    void Celebrate(RectTransform card)
    {
        Stardust.Burst((RectTransform)root.transform);
        PopIn.Play(card);
    }

    /// <summary>지금 상태에 맞는 쪽을 보여 준다. matched 는 만남 → 인상착의 → 스몰토크를 사용자가 넘긴다.</summary>
    void Render()
    {
        if (serviceLabel != null)
            serviceLabel.text = service.Description == "가상 상대" ? "시연용 가상 상대와 연결돼요" : "";
        switch (state.status)
        {
            case MatchState.Waiting:
                ShowPage(waitPage);
                if (waitPartner.texture == null)
                {
                    // 기다리는 동안 내 파트너 경희펫이 통통 뛴다
                    var collect = FindAnyObjectByType<CollectController>();
                    GameProgress.Caught partner = collect != null && collect.Progress != null ? collect.Progress.Partner : null;
                    try { waitPartner.texture = partner != null ? MonsterThumbnails.Get(partner.speciesId) : null; }
                    catch (System.Exception e) { Debug.LogWarning("[Match] 파트너 그림 실패: " + e.Message); }
                    waitPartner.enabled = waitPartner.texture != null;
                }
                waitTime.text = $"남은 시간 {state.waitSecondsLeft / 60}:{state.waitSecondsLeft % 60:00}";
                waitSearching.text = state.nowSearching > 0
                    ? $"지금 {state.nowSearching}명이 함께 산책메이트를 찾고 있어요"
                    : "근처에서 함께 걸을 사람을 찾고 있어요";
                break;
            case MatchState.Proposed:
                ShowPage(proposePage);
                proposeName.text = state.partner?.nickname ?? "산책메이트";
                proposeTastes.text = CommonTastes();
                string why = state.partner?.whyMatched;
                proposeWhyBox.SetActive(!string.IsNullOrEmpty(why));
                proposeWhy.text = why ?? "";
                proposeTime.text = $"{state.minutes}분 산책  ·  {state.decideSecondsLeft}초 안에 골라 주세요";
                break;
            case MatchState.Accepted:
                ShowPage(acceptWaitPage);
                acceptWaitPartner.texture = waitPartner.texture;
                acceptWaitPartner.enabled = acceptWaitPartner.texture != null;
                break;
            case MatchState.Matched:
                if (!meetPage.activeSelf && !coursePage.activeSelf && !lookPage.activeSelf && !talkPage.activeSelf)
                    ShowPage(meetPage);
                FillMeet();
                FillCourse();
                FillLook();
                if (talkPage.activeSelf && shownTopic == null)
                    ShowTopic();
                break;
            case MatchState.Walking:
                ShowPage(walkPage);
                walkTitle.text = $"{state.partner?.nickname ?? "산책메이트"}님과 걷는 중";
                walkCourse.text = state.HasCourse ? "오늘 코스\n" + string.Join("  →  ", StopNames(state.course)) : "";
                break;
            case MatchState.Ended:
                ShowPage(endPage);
                endTitle.text = "산책을 마쳤어요";
                endMessage.text = $"{state.partner?.nickname ?? "산책메이트"}님과 함께 걸었어요";
                break;
            case MatchState.Cancelled:
            case MatchState.Expired:
                ShowPage(endPage);
                endTitle.text = state.status == MatchState.Expired ? "매칭되지 않았어요" : "매칭이 끝났어요";
                endMessage.text = string.IsNullOrEmpty(state.message) ? "다시 찾아볼까요?" : state.message;
                break;
            default:
                ShowPage(conditionPage);
                RefreshChips();
                break;
        }
        reportButton.gameObject.SetActive(state.partner != null && state.Active && state.status != MatchState.Waiting);
    }

    void ShowPage(GameObject page)
    {
        foreach (GameObject p in new[] { conditionPage, waitPage, proposePage, acceptWaitPage, meetPage, coursePage, lookPage, talkPage, walkPage, endPage })
            p.SetActive(p == page);
    }

    string CommonTastes()
    {
        var names = new List<string>();
        if (state.partner?.commonTastes != null)
            foreach (string t in state.partner.commonTastes)
                names.Add(TasteNames.TryGetValue(t, out string n) ? n : t);
        string talk = state.partner?.talkStyle switch { "chatty" => "수다 떨며", "quiet" => "조용히 각자", "some" => "가끔 대화", _ => null };
        // 값만 굵게: 이름표와 값이 같은 굵기라 한 덩어리로 읽혔음 (REVIEW_v42)
        string tastes = names.Count > 0 ? $"같이 좋아하는 것  <b>{string.Join(" · ", names)}</b>" : "";
        return talk != null ? $"{tastes}\n걸을 때 분위기  <b>{talk}</b>" : tastes;
    }

    // ---------- 조건 ----------

    void ResetCondition()
    {
        WalkPreferences p = WalkPreferences.Load();
        minutes = p.walkMinutes >= 60 ? 60 : 30;
        sameGender = p.matchGender == "same";
    }

    void RefreshChips()
    {
        SetChip(minuteChips[0], minutes == 30);
        SetChip(minuteChips[1], minutes == 60);
        SetChip(genderChips[0], sameGender);
        SetChip(genderChips[1], !sameGender);
    }

    /// <summary>v4 칩 (UIKit.Chip) 켜짐 / 꺼짐: 초록 + 흰 굵은 글자 / 흰 바탕 + 회색 테 + 진한 글자</summary>
    static void SetChip(Image chip, bool on)
    {
        chip.color = on ? Accent : KucaTheme.Card;
        Text t = chip.GetComponentInChildren<Text>();
        t.color = on ? Color.white : Ink;
        t.font = on && UIKit.BoldFont != null ? UIKit.BoldFont : UIKit.Font;
        Outline o = chip.GetComponent<Outline>();
        if (o != null) o.enabled = !on;
    }

    void Join()
    {
        if (busy) return;
        if (AuthService.HasServer && !AuthService.HasToken)
        {
            Toast("로그인하거나 체험 모드로 시작하면 쓸 수 있어요");
            return;
        }
        var req = new MatchQueueRequest
        {
            minutes = minutes,
            sameGender = sameGender,
            buildingId = NearestSpot()?.buildingId,
            tastes = new List<string>(WalkPreferences.Load().tastes),
        };
        shownTopic = null;
        Call(service.Join(req, Apply));
    }

    // ---------- 제안 · 만남 ----------

    void Respond(bool accept) { if (!busy) Call(service.Respond(state.matchId, accept, Apply)); }

    void FillMeet()
    {
        MeetSpot spot = state.spot;
        meetName.text = spot?.name ?? "만남 스팟";
        Texture tex = null;
        if (spot != null && !string.IsNullOrEmpty(spot.species))
        {
            try { tex = MonsterThumbnails.Get(spot.species); }
            catch (System.Exception e) { Debug.LogWarning("[Match] 경희펫 그림 실패: " + e.Message); }
        }
        meetCreature.texture = tex;
        meetCreature.enabled = tex != null;
        float d = DistanceTo(spot);
        string where = d >= 0f ? $"지금 위치에서 {RouteMath.Distance(d)}  ·  " : "";
        string creature = spot != null && !string.IsNullOrEmpty(spot.species) ? $"{CreatureLibrary.NameOf(spot.species)} 앞에서 만나요" : "스팟 앞에서 만나요";
        meetInfo.text = $"{where}{creature}";
        meetNoShow.text = $"{state.noShowSecondsLeft / 60}분 {state.noShowSecondsLeft % 60:00}초 안에 만나지 않으면 자동으로 취소돼요";
    }

    void FillLook()
    {
        lookMine.text = string.IsNullOrEmpty(state.myLook) ? "아직 쓰지 않았어요" : $"나: {state.myLook}";
        lookPartner.text = string.IsNullOrEmpty(state.partnerLook) ? "상대가 아직 쓰지 않았어요" : state.partnerLook;
    }

    void SendLook()
    {
        string look = lookInput.text.Trim();
        if (look.Length is < 2 or > 40) { Toast("2~40자로 써 주세요 (예: 검은 후드, 흰 운동화)"); return; }
        if (!busy) Call(service.SetLook(state.matchId, look, Apply));
    }

    /// <summary>받아 둔 주제 i 번째 (분류가 있는 카드 우선, 없으면 글만)</summary>
    TopicCard TopicAt(int i)
    {
        if (state.cards != null && i < state.cards.Count) return state.cards[i];
        if (state.smalltalk != null && i < state.smalltalk.Count) return TopicCard.Plain(state.smalltalk[i]);
        return null;
    }

    int TopicCount => Mathf.Max(state.cards?.Count ?? 0, state.smalltalk?.Count ?? 0);

    void ShowTopic()
    {
        TopicCard card = TopicAt(topicIndex);
        if (card != null)
            shownTopic = card;
        SetTopic(shownTopic);
    }

    /// <summary>주제 카드: 위에 분류 태그 (#학식), 아래 큰 질문. 바뀔 때마다 살짝 튄다</summary>
    void SetTopic(TopicCard card)
    {
        talkTopic.text = card?.text ?? "무엇이든 편하게 이야기해요";
        talkTopicTag.text = "#" + (string.IsNullOrEmpty(card?.topic) ? "대화" : card.topic);
        talkTagSize.preferredWidth = talkTagSize.minWidth = talkTopicTag.preferredWidth + 2 * KucaTheme.Space4;
        PopIn.Play(talkCard);
    }

    void NextTopic()
    {
        topicIndex++;
        if (topicIndex < TopicCount)
        {
            ShowTopic();
            return;
        }
        if (busy) return;
        busy = true;
        StartCoroutine(Run(service.NextTopic(state.matchId, (card, error) =>
        {
            if (error != null) { Toast(error); return; }
            shownTopic = card;
            SetTopic(card);
        })));
    }

    // ---------- 함께 걸을 코스 ----------

    static List<string> StopNames(MatchCourse course)
    {
        var names = new List<string>();
        if (course?.stops != null)
            foreach (CourseStop s in course.stops) names.Add(s.name);
        return names;
    }

    /// <summary>
    /// 코스 카드: 제목 · 몇 곳 · 몇 분 · 거리, 들를 곳 목록 (번호 · 이름 · 이유), 두 사람의 동의 상태.
    /// 코스가 아직 없으면 (서버가 짜는 몇 초 · 예전 서버) 준비 중 문구를 보이고 다음 단계로 넘어갈 수 있게 둔다.
    /// </summary>
    void FillCourse()
    {
        bool has = state.HasCourse;
        courseLoading.gameObject.SetActive(!has);
        courseTitle.gameObject.SetActive(has);
        courseSummary.gameObject.SetActive(has);
        courseStops.gameObject.SetActive(has);
        if (!has)
        {
            courseStatus.text = "";
            UIKit.SetLabel(courseAgree, "다음: 인상착의 알려 주기");
            courseNext.gameObject.SetActive(false);
            return;
        }
        MatchCourse k = state.course;
        if (k.recommendationId != shownCourseId)
        {
            shownCourseId = k.recommendationId;
            BuildStops(k);
            PopIn.Play(courseCard);
        }
        courseTitle.text = k.title + CourseDifficulty.Tag(k.difficulty, KucaTheme.Label);
        courseSummary.text = CourseSummary(k);
        string partnerName = state.partner?.nickname ?? "상대";
        if (k.confirmed)
        {
            courseStatus.text = "둘 다 좋다고 했어요. 이 코스로 걸어요!";
            courseStatus.color = KucaTheme.AccentText;
        }
        else
        {
            courseStatus.text = $"나  {(k.myAgree ? "✓ 좋아요" : "고르는 중")}      {partnerName}  {(k.partnerAgree ? "✓ 좋아요" : "고르는 중")}";
            courseStatus.color = InkQuiet;
        }
        // 내가 좋다고 했으면 상대를 기다리지 않고 다음 단계로 갈 수 있다 (만나서 출발하면 지금 코스로 정해진다)
        UIKit.SetLabel(courseAgree, k.myAgree || k.confirmed ? "다음: 인상착의 알려 주기" : "이 코스 좋아요");
        courseNext.gameObject.SetActive(!k.confirmed);
        courseNext.interactable = k.changesLeft > 0;
        UIKit.SetLabel(courseNext, k.changesLeft > 0 ? $"다른 코스 보기 ({k.changesLeft}번 남음)" : "다른 코스는 다 봤어요");
    }

    /// <summary>"3곳 · 21분 · 1.5km · 약 61~86kcal" (칼로리는 서버 값이 있을 때만, 대략 범위)</summary>
    public static string CourseSummary(MatchCourse k)
    {
        string s = $"{k.stops.Count}곳  ·  {k.totalMinutes}분  ·  {RouteMath.Distance(k.totalDistanceM)}";
        return HealthEstimate.Has(k.health) ? s + $"  ·  약 {k.health.kcalMin}~{k.health.kcalMax}kcal" : s;
    }

    /// <summary>들를 곳 줄: 초록 번호 원 · 이름(굵게) · 한 줄 이유 · 걷는 분</summary>
    void BuildStops(MatchCourse course)
    {
        for (int i = courseStops.childCount - 1; i >= 0; i--)
        {
            GameObject old = courseStops.GetChild(i).gameObject;
            old.SetActive(false);   // Destroy 는 프레임 끝에 지우므로 그동안 레이아웃에서 먼저 뺀다
            Destroy(old);
        }
        for (int i = 0; i < course.stops.Count; i++)
        {
            CourseStop stop = course.stops[i];
            RectTransform row = UIKit.Rect(courseStops, "Stop" + i);
            HorizontalLayoutGroup h = UIKit.Horizontal(row, KucaTheme.Space3);
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            h.childAlignment = TextAnchor.UpperLeft;
            Image badge = UIKit.Image(row, "Number", Accent);
            badge.sprite = HudIcons.Circle;
            UIKit.Size(badge, 72f, 72f);
            UIKit.Stretch(UIKit.Bold(UIKit.Text(badge.transform, (i + 1).ToString(), KucaTheme.Label, TextAnchor.MiddleCenter, Color.white)).rectTransform);
            RectTransform col = UIKit.Rect(row, "Text");
            UIKit.Vertical(col, KucaTheme.Space1);
            UIKit.Size(col, -1f, -1f, 1f);
            UIKit.Bold(UIKit.Text(col, $"{stop.name}  ·  {stop.walkMinutes}분", KucaTheme.Body, TextAnchor.UpperLeft, Ink));
            if (!string.IsNullOrEmpty(stop.reason))
                UIKit.Text(col, stop.reason, KucaTheme.Label, TextAnchor.UpperLeft, InkQuiet);
        }
    }

    void AgreeCourse()
    {
        // 코스가 없거나 이미 좋다고 했으면 다음 단계로
        if (!state.HasCourse || state.course.myAgree || state.course.confirmed)
        {
            ShowPage(lookPage);
            Render();
            return;
        }
        if (!busy) Call(service.AgreeCourse(state.matchId, Apply));
    }

    void NextCourse()
    {
        if (!busy) Call(service.NextCourse(state.matchId, Apply));
    }

    void StartWalk()
    {
        if (busy) return;
        Call(service.StartWalk(state.matchId, (s, e) =>
        {
            Apply(s, e);
            // 서버 매칭: 나만 도착했으면 상대를 기다린다고 알려 준다
            if (s != null && s.status == MatchState.Matched && !string.IsNullOrEmpty(s.message))
                Toast(s.message);
        }));
    }
    void EndWalk()
    {
        if (busy) return;
        ConfirmDialog.Show("함께 산책을 끝낼까요?", $"지금까지 {RouteMath.Distance(WalkSession.WalkedMeters)} 같이 걸었어요.", "끝내기",
            () => { if (!busy) Call(service.End(state.matchId, Apply)); }, "계속 걷기");
    }

    void CancelMatch()
    {
        if (busy) return;
        if (state.status == MatchState.Waiting)
            Call(service.Leave((s, e) => { Apply(s, e); ResetCondition(); Render(); }));
        else
            Call(service.Cancel(state.matchId, "user", Apply));
    }

    void Again()
    {
        state = new MatchState();
        topicIndex = 0;
        shownTopic = null;
        shownCourseId = null;
        ResetCondition();
        Render();
    }

    // ---------- 신고 · 차단 ----------

    void OpenReport()
    {
        reportReason = "rude";
        reportBlock = true;
        RefreshReport();
        reportPanel.SetActive(true);
        reportPanel.transform.SetAsLastSibling();
    }

    void RefreshReport()
    {
        for (int i = 0; i < Reasons.Length; i++)
            SetChip(reasonChips[i], Reasons[i].id == reportReason);
        SetChip(blockChip, reportBlock);
    }

    void SendReport()
    {
        if (busy) return;
        reportPanel.SetActive(false);
        Call(service.Report(state.matchId, reportReason, "", reportBlock, Apply));
    }

    // ---------- 위치 ----------

    /// <summary>지금 위치에서 가장 가까운 경희스팟 (대기열에 보내는 건물 ID)</summary>
    MeetSpot NearestSpot() => Nearest(false);

    /// <summary>가장 가까운 만남 스팟과 그 앞의 경희펫 (A-V3 MeetSpotCatalog, 가상 상대가 쓴다)</summary>
    MeetSpot NearestMeetSpot() => Nearest(true);

    MeetSpot Nearest(bool meetSpotsOnly)
    {
        var spots = FindAnyObjectByType<KyungHeeSpots>();
        Vector3 me = PlayerPosition;
        KyungHeeSpot best = null;
        float bestD = float.MaxValue;
        if (spots != null)
            foreach (KyungHeeSpot s in spots.Spots)
            {
                if (s == null || s.Building == null) continue;
                if (meetSpotsOnly && !MeetSpotCatalog.IsMeetSpot(s.Building.buildingId)) continue;
                float d = (s.transform.position - me).sqrMagnitude;
                if (d < bestD) { bestD = d; best = s; }
            }
        if (best == null)
            return meetSpotsOnly ? Nearest(false) : null;
        return new MeetSpot
        {
            buildingId = best.Building.buildingId,
            name = best.Building.DisplayName,
            species = MeetSpotCatalog.SpeciesAt(best.Building.buildingId),
        };
    }

    float DistanceTo(MeetSpot spot)
    {
        if (spot == null || string.IsNullOrEmpty(spot.buildingId))
            return -1f;
        var spots = FindAnyObjectByType<KyungHeeSpots>();
        if (spots == null)
            return -1f;
        foreach (KyungHeeSpot s in spots.Spots)
            if (s != null && s.Building != null && s.Building.buildingId == spot.buildingId)
            {
                Vector3 a = s.transform.position, b = PlayerPosition;
                return new Vector2(a.x - b.x, a.z - b.z).magnitude;
            }
        return -1f;
    }

    static Vector3 PlayerPosition
    {
        get
        {
            var collect = FindAnyObjectByType<CollectController>();
            return collect != null && collect.campusMap != null && collect.campusMap.player != null
                ? collect.campusMap.player.position : Vector3.zero;
        }
    }

    static void Toast(string message)
    {
        var hud = FindAnyObjectByType<GameHUD>();
        if (hud != null && !string.IsNullOrEmpty(message)) hud.Toast(message);
    }

    // ---------- 화면 만들기 ----------

    void Build()
    {
        Canvas canvas = UIKit.CreateCanvas(transform, "MatchCanvas", 50);
        UIKit.Page page = UIKit.CreatePage(canvas.transform, "Match", null, Hide, scroll: false);
        root = page.root;

        // 위 오른쪽: 신고 · 차단 (상대가 있을 때만 보임)
        reportButton = UIKit.Button(page.header, "신고 · 차단", OpenReport, new Color(1f, 1f, 1f, 0f), KucaTheme.Label);
        reportButton.GetComponentInChildren<Text>().color = Danger;
        RectTransform rr = (RectTransform)reportButton.transform;
        rr.anchorMin = rr.anchorMax = rr.pivot = Vector2.one;
        rr.anchoredPosition = new Vector2(-KucaTheme.Gutter + 20f, -12f);
        rr.sizeDelta = new Vector2(280f, KucaTheme.TouchMin);

        // 단계 화면들은 Content 영역 전체를 하나씩 차지한다 (Step 이 레이아웃에서 빼고 꽉 채움)
        RectTransform body = page.content;

        BuildCondition(body);
        BuildWait(body);
        BuildPropose(body);
        BuildAcceptWait(body);
        BuildMeet(body);
        BuildCourse(body);
        BuildLook(body);
        BuildTalk(body);
        BuildWalk(body);
        BuildEnd(body);
        BuildReport(root.transform);
    }

    /// <summary>단계 하나: 위는 스크롤 (큰 질문 제목 + 내용), 아래는 고정 버튼 칸 (buttons 개). content 에 카드 · 칩 줄을 쌓는다</summary>
    GameObject Step(RectTransform body, string name, string title, int buttons, out RectTransform content, out RectTransform bottom, out Text heading)
    {
        RectTransform p = UIKit.Rect(body, name);
        p.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        UIKit.Stretch(p);
        float bottomH = buttons * KucaTheme.ButtonHeight + (buttons - 1) * KucaTheme.Space2 + 2 * KucaTheme.Space4;
        bottom = UIKit.Rect(p, "Bottom");
        bottom.anchorMin = Vector2.zero;
        bottom.anchorMax = new Vector2(1f, 0f);
        bottom.pivot = new Vector2(0.5f, 0f);
        bottom.offsetMin = Vector2.zero;
        bottom.offsetMax = new Vector2(0f, bottomH);
        int g = (int)KucaTheme.Gutter, s4 = (int)KucaTheme.Space4;
        UIKit.Vertical(bottom, KucaTheme.Space2, new RectOffset(g, g, s4, s4));
        RectTransform area = UIKit.Rect(p, "Area");
        UIKit.StretchBetween(area, bottomH, 0f);
        content = UIKit.ScrollList(area, KucaTheme.Space4, new RectOffset(g, g, 0, (int)KucaTheme.Space8));
        heading = UIKit.Bold(UIKit.Text(content, title, KucaTheme.Title, TextAnchor.UpperLeft, Ink));
        p.gameObject.SetActive(false);
        return p.gameObject;
    }

    GameObject Step(RectTransform body, string name, string title, int buttons, out RectTransform content, out RectTransform bottom) =>
        Step(body, name, title, buttons, out content, out bottom, out _);

    void BuildCondition(RectTransform body)
    {
        conditionPage = Step(body, "Condition", "같이 걸을 사람을\n찾아 드릴게요", 1, out RectTransform c, out RectTransform b);
        // 누르면 무슨 일이 생기는지 · AI 가 맡는 일 (REVIEW_v22_day P0 · P1). 짝은 규칙(거리 · 취향)으로 찾고 AI 는 대화 주제만 — 과장하지 않기
        Note(c, "공강에 근처 경희대 학생과 함께 걸어요. 둘 다 수락하면 만나고, 만나면 AI 가 첫 대화 주제를 골라 줘요.");   // 4줄은 길었음 (REVIEW_v26)
        SafetyCard(c);
        Question(c, "얼마나 걸을까요?");
        RectTransform minutesRow = ChipRow(c);
        minuteChips = new[]
        {
            Chip(minutesRow, "30분", () => { minutes = 30; RefreshChips(); }),
            Chip(minutesRow, "1시간", () => { minutes = 60; RefreshChips(); }),
        };
        Question(c, "같이 걸을 상대는요?");
        RectTransform genderRow = ChipRow(c);
        genderChips = new[]
        {
            Chip(genderRow, "동성만", () => { sameGender = true; RefreshChips(); }),
            Chip(genderRow, "상관없어요", () => { sameGender = false; RefreshChips(); }),
        };
        serviceLabel = Note(c, "");
        UIKit.PrimaryButton(b, "산책메이트 찾기", Join);
    }

    /// <summary>안전 카드 (당근 · 토스: 낯선 사람과 만나기 전 약속을 먼저 보여 준다)</summary>
    static void SafetyCard(RectTransform parent)
    {
        RectTransform card = UIKit.CardBox(parent, "Safety", KucaTheme.Space2);
        UIKit.SectionTitle(card, "안심하고 걸어요");
        foreach (string line in new[]
        {
            "누구나 오가는 공개 스팟에서만 만나요",
            "위치는 건물 단위로만 써요",
            "성별은 상대에게 보이지 않아요",
            "10분 안에 못 만나면 자동으로 끝나요",
            "매칭이 끝나면 기록을 지워요",
            "불편하면 언제든 신고 · 차단할 수 있어요",
        })
        {
            Text t = UIKit.Text(card, "✓  " + line, KucaTheme.Body, TextAnchor.MiddleLeft, InkQuiet);
            UIKit.Size(t, 64f);
        }
    }

    void BuildWait(RectTransform body)
    {
        waitPage = Step(body, "Wait", "산책메이트를\n찾는 중이에요", 1, out RectTransform c, out RectTransform b);
        // 원 파동이 계속 퍼지는 가운데 내 파트너 경희펫이 통통 뛴다 (실시간으로 찾는 중)
        RectTransform ripple = UIKit.Rect(c, "Ripple");
        UIKit.Size(ripple, 560f);
        MatchRipple.Add(ripple, 560f, Accent);
        waitPartner = RawIn(ripple, 300f);
        waitPartner.gameObject.AddComponent<Bob>();
        waitTime = UIKit.Bold(UIKit.Text(c, "", KucaTheme.Heading, TextAnchor.MiddleCenter, Accent));
        UIKit.Size(waitTime, 80f);
        waitSearching = UIKit.Bold(UIKit.Text(c, "", KucaTheme.Body, TextAnchor.MiddleCenter, Ink));
        UIKit.Size(waitSearching, 72f);
        Note(c, "화면을 닫아도 계속 찾아요. 찾으면 알려 드릴게요.").alignment = TextAnchor.UpperCenter;
        UIKit.SecondaryButton(b, "그만 찾기", CancelMatch);
    }

    void BuildPropose(RectTransform body)
    {
        proposePage = Step(body, "Propose", "이 사람과\n같이 걸을까요?", 2, out RectTransform c, out RectTransform b);
        RectTransform card = UIKit.CardBox(c, "Partner", KucaTheme.Space2);
        RectTransform holder = UIKit.Rect(card, "AvatarHolder");
        UIKit.Size(holder, 220f);
        Image avatar = UIKit.Image(holder, "Avatar", KucaTheme.PrimarySoft);
        avatar.sprite = HudIcons.Circle;
        Place(avatar.rectTransform, new Vector2(0f, 0.5f), Vector2.zero);
        avatar.rectTransform.pivot = new Vector2(0f, 0.5f);
        avatar.rectTransform.sizeDelta = new Vector2(200f, 200f);
        Image person = UIKit.Image(avatar.transform, "Icon", Accent);
        person.sprite = HudIcons.Person;
        person.rectTransform.sizeDelta = new Vector2(110f, 110f);
        proposeName = UIKit.Bold(UIKit.Text(card, "", KucaTheme.Heading, TextAnchor.MiddleLeft, Ink));
        UIKit.Size(proposeName, 72f);
        // AI 매칭 이유 한 줄: 옅은 강조 바탕 + 별
        Image why = UIKit.Image(card, "Why", KucaTheme.AccentSoft);
        why.sprite = HudIcons.Rounded;
        why.type = Image.Type.Sliced;
        why.pixelsPerUnitMultiplier = 0.4f;
        HorizontalLayoutGroup wh = UIKit.Horizontal(why, KucaTheme.Space2);
        wh.padding = new RectOffset((int)KucaTheme.Space4, (int)KucaTheme.Space4, (int)KucaTheme.Space3, (int)KucaTheme.Space3);
        wh.childForceExpandWidth = wh.childForceExpandHeight = false;
        wh.childAlignment = TextAnchor.MiddleLeft;
        Image star = UIKit.Image(why.transform, "Star", KucaTheme.Gold);
        star.sprite = HudIcons.Star;
        UIKit.Size(star, 52f, 52f);
        proposeWhy = UIKit.Bold(UIKit.Text(why.transform, "", KucaTheme.Label, TextAnchor.MiddleLeft, KucaTheme.AccentText));
        UIKit.Size(proposeWhy, -1f, -1f, 1f);
        proposeWhyBox = why.gameObject;
        proposeCard = card;
        proposeTastes = UIKit.Text(card, "", KucaTheme.Body, TextAnchor.UpperLeft, InkQuiet);
        proposeTastes.supportRichText = true;   // 값만 <b> (CommonTastes)
        proposeTime = UIKit.Bold(UIKit.Text(card, "", KucaTheme.Label, TextAnchor.MiddleLeft, Accent));
        UIKit.Size(proposeTime, 56f);
        UIKit.PrimaryButton(b, "같이 걸을래요", () => Respond(true));
        UIKit.SecondaryButton(b, "다음에 할게요", () => Respond(false));
    }

    void BuildAcceptWait(RectTransform body)
    {
        acceptWaitPage = Step(body, "AcceptWait", "상대의 수락을\n기다리는 중이에요", 1, out RectTransform c, out RectTransform b);
        // 찾는 중 화면과 같은 물결 + 파트너 (제목 한 줄만 있어 텅 비어 보였음, REVIEW_v42)
        RectTransform ripple = UIKit.Rect(c, "Ripple");
        UIKit.Size(ripple, 560f);
        MatchRipple.Add(ripple, 560f, Accent);
        acceptWaitPartner = RawIn(ripple, 300f);
        acceptWaitPartner.gameObject.AddComponent<Bob>();
        Note(c, "상대도 수락하면 만날 곳을 알려 드려요.");
        UIKit.SecondaryButton(b, "매칭 취소", CancelMatch);
    }

    void BuildMeet(RectTransform body)
    {
        meetPage = Step(body, "Meet", "여기서 만나요", 2, out RectTransform c, out RectTransform b);
        RectTransform card = UIKit.CardBox(c, "Spot", KucaTheme.Space2);
        meetCreature = Picture(card, 300f);
        meetCreature.gameObject.AddComponent<Bob>();
        meetName = UIKit.Bold(UIKit.Text(card, "", KucaTheme.Heading, TextAnchor.MiddleLeft, Ink));
        UIKit.Size(meetName, 72f);
        meetInfo = UIKit.Text(card, "", KucaTheme.Body, TextAnchor.UpperLeft, InkQuiet);
        meetNoShow = UIKit.Text(c, "", KucaTheme.Label, TextAnchor.UpperLeft, Danger);
        UIKit.PrimaryButton(b, "다음: 같이 걸을 코스 정하기", () => { ShowPage(coursePage); Render(); });
        UIKit.SecondaryButton(b, "매칭 취소", CancelMatch);
    }

    /// <summary>함께 걸을 코스 (10/25 피드백): 만남 스팟에서 출발하는 코스를 둘이 함께 고른다</summary>
    void BuildCourse(RectTransform body)
    {
        coursePage = Step(body, "Course", "같이 걸을 코스를\n골라요", 2, out RectTransform c, out RectTransform b);
        Note(c, "만남 스팟에서 출발해요. 둘 다 좋다고 하면 코스가 정해져요.");
        courseCard = UIKit.CardBox(c, "CourseCard", KucaTheme.Space3);
        courseTitle = UIKit.Bold(UIKit.Text(courseCard, "", KucaTheme.Heading, TextAnchor.UpperLeft, Ink));
        courseSummary = UIKit.Bold(UIKit.Text(courseCard, "", KucaTheme.Label, TextAnchor.UpperLeft, KucaTheme.AccentText));
        courseStops = UIKit.Rect(courseCard, "Stops");
        UIKit.Vertical(courseStops, KucaTheme.Space3);
        courseLoading = UIKit.Text(courseCard, "두 사람에게 맞는 코스를 짜고 있어요…", KucaTheme.Body, TextAnchor.UpperLeft, InkQuiet);
        courseStatus = UIKit.Bold(UIKit.Text(c, "", KucaTheme.Label, TextAnchor.UpperLeft, InkQuiet));
        courseAgree = UIKit.PrimaryButton(b, "이 코스 좋아요", AgreeCourse);
        courseNext = UIKit.SecondaryButton(b, "다른 코스 보기", NextCourse);
    }

    void BuildLook(RectTransform body)
    {
        lookPage = Step(body, "Look", "서로 알아볼 수 있게", 2, out RectTransform c, out RectTransform b);
        Note(c, "오늘 옷차림을 짧게 알려 주세요. 매칭 상대에게만 보이고, 산책이 끝나면 지워요.");
        RectTransform row = UIKit.Rect(c, "LookRow");
        UIKit.Size(row, KucaTheme.TouchMin);
        var h = UIKit.Horizontal(row, KucaTheme.Space2);
        h.childForceExpandWidth = false;
        lookInput = UIKit.Input(row, "예: 검은 후드, 흰 운동화", KucaTheme.Body, false, 40);
        lookInput.GetComponent<Image>().color = KucaTheme.Card;
        UIKit.Size(lookInput, KucaTheme.TouchMin, -1f, 1f);
        Button send = UIKit.Button(row, "보내기", SendLook, Ink, KucaTheme.Body);
        UIKit.Size(send, KucaTheme.TouchMin, 220f);
        lookMine = Note(c, "");
        RectTransform card = UIKit.CardBox(c, "PartnerLook", KucaTheme.Space2);
        UIKit.Text(card, "상대의 인상착의", KucaTheme.Label, TextAnchor.MiddleLeft, InkQuiet);
        lookPartner = UIKit.Bold(UIKit.Text(card, "", KucaTheme.Heading, TextAnchor.UpperLeft, Ink));
        UIKit.PrimaryButton(b, "다음: 대화 주제 보기", () => { ShowPage(talkPage); ShowTopic(); });
        UIKit.SecondaryButton(b, "매칭 취소", CancelMatch);
    }

    void BuildTalk(RectTransform body)
    {
        talkPage = Step(body, "Talk", "어색하면 이 이야기로\n시작해 보세요", 2, out RectTransform c, out RectTransform b);
        RectTransform card = UIKit.CardBox(c, "Topic", KucaTheme.Space3);
        talkCard = card;
        // 분류 태그 (#학식 · #캠퍼스 장소 …): 옅은 강조 알약
        RectTransform tagRow = UIKit.Rect(card, "TagRow");
        UIKit.Size(tagRow, 72f);
        HorizontalLayoutGroup th = UIKit.Horizontal(tagRow, 0f);
        th.childForceExpandWidth = false;
        Image tag = UIKit.Image(tagRow, "Tag", KucaTheme.AccentSoft);
        tag.sprite = HudIcons.Pill;
        tag.type = Image.Type.Sliced;
        tag.pixelsPerUnitMultiplier = 1.4f;
        talkTagSize = UIKit.Size(tag, 72f, 200f);
        talkTopicTag = UIKit.Bold(UIKit.Text(tag.transform, "#대화", KucaTheme.Label, TextAnchor.MiddleCenter, KucaTheme.AccentText));
        UIKit.Stretch(talkTopicTag.rectTransform);
        talkTopic = UIKit.Bold(UIKit.Text(card, "", KucaTheme.Title, TextAnchor.MiddleLeft, Ink));
        UIKit.Size(talkTopic, 300f);
        UIKit.SecondaryButton(c, "다음 주제", NextTopic);
        UIKit.PrimaryButton(b, "만났어요! 산책 시작", StartWalk);
        UIKit.SecondaryButton(b, "매칭 취소", CancelMatch);
    }

    void BuildWalk(RectTransform body)
    {
        walkPage = Step(body, "Walk", "", 2, out RectTransform c, out RectTransform b, out walkTitle);
        RectTransform holder = UIKit.Rect(c, "IconHolder");
        UIKit.Size(holder, 240f);
        Image ic = UIKit.Image(holder, "Icon", Accent);
        ic.sprite = HudIcons.Footprints;
        Place(ic.rectTransform, new Vector2(0f, 0.5f), Vector2.zero);
        ic.rectTransform.pivot = new Vector2(0f, 0.5f);
        ic.rectTransform.sizeDelta = new Vector2(200f, 200f);
        walkCourse = UIKit.Bold(UIKit.Text(c, "", KucaTheme.Body, TextAnchor.UpperLeft, Ink));
        Note(c, "즐겁게 걸어요! 불편한 일이 생기면 오른쪽 위 신고 · 차단을 눌러 주세요.");
        UIKit.PrimaryButton(b, "산책 끝내기", EndWalk);
        UIKit.SecondaryButton(b, "지도로 돌아가기", Hide);
    }

    void BuildEnd(RectTransform body)
    {
        endPage = Step(body, "End", "", 2, out RectTransform c, out RectTransform b, out endTitle);
        endMessage = Note(c, "");
        UIKit.PrimaryButton(b, "다시 찾기", Again);
        UIKit.SecondaryButton(b, "닫기", () => { Again(); Show(false); });
    }

    /// <summary>신고: 어둡게 덮고 아래에서 올라온 흰 시트 (사유 칩 2×2 · 차단 칩 · 빨간 신고 버튼)</summary>
    void BuildReport(Transform parent)
    {
        Image dim = UIKit.Image(parent, "Report", KucaTheme.Scrim, raycast: true);
        UIKit.Stretch(dim.rectTransform);
        reportPanel = dim.gameObject;
        RectTransform sheet = UIKit.CardBox(dim.transform, "Sheet", KucaTheme.Space3);
        sheet.GetComponent<Image>().raycastTarget = true;
        sheet.anchorMin = Vector2.zero;
        sheet.anchorMax = new Vector2(1f, 0f);
        sheet.pivot = new Vector2(0.5f, 0f);
        sheet.offsetMin = sheet.offsetMax = Vector2.zero;
        sheet.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        UIKit.Bold(UIKit.Text(sheet, "신고하기", KucaTheme.Title, TextAnchor.MiddleLeft, Ink));
        Note(sheet, "무슨 일이 있었나요? 신고하면 이번 매칭은 끝나요.");
        reasonChips = new Image[Reasons.Length];
        RectTransform row = null;
        for (int i = 0; i < Reasons.Length; i++)
        {
            if (i % 2 == 0) row = ChipRow(sheet);
            string id = Reasons[i].id;
            reasonChips[i] = Chip(row, Reasons[i].label, () => { reportReason = id; RefreshReport(); });
        }
        blockChip = Chip(ChipRow(sheet), "이 사람과 다시 매칭하지 않기 (차단)", () => { reportBlock = !reportBlock; RefreshReport(); });
        UIKit.BigButton(sheet, "신고하기", SendReport, Danger, Color.white);
        UIKit.SecondaryButton(sheet, "닫기", () => reportPanel.SetActive(false));
        RectTransform safeGap = UIKit.Rect(sheet, "SafeGap");   // 아래 홈 표시줄과 겹치지 않게
        UIKit.Size(safeGap, Screen.safeArea.yMin / Mathf.Max(1, Screen.height) * 1920f);
        BackButton.Attach(reportPanel, () => reportPanel.SetActive(false));
        reportPanel.SetActive(false);
    }

    /// <summary>
    /// 처음 보는 학생과 만나는 기능이라, 찾기 전에 흐름과 안전장치를 먼저 보여 준다 (디자인 평가 10/25:
    /// 기획서의 안전 설계가 화면에서는 회색 두 줄뿐이었다)
    /// </summary>

    // ---------- 작은 도우미 ----------

    static void Question(Transform parent, string text) =>
        UIKit.Size(UIKit.Bold(UIKit.Text(parent, text, KucaTheme.Heading, TextAnchor.LowerLeft, Ink)), 96f);

    static Text Note(Transform parent, string text) =>
        UIKit.Text(parent, text, KucaTheme.Body, TextAnchor.UpperLeft, InkQuiet);

    static RectTransform ChipRow(Transform parent)
    {
        RectTransform row = UIKit.Rect(parent, "Chips");
        UIKit.Size(row, 104f);
        UIKit.Horizontal(row, KucaTheme.Space2);
        return row;
    }

    static Image Chip(RectTransform row, string label, UnityEngine.Events.UnityAction onClick)
    {
        Button b = UIKit.Chip(row, label, false, onClick);
        b.gameObject.AddComponent<PressScale>();
        return b.GetComponent<Image>();
    }

    /// <summary>holder 가운데에 정사각 그림 (원 파동 위에 얹는다)</summary>
    static RawImage RawIn(RectTransform holder, float size)
    {
        var raw = new GameObject("Picture", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        raw.transform.SetParent(holder, false);
        raw.raycastTarget = false;
        Place(raw.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        raw.rectTransform.sizeDelta = new Vector2(size, size);
        return raw;
    }

    /// <summary>경희펫 그림 칸 (가로로 늘어나지 않게 정사각 그림을 왼쪽에)</summary>
    static RawImage Picture(Transform parent, float size)
    {
        RectTransform holder = UIKit.Rect(parent, "PictureHolder");
        UIKit.Size(holder, size);
        var raw = new GameObject("Picture", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        raw.transform.SetParent(holder, false);
        raw.raycastTarget = false;
        Place(raw.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        raw.rectTransform.sizeDelta = new Vector2(size, size);
        return raw;
    }

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pos)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos;
    }

    /// <summary>그림이 위아래로 살짝 통통 뛴다 (기다리는 중)</summary>
    class Bob : MonoBehaviour
    {
        Vector2 start;
        RectTransform rt;
        void Awake() { rt = (RectTransform)transform; start = rt.anchoredPosition; }
        void Update() => rt.anchoredPosition = start + new Vector2(0f, Mathf.Abs(Mathf.Sin(Time.unscaledTime * 3f)) * 18f);
    }
}
