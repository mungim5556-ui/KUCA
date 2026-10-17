using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 산책 취향 체크리스트 (로그인/첫 실행 때 받음). 나중에 AI 산책메이트 매칭에 쓴다.
/// 지금은 기기에만 저장한다 (PlayerPrefs). 매칭 서버가 생기면 이 모양 그대로 보낸다.
/// 값은 영어 코드로 저장한다 (tastes 의 앞 4개는 산책 추천 RouteRequest.tastes 와 같은 코드).
/// </summary>
[Serializable]
public class WalkPreferences
{
    /// <summary>1. 언제 주로 걷는지 (여러 개): morning · day · evening · night</summary>
    public List<string> walkTimes = new List<string>();
    /// <summary>2. 한 번에 얼마나 (분): 15 · 30 · 60(1시간 이상)</summary>
    public int walkMinutes;
    /// <summary>3. 걷는 속도: slow · normal · fast</summary>
    public string pace = "";
    /// <summary>4. 산책하며 하고 싶은 것 (최대 3개): photo · quiet · food · unvisited · partner · exercise (예전 답의 catch 는 partner 로 읽는다)</summary>
    public List<string> tastes = new List<string>();
    /// <summary>5. 같이 걸을 때 분위기: chatty · some · quiet</summary>
    public string talkStyle = "";
    /// <summary>6. 몇 명이 좋은지: one · small · any</summary>
    public string groupSize = "";
    /// <summary>7. 매칭 상대: same(동성만) · any(상관없음)</summary>
    public string matchGender = "";
    /// <summary>yyyy-MM-dd</summary>
    public string updatedAt = "";

    const string Key = "walk_prefs";

    public bool IsComplete =>
        walkTimes.Count > 0 && walkMinutes > 0 && pace.Length > 0 && tastes.Count > 0 && talkStyle.Length > 0 && groupSize.Length > 0 && matchGender.Length > 0;

    public static bool HasSaved => Load().IsComplete;

    public static WalkPreferences Load()
    {
        try
        {
            var p = JsonUtility.FromJson<WalkPreferences>(PlayerPrefs.GetString(Key, "{}")) ?? new WalkPreferences();
            p.walkTimes ??= new List<string>();
            p.tastes ??= new List<string>();
            p.pace ??= ""; p.talkStyle ??= ""; p.groupSize ??= ""; p.matchGender ??= ""; p.updatedAt ??= "";
            int old = p.tastes.IndexOf("catch"); // v2: 포획이 없어져 "파트너와 걷기"로 바뀜
            if (old >= 0) p.tastes[old] = "partner";
            return p;
        }
        catch (ArgumentException)
        {
            return new WalkPreferences();
        }
    }

    public void Save()
    {
        updatedAt = DateTime.Now.ToString("yyyy-MM-dd");
        PlayerPrefs.SetString(Key, JsonUtility.ToJson(this));
        PlayerPrefs.Save();
    }

    public string ToJson() => JsonUtility.ToJson(this);
}
