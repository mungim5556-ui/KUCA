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
  "likedByMe": false
}
```

- `id`: 서버가 만드는 Guid 문자열
- `photoUrl`: 서버 기준 경로. 사진이 없으면 `null`. 앱은 `서버 주소 + photoUrl` 로 이미지를 받습니다
- 작성 기기 ID(`deviceId`)는 서버에만 저장하고 **응답에는 넣지 않습니다**
- `likeCount`: 좋아요 수, `commentCount`: 댓글 수
- `likedByMe`: 요청에 `X-Device-Id` 헤더가 있고 그 기기가 좋아요를 눌렀으면 `true`. 헤더가 없으면 항상 `false`

### Comment

```json
{
  "id": "0b8e2d4c-7a1f-4e3b-9c55-1d2e3f4a5b6c",
  "memoId": "6f1c2e0a-3b7d-4c55-9a1e-2f8b9d0c7e41",
  "author": "민지",
  "text": "저도 봤어요! 2층 자판기는 돼요",
  "createdAt": "2026-10-06T06:01:10Z"
}
```

- 댓글은 글만 씁니다 (사진 없음)
- 작성 기기 ID 는 메모와 같이 응답에 넣지 않습니다

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

헤더 `X-Device-Id` 를 보내면 각 메모의 `likedByMe` 가 채워집니다 (선택).

`200 Memo[]` (메모가 없으면 `[]`) / `404` 없는 건물

### POST /api/buildings/{buildingId}/memos

메모 작성. 본문은 **multipart/form-data** 입니다 (사진 파일을 같이 보내야 하므로).

| 필드 | 필수 | 규칙 |
| --- | --- | --- |
| `author` | 예 | 앞뒤 공백 제거 후 1~20자 |
| `text` | 예 | 앞뒤 공백 제거 후 1~500자 |
| `deviceId` | 예 | 1~64자. 앱이 기기마다 한 번 만들어 저장해 둔 값 (삭제 권한 확인용) |
| `photo` | 아니오 | JPEG 또는 PNG, 10MB 이하 |

- 성공: `201 Memo`, `Location: /api/memos/{id}` 헤더
- 실패: `400` (규칙 위반, 어떤 필드가 왜 틀렸는지 `error` 에), `404` 없는 건물
- 사진은 `wwwroot/photos/{메모 id}.{jpg|png}` 로 저장하고 `photoUrl` 을 `/photos/{파일 이름}` 으로 채웁니다

### GET /api/memos/{memoId}

메모 하나. 헤더 `X-Device-Id` 를 보내면 `likedByMe` 가 채워집니다 (선택). `200 Memo` / `404`

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

- 성공: `201 Comment`, `Location: /api/comments/{id}` 헤더
- 실패: `400` (규칙 위반, 어떤 필드가 왜 틀렸는지 `error` 에), `404` 없는 메모

### DELETE /api/comments/{commentId}

댓글 삭제. **댓글을 쓴 기기만** 지울 수 있습니다 (헤더 `X-Device-Id` 가 작성할 때의 `deviceId` 와 같아야 함).

- `204` 삭제됨
- `403` 기기 ID가 다름, `404` 없는 댓글

### GET /photos/{fileName}

업로드된 사진 파일 (정적 파일).

## 웹 페이지 (앱과 무관, 브라우저용)

| 주소 | 내용 |
| --- | --- |
| `/` | 메모가 있는 건물 목록과 건물별 메모 수 |
| `/buildings/{buildingId}` | 그 건물의 메모 목록 (사진, 작성자, 시간) |
