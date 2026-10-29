using System.Text.Json;
using KucaMemoServer.Models;

namespace KucaMemoServer.Services;

/// <summary>
/// 경희스팟 25곳 (Data/spots.json). 공지를 연결할 장소 후보다.
/// 앱의 KyungHeeSpots 목록과 같아야 한다 — 한쪽을 바꾸면 다른 쪽도 같이 바꾼다 (docs/api.md).
/// </summary>
public class SpotStore
{
    readonly Dictionary<string, Spot> byId;
    readonly Dictionary<(string, string), int> walkMeters = new();

    public IReadOnlyList<Spot> All { get; }

    public SpotStore(IWebHostEnvironment env)
    {
        string path = Path.Combine(env.ContentRootPath, "Data", "spots.json");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        All = JsonSerializer.Deserialize<List<Spot>>(File.ReadAllText(path), options) ?? new List<Spot>();
        byId = All.ToDictionary(s => s.Id);
        // 스팟 사이 실제 걷는 거리 (tools/route/build_walk_graph.py 가 앱 길 찾기 그래프로 만든다). 없으면 직선으로 어림
        string walk = Path.Combine(env.ContentRootPath, "Data", "walk_meters.json");
        if (File.Exists(walk))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(walk));
            string[] ids = doc.RootElement.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToArray();
            int i = 0;
            foreach (JsonElement row in doc.RootElement.GetProperty("meters").EnumerateArray())
            {
                int j = 0;
                foreach (JsonElement m in row.EnumerateArray())
                    walkMeters[(ids[i], ids[j++])] = m.GetInt32();
                i++;
            }
        }
    }

    /// <summary>두 스팟 사이 실제 걷는 거리 (m, 막힌 길은 돌아감). 표에 없으면 null</summary>
    public int? WalkMeters(string from, string to) => walkMeters.TryGetValue((from, to), out int m) ? m : null;

    public Spot? Find(string id) => byId.GetValueOrDefault(id);
}
