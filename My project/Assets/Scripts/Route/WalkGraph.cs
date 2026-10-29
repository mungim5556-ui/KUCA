using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 캠퍼스 보행 길 그래프 (Resources/KUCA/WalkGraph.json, tools/route/build_walk_graph.py 가 OSM 에서 만든다).
/// 두 점 사이를 실제 길(보행로 · 산책로 · 계단 · 캠퍼스 도로)을 따라 잇는 최단 경로를 찾는다 (A*).
/// 좌표는 지도 월드 좌표 (1 유닛 = 1 m). 길에서 먼 점은 가장 가까운 길 노드까지 직선으로 붙인다.
/// 사용자가 산책길 지도에 막힘으로 표시한 길 · 구역은 데이터에 아예 없고, 오르막 표시는 간선 무게(w)로 피한다.
/// </summary>
public static class WalkGraph
{
    [Serializable]
    class Data
    {
        public double[] lon, lat;
        public int[] a, b;
        public float[] w;   // 간선 k 의 힘든 정도 (길이에 곱함, 평지 1 · 오르막 1.5 · 가파름 2.2). 없으면 모두 1
        public int[] rest;  // 사용자가 표시한 쉼터에서 가장 가까운 노드
    }

    static Vector3[] pos;              // 노드의 월드 좌표
    static List<(int to, float cost)>[] links;
    static CampusMap builtFor;
    static readonly List<Vector3> rests = new List<Vector3>();

    /// <summary>그래프가 준비됐는지 (지도와 데이터가 있어야 함)</summary>
    public static bool Ready(CampusMap map)
    {
        if (map == null)
            return false;
        if (builtFor == map && pos != null)
            return true;
        var asset = Resources.Load<TextAsset>("KUCA/WalkGraph");
        if (asset == null)
        {
            Debug.LogWarning("[WalkGraph] Resources/KUCA/WalkGraph.json 이 없습니다 (tools/route/build_walk_graph.py).");
            return false;
        }
        Data d = JsonUtility.FromJson<Data>(asset.text);
        if (d?.lon == null || d.lat == null || d.a == null || d.b == null || d.lon.Length != d.lat.Length)
            return false;
        pos = new Vector3[d.lon.Length];
        for (int i = 0; i < pos.Length; i++)
            pos[i] = map.GeoToWorld(d.lon[i], d.lat[i]);
        links = new List<(int, float)>[pos.Length];
        for (int i = 0; i < links.Length; i++)
            links[i] = new List<(int, float)>(3);
        for (int k = 0; k < d.a.Length && k < d.b.Length; k++)
        {
            int x = d.a[k], y = d.b[k];
            if (x < 0 || y < 0 || x >= pos.Length || y >= pos.Length)
                continue;
            float c = Flat(pos[x], pos[y]) * (d.w != null && k < d.w.Length && d.w[k] >= 1f ? d.w[k] : 1f);
            links[x].Add((y, c));
            links[y].Add((x, c));
        }
        rests.Clear();
        if (d.rest != null)
            foreach (int r in d.rest)
                if (r >= 0 && r < pos.Length)
                    rests.Add(pos[r]);
        builtFor = map;
        return true;
    }

    /// <summary>사용자가 표시한 쉼터 (길 위 가장 가까운 점). 그래프가 없으면 빈 목록</summary>
    public static IReadOnlyList<Vector3> Rests(CampusMap map) => Ready(map) ? rests : (IReadOnlyList<Vector3>)Array.Empty<Vector3>();

    static float Flat(Vector3 p, Vector3 q) => new Vector2(p.x - q.x, p.z - q.z).magnitude;

    static int Nearest(Vector3 p)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < pos.Length; i++)
        {
            float dx = pos[i].x - p.x, dz = pos[i].z - p.z;
            float d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// a → b 를 길을 따라 잇는 점들 (a 와 b 포함). 그래프가 없으면 직선 [a, b].
    /// </summary>
    public static List<Vector3> Path(CampusMap map, Vector3 a, Vector3 b)
    {
        var line = new List<Vector3> { a };
        if (!Ready(map))
        {
            line.Add(b);
            return line;
        }
        int s = Nearest(a), t = Nearest(b);
        if (s < 0 || t < 0 || s == t)
        {
            line.Add(b);
            return line;
        }
        List<int> nodes = AStar(s, t);
        if (nodes == null)
        {
            line.Add(b);
            return line;
        }
        foreach (int n in nodes)
            line.Add(pos[n]);
        line.Add(b);
        return line;
    }

    /// <summary>여러 지점을 차례로 길을 따라 이은 한 줄 (중복 점 제거)</summary>
    public static List<Vector3> Route(CampusMap map, IList<Vector3> waypoints)
    {
        var line = new List<Vector3>();
        for (int i = 0; i + 1 < waypoints.Count; i++)
        {
            List<Vector3> seg = Path(map, waypoints[i], waypoints[i + 1]);
            for (int k = line.Count == 0 ? 0 : 1; k < seg.Count; k++)
                line.Add(seg[k]);
        }
        if (line.Count == 0 && waypoints.Count > 0)
            line.Add(waypoints[0]);
        return line;
    }

    /// <summary>점들을 이은 수평 길이 (m)</summary>
    public static float Length(IList<Vector3> line)
    {
        float m = 0f;
        for (int i = 0; i + 1 < line.Count; i++)
            m += Flat(line[i], line[i + 1]);
        return m;
    }

    static List<int> AStar(int s, int t)
    {
        int n = pos.Length;
        var g = new float[n];
        var prev = new int[n];
        var closed = new bool[n];
        for (int i = 0; i < n; i++) { g[i] = float.MaxValue; prev[i] = -1; }
        g[s] = 0f;
        var open = new SortedSet<(float f, int node)>(Comparer<(float f, int node)>.Create((x, y) =>
            x.f != y.f ? x.f.CompareTo(y.f) : x.node.CompareTo(y.node)));
        open.Add((Flat(pos[s], pos[t]), s));
        while (open.Count > 0)
        {
            (float _, int u) = open.Min;
            open.Remove(open.Min);
            if (u == t)
                break;
            if (closed[u])
                continue;
            closed[u] = true;
            foreach ((int v, float c) in links[u])
            {
                float ng = g[u] + c;
                if (ng >= g[v])
                    continue;
                if (g[v] != float.MaxValue)
                    open.Remove((g[v] + Flat(pos[v], pos[t]), v));
                g[v] = ng;
                prev[v] = u;
                open.Add((ng + Flat(pos[v], pos[t]), v));
            }
        }
        if (g[t] == float.MaxValue)
            return null;
        var path = new List<int>();
        for (int v = t; v != -1; v = prev[v])
            path.Add(v);
        path.Reverse();
        return path;
    }
}
