# 운영 서버 AI 점검 — 관리자 키를 가진 B 에게 (10/26)

> A 가 만든 점검 요청서. 이 파일 전체를 Claude 에게 프롬프트로 넘기면 된다.
> **관리자 키 · AI 키는 채팅 · 저장소 · PR · 커밋 메시지 어디에도 쓰지 말 것.** 터미널 환경 변수로만 쓴다.

## 배경

- 팀 main 에 PR #82 가 병합됐다. 들어간 기능:
  - 말로 하는 산책 요청 (`POST /api/recommendations` 의 `wish`)
  - 공지의 출입 통제 · 행사를 추천에 반영
  - 쪽지 · 리뷰 · 사진으로 본 장소의 요즘 상태
  - 내용 검사 AI 의 사진 설명
- A 가 운영 서버(`https://kuca-memo.azurewebsites.net`)를 읽기만 해서 본 것:
  1. **새 코드가 배포되지 않았다.** swagger 의 `RecommendRequest` 에 `wish` 칸이 없다.
  2. **공지 AI 요약이 안 되는 것으로 보인다.**
     - 공지 36건 중 요약이 있는 5건이 모두 대체 경로 모양이다 ("본문 앞 100자 + …").
     - 글이 있는 도서관 공지도 AI 요약이 0건이다.
     - 내용 검사 · 공지 분류가 쓰는 키(`Ai__ApiKey` → 없으면 `Recommend__ApiKey`)가 없거나, 모델 `claude-haiku-5-5` 가 게이트웨이에서 안 되는 것 같다.
  3. **추천 계열 AI 가 조용히 꺼져 있을 수 있다.**
     - 추천 · 말로 요청 해석 · 장소 상태 · 대화 주제 · 매칭 이유는 `ChatKuClient` 로 `gpt-5.4-mini` 를 부르고, 요청에 `temperature: 0.7` 이 들어간다.
     - GPT-5 계열이 이 값을 거부(400)하면 경고 없이 항상 "기본 추천" 으로 나간다.
- 날씨(기상청 키)는 운영에서 정상이다.

## 연동된 키 (Azure 앱 설정, 저장소에는 없음)

| 설정 이름 | 쓰는 곳 | 비고 |
| --- | --- | --- |
| `Recommend__ApiKey` | 추천 · 말로 요청 해석 · 장소 요즘 상태 · 대화 주제 · 매칭 이유 · 짝 고르기 | FactChat 게이트웨이, 모델 `Recommend:Model` = `gpt-5.4-mini` (appsettings.json) |
| `Ai__ApiKey` (선택) | 쪽지 · 댓글 내용 검사 + 사진 설명, 공지 요약 · 통제/행사 분류 | 없으면 `Recommend__ApiKey` 를 씀. 모델 기본 `claude-haiku-5-5` (`Ai__FilterModel` · `Ai__NoticeModel` 로 바꿀 수 있음) |
| `Kma__ServiceKey` | 날씨 | 정상 |
| `Kakao__AppId`, 메일 설정 | 로그인 · 학교 메일 인증 | |
| `Admin__Key` | 관리자 API (`X-Admin-Key` 헤더) | |

## 해 줄 일 (순서대로)

### 1. 배포 확인 · 배포
1. 팀 main 최신을 받는다.
   - A 의 후속 PR(점검 스크립트 `tools/prod_check.sh` · 관리자 지표에 `wish` · `place_state` 추가 · 이 문서)이 병합된 상태여야 한다.
   - 아직이면 스크립트만 받는다: `git fetch https://github.com/Dopal-Kim/KUCA claude/dreamy-wozniak-9q95up && git checkout FETCH_HEAD -- tools/prod_check.sh`
2. 읽기 점검: `ADMIN_KEY=<관리자 키> ./tools/prod_check.sh`
   - "wish 칸: 없음" 이면 배포가 안 된 것이다.
3. 배포: `cd server && ./deploy-azure.sh`
   - Azure 에 `az login` 이 필요하다. `kuca-rg` 리소스 그룹 권한이 없으면 C 에게 부탁한다.
4. 배포 뒤 다시 `./tools/prod_check.sh` 를 돌려 "talk 칸: 있음 (최신)" 인지 본다 (10/27 PR #91 이후 기준).

### 2. 키 · AI 사용률
`ADMIN_KEY=<관리자 키> ./tools/prod_check.sh` 출력에서:
- `aiKey: False` 면 키가 없는 것이다. Azure 포털 › kuca-memo › 환경 변수에 `Recommend__ApiKey` 를 넣고 재시작한다.
- 기능별 "대체 비율"이 0.5 이상이면 AI 가 실패하고 있다. 아래 3번에서 원인을 찾는다.
- 대상 기능: `recommend` · `wish` · `place_state` · `smalltalk` · `moderation` · `match_reason` · `match_pick`.
- 호출이 0건이면 아직 아무도 안 쓴 것이다 (실패가 아니다).

### 3. AI 가 실패하면 원인 찾기
1. 로그 보기: `az webapp log tail -n kuca-memo -g kuca-rg`
   - 그동안 앱이나 아래 4번 시험으로 추천을 한 번 받는다.
2. 로그 문구별로 고친다:
   - **`챗쿠 응답 오류 400` + temperature 관련 문구:**
     - `server/KucaMemoServer/Services/ChatKuClient.cs` 의 `BuildBody` 에서 `["temperature"] = 0.7` 줄을 뺀다.
     - `cd server && dotnet test KucaMemoServer.Tests` 가 통과하면 PR 을 올린다 (C 확인). 그다음 다시 배포한다.
   - **`챗쿠 응답 오류 401/403`:** 키가 틀렸거나 만료된 것이다. C 에게 새 키를 받는다.
   - **`AI 검사 실패 (4xx)` · `공지 판별 AI 실패`:**
     - 모델 `claude-haiku-5-5` 가 게이트웨이에 없을 수 있다.
     - Azure 앱 설정에 `Ai__FilterModel=gpt-5.4-mini`, `Ai__NoticeModel=gpt-5.4-mini` 를 넣고 재시작한다. 또는 게이트웨이에서 되는 모델 이름을 C 에게 확인한다.
   - **`AI 검사 시간 초과`:** F1 요금제가 느린 것이다. `Ai__TimeoutSeconds=25`.

### 4. 기능 시험 (운영에 기록이 남으니 아래만)

**말로 하는 산책 요청 · 추천:** `ADMIN_KEY=<관리자 키> RUN_POST=1 ./tools/prod_check.sh`
- 추천을 3번 받는다. 운영 지표에 "추천 표시" 3건이 남는다.
- 기대값:

| 바람 | 기대 |
| --- | --- |
| (없음) | `출처: ai`, 이유가 장소 소개와 맞음 |
| 비 와서 실내로 30분 | `해석: ai`, 요약에 "30분 · 실내 위주", 들를 곳이 실내 건물 위주 |
| 다리 아파서 천천히, 사람 많은 곳 말고 도서관은 빼고 | `해석: ai`, 요약에 "천천히 · 붐빔 … · 중앙도서관 피하기", 중앙도서관 없음 |

- `출처: fallback` 이나 `해석: fallback` 이면 AI 가 실패한 것이다 (3번).

**공지 통제 · 행사 분류 (시험 공지를 넣고 바로 지운다):**
```bash
curl -s -X POST https://kuca-memo.azurewebsites.net/api/admin/notices -H "X-Admin-Key: $ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"title":"[시험] 체육대학관 보수공사로 출입 통제 안내","body":"11월 3일부터 11월 7일까지 체육대학관 출입이 통제됩니다. 이용에 참고 바랍니다.","url":"https://example.com/kuca-test-1"}'
```
- 기대: `"source":"ai"`, `"kind":"closure"`, `"startsOn":"2026-11-03"`, `"endsOn":"2026-11-07"`, `buildingIds` 에 체육대학관(`way-455726113`). 요약은 짧게 다시 쓴 문장이어야 한다 (본문 복사가 아님).
- 받은 `id` 로 바로 지운다: `curl -s -X DELETE https://kuca-memo.azurewebsites.net/api/admin/notices/<id> -H "X-Admin-Key: $ADMIN_KEY"`.

**쪽지 내용 검사 + 사진 설명 (시험 쪽지를 넣고 바로 지운다):**
- 캠퍼스 풍경 사진 한 장(jpg, 사람 얼굴 없는 것)으로:
```bash
curl -s -X POST https://kuca-memo.azurewebsites.net/api/buildings/way-474531805/memos \
  -F author=점검 -F text="[시험] 노천극장 단풍 확인" -F deviceId=b-check -F photo=@campus.jpg
```
- 기대: `"status":"visible"`. AI 가 사진을 봤다는 뜻이다. `pending` 이면 AI 검사가 실패한 것이다 (3번).
- 지운다: `curl -s -X DELETE https://kuca-memo.azurewebsites.net/api/admin/memos/<id> -H "X-Admin-Key: $ADMIN_KEY"`.
- 사진 설명(`photo_caption`)은 API 응답에 나오지 않는다. 확인은 `moderation` 대체 비율로 대신한다.

**장소 요즘 상태:** 확인할 API 가 없다.
- 최근 14일 공개 쪽지가 있으면, 배포 뒤 첫 추천 30분 안에 `prod_check.sh` 의 `place_state` 가 `AI 1` 로 올라가는지 본다.
- 쪽지가 하나도 없으면 호출되지 않는다 (정상).

### 5. 폰에서 (TestFlight · APK 새 빌드로)
- **산책 추천 시트:** 맨 위 "어떤 산책을 하고 싶어요?" 칸에 "그늘지고 조용한 데 30분, 오르막은 빼고" 를 적고 추천을 받는다.
  - 코스 결과에 "이렇게 이해했어요 (AI) · …" 줄이 나오는지 본다.
  - 키보드 마이크로 말해서 넣어도 되는지 본다.
- **산책 중 "끝내기":** 확인 창이 뜨는지 본다.
- **꾸미기:** 고르지 않은 색 칩도 눌리는지 본다.
- **산책 완료 화면:** "카카오톡으로 자랑하기" 로 공유가 되는지 본다.

## 결과 보고 (A 에게)

| 항목 | 결과 (정상 / 대체 / 실패) | 근거 (출력 · 로그 한 줄) |
| --- | --- | --- |
| 배포 버전 (wish 칸) | | |
| aiKey · weatherKey · kakaoAppId · email | | |
| recommend · wish · place_state 대체 비율 | | |
| smalltalk · match_reason · match_pick 대체 비율 | | |
| moderation 대체 비율 · 시험 쪽지 status | | |
| 시험 공지 source · kind · 날짜 · 요약 | | |
| 바람 3개 추천 결과 | | |
| 폰 4가지 | | |
| 고친 것 (temperature · 모델 설정 등)과 PR 링크 | | |

키 값 · 관리자 키는 보고에도 쓰지 않는다.
