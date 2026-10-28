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
게시일이 45일(`Notices:ShowDays`)보다 오래된 공지는 보내지 않는다. `summary` 는 빈 값일 수 있다 (포스터 그림만 있는 공지).
이 요청이 오면 서버가 단과대 게시판을 마지막으로 읽은 지 6시간이 지났는지 보고, 지났으면 뒤에서 새로 모은다 (아래 "공지 자동 모으기").

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

## v2 · 추천 v2 · 산책 매칭 · 산책 완료 (C-V0 초안)

> **상태:** 서버는 **추천 v2 (C-V2) · 매칭 (C-V3) · 산책 완료 · 리뷰 · 보상 (C-V4)** 까지 구현됐습니다. 지표 집계(C-V6)는 아직 없습니다.
> - B 는 이 모양으로 가짜 구현(로컬)을 먼저 만들고, 서버가 나오면 HTTP 구현으로 바꿉니다.
> - 바꿀 곳이 보이면 이 절을 고치는 PR 이나 V0 회의에서 맞춥니다.
> - `[가정]` 은 아직 정하지 않은 값의 제안입니다. `Q6` 같은 번호는 `docs/산책추천_v2_라운드.md` 3장의 결정 항목입니다.
> - 서버 구현은 라운드별로 나눕니다: 추천 v2 = C-V2, 매칭 = C-V3, 산책 완료·리뷰·보상 = C-V4.

### 공통 규칙

- v2 API 는 모두 **로그인 토큰**이 필요합니다. 헤더는 `Authorization: Bearer {토큰}` 이고, 체험 계정도 됩니다. 메모판처럼 기기 ID 로 구분하지 않습니다.
  - 예외: **추천(`POST /api/recommendations`)은 로그인 없이도 됩니다.** 토큰이 있으면 계정 취향의 걷는 속도를 씁니다.
- 성별·닉네임 등 설정이 덜 끝난 계정(`ready: false`)은 매칭을 쓸 수 없습니다.
- 앱 취향 값은 `WalkPreferences` 의 영어 코드를 그대로 씁니다.
  - 시간대: `morning` · `day` · `evening` · `night`
  - 속도: `slow` · `normal` · `fast`
  - 취향: `photo` · `quiet` · `food` · `unvisited` · `partner` · `exercise`
  - 매칭 상대: `same` · `any`

| 코드 | v2 에서의 뜻 |
| --- | --- |
| 401 | 토큰이 없거나 만료됨 → 로그인 화면으로 |
| 403 | 할 수 없는 상태. 본문 `reason` 으로 구분합니다: `not_ready`(계정 설정 덜 끝남) · `not_yours`(내 매칭·산책이 아님) |
| 404 | 매칭·산책이 없음 (끝난 지 오래되어 지워진 경우 포함) |
| 409 | 지금 상태에서 할 수 없음. 예: 이미 대기 중인데 또 들어가기, 이미 끝난 매칭에 수락 |
| 422 | 내용 검사에서 거절됨. 인상착의·리뷰 글은 메모와 같은 검사를 거칩니다 (위 "내용 검사") |

### 경희스팟 정보 (`spots.json`, A 와 맞출 필드 이름)

추천 v2 가 쓰는 소개글·태그는 A 가 `server/KucaMemoServer/Data/spots.json` 에 넣습니다. 앱이 보내는 값이 아니라 서버가 읽는 값입니다.

```json
{
  "id": "way-474085534", "name": "중앙도서관", "aliases": ["도서관"], "meetSpecies": "library_owl",
  "intro": "국제캠 한가운데 있는 도서관. 창가 열람석에서 노을이 잘 보여요.",
  "seasons": ["autumn", "winter"],
  "times": ["day", "evening"],
  "indoor": true,
  "tastes": ["quiet", "photo"]
}
```

| 필드 | 값 |
| --- | --- |
| `intro` | 2~3줄 소개. AI 가 추천 이유를 쓸 때 **이 안의 사실만** 씁니다 |
| `seasons` | `spring` · `summer` · `autumn` · `winter` 중 어울리는 것 |
| `times` | `morning` · `day` · `evening` · `night` 중 어울리는 것 (앱 취향과 같은 코드) |
| `indoor` | 실내면 `true`. 비·눈 오는 날 실내 스팟에 점수를 더 줍니다 |
| `tastes` | (선택) 어울리는 취향 코드. 없으면 취향 점수는 0 |
| `restricted` | (선택) 거주자 · 관계자만 드나드는 곳 (기숙사 등)이면 `true`. 앱이 `candidateBuildingIds` 로 보내도 **추천 후보에서 뺍니다**. 공지 연결에는 그대로 씀 |
| `meetSpecies` | (있으면) 만남 스팟. 그 자리에 세울 경희몬 ID. 앱의 `MeetSpotCatalog` 와 같아야 함 |

### POST /api/recommendations (v2)

v1 요청 본문(위 "POST /api/recommendations")은 **그대로 받습니다.** v2 필드는 모두 선택이라서, 지금 앱도 계속 동작합니다.

```json
{
  "minutes": 30,
  "tastes": ["photo", "quiet"],
  "lat": 37.2440, "lon": 127.0800,
  "visitedBuildingIds": ["way-474085534"],
  "pace": "normal",
  "afterMeal": false
}
```

| 새 필드 | 설명 |
| --- | --- |
| `pace` | `slow` · `normal` · `fast`. 없으면 계정 취향의 `pace`, 그것도 없으면 `normal`. 걷는 시간 계산에 씁니다 |
| `afterMeal` | 식후 걷기 버튼으로 요청했으면 `true`. 가까운 곳을 더 쳐서 고르고 (가까움 × 2 `[가정]`), 응답에 `healthNote` 를 넣습니다 |

- **칼로리는 다루지 않습니다** (산책이 목적이라 뺌, 10/23). 체중도 받지 않습니다.

- 시간대·계절·날씨는 **서버가 직접 정합니다** (한국 시각, 기상청 날씨). 앱은 보내지 않습니다.
- `minutes` 는 15 · 30 · 60 중 하나입니다 (D7).

**점수 (규칙 추천·AI 공통):** 취향 × 3 + 계절·시간 × 2 + 가까움 × 1

- 후보: 앱이 `candidateBuildingIds` 를 보내면 그 안에서, 비우면 **경희스팟 25곳** 안에서 고릅니다 (지금 위치에서 가까운 30곳까지)
- 취향: 요청 `tastes` 와 스팟 `tastes` 의 겹침 수. `unvisited` 는 안 가 본 곳이면 1. 스팟에 `tastes` 가 아직 없으면 `unvisited` 만 셉니다
- 계절·시간: 지금 계절·시간대가 스팟 `seasons`·`times` 에 있으면 각 1
- 가까움: 지금 위치에서 가까울수록 1 → 0 (1km 에서 0 `[가정]`)
- 비·눈일 때는 실내 스팟에 +1 `[가정]`
- AI 는 점수 상위 10곳 `[가정]` 안에서만 고르고 순서를 정합니다. 서버는 답을 검증합니다.
  - 후보 밖의 ID 는 버립니다.
  - `reason` 은 장소 이름·소개글·지금 시간대·계절·날씨와 겹치는 말이 없으면 버리고 규칙 문구로 바꿉니다 (두 글자 조각 겹침 검사 `[가정]`).
  - 걷는 시간이 넘치면 뒤쪽 장소부터 뺍니다.
- 규칙 문구: 비·눈 오는 날 실내면 "비가 와도 실내라 들르기 좋아요", `unvisited` 면 "아직 가 보지 않은 곳이에요", 아니면 소개글에서 "~좋다" 같은 추천 문장입니다 ("출입 제한" 같은 주의 문장은 쓰지 않고, 없으면 "가는 길에 들르기 좋아요"). 소개글의 `[확인 필요]` 표시는 빼고 씁니다.

`200`

```json
{
  "id": "rec_8c1f…",
  "title": "노을 보며 걷는 30분 코스",
  "source": "ai",
  "context": { "timeOfDay": "evening", "season": "autumn", "weather": "clear" },
  "stops": [
    {
      "buildingId": "way-474085534", "name": "중앙도서관",
      "reason": "창가 열람석에서 노을이 잘 보여요",
      "walkMinutes": 6, "distanceM": 420,
      "lat": 37.2421, "lon": 127.0796
    }
  ],
  "totalDistanceM": 1850,
  "totalMinutes": 18,
  "healthNote": null
}
```

| 새 필드 | 설명 |
| --- | --- |
| `id` | 추천 ID. 이 코스로 산책을 시작할 때 `POST /api/walks` 에 넣습니다 (AI·규칙 추천 수락률을 세는 데 씀) |
| `context` | 서버가 쓴 시간대·계절·날씨. `weather` 는 `GET /api/weather` 의 `condition`, 모르면 `null` |
| `stops[].distanceM` | 앞 장소(첫 곳은 지금 위치)에서 걷는 거리 (m, 직선 × 1.3 `[가정]`) |
| `totalDistanceM`, `totalMinutes` | 코스 전체 걷는 거리·시간. `totalMinutes` 는 `minutes` 의 80% 를 넘지 않습니다 (나머지는 둘러보는 시간 `[가정]`) |
| `healthNote` | `afterMeal` 이면 안내 문구. 끝에 "일반 건강 정보이며 의료 조언이 아닙니다." 가 붙습니다. 근거 문구는 A 가 줍니다 |

- 걷는 속도: slow 60 · normal 75 · fast 90 m/분 `[가정]`
- 들를 곳: 15분 최대 2곳 · 30분 4곳 · 60분 6곳 (10/26 늘림)
- 서버는 내준 추천(ID·AI/규칙·들를 곳·계획 거리)을 기록합니다. 계정·위치는 저장하지 않습니다
- `source`: `ai` 또는 `fallback`. 앱은 "AI 추천 / 기본 추천" 으로 표시합니다

#### AI 판단 우선 추천 (10/27)

문장 → **판단** → 장소 **사실** 비교 → 경로. 규칙 점수로 후보를 미리 자르지 않습니다.

1. **판단**: 바람 해석이 `judgment`(어떤 산책이 맞는지 판단과 이유 한 문장, 60자)와 `priorities`(요소 비중 0~3)를 냅니다.
   요소: `novelty` 안 가 본 곳 · `familiar` 자주 가던 곳 · `scenery` 경관 · `weather` 오늘 날씨에 맞는 곳 · `quiet` 조용함 · `near` 가까움.
   바람이 없거나 AI 가 안 되면 말 찾기 · 고른 취향 칩(unvisited → novelty 3, photo → scenery 3, quiet → quiet 3, 식후 → near 3)으로 정합니다. 바람과 칩은 큰 쪽.
2. **사실** (장소마다): 방문 횟수(요청 `visitCounts`: `[{ "buildingId", "count" }]`, 없으면 `visitedBuildingIds` 를 1번으로, 3번 이상이면 자주 감 `[가정]`),
   오늘 날씨 맞음(말로 실내/바깥을 정했으면 그것, 아니면 기상청: 비 · 눈 → 실내, 맑은 낮 → 바깥), 경관 말(소개 · 요즘 상태의 노을 · 단풍 · 꽃 · 초록 · 온실 …), 조용함, 가까움.
3. **경로**: AI 에게 후보 전체(가까운 순 최대 30곳)를 **점수 없이** 사실 · 판단 · 비중과 함께 주고 고르게 합니다. 규칙(대체)은 Σ 비중 × 사실 × 1.5 `[가정]` + 계절 · 시간 + 바람 · 행사 · 불편한 점.
4. 응답 `why`: 코스 전체 이유 한 문장(80자). AI 문장은 고른 곳의 사실 · 판단과 겹치는 말이 있을 때만 쓰고, 아니면 비중으로 만든 문장 ("안 가 본 곳 4곳 · 경관 좋은 곳 3곳 중심으로 이었어요"). `wish.judgment` 도 응답에 있습니다.

#### 말로 하는 요청 · 공지 · 장소의 요즘 상태 (10/26)

요청에 `wish`(선택, 200자 이내)를 넣으면 말로 한 바람을 해석해 추천에 반영합니다.

```json
{ "minutes": 30, "tastes": [], "lat": 37.2440, "lon": 127.0800, "wish": "그늘지고 조용한 데 20분, 오르막은 빼고 도서관 말고" }
```

1. **해석** (`WishInterpreter`): AI 가 시간(15 · 30 · 60 중 가까운 값) · 취향 코드 · 실내/바깥 · 식후 · 속도 · 바라는 모습 · 피할 것 · 넣을/뺄 경희스팟으로 바꿉니다. 목록 밖 코드 · 장소(출입 제한 포함)는 버립니다. AI 가 안 되면 정해 둔 말 찾기(`WishRules`). 같은 문장은 10분 기억합니다 (코스 3개 요청이 해석은 한 번).
2. **결정**: 해석한 시간 · 속도 · 식후가 요청 값보다 앞서고, 취향은 합칩니다. 뺄 장소는 후보에서 빼고, 점수에 더합니다 `[가정]`: 실내/바깥이 맞으면 +2 틀리면 −2, 바라는 모습이 장소 글(소개 · 요즘 상태 · 공지)에 있으면 하나에 +1.5 (최대 2개), 피할 것이 있으면 하나에 −3, 이름을 말한 곳 +4. AI 에게는 원래 문장(`<wish>`)과 해석, 장소마다 요즘 상태 · 행사 공지를 함께 주고 "바람을 먼저 반영하되 장소 정보에 없는 것은 지어내지 말라" 고 합니다.
3. 응답에 `wish` 가 붙습니다:

```json
"wish": { "summary": "15분 · 조용한 곳 · 그늘 · 오르막 · 중앙도서관 피하기", "source": "ai", "minutes": 15, "want": ["그늘"], "avoid": ["오르막"] }
```

`summary` 는 서버가 해석 칸으로 만든 "이렇게 이해했어요" 한 줄입니다 (AI 문장이 아님).

**다시 입력해 주세요 (10/27):** 해석할 수 없는 바람이면 코스를 지어내지 않고 `422 {"error": "…다시 입력해 주세요", "code": "wish_unclear"}` 를 돌려줍니다. 앱은 `error` 를 입력칸 아래에 보여 주고 문장을 그대로 둡니다.
- AI 가 `unclear: true` 라고 답할 때 (산책과 관계없는 말 · 알아볼 수 없는 말 · 하나로 정할 수 없는 요청), 또는 AI 가 같은 장소를 넣고 빼라고 답할 때
- AI 가 안 될 때 (말 찾기): 같은 장소 · 모습을 한 번은 바라고 한 번은 피함 ("도서관 가고 싶은데 도서관은 빼줘" · "오르막 좋아 오르막 싫어"), 빠르게 + 천천히, 시간이 둘 ("5분만 한 시간"), 안 가 본 곳 + 늘 가던 곳, 아무 조건도 못 찾았는데 산책 이야기도 아님 ("배고파" · "ㅋㅋㅋ" · 한글 없음 · 링크), 출입 제한 장소만 말함 ("원자로센터는 출입이 제한된 곳이라…")
- "산책" · "아무거나" · "좋은 데" · "기분 전환" 처럼 조건은 없어도 산책 이야기면 지금 걷기 좋은 곳으로 짭니다. "N시간" 은 60분으로.

**코스 안전장치 `[가정]`:** 출발점에서 50 m 안의 건물은 들를 곳으로 고르지 않고 (서 있는 건물), 들를 곳끼리 60 m 안이면 하나만 남깁니다. 이름을 말한 곳은 걷는 시간이 넘어도 빼지 않습니다. AI 코스 이유가 시간 때문에 빠진 장소를 말하면 비중으로 만든 이유로 바꿉니다.

**걸으며 이야기하기:** 요청에 `"talk": true` (산책 메이트 매칭 코스가 보냄) 이거나 바람에 "얘기 · 대화 · 수다 · 메이트 · 같이 걷 …" 이 있으면 조용함 3 · 가까움 2 비중을 더하고, `spots.json` 의 `noisy`(큰길 80 m 안이거나 늘 붐빔: 정문 · 학생회관 · 글로벌관 · 멀티미디어교육관) · `hill`(가는 길이 힘든 오르막: 천문대 · 선승관 · 국제대학, 사용자 표시 10/27) 장소를 후보에서 뺍니다. 바람에 오르막 · 계단을 피하라는 말이 있으면 `hill`, 붐빔 · 시끄러움 · 찻길이면 `noisy` 도 같은 방식으로 뺍니다 (이름을 말한 곳은 남김).

**공지 (통제 · 행사):** 공지 분류가 `kind`(`closure` 출입 통제 · 공사 · 휴관 / `event` 행사 · 공연 / `general`)와 기간(`startsOn` · `endsOn`)도 냅니다. AI 가 준 날짜는 원문에 그 월 · 일이 있을 때만 씁니다. 오늘 `closure` 인 장소는 **바람과 상관없이 추천에서 빠지고**, `event` 면 +1.5 `[가정]` 이고 규칙 이유가 "지금 '…' 소식이 있어요" 입니다. 기간이 없는 공지는 게시일부터 14일 `[가정]`. 단과대 게시판에서 모은 공지 중 장소가 안 나온 통제 공지는 쓰지 않습니다 (다른 건물 공사로 단과대 건물을 빼지 않게).

**장소의 요즘 상태 (`PlaceStateService`):** 최근 14일의 공개 쪽지 글, 내용 검사 AI 가 사진을 보고 쓴 설명(`memos.photo_caption`, API 응답에는 없음), 산책 리뷰 한 줄을 장소별로 모아 AI 가 한 번에 "요즘 모습" 한 줄과 좋은 점 · 불편한 점을 정리합니다 (30분마다 뒤에서, 닉네임 없이). 재료 글과 겹치지 않는 요약 · 금지어가 든 요약은 버립니다. AI 가 안 되면 정해 둔 말(공사 · 붐빔 · 단풍 …)로 좋은 점 · 불편한 점만 채웁니다. 불편한 점 하나에 −0.5 (최대 2개) `[가정]`, 추천 이유의 근거로도 씁니다 ("요즘 쪽지로는 …").

**사진 쪽지:** AI 가 사진을 보지 못하고 금지어 검사만 통과한 사진 쪽지는 `pending`(검토 대기)입니다.

### POST /api/music (산책 음악 추천, 10/27 B)

산책하며 들을 노래를 AI 가 고른다. 로그인은 선택 (로그인했으면 계정의 걷는 속도를 쓴다). 본문은 모두 선택.

```json
{ "minutes": 30, "tastes": ["quiet", "photo"], "pace": "slow",
  "courseTitle": "가을 조용한 캠퍼스길", "courseWhy": "사람이 적고 단풍이 예쁜 길을 이었어요",
  "places": ["중앙도서관", "사색의 광장"], "count": 3 }
```

- 코스 카드에서는 `courseTitle` · `courseWhy` · `places` 를 같이 보내 그 코스 분위기에 맞춘다. 시트의 "산책 음악" 은 취향만.
- `count` 3~8 (기본 5). 서버가 지금 시간대 · 계절 · 날씨를 더한다.

응답 200:

```json
{ "source": "ai", "mood": "calm", "theme": "가을 캠퍼스에 어울리는 잔잔한 길",
  "tracks": [ { "title": "가을 아침", "artist": "아이유", "why": "가을 풍경과 잘 어울려요" } ] }
```

- `mood`: calm · bright · energetic · rainy · night. `why` 는 없을 수 있다 (기본 목록).
- AI 가 실패하면 `source: "fallback"`: 분위기 규칙 (비 · 눈 → rainy, 운동 · 빠르게 → energetic, 밤 → night, 조용한 곳 · 천천히 → calm, 그 밖 → bright) 으로 `Data/music.json` 에서 고른다.
- 앱은 곡을 재생하지 않고, 설정의 음악 앱 (멜론 · Spotify · YouTube Music) 검색을 "가수 곡 이름" 으로 연다. AI 가 곡명을 조금 틀려도 검색 결과는 나온다.
- 400: 모르는 `tastes` · `pace`, `count` 범위 밖, 코스 정보가 너무 김. 지표: `/api/admin/health` 의 `aiLast24h.music`.

### 매칭 흐름 한눈에

```
idle ──들어가기──▶ waiting ──짝 찾음──▶ proposed ──둘 다 수락──▶ meeting ──둘 다 도착──▶ walking ──산책 끝──▶ ended
                    │ 10분 동안 못 찾음        │ 거절·응답 없음              │ 10분 노쇼·취소·신고          │ 취소·신고
                    ▼                         ▼                            ▼                             ▼
                  ended                     waiting (다시 찾기) / ended    ended                          ended
```

- 앱은 `GET /api/match` 를 **3초마다** 불러서 상태를 봅니다 (폴링). 화면은 `state` 로 고릅니다.
- 한 계정은 한 번에 매칭 하나만 할 수 있습니다.
- 끝난 매칭(`ended`)은 앱이 다시 들어가거나 1시간이 지나면 `idle` 로 돌아갑니다.

### POST /api/match/queue (대기열 들어가기)

```json
{ "minutes": 30, "matchGender": "same", "buildingId": "way-474085535" }
```

| 필드 | 설명 |
| --- | --- |
| `minutes` | **30 · 60** (함께 걷기는 최소 30분, 10/25 피드백). 15분은 `400`. 혼자 걷는 추천은 15분도 됩니다 |
| `matchGender` | `same`(동성만) · `any`(상관없음). 없으면 계정 취향의 값, 그것도 없으면 `any` |
| `buildingId` | 지금 가장 가까운 **건물 ID**. GPS 좌표는 보내지 않고, 서버도 건물 단위로만 저장합니다 |

- `200 MatchState` (`state: "waiting"`) 를 돌려줍니다.
- `403 reason: "not_ready"`: 성별 등 계정 설정이 덜 끝남
- `409`: 이미 대기 중이거나 매칭 중
- **운영 시간은 없습니다** (24시간, 10/23 결정).

**짝 찾기 (서버)**
1. **구역**: 두 사람의 건물이 가까운 순서
2. **시간**: `minutes` 가 같은 사람
3. **취향 점수**: 계정 취향(`preferencesJson`)의 `tastes`·`walkTimes`·`talkStyle` 겹침
4. **성별 조건**: 한쪽이라도 `same` 이면 성별이 같아야 함
5. **제외**: 서로 차단한 사람, 신고를 3번 이상 받은 사람 `[가정]`, 1시간 안에 서로 거절한 사람 `[가정]`. 상대 별점은 없으므로 별점으로 빼지 않습니다
- 실제 순서: 위 조건(2·4·5)을 통과한 사람 중 **건물 거리 − 공통 취향 수 × 100m** `[가정]` 가 가장 작은 사람. 먼저 기다린 사람부터 짝을 찾습니다
- **AI 짝 고르기 (10/26):** 들어오거나 폴링할 때 조건을 통과한 후보가 **2명 이상**이면, 위 점수 상위 5명 중 AI 가 가장 잘 맞는 한 명을 고릅니다.
  - AI 가 고르는 동안(최대 10초 `[가정]`) 그 사람은 다른 짝 찾기에서 잠깐 빠집니다.
  - AI 에게는 후보마다 Label(A·B…), 겹치는 취향 · 시간대 · 대화 스타일, 거리만 보냅니다 (닉네임 · 성별 · 계정 · 위치 없음).
  - AI 가 고른 이유가 그대로 `partner.whyMatched` 가 됩니다.
  - AI 가 못 고르거나(키 없음 · 실패 · 후보에 없는 답) 그사이 후보가 사라지면 같은 후보 중 점수 1등과 짝이 됩니다.
  - 안전 조건(시간 · 동성만 · 차단 · 신고 · 방금 거절)은 AI 가 바꿀 수 없습니다. AI 는 이미 걸러진 후보 안에서만 고릅니다

### DELETE /api/match/queue (대기 취소)

- `204`. 대기열에서 빠지고 위치(건물) 기록을 지웁니다.
- 대기 중이 아니어도 `204` 입니다.

### GET /api/match (지금 상태, 폴링)

`200 MatchState`

```json
{
  "state": "meeting",
  "matchId": "m_5a2e…",
  "minutes": 30,
  "partner": { "nickname": "민지", "commonTastes": ["photo", "quiet"], "appearance": "검은 후드, 흰 운동화",
               "whyMatched": "저녁 산책과 사진을 함께 즐길 두 분이에요", "whyMatchedSource": "ai" },
  "respondBy": null,
  "myAnswer": "accepted",
  "meetSpot": {
    "buildingId": "way-474085535", "name": "학생회관",
    "lat": 37.2446, "lon": 127.0794, "meetSpecies": "pigeon"
  },
  "meetBy": "2026-10-20T03:22:00Z",
  "smalltalk": ["학식 최애 메뉴 있어요?", "요즘 날씨에 걷기 좋은 길 알아요?", "아직 한 번도 안 가 본 건물 있어요?"],
  "smalltalkSource": "ai",
  "myAppearance": "회색 맨투맨, 파란 백팩",
  "arrived": { "me": true, "partner": false },
  "walkId": null,
  "endReason": null,
  "smalltalkCards": [
    { "text": "학식 최애 메뉴 있어요?", "topic": "학식", "emoji": "🍚" },
    { "text": "요즘 날씨에 걷기 좋은 길 알아요?", "topic": "계절", "emoji": "🍂" },
    { "text": "아직 한 번도 안 가 본 건물 있어요?", "topic": "캠퍼스 장소", "emoji": "🏛️" }
  ],
  "course": {
    "recommendationId": "rec_3b9a…", "title": "가을 오후 함께 걷는 30분", "source": "ai",
    "stops": [ { "buildingId": "way-474085534", "name": "중앙도서관", "reason": "…", "walkMinutes": 4, "distanceM": 310, "lat": 37.2421, "lon": 127.0796 } ],
    "totalDistanceM": 1450, "totalMinutes": 21,
    "myAgree": true, "partnerAgree": false, "confirmed": false, "changesLeft": 2,
    "health": { "steps": 2140, "kcalMin": 61, "kcalMax": 86, "intensity": "moderate", "intensityLabel": "보통 걷기" },
    "difficulty": { "level": "normal", "label": "중", "hills": 0 }
  }
}
```

| `state` | 뜻 | 채워지는 필드 |
| --- | --- | --- |
| `idle` | 매칭 안 함 | 없음 |
| `waiting` | 짝을 찾는 중 | `waitingSince`, `waitingUntil` (10분 `[가정]`), `nowSearching` (나 말고 지금 짝을 찾는 사람 수 — 대기 화면의 실시간 느낌용) |
| `proposed` | 상대를 찾음, 수락 기다림 | `partner.nickname`, `partner.commonTastes`, `partner.whyMatched`, `respondBy` (60초 `[가정]`), `myAnswer` (`null` · `accepted`) |
| `meeting` | 둘 다 수락, 만남 스팟으로 가는 중 | `meetSpot`, `meetBy` (노쇼 기준, Q7 제안 10분), `smalltalk`, `smalltalkCards`, `myAppearance`, `partner.appearance`, `arrived`, `course` (함께 걸을 코스, 만드는 몇 초 동안은 `null`) |
| `walking` | 만나서 산책 중 | `walkId` (산책 완료 API 에 씀) |
| `ended` | 끝남 | `endReason` |

- **`endReason`**
  - `no_partner`: 대기 시간 안에 못 찾음
  - `declined`: 상대 거절
  - `timeout`: 응답 없음
  - `cancelled`: 누군가 취소. `cancelledBy` 는 `me` · `partner`
  - `noshow`: `meetBy` 까지 한쪽이 안 옴
  - `reported`: 신고로 끝남
  - `blocked`: 차단으로 끝남
  - `completed`: 두 사람 모두 산책 끝(`POST /api/walks/{walkId}/finish`). 아무도 안 눌러도 산책 시작 뒤 고른 시간 + 30분이 지나면 매칭은 `completed` 로 끝납니다 `[가정]` (산책 기록은 남아서 나중에 끝낼 수 있음)
- `proposed` 에서 상대가 거절하거나 응답하지 않으면, 수락한 쪽은 자동으로 `waiting` 으로 돌아가 다시 찾습니다.
- **상대 정보는 닉네임·공통 취향·인상착의만** 보입니다. 성별·계정 ID·건물은 보이지 않습니다.
- **`partner.whyMatched` (AI 매칭 이유):** "왜 둘을 연결했는지" 한 줄. 두 사람이 같은 문장을 봅니다. `proposed` 부터 끝날 때까지 있습니다.
  - 짝은 위 규칙으로 정하고, AI 는 그 짝을 설명만 합니다 (안전 조건은 AI 가 바꿀 수 없음).
  - AI 에게는 겹치는 취향 · 시간대 · 대화 스타일, 건물 사이 거리, 걸을 시간만 보냅니다. 닉네임 · 성별 · 계정 · 위치는 보내지 않습니다.
  - `whyMatchedSource`: `ai` 또는 `fallback`. AI 가 안 되면 겹치는 것으로 만든 틀 문장입니다 (예: "둘 다 사진 찍기를 좋아해서 연결했어요").
- `partner.appearance` 는 상대가 입력하기 전에는 `null` 입니다.
- 만남 스팟은 `meetSpecies` 가 있는 스팟 10곳 중, 두 사람 중 더 먼 쪽이 걷는 거리가 가장 짧은 곳입니다.

### POST /api/match/{matchId}/accept · POST /api/match/{matchId}/decline

- 본문 없음. `200 MatchState`.
- 둘 다 수락하면 `meeting` 이 됩니다. 이때 만남 스팟과 스몰토크 3개가 정해집니다.
  - 스몰토크는 먼저 예시(`fallback`)로 채우고, AI 답이 오면 바꿉니다. 폴링으로 바뀐 카드를 받습니다.
- 거절하면 나는 `ended` (`endReason: "declined"`, `cancelledBy: "me"`) 가 됩니다. 상대는 다시 대기합니다.
- `409`: `proposed` 가 아님 (이미 끝났거나 시간이 지남)

### PUT /api/match/{matchId}/appearance (인상착의)

```json
{ "text": "회색 맨투맨, 파란 백팩" }
```

- 1~60자 `[가정]`. 저장 전에 메모와 같은 내용 검사를 거칩니다. 거절되면 `422`.
  - 애매함(`review`)도 저장하지 않고 `422` 로 돌려줍니다. 검토 대기를 기다릴 시간이 없기 때문입니다.
- **상대에게만** 보이고, 매칭이 끝나면 지웁니다.
- `200 MatchState`. `meeting` 에서만 쓸 수 있고, 다른 상태면 `409` 입니다.

### 함께 걸을 코스 (10/25 피드백)

둘 다 수락하는 순간 서버가 **만남 스팟에서 출발하는 코스**를 만들어 두 사람에게 똑같이 보여 줍니다 (`MatchState.course`).
- 고른 시간(30 · 60분), 두 사람의 공통 취향, 지금 시간대 · 계절 · 날씨로 추천 v2 가 짭니다 (AI 키가 있으면 AI). 만남 스팟은 코스에 넣지 않습니다.
- 만드는 데 몇 초 걸리므로 그동안 `course` 는 `null` 입니다 ("코스 준비 중"). 폴링으로 받습니다.

`POST /api/match/{matchId}/course/agree` — "이 코스 좋아요"
- 본문 없음. `200 MatchState`. **두 사람 모두 누르면 `course.confirmed: true`** (확정).
- `myAgree` · `partnerAgree` 로 누가 눌렀는지 보여 줍니다. 가상 상대는 바로 동의합니다.

`POST /api/match/{matchId}/course/next` — "다른 코스 보기"
- 본문 없음. `200 MatchState`. 새 코스로 바뀌고 **두 사람의 동의가 지워집니다** (다시 함께 골라야 함).
- 이미 보여 준 장소는 빼고 짜서 실제로 다른 코스가 나옵니다.
- 매칭마다 **3번까지** (`changesLeft`). 넘으면 `429`, 확정된 뒤에는 `409`, 코스를 아직 만드는 중이면 `409`.

출발 (둘 다 `arrived`) 하면 그때의 코스로 확정되고, 산책 기록(`walkId`)의 계획 거리 · 장소가 됩니다. 완료 판정은 혼자 걷는 추천 코스와 같습니다 (마지막 장소 도착 또는 계획 거리의 80%).

### POST /api/match/{matchId}/smalltalk (다음 주제)

- 본문 없음. `200 { "smalltalk": ["…", "…", "…"], "source": "ai", "smalltalkCards": [ … ] }`. `meeting`·`walking` 에서만 됩니다 (아니면 `409`)
- `smalltalkCards`: 같은 주제에 분류(`topic`: 공강 · 학식 · 전공 · 캠퍼스 장소 · 계절 · 날씨 · 취향 · 대화)와 `emoji` 를 붙인 것. 앱은 이걸로 대화 카드를 꾸밉니다
- 새 주제 3개를 돌려주고, 두 사람 모두 같은 카드를 봅니다.
- 앱의 "다음 주제" 버튼은 받은 3개를 먼저 넘기고, 다 보면 이걸 부릅니다.
- AI 는 공통 취향 + 만남 스팟 + 날씨로 씁니다. AI 가 실패하면 `Data/smalltalk.json` 예시 20개 중에서 고릅니다 (`source: "fallback"`).
- 매칭 하나에 5번까지 됩니다 `[가정]`. 넘으면 `429` 입니다.

### POST /api/match/{matchId}/arrived (만남 스팟 도착)

- 본문 `{ "buildingId": "way-474085535" }`. 앱은 스팟 30m 안에 들어오면 부르거나, "도착했어요" 버튼으로 부릅니다. 만남 스팟과 다른 건물이면 `400`
- 둘 다 도착하면 `walking` 이 되고 `walkId` 가 생깁니다. 산책 기록은 아래 "산책" API 와 같습니다.
- `200 MatchState`

### POST /api/match/{matchId}/cancel

- 본문 `{ "reason": "late" }` (선택): `late`(늦어짐) · `changed_mind`(마음이 바뀜) · `other`
- `200 MatchState` (`ended`, `endReason: "cancelled"`). `proposed` 에서 취소하면 거절과 같습니다
- `meeting`·`walking` 에서 취소하면 상대에게 "상대가 취소했어요" 로 보입니다.

### POST /api/reports (신고) · POST /api/blocks (차단)

```json
{ "matchId": "m_5a2e…", "reason": "rude", "text": "선택, 0~300자" }
```

- 신고 `reason`: `noshow`(안 나옴) · `rude`(무례함) · `unsafe`(불안·위험) · `fake`(사칭·광고) · `other`
- 신고하면 그 상대를 **자동으로 차단**합니다. 진행 중인 매칭이면 바로 `ended` (`endReason: "reported"`) 가 됩니다.
- 차단만 할 때는 `POST /api/blocks` 본문 `{ "matchId": "…" }` 를 보냅니다. 차단한 사람과는 다시 짝이 되지 않습니다. 진행 중인 매칭이면 `endReason: "blocked"` 로 끝납니다.
- 상대 계정 ID 는 앱에 주지 않으므로 **`matchId` 로 상대를 가리킵니다.** 지난 매칭도 기록이 남아 있는 동안(아래 보관 기간)은 됩니다.
- 응답 `201`. 남의 매칭이면 `403`, 기록이 없으면 `404`.

### 시연용 가상 상대

- 서버 설정 `Match:DemoPartner = true` 일 때만 켜집니다.
- **체험 계정(`isGuest: true`, 로그인 화면의 "둘러보기")에게만** 나타납니다. 체험 계정이 5초 안에 실제 짝을 못 찾으면 가상 상대 `체험메이트` 가 나타납니다.
  - 경희대 메일로 로그인한 **실제 학생은 켜 둬도 실제 학생끼리만** 짝이 됩니다. 그래서 심사 · 실사용 기간에 켜 둬도 됩니다.
  - 체험 계정도 그 5초 안에 기다리는 사람이 있으면 그 사람과 먼저 짝이 됩니다.
  - 자동으로 수락하고, 인상착의를 넣고, 바로 도착합니다.
- 앱은 다른 처리 없이 평소처럼 동작합니다. 시연 영상에서 폰 한 대로 전체 흐름을 보여 줄 수 있습니다.

### 산책 · 완료 · 리뷰 · 보상

#### POST /api/walks (산책 시작, 혼자 걷기)

```json
{ "recommendationId": "rec_8c1f…", "minutes": 30 }
```

- 추천 코스로 혼자 걸을 때 부릅니다. 본문의 두 필드 모두 선택이고, `recommendationId` 가 없으면 자유 산책입니다 (`plannedDistanceM: null`, `stops: []`).
  - 매칭 산책은 둘 다 도착하는 순간 서버가 두 사람 각각에게 만듭니다 (`MatchState.walkId`, 두 사람이 같은 ID).
- `201 { "id": "w_91d0…", "startedAt": "…", "plannedDistanceM": 1850, "stops": ["way-…"] }`
- `401` 로그인 없음, `404` 없는 추천 ID, `400` `minutes` 가 15 · 30 · 60 이 아님

#### POST /api/walks/{walkId}/finish (산책 끝)

```json
{ "distanceM": 1620, "durationSec": 1560, "visitedBuildingIds": ["way-474085534", "way-474085535"] }
```

- **완료 판정 (서버):** 추천 코스면 마지막 스팟 도착(`visitedBuildingIds` 에 있음) 또는 계획 거리의 80% 이상
  - 매칭 산책은 함께 정한 코스 기준 (위와 같음)
  - 자유 산책은 500m 이상이면 완료 `[가정]`
- 걸은 거리가 시간에 비해 너무 길면(초속 2.5m 초과) 거리를 깎습니다 `[가정]`. 탈것으로 이동한 경우를 막기 위해서입니다.
- 매칭 산책은 한 사람이 `finish` 하면 그 사람 산책이 끝납니다. 두 사람 모두 끝나면 매칭이 `ended` (`completed`) 가 됩니다.

`200`

```json
{
  "completed": true,
  "distanceM": 1620,
  "reward": {
    "tickets": 1,
    "xp": 106,
    "breakdown": [
      { "kind": "distance", "xp": 16 },
      { "kind": "walk", "xp": 30 },
      { "kind": "together", "xp": 60 }
    ]
  },
  "progress": { "level": 3, "xp": 240, "levelXp": 210, "nextLevelXp": 330, "totalWalks": 3, "totalDistanceM": 4100, "togetherWalks": 1 }
}
```

- **경험치 표 (Q6 제안):** 1km 10 (100m 단위 버림) · 산책 완료 30 · 함께 걸음 60 · 리뷰 10. 위 예시는 1.62km 를 함께 걸은 경우입니다. 완료가 아니면(`completed: false`) 거리 경험치만 줍니다.
- **보상 티켓:** 완료 1장 `[가정]`. 걸은 거리 티켓(1km = 1장)은 V1 처럼 **앱이 계속 셉니다.** 서버는 완료 보너스만 줍니다 (B 와 맞출 것).
- `distanceM`: 서버가 인정한 거리 (깎였으면 깎인 값)
- **레벨:** 다음 레벨까지 100 + 10 × (레벨 − 1) (1→2 100, 2→3 110, 3→4 120 …, 10/24 결정). 레벨 2 는 100, 3 은 210, 4 는 330 부터입니다. 앱은 받은 값만 표시합니다 (D9).
  - `levelXp` 는 지금 레벨이 시작된 경험치, `nextLevelXp` 는 다음 레벨이 되는 경험치. 진행 막대 = (`xp` − `levelXp`) / (`nextLevelXp` − `levelXp`)
- 이미 끝낸 산책을 또 `finish` 하면 `409`, 남의 산책이면 `403 reason: "not_yours"`, 없으면 `404` 입니다.

#### POST /api/walks/{walkId}/review (리뷰)

```json
{ "courseRating": 5, "text": "벤치 쉬는 곳이 좋았어요" }
```

- `courseRating` 1~5 (필수), `text` 0~100자 (내용 검사, 거절은 `422`)
- **리뷰는 코스만 평가합니다.** 매칭 산책이어도 상대 별점은 받지 않습니다 (10/24 결정). 예전 앱이 `partnerRating` 을 보내도 무시합니다.
- 산책을 끝낸 뒤에만, 한 번만, 끝난 뒤 24시간 안에만 됩니다 `[가정]`. 아니면 `409`.
- 리뷰 글은 검토 대기 없이, 애매해도 `422` 로 거절합니다.
- `201 { "reward": { "xp": 10 }, "progress": { … } }`

#### GET /api/me/progress

- `200 { "level": 3, "xp": 240, "levelXp": 210, "nextLevelXp": 330, "totalWalks": 7, "totalDistanceM": 12400, "togetherWalks": 2 }`
- `totalWalks` 는 완료한 산책 수, `totalDistanceM` 은 끝낸 산책의 인정 거리 합
- 앱 시작과 산책 완료 화면에서 부릅니다.

### 건강 · 걸음 수 · 칼로리 · 주간 목표 · AI 코치 (10/27)

숫자는 **서버가 공식으로만** 계산하고, AI 는 그 숫자로 말만 합니다. 걸음 센서가 없으므로 칼로리는 **범위**로 보여 줍니다.

**`health` (HealthEstimate)**: 추천 응답(`POST /api/recommendations`)과 산책 끝 응답(`POST /api/walks/{id}/finish`)에 붙습니다.

```json
{ "steps": 2000, "kcalMin": 58, "kcalMax": 82, "intensity": "moderate", "intensityLabel": "보통 걷기", "stepsMeasured": false }
```

- 걸음 수 = 거리 ÷ 보폭 0.7m `[가정]` (10 걸음 단위)
  - **센서 걸음 수(`steps`, 선택)**: 산책 끝 요청에 휴대폰 걸음 센서로 잰 이번 산책 걸음 수를 실을 수 있습니다. `0` 이하는 "모름", 100,000 초과는 `400`.
    걸은 거리(100m 이상)로 나눈 보폭이 0.25~1.6m `[가정]` 일 때만 그 값을 쓰고 `"stepsMeasured": true` 를 붙입니다. 안 맞으면 거절하지 않고 어림값을 씁니다 (걸음 수는 보상 · 완료에 쓰지 않음). 저장하지 않습니다.
- 칼로리 = MET × 몸무게(kg) × 시간(h). MET 은 걷기 빠르기로: 느긋 2.8 · 보통 3.5 · 빠름 4.3 (2011 Compendium of Physical Activities)
  - 몸무게를 모르면 50~70kg 범위 `[가정]`, 알면 그 몸무게 ±10% `[가정]`
  - 추천은 고른 걷는 속도로, 산책 끝은 실제 빠르기(분당 70m 미만 느긋 · 85m 미만 보통 · 그 위 빠름)로 셉니다
- `intensity`: `light`(가벼운 산책) · `moderate`(보통 걷기) · `brisk`(운동 되는 걷기)
- **`difficulty` (난이도 하 · 중 · 상)**: 추천 응답과 함께 걷기 코스(`course`)에 붙습니다. `{ "level": "normal", "label": "중", "hills": 0 }`
  - 하(`easy`): 1.5km 이하이고 오르막 장소 없음 · 상(`hard`): 3km 넘음, 오르막 장소 2곳 이상, 또는 오르막이 있고 2km 넘음 · 중(`normal`): 나머지 `[가정]`
  - 오르막 장소는 `spots.json` 의 `hill` (가는 길에 힘든 오르막이 있는 곳)
- 함께 걷기 코스의 `health` 는 몸무게를 받지 않으므로 50~70kg 범위입니다.
- **몸무게(`weightKg`, 선택)**: 추천 요청 · 산책 끝 요청에 실어 보냅니다. 30~200, 아니면 `400`. `0` 이하는 "모름" 입니다. **서버는 저장하지 않습니다** (앱이 이 기기에만 저장, 취향 체크리스트 마지막 단계)

**주간 목표 (WHO: 일주일 중간 강도 활동 150분)**
- 한 주는 한국 시각 **월요일 0시**부터. 끝낸 산책의 걸은 시간을 더합니다.
- `GET /api/me/progress` 와 산책 끝 응답의 `progress.week`: `{ "weekMinutes": 82, "goalMinutes": 150, "minutesLeft": 68, "weekWalks": 3 }`
- **목표 맞춤 코스**: 로그인해서 추천을 받으면 응답에 `goal` 이 붙습니다.
  `{ "weekMinutes": 130, "goalMinutes": 150, "minutesLeft": 20, "completesGoal": true, "note": "이 코스로 걸으면 이번 주 목표를 채워요" }`
  AI 에게도 남은 분을 알려 주어 코스 이름 · 이유에 응원하는 말을 담을 수 있습니다 (숫자는 쓰지 않게).

**`GET /api/me/coach` (주간 AI 코치)**: 로그인 필요 (`401`).

```json
{ "text": "이번 주 82분 걸었어요. 목요일 공강에 30분만 더 걸으면 목표 달성이에요!", "source": "ai",
  "week": { "weekMinutes": 82, "goalMinutes": 150, "minutesLeft": 68, "weekWalks": 3 },
  "note": "일반 건강 정보이며 의료 조언이 아닙니다." }
```

- AI 에게는 **숫자 요약만** 보냅니다: 이번 주 걸은 분 · 목표 · 남은 분 · 횟수 · 남은 날, 최근 4주에 자주 걷는 요일 · 시간대. 몸무게 · 닉네임 · 위치는 보내지 않습니다.
- 검증: 위 숫자(와 15 · 30 · 60) 말고 **다른 숫자**가 있거나, 체중 · 다이어트 · 혈당 · 질병 · 치료 같은 **효과 단정 · 의료 표현**이 있거나, 90자를 넘으면 버리고 틀 문장으로 바꿉니다 (`source: "fallback"`).
- 같은 날 같은 기록이면 다시 AI 를 부르지 않습니다. 화면에는 `note` 를 함께 보여 줍니다.

### 개인정보 보관 기간 (Q8 제안)

| 정보 | 어디에 씀 | 보관 |
| --- | --- | --- |
| 성별 | "동성만" 조건 | 탈퇴할 때까지. 다른 사람에게 보이지 않음 |
| 몸무게 (선택) | 걸음 수 · 칼로리 범위 | **이 폰에만** (앱 PlayerPrefs). 요청할 때만 보내고 서버는 저장하지 않음 |
| 걸음 센서 값 (선택) | 이번 산책 걸음 수 | 산책 끝 요청에 이번 산책 걸음 수 하나만. 서버는 저장하지 않음. 설정에서 끌 수 있고, 권한을 거절하면 어림값 |
| 위치 (건물 단위) | 짝 찾기·만남 스팟 | **대기 취소·매칭 끝나면 바로 삭제**. GPS 좌표는 받지 않음 |
| 인상착의 | 상대가 나를 알아보기 | **매칭 끝나면 바로 삭제** |
| 매칭 기록 (두 계정·스팟·시각·끝난 이유) | 신고·차단, 성사율 집계 | 30일 `[가정]` |
| 신고 | 안전 | 90일 `[가정]` |
| 차단 | 다시 짝이 되지 않게 | 탈퇴할 때까지 |
| 산책·리뷰(코스 별점)·경험치 | 보상, 완주율 · 코스 만족도 집계 | 탈퇴할 때까지. 리뷰는 다른 사람에게 보이지 않음 |
| 체험 계정 | 시연 | 7일 (지금 `AccountStore.GuestLifetime`) |

- 탈퇴(`DELETE /api/me`)하면 위 기록을 모두 지웁니다. 집계 숫자(성사율 등)에는 개인을 알 수 없는 개수만 남깁니다.

### 지표 (C-V6, 앱이 따로 할 일 없음)

서버가 아래 순간을 스스로 기록합니다.
- 추천: 표시(`POST /api/recommendations`) → 시작(`POST /api/walks` 의 `recommendationId`) → 완료
- 매칭: 요청 → 성사(`meeting`) → 완료
- 리뷰 작성 (코스 별점)
- AI 를 썼는지, 규칙으로 대신했는지 (추천 · 스몰토크 · 내용 검사)

**누가 했는지는 남기지 않습니다** (계정 · 위치 없이 종류와 시각만). 관리자 집계는 아래 "관리자 API › 지표" 에서 봅니다.
시연용 가상 상대와의 매칭은 성사율에 넣지 않습니다 (요청 수에는 들어감).

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

### 공지 자동 모으기 (10/25, B)

경희스팟 건물을 쓰는 단과대 · 학과 · 도서관 공지 게시판 12곳(`Data/notice_sources.json`)에서 최근 공지를 모아 위와 같은 장소 공지로 넣는다.

- 게시판마다 연결할 경희스팟이 정해져 있다 (예: 공과대학 → 공학관 · 공학실험동). AI 는 요약만 한다.
- 서울캠퍼스 표시 글, 다른 사이트로 가는 학교 전체 공지, 30일보다 오래된 글, 이미 넣은 링크는 건너뛴다.
- 같은 제목이 여러 게시판에 있으면 (학교 전체 공지) 한 공지에 건물만 더한다.
- 한 번에 게시판마다 새 글 5건까지, 요청 사이 0.8초를 띄운다. 원문은 링크로만 보여 준다 (본문은 앱에 보내지 않음).
- 따로 예약 작업은 없다: `GET /api/notices` 가 6시간(`Notices:IntervalHours`)마다 한 번 뒤에서 모으기를 시작한다. `Notices:AutoCollect=false` 면 끈다.

| 요청 | 하는 일 | 응답 |
| --- | --- | --- |
| `POST /api/admin/notices/collect` | 지금 모으기 (끝날 때까지 기다림) | `200` 게시판마다 `{source, listed, added, error}` / `409` 이미 모으는 중 |
| `GET /api/admin/notices/collect` | 게시판 목록과 마지막 결과 | `200` |

### 지표 (C-V6)

`GET /api/admin/stats?days=7` (헤더 `X-Admin-Key`, `days` 1~90, 기본 7)

```json
{
  "from": "2026-10-13T03:00:00Z", "to": "2026-10-20T03:00:00Z", "days": 7,
  "recommendations": {
    "ai":       { "shown": 40, "started": 22, "finished": 20, "completed": 17, "acceptRate": 0.55, "completionRate": 0.773, "reviews": 9, "avgCourseRating": 4.3 },
    "fallback": { "shown": 12, "started": 4, "finished": 4, "completed": 2, "acceptRate": 0.333, "completionRate": 0.5, "reviews": 1, "avgCourseRating": 3.0 }
  },
  "walks": {
    "ai": { "started": 22, "finished": 20, "completed": 17, "completionRate": 0.773, "reviews": 9, "avgCourseRating": 4.3 },
    "fallback": { … }, "free": { … }, "match": { … }
  },
  "matching": {
    "requests": 30, "proposed": 14, "met": 11, "walking": 9, "completed": 8,
    "successRate": 0.367, "completionRate": 0.727,
    "endReasons": { "completed": 8, "no_partner": 9, "declined": 3, "noshow": 1 },
    "acceptRateByReason": { "ai": 0.82, "fallback": 0.64 },
    "acceptRateByPick": { "ai": 0.85, "rule": 0.7 }
  },
  "ai": {
    "recommend":  { "ai": 40, "fallback": 12, "fallbackRate": 0.231 },
    "smalltalk":  { "ai": 20, "fallback": 2, "fallbackRate": 0.091 },
    "match_reason": { "ai": 14, "fallback": 1, "fallbackRate": 0.067 },
    "match_pick": { "ai": 6, "fallback": 0, "fallbackRate": 0.0 },
    "moderation": { "ai": 85, "fallback": 3, "fallbackRate": 0.034 }
  }
}
```

| 값 | 계산 |
| --- | --- |
| `acceptRate` (추천 수락률) | 그 추천으로 산책을 시작한 수 ÷ 추천을 보여 준 수 |
| `completionRate` (완주율) | 완료한 산책 ÷ 시작한 산책 |
| `avgCourseRating` | 리뷰 코스 별점 평균 |
| `successRate` (매칭 성사율) | 둘 다 수락(`meeting`)한 수 ÷ 대기열 요청 수 |
| `matching.completionRate` | 함께 산책을 끝낸 매칭 ÷ 성사된 매칭 |
| `fallbackRate` | AI 대신 규칙 · 예시 · 금지어로 처리한 비율. 높으면 AI 키 · 게이트웨이를 확인 |
| `acceptRateByReason` | 제안 화면의 매칭 이유가 AI 문장일 때 / 틀 문장일 때, 두 사람 모두 수락한 비율 (AI 매칭 이유의 효과) |
| `acceptRateByPick` | AI 가 고른 짝 / 규칙이 고른 짝의 두 사람 모두 수락한 비율 (AI 짝 고르기의 효과) |

- 분모가 0 이면 비율은 `null` 입니다.
- `walks` 의 종류: `ai` · `fallback` (추천 코스), `free` (추천 없이), `match` (매칭 산책)

### 운영 점검 (C-V6)

`GET /api/admin/health` (헤더 `X-Admin-Key`)

```json
{ "aiKey": true, "weatherKey": true, "kakaoAppId": true, "email": true, "demoPartner": false, "devLoginOff": true,
  "aiLast24h": { "recommend": { "ai": 5, "fallback": 0, "fallbackRate": 0.0 }, "smalltalk": { … }, "moderation": { … } } }
```

- 키는 **들어 있는지만** 보여 주고 값은 내보내지 않습니다.
- `devLoginOff` 가 `false` 면 Azure 에 개발용 설정(`Auth:DevLogin`, `Email:DevEcho`)이 켜진 것이니 끕니다.

## 웹 페이지 (앱과 무관, 브라우저용)

| 주소 | 내용 |
| --- | --- |
| `/` | 메모가 있는 건물 목록과 건물별 메모 수 |
| `/buildings/{buildingId}` | 그 건물의 메모 목록 (사진, 작성자, 시간) |
