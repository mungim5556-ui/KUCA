using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 산책 취향 체크리스트 화면. 한 화면에 질문 하나씩, 6단계.
/// 첫 실행 때 자동으로 열리고 (나중에는 로그인 직후에 Open 을 부르면 된다), 설정 › 취향 다시 고르기에서도 연다.
/// 답은 WalkPreferences 로 기기에 저장한다 (AI 산책메이트 매칭에 쓸 자료).
/// </summary>
public class PreferenceSurvey : MonoBehaviour
{
    static readonly Color Ink = new Color(0.11f, 0.11f, 0.12f);
    static readonly Color InkQuiet = new Color(0.11f, 0.11f, 0.12f, 0.55f);
    static readonly Color Accent = new Color(0.16f, 0.42f, 0.95f);
    static readonly Color ChipOff = new Color(0.95f, 0.96f, 0.97f);
    static readonly Color Paper = new Color(0.99f, 0.99f, 1f);

    class Question
    {
        public string title, hint;
        public bool multi;
        public int max = 1;
        public (string code, string label, string sub)[] options;
    }

    static readonly Question[] Questions =
    {
        new Question { title = "언제 주로 걸어요?", hint = "여러 개 고를 수 있어요", multi = true, max = 4, options = new[]
            { ("morning", "아침", "6~11시"), ("day", "낮", "11~17시"), ("evening", "저녁", "17~20시"), ("night", "밤", "20시 이후") } },
        new Question { title = "한 번에 얼마나 걸어요?", hint = "하나만 골라요", options = new[]
            { ("15", "15분", "잠깐 바람 쐬기"), ("30", "30분", "캠퍼스 반 바퀴"), ("60", "1시간 이상", "느긋하게 한 바퀴") } },
        new Question { title = "걷는 속도는요?", hint = "하나만 골라요", options = new[]
            { ("slow", "느긋하게", "구경하며 천천히"), ("normal", "보통", ""), ("fast", "빠르게", "운동하듯 성큼성큼") } },
        new Question { title = "산책하면서 하고 싶은 것", hint = "최대 3개까지 골라요", multi = true, max = 3, options = new[]
            { ("photo", "사진 명소", ""), ("quiet", "조용한 곳", ""), ("food", "맛집·카페", ""),
              ("unvisited", "안 가 본 곳", ""), ("partner", "파트너와 걷기", ""), ("exercise", "운동", "") } },
        new Question { title = "같이 걸을 때 분위기는요?", hint = "하나만 골라요", options = new[]
            { ("chatty", "수다 떨며", "이야기하는 게 좋아요"), ("some", "가끔 대화", "편하게 한두 마디"), ("quiet", "조용히 각자", "옆에서 같이 걷기만") } },
        new Question { title = "몇 명이서 걷는 게 좋아요?", hint = "하나만 골라요", options = new[]
            { ("one", "1:1", "한 명과 같이"), ("small", "3~4명 소그룹", "여럿이 함께"), ("any", "상관없어요", "") } },
        new Question { title = "같이 걸을 상대는요?", hint = "하나만 골라요", options = new[]
            { ("same", "동성만", "나와 같은 성별인 사람과만"), ("any", "상관없어요", "") } },
    };

    GameObject root;
    Text stepText, titleText, hintText;
    RectTransform optionList;
    Image[] dots;
    Button nextButton, backButton;
    int step;
    List<string>[] answers;
    Action onDone;

    /// <summary>체크리스트를 연다. 저장된 답이 있으면 그 값으로 채워 둔다.</summary>
    public static void Open(Action done = null)
    {
        PreferenceSurvey s = FindAnyObjectByType<PreferenceSurvey>();
        if (s == null)
            s = new GameObject("PreferenceSurvey").AddComponent<PreferenceSurvey>();
        s.Show(done);
    }

    /// <summary>아직 한 번도 답하지 않았으면 연다 (첫 실행).</summary>
    public static void OpenIfNeeded()
    {
        if (!WalkPreferences.HasSaved)
            Open();
    }

    void Awake() => Build();

    void Show(Action done)
    {
        onDone = done;
        WalkPreferences p = WalkPreferences.Load();
        answers = new[]
        {
            new List<string>(p.walkTimes),
            p.walkMinutes > 0 ? new List<string> { p.walkMinutes.ToString() } : new List<string>(),
            Single(p.pace), new List<string>(p.tastes), Single(p.talkStyle), Single(p.groupSize), Single(p.matchGender),
        };
        step = 0;
        root.SetActive(true);
        UIInputBlocker.SetModal(this, true);
        Fill();
    }

    static List<string> Single(string v) => string.IsNullOrEmpty(v) ? new List<string>() : new List<string> { v };

    void Fill()
    {
        Question q = Questions[step];
        stepText.text = $"{step + 1} / {Questions.Length}";
        titleText.text = q.title;
        hintText.text = q.hint;
        for (int i = 0; i < dots.Length; i++)
            dots[i].color = i <= step ? Accent : new Color(0f, 0f, 0f, 0.12f);
        backButton.gameObject.SetActive(step > 0);
        UIKit.SetLabel(nextButton, step == Questions.Length - 1 ? "완료" : "다음");

        for (int i = optionList.childCount - 1; i >= 0; i--)
            Destroy(optionList.GetChild(i).gameObject);
        List<string> chosen = answers[step];
        foreach (var (code, label, sub) in q.options)
        {
            string c = code;
            bool on = chosen.Contains(code);
            Image row = UIKit.Image(optionList, code, on ? Accent : ChipOff, raycast: true);
            row.sprite = HudIcons.Rounded;
            row.type = Image.Type.Sliced;
            row.pixelsPerUnitMultiplier = 0.7f;
            UIKit.Size(row, sub.Length > 0 ? 136f : 112f);
            var b = row.gameObject.AddComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.onClick.AddListener(() => Toggle(c));
            row.gameObject.AddComponent<PressScale>();
            Text t = UIKit.Text(row.transform, label, 40, TextAnchor.MiddleLeft, on ? Color.white : Ink);
            t.fontStyle = FontStyle.Bold;
            t.rectTransform.anchorMin = new Vector2(0f, sub.Length > 0 ? 0.42f : 0f);
            t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(44f, 0f);
            t.rectTransform.offsetMax = new Vector2(-110f, 0f);
            if (sub.Length > 0)
            {
                Text st = UIKit.Text(row.transform, sub, 28, TextAnchor.UpperLeft, on ? new Color(1f, 1f, 1f, 0.85f) : InkQuiet);
                st.rectTransform.anchorMin = Vector2.zero;
                st.rectTransform.anchorMax = new Vector2(1f, 0.42f);
                st.rectTransform.offsetMin = new Vector2(44f, 0f);
                st.rectTransform.offsetMax = new Vector2(-110f, 0f);
            }
            // 오른쪽 체크 표시
            Image mark = UIKit.Image(row.transform, "Mark", on ? Color.white : new Color(0f, 0f, 0f, 0.12f));
            mark.sprite = HudIcons.Circle;
            mark.rectTransform.anchorMin = mark.rectTransform.anchorMax = mark.rectTransform.pivot = new Vector2(1f, 0.5f);
            mark.rectTransform.anchoredPosition = new Vector2(-40f, 0f);
            mark.rectTransform.sizeDelta = new Vector2(48f, 48f);
            if (on)
            {
                Text check = UIKit.Text(mark.transform, "✓", 34, TextAnchor.MiddleCenter, Accent);
                check.fontStyle = FontStyle.Bold;
                UIKit.Stretch(check.rectTransform);
            }
        }
        nextButton.interactable = chosen.Count > 0;
        nextButton.GetComponent<Image>().color = chosen.Count > 0 ? Accent : new Color(Accent.r, Accent.g, Accent.b, 0.35f);
    }

    void Toggle(string code)
    {
        Question q = Questions[step];
        List<string> chosen = answers[step];
        if (!q.multi)
        {
            chosen.Clear();
            chosen.Add(code);
        }
        else if (!chosen.Remove(code))
        {
            if (chosen.Count >= q.max)
            {
                Toast($"최대 {q.max}개까지 고를 수 있어요");
                return;
            }
            chosen.Add(code);
        }
        Fill();
    }

    void Next()
    {
        if (answers[step].Count == 0)
            return;
        if (step < Questions.Length - 1)
        {
            step++;
            Fill();
            return;
        }
        var p = new WalkPreferences
        {
            walkTimes = new List<string>(answers[0]),
            walkMinutes = int.TryParse(answers[1][0], out int m) ? m : 30,
            pace = answers[2][0],
            tastes = new List<string>(answers[3]),
            talkStyle = answers[4][0],
            groupSize = answers[5][0],
            matchGender = answers[6][0],
        };
        p.Save();
        Debug.Log("[Preferences] " + p.ToJson());
        if (AuthService.HasToken && AuthService.HasServer)
            StartCoroutine(AuthService.SavePreferences(p, (_, error) => { if (error != null) Toast("취향을 서버에 올리지 못했어요: " + error); }));
        root.SetActive(false);
        UIInputBlocker.SetModal(this, false);
        Toast("산책 취향을 저장했어요");
        onDone?.Invoke();
    }

    void Back()
    {
        if (step == 0) return;
        step--;
        Fill();
    }

    static void Toast(string message)
    {
        var hud = FindAnyObjectByType<GameHUD>();
        if (hud != null) hud.Toast(message);
    }

    void OnDisable() => UIInputBlocker.SetModal(this, false);

    void Build()
    {
        Canvas canvas = UIKit.CreateCanvas(transform, "SurveyCanvas", 45);
        root = UIKit.Image(canvas.transform, "Survey", Paper, raycast: true).gameObject;
        UIKit.Stretch((RectTransform)root.transform);
        Rect sa = Screen.safeArea;
        RectTransform safe = UIKit.Rect(root.transform, "Safe");
        safe.anchorMin = new Vector2(sa.xMin / Mathf.Max(1, Screen.width), sa.yMin / Mathf.Max(1, Screen.height));
        safe.anchorMax = new Vector2(sa.xMax / Mathf.Max(1, Screen.width), sa.yMax / Mathf.Max(1, Screen.height));
        safe.offsetMin = safe.offsetMax = Vector2.zero;

        Text brand = UIKit.Text(safe, "산책 취향 알려 주기", 32, TextAnchor.MiddleLeft, Accent);
        brand.fontStyle = FontStyle.Bold;
        Box(brand.rectTransform, 60f, 50f, 200f, 50f);
        stepText = UIKit.Text(safe, "", 30, TextAnchor.MiddleRight, InkQuiet);
        Box(stepText.rectTransform, 60f, 50f, 60f, 50f);

        // 진행 점 6개
        dots = new Image[Questions.Length];
        for (int i = 0; i < dots.Length; i++)
        {
            Image d = UIKit.Image(safe, "Dot", Accent);
            d.sprite = HudIcons.Pill;
            d.type = Image.Type.Sliced;
            d.pixelsPerUnitMultiplier = 10f;
            RectTransform r = d.rectTransform;
            r.anchorMin = new Vector2(i / (float)dots.Length, 1f);
            r.anchorMax = new Vector2((i + 1) / (float)dots.Length, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.offsetMin = new Vector2(i == 0 ? 60f : 6f, -122f);
            r.offsetMax = new Vector2(i == dots.Length - 1 ? -60f : -6f, -112f);
            dots[i] = d;
        }

        titleText = UIKit.Text(safe, "", 56, TextAnchor.MiddleLeft, Ink);
        titleText.fontStyle = FontStyle.Bold;
        Box(titleText.rectTransform, 60f, 180f, 60f, 90f);
        hintText = UIKit.Text(safe, "", 32, TextAnchor.MiddleLeft, InkQuiet);
        Box(hintText.rectTransform, 60f, 270f, 60f, 50f);
        Text why = UIKit.Text(safe, "AI 산책메이트를 찾아 줄 때만 써요. 설정에서 언제든 바꿀 수 있어요.", 26, TextAnchor.MiddleLeft, InkQuiet);
        Box(why.rectTransform, 60f, 318f, 60f, 44f);

        RectTransform area = UIKit.Rect(safe, "Options");
        area.anchorMin = Vector2.zero;
        area.anchorMax = Vector2.one;
        area.offsetMin = new Vector2(0f, 280f);
        area.offsetMax = new Vector2(0f, -390f);
        optionList = UIKit.ScrollList(area, 20f, new RectOffset(60, 60, 0, 30));

        nextButton = UIKit.Button(safe, "다음", Next, Accent, 42);
        Image ni = nextButton.GetComponent<Image>();
        ni.sprite = HudIcons.Pill;
        ni.type = Image.Type.Sliced;
        ni.pixelsPerUnitMultiplier = 120f / 124f;
        nextButton.GetComponentInChildren<Text>().fontStyle = FontStyle.Bold;
        RectTransform nr = (RectTransform)nextButton.transform;
        nr.anchorMin = new Vector2(0f, 0f);
        nr.anchorMax = new Vector2(1f, 0f);
        nr.pivot = new Vector2(0.5f, 0f);
        nr.offsetMin = new Vector2(60f, 60f);
        nr.offsetMax = new Vector2(-60f, 184f);

        backButton = UIKit.Button(safe, "이전", Back, new Color(0f, 0f, 0f, 0f), 34);
        backButton.GetComponentInChildren<Text>().color = InkQuiet;
        RectTransform br = (RectTransform)backButton.transform;
        br.anchorMin = br.anchorMax = br.pivot = new Vector2(0.5f, 0f);
        br.anchoredPosition = new Vector2(0f, 190f);
        br.sizeDelta = new Vector2(240f, 70f);

        root.SetActive(false);
    }

    static void Box(RectTransform rt, float left, float top, float right, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(left, -top - height);
        rt.offsetMax = new Vector2(-right, -top);
    }
}
