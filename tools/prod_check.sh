#!/usr/bin/env bash
# 운영 서버 점검: 어떤 키가 들어 있는지 · 기능별 AI 가 실제로 쓰이는지 · 배포된 코드가 최신인지.
#
#   ADMIN_KEY=<관리자 키> ./tools/prod_check.sh            읽기만 (GET)
#   ADMIN_KEY=<관리자 키> RUN_POST=1 ./tools/prod_check.sh  + 추천 3번 (운영 지표에 "추천 표시" 3건이 남음)
#
# 관리자 키는 저장소 · 채팅에 넣지 않는다. 환경 변수로만 넘긴다.
set -uo pipefail
BASE="${BASE:-https://kuca-memo.azurewebsites.net}"
KEY="${ADMIN_KEY:-}"
J() { python3 -c "import json,sys; d=json.load(sys.stdin); $1" 2>/dev/null || echo "  (읽지 못함)"; }

echo "== 서버 · 날씨"
curl -sS -m 70 "$BASE/api/health"; echo
curl -sS -m 30 "$BASE/api/weather" | J 'print("  날씨:", d.get("condition"), "출처:", d.get("source"))'

echo "== 배포된 코드 (wish 칸 = 10/26 PR #82 이후, talk 칸 = 10/27 PR #91 이후: 다시 입력 안내 · 메이트 대화 코스)"
curl -sS -m 30 "$BASE/swagger/v1/swagger.json" | J '
p=d["components"]["schemas"]["RecommendRequest"]["properties"]
print("  wish 칸:", "있음" if "wish" in p else "없음")
print("  talk 칸:", "있음 (최신)" if "talk" in p else "없음 → 병합한 코드가 아직 배포 안 됨 (server/deploy-azure.sh)")'

echo "== 공지 요약 (AI 요약인지, 본문 앞 100자 자르기인지)"
curl -sS -m 70 "$BASE/api/notices?limit=100" | J '
u={n["id"]:n for n in d}.values()
s=[n["summary"] for n in u]
cut=sum(1 for x in s if len(x)>=99 and x.endswith("…"))
print(f"  공지 {len(s)}건: 요약 없음 {sum(1 for x in s if not x)} · 본문 자르기(대체) {cut} · 그 밖(AI 일 수 있음) {sum(1 for x in s if x and not (len(x)>=99 and x.endswith(chr(8230))))}")'

if [ -z "$KEY" ]; then
  echo "== 관리자 점검은 건너뜀 (ADMIN_KEY 가 없음)"
  exit 0
fi

echo "== 키 설정 (/api/admin/health, 값은 안 보이고 있는지만)"
curl -sS -m 30 -H "X-Admin-Key: $KEY" "$BASE/api/admin/health" | J '
for k in ["aiKey","weatherKey","kakaoAppId","email","demoPartner","devLoginOff"]: print(f"  {k}: {d.get(k)}")
print("  최근 24시간 AI:")
for f,v in (d.get("aiLast24h") or {}).items(): print(f"    {f}: AI {v.get("ai")} · 대체 {v.get("fallback")} · 대체 비율 {v.get("fallbackRate")}")'

echo "== 최근 7일 지표 (/api/admin/stats)"
curl -sS -m 30 -H "X-Admin-Key: $KEY" "$BASE/api/admin/stats?days=7" | J '
for f,v in d["ai"].items(): print(f"  AI {f}: AI {v["ai"]} · 대체 {v["fallback"]} · 대체 비율 {v["fallbackRate"]}")
for s,v in d["recommendations"].items(): print(f"  추천 {s}: 표시 {v["shown"]} · 시작 {v["started"]} · 완료 {v["completed"]} · 별점 {v["avgCourseRating"]}")
m=d["matching"]; print(f"  매칭: 요청 {m["requests"]} · 제안 {m["proposed"]} · 만남 {m["met"]} · 완료 {m["completed"]}")'

if [ "${RUN_POST:-}" = "1" ]; then
  echo "== 추천 실제로 받아 보기 (운영 지표에 남음)"
  for w in "" "비 와서 실내로 30분" "다리 아파서 천천히, 사람 많은 곳 말고 도서관은 빼고"; do
    body=$(python3 -c "import json,sys; print(json.dumps({'minutes':30,'tastes':[],'lat':37.2440,'lon':127.0800,'wish':sys.argv[1] or None}))" "$w")
    echo "  -- 바람: ${w:-(없음)}"
    curl -sS -m 90 -X POST "$BASE/api/recommendations" -H "Content-Type: application/json" -d "$body" | J '
print("    출처:", d.get("source"), "| 제목:", d.get("title"))
w=d.get("wish")
if w: print("    해석:", w.get("source"), "|", w.get("summary"))
for s in d.get("stops",[]): print("    -", s["name"], ":", s["reason"])'
  done
fi
