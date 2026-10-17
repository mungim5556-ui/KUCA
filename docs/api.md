# KUCA 장소 메모 API 명세

앱(Unity)과 메모 서버(`server/`)가 지키는 약속입니다. **이 문서를 바꿀 때는 앱 담당과 서버 담당이 함께 확인합니다.**

- 개발용 주소: `http://localhost:5080` (같은 와이파이의 폰에서는 `http://<맥/PC의 IP>:5080`)
- 요청과 응답 본문은 JSON(UTF-8), 필드 이름은 camelCase
- 시각은 ISO 8601 UTC 문자열 (예: `"2026-10-06T05:12:30Z"`)
- 실패하면 `{"error": "사람이 읽을 수 있는 이유"}` 와 함께 아래 상태 코드를 돌려줍니다

| 코드 | 뜻 |
| --- | --- |
| 200 | 성공 |
| 201 | 만들어짐 (메모·댓글 작성) |
| 204 | 성공, 본문 없음 (삭제) |
| 400 | 요청 값이 규칙에 맞지 않음 |
| 403 | 권한 없음 (남의 메모·댓글 삭제) |
| 404 | 건물·메모·댓글이 없음 |
| 422 | 내용 검사에서 거절됨 (아래 "내용 검사") |

## 사용자 구분 (기기 ID)

- 로그인은 없습니다. 앱이 기기마다 한 번 만들어 저장해 둔 **기기 ID** 로 "누가 했는지" 를 구분합니다 (메모·댓글 삭제 권한, 좋아요 중복 방지).
- 화면에는 기기 ID 대신 사용자가 입력한 **닉네임**(`author`)을 보여 줍니다.
- 기기 ID 는 서버에만 저장하고 **응답에는 넣지 않습니다**.
- 기기 ID 를 보내는 곳: 작성 본문의 `deviceId` 필드, 그 밖의 요청은 `X-Device-Id` 헤더 (1~64자).

## 건물 ID

- OpenStreetMap 종류와 번호를 이은 문자열입니다: `way-455725718`(공학관), `relation-8269760`(우정원)
- 전체 목록은 `server/KucaMemoServer/Data/buildings.json` (632개, 앱의 3D 건물과 같음)
- 이 목록에 없는 ID로 메모를 쓰거나 읽으면 404

## 데이터 모양

### Building

```json
{ "id": "way-455725718", "name": "공학관", "type": "yes", "lat": 37.2463611, "lon": 127.0805898 }
```

`name`, `type` 은 없으면 `null`.

### Memo

```json
{
  "id": "6f1c2e0a-3b7d-4c55-9a1e-2f8b9d0c7e41",
  "buildingId": "way-455725718",
  "author": "원준",
  "text": "공학관 1층 자판기 고장났어요",
  "photoUrl": "/photos/6f1c2e0a-3b7d-4c55-9a1e-2f8b9d0c7e41.jpg",
  "createdAt": "2026-10-06T05:12:30Z",
  "likeCount": 3,
  "commentCount": 2,
  "likedByMe": false,
  "status": "visible"
}
```

- `id`: 서버가 만드는 Guid 문자열
- `photoUrl`: 서버 기준 경로. 사진이 없으면 `null`. 앱은 `서버 주소 + photoUrl` 로 이미지를 받습니다
- 작성 기기 ID(`deviceId`)는 서버에만 저장하고 **응답에는 넣지 않습니다**
- `likeCount`: 좋아요 수, `commentCount`: 댓글 수
- `likedByMe`: 요청에 `X-Device-Id` 헤더가 있고 그 기기가 좋아요를 눌렀으면 `true`. 헤더가 없으면 항상 `false`
- `status`: `"visible"`(모두에게 보임) 또는 `"pending"`(검토 대기, 아래 "내용 검사")
- `commentCount` 는 `visible` 댓글만 셉니다

### Comment

```json
{
  "id": "0b8e2d4c-7a1f-4e3b-9c55-1d2e3f4a5b6c",
  "memoId": "6f1c2e0a-3b7d-4c55-9a1e-2f8b9d0c7e41",
  "author": "민지",
  "text": "저도 봤어요! 2층 자판기는 돼요",
  "createdAt": "2026-10-06T06:01:10Z",
  "status": "visible"
}
```

- 댓글은 글만 씁니다 (사진 없음)
- 작성 기기 ID 는 메모와 같이 응답에 넣지 않습니다
- `status`: Memo 와 같음

## 내용 검사 (AI 필터)

메모(글·사진)와 댓글(글)을 저장하기 전에 서버가 AI 로 검사합니다. 앱은 따로 할 일이 없고, 결과에 따라 응답만 다르게 처리합니다.

**거절하는 내용**

| `reason` | 내용 |
| --- | --- |
| `abusive` | 욕설·비하·혐오·성적인 내용 (사진 포함) |
| `personal_info` | 전화번호·이메일·학번 같은 개인정보, 실명을 지목한 비방 (사진 속 개인정보 포함) |
| `advertising` | 광고·홍보·판매·외부 링크 홍보 |

**검사 결과별 응답**

| 결과 | 응답 | 앱이 할 일 |
| --- | --- | --- |
| 통과 | `201`, `status: "visible"` | 평소처럼 목록에 넣기 |
| 거절 | `422 {"error": "사용자에게 보여 줄 문구", "reason": "personal_info"}` · 저장 안 함 | `error` 문구를 그대로 보여 주고, 사용자가 고쳐서 다시 쓸 수 있게 입력 내용 유지 |
| 애매함 | `201`, `status: "pending"` · 저장하지만 **다른 사람에게는 안 보임** | "검토 후 공개돼요" 안내. 내 목록에는 보임 |

- `error` 예: `"전화번호 같은 개인정보가 있어 올릴 수 없어요."`
- **검토 대기(`pending`)** 메모·댓글은 작성한 기기(`X-Device-Id` 가 같은 요청)에만 목록·조회에 나오고, 다른 기기에는 없는 것처럼(목록에서 빠짐, 단건 `404`) 처리됩니다. 좋아요·댓글은 `visible` 메모에만 달 수 있습니다.
- 관리자가 검토해서 공개하면 `visible` 이 되고, 거절하면 삭제됩니다 (아래 "관리자 API").
- **AI 가 응답하지 않거나 느리면**(시간 초과) 금지어 목록과 전화번호·이메일 패턴으로 대신 검사합니다. 걸리면 거절, 아니면 통과입니다. 이때 사진은 검사하지 않습니다. 그래서 AI 장애가 있어도 작성은 막히지 않습니다.

## API

### GET /api/health

서버 동작 확인. `200 {"status":"ok"}`

### GET /api/buildings

건물 목록. `?named=true` 면 이름 있는 건물만. `200 Building[]`

### GET /api/buildings/{buildingId}

건물 하나. `200 Building` / `404`

### GET /api/buildings/{buildingId}/memos

건물의 메모 목록, **최신순**.

| 쿼리 | 기본값 | 설명 |
| --- | --- | --- |
| `limit` | 20 | 1~50. 범위를 벗어나면 가까운 값으로 맞춤 |
| `before` | 없음 | 이 시각보다 이전 메모만 (다음 페이지 불러오기용, 마지막 메모의 `createdAt` 을 넣음) |

헤더 `X-Device-Id` 를 보내면 각 메모의 `likedByMe` 가 채워지고, 그 기기가 쓴 검토 대기 메모도 함께 나옵니다 (선택).

`200 Memo[]` (메모가 없으면 `[]`) / `404` 없는 건물

### POST /api/buildings/{buildingId}/memos

메모 작성. 본문은 **multipart/form-data** 입니다 (사진 파일을 같이 보내야 하므로).

| 필드 | 필수 | 규칙 |
| --- | --- | --- |
| `author` | 예 | 앞뒤 공백 제거 후 1~20자 |
| `text` | 예 | 앞뒤 공백 제거 후 1~500자 |
| `deviceId` | 예 | 1~64자. 앱이 기기마다 한 번 만들어 저장해 둔 값 (삭제 권한 확인용) |
| `photo` | 아니오 | JPEG 또는 PNG, 10MB 이하 |

- 성공: `201 Memo`, `Location: /api/memos/{id}` 헤더. 검토 대기면 `status: "pending"`
- 실패: `400` (규칙 위반, 어떤 필드가 왜 틀렸는지 `error` 에), `404` 없는 건물, `422` 내용 검사 거절 (`error`, `reason`)
- 글과 사진을 함께 검사합니다 (위 "내용 검사")
- 사진은 `wwwroot/photos/{메모 id}.{jpg|png}` 로 저장하고 `photoUrl` 을 `/photos/{파일 이름}` 으로 채웁니다

### GET /api/memos/{memoId}

메모 하나. 헤더 `X-Device-Id` 를 보내면 `likedByMe` 가 채워집니다 (선택). `200 Memo` / `404` (없거나, 남이 쓴 검토 대기 메모)

### DELETE /api/memos/{memoId}

메모 삭제. 헤더 `X-Device-Id` 가 작성할 때의 `deviceId` 와 같아야 합니다.

- `204` 삭제됨 (사진 파일, 좋아요, 댓글도 함께 지움)
- `403` 기기 ID가 다름, `404` 없는 메모

### PUT /api/memos/{memoId}/like

좋아요 누르기. 헤더 `X-Device-Id` 필수. 한 기기는 한 메모에 좋아요를 한 번만 셀 수 있고, 이미 눌렀으면 그대로 둡니다 (여러 번 보내도 결과가 같음).

- `200 {"likeCount": 4, "likedByMe": true}`
- `400` `X-Device-Id` 없음·64자 초과, `404` 없는 메모

### DELETE /api/memos/{memoId}/like

좋아요 취소. 헤더 `X-Device-Id` 필수. 누른 적이 없어도 성공으로 봅니다.

- `200 {"likeCount": 3, "likedByMe": false}`
- `400` `X-Device-Id` 없음·64자 초과, `404` 없는 메모

> 앱은 응답의 `likeCount`·`likedByMe` 로 화면을 맞춥니다. 화면을 먼저 바꿨다면(빠른 반응) 실패 시 되돌립니다.

### GET /api/memos/{memoId}/comments

메모의 댓글 목록, **오래된 순** (대화처럼 위에서 아래로).

| 쿼리 | 기본값 | 설명 |
| --- | --- | --- |
| `limit` | 50 | 1~100. 범위를 벗어나면 가까운 값으로 맞춤 |
| `after` | 없음 | 이 시각보다 나중 댓글만 (다음 페이지, 마지막 댓글의 `createdAt` 을 넣음) |

헤더 `X-Device-Id` 를 보내면 그 기기가 쓴 검토 대기 댓글도 함께 나옵니다 (선택).

`200 Comment[]` (댓글이 없으면 `[]`) / `404` 없는 메모

### POST /api/memos/{memoId}/comments

댓글 작성. 본문은 **JSON** 입니다 (사진이 없으므로).

```json
{ "author": "민지", "text": "저도 봤어요!", "deviceId": "기기 ID" }
```

| 필드 | 필수 | 규칙 |
| --- | --- | --- |
| `author` | 예 | 앞뒤 공백 제거 후 1~20자 |
| `text` | 예 | 앞뒤 공백 제거 후 1~200자 |
| `deviceId` | 예 | 1~64자 |

- 성공: `201 Comment`, `Location: /api/comments/{id}` 헤더. 검토 대기면 `status: "pending"`
- 실패: `400` (규칙 위반, 어떤 필드가 왜 틀렸는지 `error` 에), `404` 없는 메모, `422` 내용 검사 거절 (`error`, `reason`)

### DELETE /api/comments/{commentId}

댓글 삭제. **댓글을 쓴 기기만** 지울 수 있습니다 (헤더 `X-Device-Id` 가 작성할 때의 `deviceId` 와 같아야 함).

- `204` 삭제됨
- `403` 기기 ID가 다름, `404` 없는 댓글

### GET /photos/{fileName}

업로드된 사진 파일 (정적 파일).

### GET /api/weather

캠퍼스 지금 날씨. 앱의 캐릭터 출현 조건(`SpawnConditions`)이 씁니다. 서버가 기상청 단기예보 조회서비스
(초단기실황 강수형태 PTY + 초단기예보 하늘상태 SKY, 격자 62·120 = 국제캠퍼스)를 대신 불러 10분 동안 재사용합니다.

```json
{ "condition": "rain", "observedAt": "2026-10-09T05:12:30Z", "source": "KMA" }
```

- `condition`: `clear` 맑음 · `cloudy` 구름많음·흐림 · `rain` 비·빗방울·비/눈 · `snow` 눈·눈날림
- `503` 서버에 기상청 키(`Kma:ServiceKey`)가 없음, `502` 기상청 응답을 못 받았고 이전 값도 없음
- 앱은 실패하면 날씨를 모름으로 두고 날씨 조건 없이 진행합니다

## 산책 추천 · 장소 공지 (B-4 제안, 서버는 C 구현 예정)

> **상태**
> - 산책 추천: 서버 구현 완료 (챗쿠 AI. 키가 없으면 규칙 기반).
> - 장소 공지: 명세 확정 (C-4, 아래 "GET /api/notices" 와 "관리자 API › 공지 넣기").
> - 서버에 아직 없으면(`404`·연결 실패) 앱이 기기 안 기본 추천과 예시 공지로 대신합니다.
> - 모양을 바꿔야 하면 이 절을 고치는 PR 로 B 와 합의해 주세요.

### POST /api/recommendations

걸을 시간·취향·지금 위치를 받아 AI 가 경희스팟 산책 코스를 짭니다. 본문은 **JSON** 입니다.

```json
{
  "minutes": 30,
  "tastes": ["photo", "unvisited"],
  "lat": 37.2440, "lon": 127.0800,
  "visitedBuildingIds": ["way-474085534"],
  "candidateBuildingIds": ["way-473963422", "way-474085534", "way-474085535"]
}
```

| 필드 | 설명 |
| --- | --- |
| `minutes` | 15, 30, 60 중 하나 |
| `tastes` | 0개 이상. `photo`(사진 명소) · `quiet`(조용한 곳) · `food`(맛집·카페) · `unvisited`(안 가 본 곳) |
| `lat`, `lon` | 지금 위치 (GPS). 캠퍼스 밖이면 정문 기준으로 봐도 됨 |
| `visitedBuildingIds` | 이미 가 본 경희스팟 건물 ID (`unvisited` 판단용) |
| `candidateBuildingIds` | 추천 후보 (앱의 경희스팟 건물 ID). 서버는 이 안에서만 고릅니다. 비우면 지금 위치 700m 안의 이름 있는 건물 |

`200`

```json
{
  "title": "노을 보며 걷는 30분 코스",
  "source": "ai",
  "stops": [
    { "buildingId": "way-474085534", "name": "중앙도서관", "reason": "창가 자리에서 노을이 잘 보여요", "walkMinutes": 6, "lat": 37.2421, "lon": 127.0796 }
  ]
}
```

- `stops`: 들를 순서대로 2~5곳. `walkMinutes` 는 앞 장소(첫 곳은 지금 위치)에서 걸어서 몇 분. `reason` 은 한 문장
- `source`: `ai` 또는 `fallback` (AI 가 실패해서 서버가 규칙으로 고른 경우)
- 경희스팟이 아닌 곳을 추천하면 `lat`/`lon` 으로 지도에 표시합니다
- 실패 시 `400` (필드 오류). AI 가 실패하거나 엉뚱한 답을 주면 `200` 에 `source: "fallback"` (가까운 곳부터 고름)
- 후보는 지금 위치에서 가까운 30곳까지 AI 에게 줍니다. AI 가 후보에 없는 ID 를 주면 버립니다
- **AI 설정 (서버)** — 챗쿠(BAZE 게이트웨이, 기본 모델 `gpt-5.4-mini`). 키는 저장소에 넣지 않고 Azure 앱 설정 `Recommend__ApiKey` 에만 넣습니다 (로컬은 `dotnet user-secrets`). 키가 없으면 규칙 기반으로만 추천합니다. 자세한 것은 `server/README.md`

### GET /api/notices

학교 공지 중 특정 장소와 연결된 것, **최신순**. `?buildingId=` 를 주면 그 장소 것만.

`200 PlaceNotice[]`

```json
[
  {
    "id": "khu-2026-1008-01",
    "buildingId": "way-474085534",
    "buildingName": "중앙도서관",
    "title": "중간고사 기간 열람실 연장 운영",
    "summary": "시험 기간 동안 제1열람실을 밤 12시까지 엽니다.",
    "url": "https://www.khu.ac.kr/...",
    "postedAt": "2026-10-08"
  }
]
```

| 쿼리 | 기본값 | 설명 |
| --- | --- | --- |
| `buildingId` | 없음 | 이 장소 공지만 |
| `limit` | 20 | 1~50. 범위를 벗어나면 가까운 값으로 맞춤 |

- 장소와 연결되지 않은 공지는 넣지 않습니다
- 공지 ↔ 장소 연결은 서버(AI 판별)가 정합니다. 연결 후보는 **경희스팟 25곳**뿐입니다 (`server/KucaMemoServer/Data/spots.json`, 앱의 `KyungHeeSpots` 목록과 같아야 함)
- 한 공지가 여러 장소에 해당하면 **장소마다 한 줄씩** 나옵니다 (`id` 는 같고 `buildingId` 가 다름). 한 공지당 최대 3곳
- `summary` 는 AI 가 쓴 한두 문장 요약(100자 이내)입니다. AI 가 실패하면 본문 앞부분입니다
- `url` 은 원문 링크입니다 (항상 있음). 앱은 누르면 브라우저로 엽니다
- `postedAt` 은 공지 게시일 `YYYY-MM-DD`

## 로그인 · 계정

앱은 **로그인 필수**입니다 (또는 체험 모드). 흐름은 다음과 같습니다.
1. 카카오 또는 Apple 로 로그인 → KUCA 로그인 토큰을 받는다. 체험 모드는 `POST /api/auth/guest`.
2. 경희대 메일(@khu.ac.kr) 인증. 체험 계정은 건너뜀.
3. 닉네임. 체험 계정은 서버가 `체험생-1234` 로 정해 줌.
4. 성별 (필수).
5. 산책 취향 체크리스트.

이후 요청에는 헤더 `Authorization: Bearer {토큰}` 를 붙입니다. 토큰은 90일 (체험 계정은 7일) 동안 쓸 수 있습니다.

### Account

```json
{
  "id": "3f0c…", "provider": "kakao", "nickname": "원준",
  "khuEmail": "wonjun@khu.ac.kr", "emailVerified": true,
  "preferencesJson": "{\"walkTimes\":[\"evening\"],…}",
  "createdAt": "2026-10-13T12:00:00Z", "gender": "male",
  "isGuest": false, "ready": true
}
```

- `ready`: 메일 인증 (체험 계정은 면제) · 닉네임 · 성별이 모두 끝났으면 `true`. `false` 면 앱은 남은 단계를 보여 줍니다.
- `gender`: `"male"` · `"female"` · 아직 없으면 `null`. 매칭 "동성만" 조건에만 쓰고 **다른 사람에게 보여 주지 않습니다.**
- `isGuest`: 체험 계정이면 `true`.
- `preferencesJson`: 앱의 `WalkPreferences` JSON 원문입니다 (AI 산책메이트 매칭용).

### POST /api/auth/kakao · POST /api/auth/apple

- 카카오 본문: `{ "accessToken": "카카오 SDK 의 액세스 토큰" }`
  - 서버가 카카오에 토큰을 확인합니다. 앱 ID 가 설정 `Kakao:AppId` 와 같아야 합니다.
- Apple 본문: `{ "identityToken": "Sign in with Apple 의 JWT" }`
  - 서버가 Apple 공개키로 서명을 확인하고, `aud` 가 번들 ID 인지 봅니다.
- 응답 `200 { "token": "…", "user": Account }`. 처음이면 계정을 새로 만듭니다.
- 실패: `400` 본문 없음, `401` 확인 실패, `503` 카카오 설정 없음.

### POST /api/auth/guest (체험 모드)

- 본문 없음. 응답 `200 { "token": "…", "user": Account }` (`provider: "guest"`, `isGuest: true`, 닉네임 `체험생-1234`).
- 메일 인증 없이 성별만 넣으면 `ready: true` 가 되어 모든 기능을 쓸 수 있습니다.
- 만든 지 **7일**이 지나면 계정과 토큰이 지워집니다 (`AccountStore.GuestLifetime`).

### GET /api/me · PUT /api/me/nickname · PUT /api/me/gender · PUT /api/me/preferences · DELETE /api/me · POST /api/auth/logout

- `GET /api/me`: 내 계정. 토큰이 없거나 만료면 `401`.
- `PUT /api/me/nickname`: `{ "nickname": "1~20자" }`
- `PUT /api/me/gender`: `{ "gender": "male" | "female" }`. 그 밖의 값은 `400`.
- `DELETE /api/me`: 회원 탈퇴. 계정 · 토큰 · 인증번호를 지웁니다 (`204`). 같은 카카오 계정으로 다시 로그인하면 새 계정입니다.
- `PUT /api/me/preferences`: 본문은 `WalkPreferences` JSON 그대로 (4KB 이하).
- `POST /api/auth/logout`: 토큰을 지웁니다 (`204`).

### POST /api/me/email/start · POST /api/me/email/verify

- `start` 본문 `{ "email": "…@khu.ac.kr" }`: 6자리 인증번호를 메일로 보냅니다. 10분 동안 쓸 수 있습니다.
  - `400` 경희대 메일이 아님, `409` 다른 계정이 이미 인증한 메일, `429` 60초 안에 다시 요청.
  - `503`/`502` 메일 설정 없음/보내기 실패.
- `verify` 본문 `{ "code": "123456" }`: 맞으면 `200 Account` (`emailVerified: true`).
  - 5번 틀리면 `429` 이고, 인증번호를 다시 받아야 합니다.

### 서버 설정 (키는 저장소에 넣지 않음)

| 설정 | 뜻 |
| --- | --- |
| `Kakao:AppId` | 카카오 개발자 콘솔의 "앱 ID" (숫자) |
| `Apple:BundleId` | 기본 `com.mungim.kuca` |
| `Email:ConnectionString` / `Email:From` | 인증 메일을 보낼 **Azure Communication Services** 연결 문자열과 보내는 주소 (있으면 이것을 씀) |
| `Email:User` / `Email:Password` | (대체) Gmail 주소와 **앱 비밀번호** |
| `Auth:DevLogin`, `Email:DevEcho` | 로컬 테스트 전용. 앞의 것은 `POST /api/auth/dev` 를 켜고, 뒤의 것은 메일 대신 응답에 번호를 넣습니다. **Azure 에서는 켜지 않습니다** |

## 관리자 API (검토 대기 처리, 앱과 무관)

운영자가 검토 대기 메모·댓글을 공개하거나 지웁니다. 헤더 `X-Admin-Key` 가 서버 설정 `Admin:Key` 와 같아야 하고, 다르면 `403` 입니다. 서버에 `Admin:Key` 가 설정되지 않았으면 관리자 API 는 꺼져 있습니다 (`404`).

| 요청 | 하는 일 | 응답 |
| --- | --- | --- |
| `GET /api/admin/pending` | 검토 대기 목록 (오래된 순) | `200 {"memos": PendingMemo[], "comments": PendingComment[]}` |
| `POST /api/admin/memos/{id}/approve` | 메모 공개 | `200 Memo` (`status: "visible"`) |
| `DELETE /api/admin/memos/{id}` | 메모 거절 (사진·좋아요·댓글과 함께 삭제) | `204` |
| `POST /api/admin/comments/{id}/approve` | 댓글 공개 | `200 Comment` |
| `DELETE /api/admin/comments/{id}` | 댓글 거절 (삭제) | `204` |

`PendingMemo`·`PendingComment` 는 Memo·Comment 에 검사 메모 `flagNote`(AI 가 애매하다고 본 이유) 가 더해진 모양입니다.

### 공지 넣기 (C-4)

운영자가 학교 공지를 붙여 넣으면 서버가 AI 로 **관련 경희스팟**과 **짧은 요약**을 붙여 저장합니다. 앱은 `GET /api/notices` 로 받아 갑니다.

| 요청 | 하는 일 | 응답 |
| --- | --- | --- |
| `POST /api/admin/notices` | 공지 넣기 (아래 본문) | `201` 저장됨 / `200` 관련 장소가 없어 버림 |
| `DELETE /api/admin/notices/{id}` | 공지 지우기 (모든 장소에서) | `204` / `404` |

```json
{ "title": "중간고사 기간 열람실 연장 운영", "body": "공지 본문 전체…", "url": "https://www.khu.ac.kr/...", "postedAt": "2026-10-08" }
```

| 필드 | 필수 | 규칙 |
| --- | --- | --- |
| `title` | 예 | 1~200자 |
| `body` | 예 | 1~5000자 (원문 본문을 붙여 넣음) |
| `url` | 예 | `http://` 또는 `https://` 로 시작하는 원문 링크 (품질 관리: 원문 링크 필수) |
| `postedAt` | 아니오 | `YYYY-MM-DD`. 없으면 넣은 날(한국 시간) |

- `201`: `{"id": "...", "buildingIds": ["way-474085534"], "summary": "...", "source": "ai"}` — 저장됨
- `200`: `{"saved": false, "reason": "no_place", "source": "ai"}` — 어느 경희스팟과도 관련이 없어 저장하지 않음
- `source`: `ai` 또는 `fallback`(AI 실패 시 제목·본문에 경희스팟 이름이 나오는지로 판단하고, 요약은 본문 앞부분)
- `400` 필드 규칙 위반, `409` 같은 `url` 의 공지가 이미 있음

## 웹 페이지 (앱과 무관, 브라우저용)

| 주소 | 내용 |
| --- | --- |
| `/` | 메모가 있는 건물 목록과 건물별 메모 수 |
| `/buildings/{buildingId}` | 그 건물의 메모 목록 (사진, 작성자, 시간) |
