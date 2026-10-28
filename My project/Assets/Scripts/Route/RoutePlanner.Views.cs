using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// RoutePlanner 의 화면: 조건 시트 · 장소 고르기 · 기다리기 · 코스 고르기 · 걷는 중 판 · 장소 팝업 · 위에서 본 지도.
/// v4 (docs/디자인_v4.md): 카카오맵 · Apple 지도 장소 시트 — 지도 위 아래 시트, 큰 제목, 숫자 줄, 번호 목록, 아래 꽉 찬 버튼.
/// </summary>
public partial class RoutePlanner
{
    const float WalkPanelHeight = 360f;   // 걷는 중 판: 지도를 가리지 않게 낮게 (10/27 폰에서 글자 · 판이 너무 큼)
    static readonly string SoftHex = ColorUtility.ToHtmlStringRGB(KucaTheme.InkSoft);
    static readonly string PrimaryHex = ColorUtility.ToHtmlStringRGB(KucaTheme.Primary);

    GameObject sheet, picker, loading, chooser, walkPanel, placePopup, musicSheet;
    RectTransform musicList;
    Image[] timeChips, tasteChips;
    Image mealChip;
    InputField wishInput;
    /// <summary>말로 적은 산책 바람 (없으면 빈 값)</summary>
    string Wish => wishInput != null ? wishInput.text.Trim() : "";
    Text mealNote, startValue, destValue, pickerTitle, wishHint;
    RectTransform pickerList;
    RawImage loadingPartner;

    RawImage chooserMap;
    RectTransform chooserOverlay, chooserCards;
    Button chooserGo;

    Image walkDot, pathToggle;
    Text walkTitle, walkNext, walkReason, walkProgress, pathToggleText;

    Text popupName, popupReason;
    RectTransform popupNotices;
    Button popupBoardButton;
    RawImage miniMap;
    RectTransform miniOverlay;

    RenderTexture miniTexture, chooserTexture;

    // ---------- 조건 시트 ----------

    void ShowSheet()
    {
        EnsureService();
        if (active != null)
        {
            Toast("걷고 있는 코스가 있어요. 아래 판에서 끝내고 새로 고를 수 있어요");
            return;
        }
        if (FreeWalk.Active)
        {
            Toast("자유 산책 중이에요. 아래 판에서 끝낼 수 있어요");
            return;
        }
        if (afterMeal == false && RouteMath.MealOf(System.DateTime.Now) != null)
            afterMeal = true; // 점심 · 저녁 시간대엔 식후 걷기를 켜고 연다
        RefreshChips();
        sheet.SetActive(true);
        sheet.transform.SetAsLastSibling();
        UIInputBlocker.SetModal(this, true);
    }

    void HideSheet()
    {
        sheet.SetActive(false);
        UIInputBlocker.SetModal(this, loading.activeSelf || placePopup.activeSelf || chooser.activeSelf || picker.activeSelf);
    }

    void RefreshChips()
    {
        for (int i = 0; i < timeChips.Length; i++)
            SetChip(timeChips[i], TimeChoices[i].minutes == minutes);
        for (int i = 0; i < tasteChips.Length; i++)
            SetChip(tasteChips[i], tastes.Contains(TasteChoices[i].id));
        string meal = RouteMath.MealOf(System.DateTime.Now);
        SetChip(mealChip, afterMeal);
        UIKit.SetLabel(mealChip.GetComponent<Button>(), "식후 걷기");   // "지금 점심시간" 은 칩 아래 안내로 (칩 안에 넣으면 세 줄로 넘쳤다)
        string now = meal != null ? $"지금 {meal}시간이에요. " : "";
        mealNote.text = afterMeal ? $"{now}식후 걷기: 가까운 평지 위주로 골라요.  {RouteMath.HealthNote}" : meal != null ? $"{now}식후 걷기를 켜면 가까운 곳 위주로 골라요." : "";
        startValue.text = Escape(StartLabel);
        destValue.text = roundTrip ? "출발지로 돌아오기"
            : destSpot != null ? Escape(destSpot.Building.buildingName) : "AI 가 정해요";   // "추천대로" 는 뜻을 몰랐음 (REVIEW_v22_day)
    }

    /// <summary>v4 칩 (UIKit.Chip) 켜짐 / 꺼짐: 초록 + 흰 굵은 글자 / 흰 바탕 + 회색 테 + 진한 글자</summary>
    static void SetChip(Image chip, bool on, Color? onColor = null)
    {
        chip.color = on ? onColor ?? RouteBlue : KucaTheme.Card;
        Text t = chip.GetComponentInChildren<Text>();
        t.color = on ? Color.white : Ink;
        t.font = on && UIKit.BoldFont != null ? UIKit.BoldFont : UIKit.Font;
        Outline o = chip.GetComponent<Outline>();
        if (o != null) o.enabled = !on;
    }

    void BuildSheet()
    {
        sheet = Sheet("RouteSheet", "어디로 걸을까요?", HideSheet, 0.9f, 2, out RectTransform c, out RectTransform b, out _);

        // 메인은 말로 하는 요청: AI 가 문장을 읽고 코스를 짠다 (사용자 결정 10/27 — 칩은 작은 기본 조건으로)
        Question(c, "어떤 산책을 하고 싶어요?");
        UIKit.Text(c, "적은 문장을 AI 가 읽고 코스를 짜요. 키보드 마이크로 말해도 돼요", KucaTheme.Label, TextAnchor.UpperLeft, KucaTheme.PrimaryDeep);
        wishInput = UIKit.Input(c, "예: 밥 먹고 소화시킬 겸 30분, 오르막은 빼고", KucaTheme.Body, true, 200);
        UIKit.Size(wishInput, 170f);
        // 서버가 알아듣지 못한 문장이면 (422) 여기에 "다시 입력해 주세요" — 고쳐 쓰기 시작하면 지운다
        wishHint = UIKit.Text(c, "", KucaTheme.Label, TextAnchor.UpperLeft, KucaTheme.Crimson);
        wishInput.onValueChanged.AddListener(_ => wishHint.text = "");
        RectTransform places = UIKit.ListGroup(c, null);
        places.GetComponent<Image>().color = KucaTheme.Fill;   // 흰 시트 위라 회색 묶음
        startValue = PlaceRow(places, "출발", () => ShowPicker(true));
        destValue = PlaceRow(places, "도착", () => ShowPicker(false));

        // 기본 조건: 작은 칩 두 줄 (시간 + 식후 / 취향, 여러 개). 문장이 비면 이것만 쓴다
        UIKit.Text(c, "기본 조건  ·  문장이 없으면 이것만 써요", KucaTheme.Label, TextAnchor.LowerLeft, InkQuiet);
        RectTransform row = ChipRow(c, 80f);
        timeChips = new Image[TimeChoices.Length];
        for (int i = 0; i < TimeChoices.Length; i++)
        {
            int m = TimeChoices[i].minutes;
            timeChips[i] = Chip(row, TimeChoices[i].label, () => { minutes = m; RefreshChips(); }, true);
        }
        mealChip = Chip(row, "식후 걷기", () => { afterMeal = !afterMeal; RefreshChips(); }, true);
        row = ChipRow(c, 80f);
        tasteChips = new Image[TasteChoices.Length];
        for (int i = 0; i < TasteChoices.Length; i++)
        {
            string id = TasteChoices[i].id;
            tasteChips[i] = Chip(row, TasteChoices[i].label, () =>
            {
                if (!tastes.Remove(id)) tastes.Add(id);
                RefreshChips();
            }, true);
        }
        mealNote = UIKit.Text(c, "", KucaTheme.Label, TextAnchor.UpperLeft, InkQuiet);

        // 코스 없이도: 고른 시간 · 취향으로 걸으며 들을 노래 (코스 카드에는 코스마다 따로 나온다)
        RectTransform music = UIKit.ListGroup(c, null);
        music.GetComponent<Image>().color = KucaTheme.Fill;
        UIKit.ListRow(music, "산책하며 들을 노래 추천 받기", ShowMusic);

        UIKit.PrimaryButton(b, "AI 코스 3개 추천 받기", RequestRoutes);
        UIKit.SecondaryButton(b, "코스 없이 자유 산책 시작", () => { HideSheet(); FreeWalk.Begin(); });
    }

    /// <summary>"출발 · 내 위치 ›" 처럼 이름표와 값이 있는 목록 한 줄. 값 글자를 돌려준다</summary>
    static Text PlaceRow(RectTransform group, string label, UnityEngine.Events.UnityAction onClick)
    {
        Button r = UIKit.ListRow(group, label, onClick);
        r.GetComponentInChildren<Text>().color = InkQuiet;
        Text v = UIKit.Bold(UIKit.Text(r.transform, "", KucaTheme.Body, TextAnchor.MiddleRight, Ink));
        UIKit.Stretch(v.rectTransform);
        v.rectTransform.offsetMin = new Vector2(180f, 0f);
        v.rectTransform.offsetMax = new Vector2(-96f, 0f);
        return v;
    }

    // ---------- 출발지 · 목적지 고르기 ----------

    bool pickingStart;

    void ShowPicker(bool start)
    {
        pickingStart = start;
        pickerTitle.text = start ? "어디서 출발할까요?" : "어디로 갈까요?";
        for (int i = pickerList.childCount - 1; i >= 0; i--)
            Destroy(pickerList.GetChild(i).gameObject);
        Vector3 me = PlayerPosition;
        if (start)
            PickerRow("내 위치", "지금 있는 곳에서 출발", startSpot == null, () => startSpot = null);
        else
        {
            PickerRow("정하지 않음", "추천에 맡기기 (마지막 장소에서 끝나요)", destSpot == null && !roundTrip, () => { destSpot = null; roundTrip = false; });
            PickerRow("출발지로 돌아오기", $"{StartLabel}에서 출발해 한 바퀴 돌고 다시 돌아와요", roundTrip, () => { destSpot = null; roundTrip = true; });
        }
        List<KyungHeeSpot> list = SpotList;
        list.Sort((a, b) => Flat(me, SpotPosition(a)).CompareTo(Flat(me, SpotPosition(b))));   // 가까운 곳부터
        foreach (KyungHeeSpot s in list)
        {
            KyungHeeSpot spot = s;
            // 출발지와 같은 곳을 목적지로 고르면 한 바퀴 코스가 된다 (FixRoundTrip)
            bool same = start ? spot == destSpot : spot == startSpot;
            bool on = start ? startSpot == spot : destSpot == spot;
            string sub = $"지금 위치에서 {RouteMath.Distance(Flat(me, SpotPosition(spot)))}" + (same ? (start ? " · 도착지와 같은 곳 (한 바퀴)" : " · 출발지 (한 바퀴)") : "");
            PickerRow(spot.Building.buildingName, sub, on, () =>
            {
                if (pickingStart) startSpot = spot; else { destSpot = spot; roundTrip = false; }
                FixRoundTrip();
            });
        }
        picker.SetActive(true);
        picker.transform.SetAsLastSibling();
        UIInputBlocker.SetModal(this, true);
    }

    void PickerRow(string name, string sub, bool on, System.Action pick)
    {
        Image row = UIKit.Image(pickerList, "Row", on ? KucaTheme.PrimarySoft : KucaTheme.Fill, raycast: true);
        row.sprite = HudIcons.Rounded;
        row.type = Image.Type.Sliced;
        row.pixelsPerUnitMultiplier = 0.4f;
        UIKit.Vertical(row, KucaTheme.Space1, new RectOffset(48, 48, 32, 32));
        var b = row.gameObject.AddComponent<Button>();
        b.transition = Selectable.Transition.None;
        b.onClick.AddListener(() => { pick(); ClosePicker(); RefreshChips(); });
        row.gameObject.AddComponent<PressScale>();
        UIKit.Bold(UIKit.Text(row.transform, (on ? $"<color=#{PrimaryHex}>✓</color>  " : "") + Escape(name), KucaTheme.Body, TextAnchor.UpperLeft, Ink));
        UIKit.Text(row.transform, sub, KucaTheme.Label, TextAnchor.UpperLeft, InkQuiet);
    }

    void ClosePicker()
    {
        picker.SetActive(false);
        UIInputBlocker.SetModal(this, sheet.activeSelf);
    }

    void BuildPicker()
    {
        picker = Sheet("RoutePicker", "", ClosePicker, 0.9f, 0, out pickerList, out _, out pickerTitle);
    }

    // ---------- 기다리기 ----------

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
        UIInputBlocker.SetModal(this, on || sheet.activeSelf || placePopup.activeSelf || chooser.activeSelf);
    }

    void BuildLoading()
    {
        loading = UIKit.Image(canvasRect, "RouteLoading", Dim, raycast: true).gameObject;
        UIKit.Stretch((RectTransform)loading.transform);
        RectTransform card = UIKit.CardBox(loading.transform, "Card", KucaTheme.Space2);
        card.anchorMin = card.anchorMax = card.pivot = new Vector2(0.5f, 0.5f);
        card.sizeDelta = new Vector2(880f, 0f);
        card.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        RectTransform holder = UIKit.Rect(card, "PartnerHolder");
        UIKit.Size(holder, 340f);
        var go = new GameObject("Partner", typeof(RectTransform), typeof(RawImage));
        go.transform.SetParent(holder, false);
        loadingPartner = go.GetComponent<RawImage>();
        loadingPartner.raycastTarget = false;
        Place(loadingPartner.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero);
        loadingPartner.rectTransform.sizeDelta = new Vector2(300f, 300f);
        UIKit.Size(UIKit.Bold(UIKit.Text(card, "코스 3개를 짜고 있어요", KucaTheme.Heading, TextAnchor.MiddleCenter, Ink)), 80f);
        UIKit.Size(UIKit.Text(card, "파트너가 길을 찾고 있어요", KucaTheme.Label, TextAnchor.MiddleCenter, InkQuiet), 56f);
        UIKit.SecondaryButton(card, "취소", () => { request++; ShowLoading(false); ShowSheet(); });
        loading.SetActive(false);
    }

    // ---------- 코스 고르기 (위 지도 + 아래 시트) ----------

    void ShowChooser(int pick)
    {
        chosen = Mathf.Clamp(pick, 0, options.Count - 1);
        chooser.SetActive(true);
        chooser.transform.SetAsLastSibling();
        UIInputBlocker.SetModal(this, true);
        BuildOptionCards();
        Canvas.ForceUpdateCanvases();
        DrawChooserMap();
        UIKit.SetLabel(chooserGo, $"{options[chosen].variant.label}로 걷기");
        chooserGo.GetComponent<Image>().color = options[chosen].variant.color;
    }

    void HideChooser()
    {
        chooser.SetActive(false);
        UIInputBlocker.SetModal(this, sheet.activeSelf || placePopup.activeSelf);
    }

    void DrawChooserMap()
    {
        var all = new List<Vector3> { StartPosition };
        foreach (Option o in options)
            all.AddRange(o.path);
        Camera cam = RenderTop(chooserMap, ref chooserTexture, all);
        Clear(chooserOverlay);
        if (cam == null)
            return;
        Vector2 size = chooserOverlay.rect.size;
        // 고르지 않은 코스를 먼저 옅게, 고른 코스를 위에 진하게
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < options.Count; i++)
            {
                bool sel = i == chosen;
                if ((pass == 1) != sel) continue;
                Color c = options[i].variant.color;
                if (!sel) c.a = 0.8f;
                float w = sel ? 14f : 9f;
                TopMap.Polyline(chooserOverlay, cam, size, options[i].path, w + 7f, new Color(1f, 1f, 1f, sel ? 0.95f : 0.7f));   // 흰 테두리
                TopMap.Polyline(chooserOverlay, cam, size, options[i].path, w, c, sel);
            }
        Option o2 = options[chosen];
        for (int s = 0; s < o2.stops.Count; s++)
        {
            if (o2.returns && s == o2.stops.Count - 1)
                continue; // 돌아오는 곳은 출발 표시와 같은 자리
            bool last = s == o2.stops.Count - 1 && destSpot != null;
            TopMap.Marker(chooserOverlay, cam, size, o2.stops[s], o2.variant.color, last ? "도착" : (s + 1).ToString(), last ? 74f : 58f);
        }
        TopMap.Marker(chooserOverlay, cam, size, StartPosition, Ink, o2.returns ? "출발\n도착" : "출발", 74f);
    }

    /// <summary>시트 내용: 코스 3개 칩 → 고른 코스의 큰 이름 · 숫자 줄 · 들를 곳 번호 목록 (카카오맵 장소 시트)</summary>
    void BuildOptionCards()
    {
        Clear(chooserCards);
        RectTransform tabs = ChipRow(chooserCards);
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            Image chip = Chip(tabs, options[i].variant.label.Replace(" 코스", ""), () => ShowChooser(index));
            SetChip(chip, i == chosen, options[i].variant.color);
        }

        Option o = options[chosen];
        string source = o.result.source == "ai" ? "AI 추천" : "기본 추천";
        UIKit.Bold(UIKit.Text(chooserCards, o.variant.label + CourseDifficulty.Tag(o.result.difficulty, KucaTheme.Label), KucaTheme.Title, TextAnchor.UpperLeft, Ink));
        UIKit.Text(chooserCards, source + (o.returns ? "  ·  출발지로 돌아오는 한 바퀴" : ""), KucaTheme.Label, TextAnchor.UpperLeft, InkQuiet);
        // 문장을 어떻게 읽었는지만 한 줄 (틀렸으면 고쳐 적게). 코스 설명은 길어서 뺌 (사용자 결정 10/27)
        if (!string.IsNullOrEmpty(o.result.wish?.summary))
            UIKit.Paragraph(chooserCards, $"이렇게 이해했어요  ·  {Escape(o.result.wish.summary)}", KucaTheme.Label, KucaTheme.PrimaryDeep);
        int visits = o.result.stops.Count - (o.returns ? 1 : 0);
        UIKit.StatRow(chooserCards, (RouteMath.Distance(o.meters), "거리"), ($"{o.walkMinutes}분", "걸리는 시간"), ($"{visits}곳", "들를 곳"));
        ShowCourseHealth(o);

        UIKit.SectionTitle(chooserCards, "들를 곳");
        for (int s = 0; s < o.result.stops.Count; s++)
        {
            RouteStop st = o.result.stops[s];
            string dist = st.distanceM > 0 ? $"  <size={KucaTheme.Label}><color=#{SoftHex}>{RouteMath.Distance(st.distanceM)} · {st.walkMinutes}분</color></size>" : "";
            StopRow(chooserCards, (s + 1).ToString(), o.variant.color, Escape(st.name) + dist, Escape(st.reason));
        }
        if (!string.IsNullOrEmpty(o.result.healthNote))
            UIKit.Text(chooserCards, Escape(o.result.healthNote), KucaTheme.Caption, TextAnchor.UpperLeft, InkQuiet);

        UIKit.SectionTitle(chooserCards, "이 코스와 어울리는 노래");
        RectTransform songs = UIKit.Rect(chooserCards, "Music");
        UIKit.Vertical(songs, KucaTheme.Space2);
        WalkMusic.Fill(this, songs, CourseMusic(o), $"{request}:{o.Key}");   // 탭을 오가도 같은 추천의 같은 코스는 한 번만
    }

    /// <summary>
    /// 걸음 수 · 칼로리 범위 · 강도 한 줄 + 이번 주 목표 한 줄 (서버 health · goal 이 있을 때만).
    /// 앱이 출발지로 돌아오는 길을 붙였으면 (o.returns) 서버 값을 늘어난 거리 · 시간만큼 키운다.
    /// </summary>
    void ShowCourseHealth(Option o)
    {
        HealthEstimate h = o.result.health;
        if (!HealthEstimate.Has(h)) return;
        bool grow = o.returns && o.result.totalDistanceM > 0 && o.result.totalMinutes > 0;
        float byDistance = grow ? Mathf.Max(1f, o.meters / (float)o.result.totalDistanceM) : 1f;
        float byTime = grow ? Mathf.Max(1f, o.walkMinutes / (float)o.result.totalMinutes) : 1f;
        int steps = Mathf.RoundToInt(h.steps * byDistance / 10f) * 10;
        int kcalMin = Mathf.RoundToInt(h.kcalMin * byTime), kcalMax = Mathf.RoundToInt(h.kcalMax * byTime);
        // 큰 숫자 줄(StatRow)은 거리 · 시간 · 들를 곳 하나만: 건강 숫자까지 크게 쓰면 360px 폭에서 칸끼리 겹친다 (10/27 폰 확인)
        UIKit.Bold(UIKit.Paragraph(chooserCards, $"약 {steps:N0}걸음  ·  {kcalMin}~{kcalMax}kcal  ·  {Escape(h.intensityLabel)}", KucaTheme.Body, Ink));
        if (!string.IsNullOrEmpty(o.result.goal?.note))
            UIKit.Paragraph(chooserCards, $"이번 주 목표  ·  {Escape(o.result.goal.note)}", KucaTheme.Label, KucaTheme.PrimaryDeep);
        string weight = HealthProfile.WeightKg.HasValue ? "입력한 몸무게" : "몸무게 50~70kg";
        UIKit.Paragraph(chooserCards, $"걸음 수 · 칼로리는 {weight}로 셈한 대략값이에요. 일반 건강 정보이며 의료 조언이 아닙니다.",
            KucaTheme.Caption, InkQuiet);
    }

    WalkMusic.Request CourseMusic(Option o)
    {
        WalkMusic.Request r = MusicRequest(3);
        r.minutes = o.minutes;
        r.courseTitle = string.IsNullOrEmpty(o.result.title) ? o.variant.label : o.result.title;
        r.courseWhy = o.result.why;
        int visits = o.result.stops.Count - (o.returns ? 1 : 0);
        for (int i = 0; i < visits && r.places.Count < 10; i++)
            r.places.Add(o.result.stops[i].name);
        return r;
    }

    /// <summary>시트에서 고른 시간 · 취향 + 체크리스트의 걷는 속도</summary>
    WalkMusic.Request MusicRequest(int count)
    {
        var r = new WalkMusic.Request { minutes = minutes, count = count, tastes = new List<string>(tastes) };
        string pace = WalkPreferences.Load().pace;
        if (!string.IsNullOrEmpty(pace)) r.pace = pace;
        return r;
    }

    // ---------- 산책하며 들을 노래 (시트에서) ----------

    void ShowMusic()
    {
        Clear(musicList);
        string what = tastes.Count > 0 ? "고른 취향 · " : "";
        UIKit.Paragraph(musicList, $"{what}{minutes}분 산책 · 지금 날씨와 시간대에 맞춰 AI 가 골라요.", KucaTheme.Label, InkQuiet);
        RectTransform songs = UIKit.Rect(musicList, "Music");
        UIKit.Vertical(songs, KucaTheme.Space2);
        WalkMusic.Fill(this, songs, MusicRequest(5), null);
        musicSheet.SetActive(true);
        musicSheet.transform.SetAsLastSibling();   // 경로 시트 위에
    }

    void BuildMusicSheet()
    {
        musicSheet = Sheet("MusicSheet", "산책하며 들을 노래", () => musicSheet.SetActive(false), 0.82f, 1, out musicList, out RectTransform b, out _);
        UIKit.PrimaryButton(b, "다른 노래 추천 받기", ShowMusic);
    }

    /// <summary>들를 곳 한 줄: 번호 원 + 굵은 이름 (+ 거리) + 이유</summary>
    static void StopRow(RectTransform parent, string number, Color color, string name, string reason)
    {
        RectTransform row = UIKit.Rect(parent, "Stop");
        var h = UIKit.Horizontal(row, KucaTheme.Space3);
        h.childForceExpandWidth = h.childForceExpandHeight = false;
        h.childAlignment = TextAnchor.UpperLeft;
        RectTransform holder = UIKit.Rect(row, "Number");
        UIKit.Size(holder, 80f, 80f);
        Image c = Circle(holder, color, 72f, number);
        c.rectTransform.anchorMin = c.rectTransform.anchorMax = c.rectTransform.pivot = new Vector2(0.5f, 1f);
        c.rectTransform.anchoredPosition = Vector2.zero;
        RectTransform col = UIKit.Rect(row, "Text");
        UIKit.Vertical(col, 4f);
        UIKit.Size(col, -1f, -1f, 1f);
        UIKit.Bold(UIKit.Text(col, name, KucaTheme.Body, TextAnchor.UpperLeft, Ink));
        if (!string.IsNullOrEmpty(reason))
            UIKit.Text(col, reason, KucaTheme.Label, TextAnchor.UpperLeft, InkQuiet);
    }

    void BuildChooser()
    {
        Image bg = UIKit.Image(canvasRect, "RouteChooser", KucaTheme.Surface, raycast: true);
        UIKit.Stretch(bg.rectTransform);
        chooser = bg.gameObject;
        UnityEngine.Events.UnityAction back = () => { HideChooser(); ShowSheet(); };
        BackButton.Attach(chooser, () => back());

        // 위 45%: 코스 3개가 그려진 지도
        Image frame = TopMap.Frame(bg.transform, "Map", out chooserMap, out chooserOverlay);
        frame.pixelsPerUnitMultiplier = 100f;   // 화면 끝까지 꽉 (둥근 모서리 없음)
        RectTransform fr = frame.rectTransform;
        fr.anchorMin = new Vector2(0f, 0.55f);
        fr.anchorMax = Vector2.one;
        fr.offsetMin = new Vector2(0f, -60f);
        fr.offsetMax = Vector2.zero;
        Button backButton = UIKit.IconButton(bg.transform, HudIcons.Chevron, back, Ink, Vector2.zero, flip: true);
        Image backDisc = UIKit.Image(backButton.transform, "Disc", KucaTheme.Card);   // 지도 위에서 보이게 흰 원
        backDisc.sprite = HudIcons.Circle;
        backDisc.rectTransform.sizeDelta = new Vector2(112f, 112f);
        backDisc.transform.SetAsFirstSibling();
        RectTransform br = (RectTransform)backButton.transform;
        Rect sa = Screen.safeArea;
        br.anchoredPosition = new Vector2(24f, -24f - (Screen.height - sa.yMax) / Mathf.Max(1, Screen.height) * 1920f);

        // 아래 시트: 코스 정보 + "이 코스로 걷기"
        Image panel = UIKit.Image(bg.transform, "Sheet", KucaTheme.Card, raycast: true);
        panel.sprite = HudIcons.Rounded;
        panel.type = Image.Type.Sliced;
        panel.pixelsPerUnitMultiplier = 0.34f;
        RectTransform pr = panel.rectTransform;
        pr.anchorMin = Vector2.zero;
        pr.anchorMax = new Vector2(1f, 0.55f);
        pr.offsetMin = new Vector2(0f, -60f);   // 아래 둥근 모서리는 화면 밖으로
        pr.offsetMax = Vector2.zero;
        Grip(pr);
        float bottomH = KucaTheme.ButtonHeight + 2 * KucaTheme.Space4 + 60f + sa.yMin / Mathf.Max(1, Screen.height) * 1920f;
        RectTransform bottom = Bottom(pr, bottomH);
        RectTransform area = UIKit.Rect(pr, "Options");
        UIKit.StretchBetween(area, bottomH, 48f);
        int g = (int)KucaTheme.Gutter;
        chooserCards = UIKit.ScrollList(area, KucaTheme.Space3, new RectOffset(g, g, 16, (int)KucaTheme.Space6));
        chooserGo = UIKit.PrimaryButton(bottom, "이 코스로 걷기", () => StartWalk(options[chosen]));
        chooser.SetActive(false);
    }

    // ---------- 걷는 중 판 ----------

    /// <summary>한 줄로만 쓰고 칸보다 길면 min 까지 글자를 줄인다 (가상 산책 중엔 판이 좁아진다)</summary>
    static void OneLine(Text t, int max, int min)
    {
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Truncate;
        t.resizeTextForBestFit = true;
        t.resizeTextMaxSize = max;
        t.resizeTextMinSize = min;
    }

    void RefreshWalkPanel()
    {
        if (active == null)
            return;
        Option o = active;
        walkDot.color = o.variant.color;
        walkTitle.text = o.variant.label;   // 거리 · 시간은 아래 진행 줄로 (가상 산책 중 좁은 판에서 제목이 세 줄로 넘쳤다)
        int next = done.FindIndex(x => !x);
        int count = done.FindAll(x => x).Count;
        if (next >= 0)
        {
            RouteStop s = o.result.stops[next];
            float left = Flat(PlayerPosition, o.stops[next]);
            bool last = next == o.result.stops.Count - 1;
            walkNext.text = $"<color=#{ColorUtility.ToHtmlStringRGB(o.variant.color)}>{(last ? "마지막" : "다음")} {next + 1}</color>  {Escape(s.name)}  <size={KucaTheme.Caption}><color=#{SoftHex}>· {RouteMath.Distance(left)}</color></size>";
            walkReason.text = Escape(s.reason);
        }
        walkProgress.text = $"{count}/{o.result.stops.Count}곳 들렀어요  <color=#{SoftHex}>· {RouteMath.Distance(o.meters)} · 약 {o.walkMinutes}분</color>";
        pathToggle.color = showPath ? o.variant.color : KucaTheme.Fill;
        pathToggleText.color = showPath ? Color.white : Ink;
        pathToggleText.text = showPath ? "경로 켜짐" : "경로 꺼짐";
    }

    /// <summary>걷는 중 지도 아래 떠 있는 흰 카드: 코스 이름 · 다음 장소(크게) · 이유 · 진행</summary>
    void BuildWalkPanel()
    {
        Image card = UIKit.Image(canvasRect, "WalkPanel", KucaTheme.Card, raycast: true);
        card.sprite = HudIcons.Rounded;
        card.type = Image.Type.Sliced;
        card.pixelsPerUnitMultiplier = 0.34f;
        card.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.12f);
        walkPanel = card.gameObject;
        RectTransform pr = card.rectTransform;
        pr.anchorMin = new Vector2(0f, 0f);
        pr.anchorMax = new Vector2(1f, 0f);
        pr.pivot = new Vector2(0.5f, 0f);
        pr.sizeDelta = new Vector2(-48f, WalkPanelHeight);
        pr.anchoredPosition = new Vector2(0f, 320f);
        UIInputBlocker.Register(pr);

        walkDot = UIKit.Image(pr, "Dot", RouteBlue);
        walkDot.sprite = HudIcons.Circle;
        Place(walkDot.rectTransform, new Vector2(0f, 1f), new Vector2(44f, -52f));
        walkDot.rectTransform.sizeDelta = new Vector2(32f, 32f);
        walkTitle = UIKit.Bold(UIKit.Text(pr, "", KucaTheme.Label, TextAnchor.MiddleLeft, Ink));
        Box(walkTitle.rectTransform, 92f, 30f, 430f, 64f);
        OneLine(walkTitle, KucaTheme.Label, KucaTheme.Caption);

        Button toggle = UIKit.Button(pr, "경로 켜짐", () => { showPath = !showPath; RefreshWalkPanel(); }, RouteBlue, KucaTheme.Caption);
        pathToggle = toggle.GetComponent<Image>();
        pathToggleText = toggle.GetComponentInChildren<Text>();
        Place((RectTransform)toggle.transform, new Vector2(1f, 1f), new Vector2(-224f, -24f));
        ((RectTransform)toggle.transform).sizeDelta = new Vector2(200f, 92f);
        Button end = UIKit.Button(pr, "끝내기", ConfirmEnd, KucaTheme.Fill, KucaTheme.Caption);
        end.GetComponentInChildren<Text>().color = Ink;
        Place((RectTransform)end.transform, new Vector2(1f, 1f), new Vector2(-24f, -24f));
        ((RectTransform)end.transform).sizeDelta = new Vector2(184f, 92f);

        // 다음 장소 (누르면 장소 팝업)
        Image next = UIKit.Image(pr, "Next", new Color(1f, 1f, 1f, 0f), raycast: true);
        Box(next.rectTransform, 32f, 128f, 32f, WalkPanelHeight - 152f);
        var nb = next.gameObject.AddComponent<Button>();
        nb.transition = Selectable.Transition.None;
        nb.onClick.AddListener(() => { int i = done.FindIndex(x => !x); OpenPlace(i < 0 ? 0 : i); });
        // 다음 장소는 본문 크기 (카드 제목 크기 Heading 은 360px 폭에서 장소 이름이 잘렸다), 이유 · 진행은 작은 글씨
        walkNext = UIKit.Bold(UIKit.Text(next.transform, "", KucaTheme.Body, TextAnchor.UpperLeft, Ink));
        Box(walkNext.rectTransform, 12f, 0f, 12f, 60f);
        OneLine(walkNext, KucaTheme.Body, KucaTheme.Caption);
        walkReason = UIKit.Text(next.transform, "", KucaTheme.Caption, TextAnchor.UpperLeft, InkQuiet);
        walkReason.verticalOverflow = VerticalWrapMode.Truncate;
        Box(walkReason.rectTransform, 12f, 66f, 12f, 84f);   // 두 줄까지
        walkProgress = UIKit.Bold(UIKit.Text(next.transform, "", KucaTheme.Caption, TextAnchor.UpperLeft, RouteBlue));
        Box(walkProgress.rectTransform, 12f, 156f, 12f, 44f);
        OneLine(walkProgress, KucaTheme.Caption, KucaTheme.Caption - 2);
        walkPanel.SetActive(false);
    }

    /// <summary>끝내기는 한 번 묻는다 (경로 켜기 버튼 옆이라 잘못 누르기 쉽다)</summary>
    void ConfirmEnd()
    {
        if (active == null)
            return;
        int count = done.FindAll(x => x).Count, total = active.result.stops.Count;
        string walked = RouteMath.Distance(WalkSession.WalkedMeters);
        string note = count >= total ? "" : $"\n{total - count}곳이 남았어요. 지금 끝내면 들른 곳까지만 기록돼요.";
        ConfirmDialog.Show("산책을 끝낼까요?", $"지금까지 {walked} · {count}/{total}곳{note}", "끝내기", () => EndRoute(true), "계속 걷기");
    }

    // ---------- 장소 팝업 (미니맵 + 공지) ----------

    void OpenPlace(int index)
    {
        if (active == null || index < 0 || index >= active.result.stops.Count)
            return;
        RouteStop s = active.result.stops[index];
        string hex = ColorUtility.ToHtmlStringRGB(active.variant.color);
        popupName.text = $"<color=#{hex}>{index + 1}</color>  {Escape(s.name)}";
        popupReason.text = $"{Escape(s.reason)}  ·  도보 {s.walkMinutes}분";
        Clear(popupNotices);
        List<PlaceNotice> list = NoticesOf(s.buildingId);
        if (list.Count == 0)
            UIKit.Size(UIKit.Text(popupNotices, "이 장소의 공지는 아직 없어요", KucaTheme.Body, TextAnchor.MiddleLeft, InkQuiet), 90f);
        foreach (PlaceNotice n in list)
            NoticeRow(popupNotices, n, false);
        KyungHeeSpot spot = SpotOf(s.buildingId);
        popupBoardButton.gameObject.SetActive(spot != null);
        popupBoardButton.onClick.RemoveAllListeners();
        popupBoardButton.onClick.AddListener(() =>
        {
            ClosePlace();
            var board = FindAnyObjectByType<MemoBoard>();
            if (board != null && spot != null) board.OpenSpot(spot);
        });
        placePopup.SetActive(true);
        placePopup.transform.SetAsLastSibling();
        UIInputBlocker.SetModal(this, true);
        Canvas.ForceUpdateCanvases();
        DrawPlaceMap(index);
    }

    void ClosePlace()
    {
        placePopup.SetActive(false);
        UIInputBlocker.SetModal(this, sheet.activeSelf || loading.activeSelf || chooser.activeSelf);
    }

    void DrawPlaceMap(int focus)
    {
        var all = new List<Vector3>(active.path) { PlayerPosition };
        Camera cam = RenderTop(miniMap, ref miniTexture, all);
        Clear(miniOverlay);
        if (cam == null)
            return;
        Vector2 size = miniOverlay.rect.size;
        TopMap.Polyline(miniOverlay, cam, size, active.path, 19f, new Color(1f, 1f, 1f, 0.95f));
        TopMap.Polyline(miniOverlay, cam, size, active.path, 12f, active.variant.color, true);
        for (int i = 0; i < active.stops.Count; i++)
        {
            bool arrived = i < done.Count && done[i];
            TopMap.Marker(miniOverlay, cam, size, active.stops[i], arrived ? DoneGreen : active.variant.color, arrived ? "✓" : (i + 1).ToString(), i == focus ? 84f : 58f);
        }
        TopMap.Marker(miniOverlay, cam, size, PlayerPosition, Ink, "나", 58f);
    }

    void BuildPlacePopup()
    {
        placePopup = Sheet("PlacePopup", "", ClosePlace, 0.88f, 1, out RectTransform c, out RectTransform b, out popupName);
        popupReason = UIKit.Text(c, "", KucaTheme.Body, TextAnchor.UpperLeft, InkQuiet);
        Image frame = TopMap.Frame(c, "MiniMap", out miniMap, out miniOverlay);
        frame.pixelsPerUnitMultiplier = 0.34f;
        UIKit.Size(frame, 640f);
        UIKit.SectionTitle(c, "장소 공지");
        popupNotices = UIKit.Rect(c, "Notices");
        UIKit.Vertical(popupNotices, KucaTheme.Space2);
        popupBoardButton = UIKit.PrimaryButton(b, "이 장소 쪽지판 열기", () => { });
    }

    /// <summary>공지 한 줄: (장소) 제목 · 요약 · 날짜 · 원문 보기. 누르면 원문을 연다 (메모판 공지 탭도 씀)</summary>
    internal static void NoticeRow(RectTransform parent, PlaceNotice n, bool withPlace)
    {
        Image row = UIKit.Image(parent, "Notice", KucaTheme.Fill, raycast: true);
        row.sprite = HudIcons.Rounded;
        row.type = Image.Type.Sliced;
        row.pixelsPerUnitMultiplier = 0.4f;
        UIKit.Vertical(row, KucaTheme.Space1, new RectOffset(40, 40, 32, 32));
        row.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var b = row.gameObject.AddComponent<Button>();
        b.transition = Selectable.Transition.None;
        string url = n.url;
        b.onClick.AddListener(() => { if (!string.IsNullOrEmpty(url)) Application.OpenURL(url); });
        if (withPlace)
            UIKit.Bold(UIKit.Text(row.transform, n.buildingName, KucaTheme.Label, TextAnchor.UpperLeft, RouteBlue));
        // 두 줄 남짓에서 자르기 (세 줄까지 꺾여 카드 높이가 제각각이었음, REVIEW_v28). shortcut: 글자 수로 자름, 글꼴 폭이 크게 바뀌면 줄 수로
        string title = n.title != null && n.title.Length > 34 ? n.title.Substring(0, 33) + "…" : n.title;
        UIKit.Bold(UIKit.Text(row.transform, title, KucaTheme.Body, TextAnchor.UpperLeft, Ink));
        if (!string.IsNullOrEmpty(n.summary))
            UIKit.Text(row.transform, n.summary, KucaTheme.Label, TextAnchor.UpperLeft, InkQuiet);
        UIKit.Text(row.transform, $"{n.postedAt}   <color=#{PrimaryHex}>원문 보기 ›</color>", KucaTheme.Caption, TextAnchor.UpperLeft, InkQuiet);
    }

    // ---------- 위에서 본 지도 ----------

    /// <summary>위에서 본 지도를 찍는다. 본 지도의 코스 선은 찍히지 않게 잠깐 숨긴다 (위에 다시 그린다)</summary>
    Camera RenderTop(RawImage target, ref RenderTexture texture, List<Vector3> points)
    {
        if (guide != null) guide.SetVisible(false);
        Camera cam = TopMap.Render(target, ref texture, points);
        if (guide != null) guide.SetVisible(active != null && showPath);
        return cam;
    }

    // ---------- 작은 도우미 ----------

    /// <summary>
    /// 지도 위 아래 시트 (카카오맵 · Apple 지도): 어둡게 덮고 아래에서 올라온 흰 시트 — 손잡이 · 큰 제목 · ×, 스크롤 내용,
    /// 아래 고정 버튼 칸 (buttons 개). 바깥을 누르거나 뒤로 가기로 닫힌다.
    /// </summary>
    GameObject Sheet(string name, string title, UnityEngine.Events.UnityAction onClose, float heightFrac, int buttons,
        out RectTransform content, out RectTransform bottom, out Text titleText)
    {
        Image dim = UIKit.Image(canvasRect, name, Dim, raycast: true);
        UIKit.Stretch(dim.rectTransform);
        dim.gameObject.AddComponent<Button>().onClick.AddListener(onClose); // 바깥을 누르면 닫힘
        BackButton.Attach(dim.gameObject, () => onClose());

        Image panel = UIKit.Image(dim.transform, "Panel", KucaTheme.Card, raycast: true);
        panel.sprite = HudIcons.Rounded;
        panel.type = Image.Type.Sliced;
        panel.pixelsPerUnitMultiplier = 0.34f;
        RectTransform pr = panel.rectTransform;
        pr.anchorMin = Vector2.zero;
        pr.anchorMax = new Vector2(1f, heightFrac);
        pr.offsetMin = new Vector2(0f, -60f);   // 아래 둥근 모서리는 화면 밖으로
        pr.offsetMax = Vector2.zero;
        Swallow(panel.gameObject);
        Grip(pr);

        titleText = UIKit.Bold(UIKit.Text(pr, title, KucaTheme.Title, TextAnchor.MiddleLeft, Ink));
        Box(titleText.rectTransform, KucaTheme.Gutter, 56f, 180f, 100f);
        var x = (RectTransform)UIKit.IconButton(pr, HudIcons.Close, onClose, Ink, Vector2.zero).transform;
        x.anchorMin = x.anchorMax = x.pivot = Vector2.one;
        x.anchoredPosition = new Vector2(-24f, -40f);

        float safeBottom = Screen.safeArea.yMin / Mathf.Max(1, Screen.height) * 1920f;
        float bottomH = (buttons > 0 ? buttons * KucaTheme.ButtonHeight + (buttons - 1) * KucaTheme.Space2 + 2 * KucaTheme.Space4 : 0f) + 60f + safeBottom;
        bottom = Bottom(pr, bottomH);
        RectTransform area = UIKit.Rect(pr, "Area");
        UIKit.StretchBetween(area, bottomH, 176f);
        int g = (int)KucaTheme.Gutter;
        content = UIKit.ScrollList(area, KucaTheme.Space3, new RectOffset(g, g, 8, (int)KucaTheme.Space6));
        UIKit.MoreBelow(pr, area, bottomH);   // 버튼 위에서 반쯤 잘린 제목이 깨져 보였음 (REVIEW_v45)
        dim.gameObject.SetActive(false);
        return dim.gameObject;
    }

    /// <summary>시트 위 가운데 회색 손잡이</summary>
    static void Grip(RectTransform panel)
    {
        Image grip = UIKit.Image(panel, "Grip", KucaTheme.Line);
        grip.sprite = HudIcons.Pill;
        grip.type = Image.Type.Sliced;
        grip.pixelsPerUnitMultiplier = 10f;
        Place(grip.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -20f));
        grip.rectTransform.sizeDelta = new Vector2(120f, 12f);
    }

    /// <summary>시트 아래 고정 버튼 칸 (아래 60 은 화면 밖으로 내린 둥근 모서리 + 홈 표시줄 몫)</summary>
    static RectTransform Bottom(RectTransform panel, float height)
    {
        RectTransform b = UIKit.Rect(panel, "Bottom");
        b.anchorMin = Vector2.zero;
        b.anchorMax = new Vector2(1f, 0f);
        b.pivot = new Vector2(0.5f, 0f);
        b.offsetMin = Vector2.zero;
        b.offsetMax = new Vector2(0f, height);
        int g = (int)KucaTheme.Gutter, s4 = (int)KucaTheme.Space4;
        UIKit.Vertical(b, KucaTheme.Space2, new RectOffset(g, g, s4, 0)).childAlignment = TextAnchor.UpperCenter;   // 버튼은 위부터, 남는 아래는 비움
        return b;
    }

    static void Question(RectTransform parent, string text) =>
        UIKit.Size(UIKit.Bold(UIKit.Text(parent, text, KucaTheme.Heading, TextAnchor.LowerLeft, Ink)), 96f);

    static RectTransform ChipRow(RectTransform parent, float height = 104f)
    {
        RectTransform row = UIKit.Rect(parent, "Chips");
        UIKit.Size(row, height);
        UIKit.Horizontal(row, KucaTheme.Space2);
        return row;
    }

    static Image Chip(RectTransform row, string label, UnityEngine.Events.UnityAction onClick, bool small = false)
    {
        Button b = UIKit.Chip(row, label, false, onClick, small);
        b.gameObject.AddComponent<PressScale>();
        return b.GetComponent<Image>();
    }

    static void Clear(RectTransform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
            Destroy(parent.GetChild(i).gameObject);
    }

    static Image Circle(Transform parent, Color color, float size, string label)
    {
        Image c = UIKit.Image(parent, "Badge", color);
        c.sprite = HudIcons.Circle;
        c.rectTransform.anchorMin = c.rectTransform.anchorMax = c.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        c.rectTransform.sizeDelta = new Vector2(size, size);
        Text t = UIKit.Bold(UIKit.Text(c.transform, label, Mathf.RoundToInt(size * 0.48f), TextAnchor.MiddleCenter, Color.white));
        UIKit.Stretch(t.rectTransform);
        return c;
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
}
