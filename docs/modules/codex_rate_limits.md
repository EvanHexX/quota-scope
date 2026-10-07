# Codex Rate Limits

## Purpose

QuotaScope는 Codex app-server JSON-RPC를 사용해 현재 계정의 rate limit snapshot을 읽는다.

## Confirmed Schema

현재 설치된 `codex-cli 0.125.0`에서 다음 명령으로 schema를 확인했다.

```powershell
cmd /c codex app-server generate-json-schema --experimental --out E:\Business\outputs\codex-app-server-schema
```

확인된 method와 notification:

- request: `account/rateLimits/read`
- notification: `account/rateLimits/updated`

`GetAccountRateLimitsResponse` 주요 field:

- `rateLimits`: backward-compatible 단일 snapshot
- `rateLimitsByLimitId`: limit id별 snapshot map, optional
- `rateLimitsByLimitId.codex_bengalfox`: `GPT-5.3-Codex-Spark` usage로 확인됨
- `RateLimitSnapshot.primary`: 짧은 window로 해석
- `RateLimitSnapshot.secondary`: 긴 window로 해석
- `RateLimitWindow.usedPercent`: 사용된 percent
- `RateLimitWindow.resetsAt`: Unix timestamp
- `RateLimitWindow.windowDurationMins`: window 길이

## Schema Update (codex-cli 0.145.0-alpha.27, 2026-07-22 실측)

실제 `account/rateLimits/read` 응답이 다음과 같이 변경됨을 확인했다:

```json
"rateLimits": {
  "limitId": "codex",
  "limitName": null,
  "primary":   { "usedPercent": 0, "windowDurationMins": 10080, "resetsAt": 1785269431 },
  "secondary": null,
  "credits":   { "hasCredits": true, "unlimited": false, "balance": "146.0874125000" },
  "individualLimit": null,
  "spendControlReached": false,
  "planType": "pro",
  "rateLimitReachedType": null
},
"rateLimitsByLimitId": {
  "codex": { "...": "rateLimits와 동일" },
  "codex_bengalfox": {
    "limitName": "GPT-5.3-Codex-Spark",
    "primary": { "usedPercent": 4, "windowDurationMins": 10080, "resetsAt": 1785269459 },
    "secondary": null,
    "credits": null
  }
},
"rateLimitResetCredits": { "availableCount": 0, "credits": [] }
```

변경 요점:

- **5시간 window가 사라졌다.** `primary`가 곧바로 주간(10080분) window이고 `secondary`는 `null`이다. Spark limit도 동일하게 주간 window 하나만 온다.
- 신규 field: `credits`(잔액 문자열), `individualLimit`, `spendControlReached`, top-level `rateLimitResetCredits`.
- 따라서 primary=짧은 window / secondary=긴 window라는 기존 가정은 더 이상 유효하지 않다. window 의미는 `windowDurationMins`로만 판별해야 한다.

## Handshake (codex-cli 0.146.0-alpha.3.1, 2026-07-25 실측)

app-server가 LSP/MCP식 2단계 핸드셰이크를 요구하도록 바뀌었다:

1. client -> `initialize` request
2. server -> `initialize` response
3. **client -> `initialized` notification** (`{"method":"initialized"}`, id 없음)
4. 이후에야 `account/rateLimits/read` 등 다른 request가 처리된다.

3단계를 생략하면 server는 `account/rateLimits/read`를 **조용히 무시**한다 (error 응답도 없음). QuotaScope 입장에서는 15초 timeout -> `Codex connection required` / `connection timed out`으로 보인다. 0.145.x까지는 `initialized` 없이도 동작했으나 0.146부터 필수다. `CodexAppServerClient.StartProcessAsync`가 initialize 응답 직후 `initialized` notification을 보낸다. `ClientNotification` schema에 `initialized`가 유일한 notification으로 정의되어 있다.

## Rolling Update와 5시간 + 주간 플랜 (codex-cli 0.162.0-alpha.2)

`codex app-server generate-json-schema`로 생성한 v2 schema(`AccountRateLimitsUpdatedNotification.json`, `GetAccountRateLimitsResponse.json`)에서 확인한 내용:

- `account/rateLimits/updated`의 params는 `{ "rateLimits": RateLimitSnapshot }` 하나뿐이다. schema 설명상 이것은 **sparse rolling update**이고, client는 받은 값을 가장 최근 `account/rateLimits/read` 응답에 merge하거나 snapshot을 다시 읽어야 한다. rolling update에서 비어 있는 nullable 값은 이전에 관찰된 값을 지우지 않는다.
- `RateLimitSnapshot`의 field는 모두 nullable이다: `limitId`, `limitName`, `planType`, `primary`, `secondary`, `credits`, `individualLimit`, `rateLimitReachedType`, `spendControlReached`, `normalModelSlug`.
- `RateLimitWindow`는 `usedPercent`(필수, int)와 nullable `resetsAt`, `windowDurationMins`로 구성된다.
- read 응답의 `rateLimits`는 backward-compatible 단일 bucket view이고, `rateLimitsByLimitId`는 `limit_id`(예: `codex`)별 multi-bucket view다.

이전 구현은 notification params를 full read처럼 mapping해 Codex usage 전체를 교체했다. 그래서 5h window만 담긴 update가 오면 다음 poll까지 주간 row, Spark row, Credits row가 사라졌고, Spark bucket(`codex_bengalfox`)의 update는 main row를 Spark 값으로 덮어썼다.

### 플랜별 window

- Pro: 주간(10080분) window 하나가 `primary`로 오고 `secondary`는 `null`이다 (위 0.145 실측).
- Plus 등 non-Pro 플랜: 5시간(300분) window와 주간 window가 함께 온다 (Claude와 같은 구성). maintainer가 확인한 사항이며, 이 문서 작성 시점에 Plus payload 원문은 캡처하지 않았다.
- 어느 window가 어느 slot에 오는지는 보장되지 않으므로 window 의미는 계속 `windowDurationMins`로만 판별한다.

### Rolling update merge 규칙

`RateLimitMapper.MergeRollingUpdate(lastRead, notificationParams)`가 merge된 read 형태의 결과를 돌려주고, 그 결과를 `FromJsonResult`가 평소처럼 mapping한다.

- `CodexAppServerClient`는 마지막으로 성공한 `account/rateLimits/read` 결과를 기억한다. 저장은 read loop 안에서 응답을 받은 순서대로 하므로, 응답 바로 뒤에 온 update는 그 응답에 merge된다. notification이 오면 merge 결과를 새 기준으로 저장하고 그 mapping을 `RateLimitsUpdated`로 올린다.
- 아직 read가 없으면(시작 직후, reconnect 직후) update만으로 usage를 만들지 않는다. 다음 poll이 full snapshot을 가져온다.
- 대상 bucket은 update의 `rateLimits.limitId`다. null이거나 없으면 main bucket이다. main bucket id는 마지막 read의 `rateLimits.limitId`(실제로는 `codex`, 비어 있으면 `codex`로 간주)다.
- main bucket update는 `rateLimits`와, 있으면 `rateLimitsByLimitId[<main id>]`에 함께 적용한다. 그 밖의 id는 `rateLimitsByLimitId[<id>]`에만 적용하며, entry나 map이 없으면 새로 만든다.
- null이거나 없는 field는 이전 값을 지우지 않는다. object(`primary`/`secondary` window, `credits`, `individualLimit`)는 field 단위로 merge한다: `usedPercent`는 덮어쓰고, null인 `resetsAt`/`windowDurationMins`는 이전 값을 유지한다.
- 예외: update window의 `windowDurationMins`가 들어갈 slot에 저장된 window의 길이와 다르면(둘 다 값이 있을 때) 다른 window이므로 field 단위 merge 대신 통째로 교체한다. field 단위로 merge하면 새 window가 이전 window의 `resetsAt`을 물려받기 때문이다. 이때 update의 `resetsAt`이 null이면 다음 poll까지 초기화 시각을 표시하지 않는다 (틀린 시각보다 낫다).
- Slot guard: update window의 `windowDurationMins`가 대상 bucket에서 **반대** slot의 이전 길이와 같고 자기 slot의 이전 길이와는 다르면, 반대 slot에 merge한다. slot만 보고 merge하면 5h + 7d 조합이 7d row 두 개로 바뀔 수 있기 때문이다. 두 window의 slot은 merge 전에 read 기준으로 함께 정하며, 두 window가 같은 slot으로 가는 일은 없다: 길이 일치가 자기 slot 이름보다 우선하고, slot을 양보한 window는 남은 slot으로 간다. 그래서 결과가 JSON property 순서에 따라 달라지지 않는다 (예: 주간 window 하나뿐인 read에 `primary` = 5h, `secondary` = 주간인 update가 오면, 주간은 `primary`에 merge되고 5h는 `secondary`로 간다).
- 형식이 잘못된 update(`rateLimits` object 없음 등)는 버리고 이전 값을 유지한다. 이 실패가 read loop를 끝내지 않는다 (read loop가 pending request도 완료시키기 때문).
- reconnect는 기억한 read를 지운다. 새 process가 다른 계정으로 로그인되어 있을 수 있기 때문이다.

### Row 순서와 라벨

- 한 snapshot 안에서 `primary`/`secondary` window는 `windowDurationMins` 오름차순(짧은 것 먼저, null은 마지막)으로 row가 된다. backend가 어느 slot을 쓰든 5h row가 주간 row 앞에 온다 (Claude의 5h -> 7d 순서와 같음). Spark snapshot도 같은 규칙을 따른다.
- 표시 라벨(`app-winui/Loc.cs`의 `RowLabel`)은 `<scope> · <window>` 형식이고, scope는 같은 window의 row끼리 구분이 필요할 때만 붙인다 (maintainer 결정, 2026-10-08):
  - 5시간 window가 있는 플랜(Plus 등): `5h` -> `5h` / `5시간`, `7d`, `1w` -> `Weekly` / `주간`
  - 5시간 window가 없는 플랜(Pro: 주간만), 즉 main bucket의 모든 window 길이가 하루 이상으로 확인된 경우: main row에 플랜 이름을 붙인다. 길이(`windowDurationMins`)를 모르는 window가 있으면 그것이 5h일 수 있으므로 붙이지 않는다 (`Pro · Weekly` / `Pro · 주간`). mapper가 `planType`에서 플랜 이름을 정해 `UsageRow.Scope`에 넣는다. 표시 이름이 확실한 플랜(`free`, `go`, `plus`, `pro`, `team`, `business`, `enterprise`, `edu`)만 이름을 붙이고, 그 밖의 값(`unknown` 포함)은 추측하지 않고 scope 없이 표시한다.
  - `Spark 5h` -> `Spark · 5h` / `Spark · 5시간`, `Spark 7d` -> `Spark · Weekly` / `Spark · 주간`
  - 그 밖의 길이는 duration 라벨을 그대로 쓴다.
- 팝업, 트레이 툴팁, 설정 행 목록이 모두 같은 `RowLabel`을 쓴다. Win32 툴팁은 127자에서 잘리고 Codex가 먼저 나오므로 라벨을 짧게 유지한다.
- 설정 key는 영어 raw 라벨(`codex|5h`, `codex|7d`)을 쓰고 플랜 이름은 key에 들어가지 않으므로, 표시 라벨이 바뀌어도 저장된 설정은 그대로다.

## Mapping

- 전 계층 수치는 `usedPercent`로 통일한다 (0 = 미사용, 100 = 소진). remaining 값은 어디에도 존재하지 않는다.
- Row는 payload 기반 동적 생성이다. 존재하는 window만 row가 되고, raw 라벨은 `windowDurationMins`에서 유도한다 (300 -> `5h`, 10080 -> `7d`). 라벨 단위는 Claude와 통일된 시간/일 단위를 쓴다. 화면에 보이는 라벨은 `Loc.RowLabel`이 raw 라벨에서 만든다 (위 "Row 순서와 라벨").
- Row 순서는 slot 순서가 아니라 window 길이 순서다 (짧은 것 먼저). Pro는 주간 row 하나, Plus 등은 5h row 다음 주간 row가 된다.
- `account/rateLimits/updated`는 단독으로 mapping하지 않는다. 마지막 read에 merge한 결과를 mapping한다 (위 "Rolling update merge 규칙").
- overall 수치는 main snapshot의 window usedPercent 중 가장 높은 값이다.
- Spark rows는 `limitName`/`limitId`에서 `spark`, `bengalfox`, `gpt-5.3-codex`를 찾고, 해당 snapshot의 window들을 `Spark <라벨>` secondary row로 표시한다.
- `credits.hasCredits == true`이면 잔액을 `Credits` row로 표시할 수 있다 (per-provider 표시 옵션, 기본 off). Claude의 `extra_usage`와 대칭 구조다.
- `balance`는 **남은** 잔액이고 상한이 없으므로, gauge는 provider 설정 `CreditsFullAmount`(기본 2500)를 100% 기준으로 삼아 usedPercent = `(1 - balance / 기준값) * 100`으로 그린다. `unlimited`이거나 기준값이 0이거나 `balance` 파싱에 실패하면 gauge 없이 잔액 텍스트만 표시한다. 잔액이 기준값보다 크면 usedPercent를 0으로 고정하고(잔여량 지표에서 gauge가 가득 참) `UsageRow.BeyondFull`을 세워, 팝업 퍼센트가 `100%` 대신 `>100%`로 나오게 한다 (잔여량 지표일 때. 사용량 지표에서는 `0%`). 보조 텍스트는 실제 잔액을 그대로 쓴다 (예: `3000 / 2500`).
- `Credits` row는 `IsPrimary: false`이므로 overall 수치와 트레이 아이콘에 반영되지 않고, 트레이 툴팁의 window 목록에서도 제외된다.

## Failure Handling

- app-server 시작 실패: `Codex connection required` 상태를 popup에 표시한다.
- timeout/cancel failure: `Codex connection timed out. Use Settings > Codex Connection > Reconnect.`를 표시한다.
- `Settings > Codex Connection > Reconnect`는 child process를 종료하고 새 app-server process를 initialize한 뒤 rate limit을 다시 읽는다.
- JSON-RPC error: error text를 상태 문구에 포함한다.
- schema field가 없거나 null이면 해당 row는 `--%`, `reset --`로 표시한다.
- 앱은 app-server를 stdio child process로 실행하고 종료 시 process tree를 정리한다.



