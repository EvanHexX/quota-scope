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

## Mapping

- 앱에 표시하는 percent는 `remaining = 100 - usedPercent`다.
- Row는 payload 기반 동적 생성이다. 존재하는 window만 row가 되고, 라벨은 `windowDurationMins`에서 유도한다 (300 -> `5h`, 10080 -> `7d`). 라벨 단위는 Claude와 통일된 시간/일 단위를 쓴다.
- overall 남은 사용량은 main snapshot의 window remaining 중 가장 낮은 값으로 표시한다.
- Spark rows는 `limitName`/`limitId`에서 `spark`, `bengalfox`, `gpt-5.3-codex`를 찾고, 해당 snapshot의 window들을 `Spark <라벨>` secondary row로 표시한다.
- `credits.hasCredits == true`이면 잔액을 `Credits` row로 표시할 수 있다 (per-provider 표시 옵션, 기본 off). Claude의 `extra_usage`와 대칭 구조다.

## Failure Handling

- app-server 시작 실패: `Codex connection required` 상태를 popup에 표시한다.
- timeout/cancel failure: `Codex connection timed out. Use Settings > Codex Connection > Reconnect.`를 표시한다.
- `Settings > Codex Connection > Reconnect`는 child process를 종료하고 새 app-server process를 initialize한 뒤 rate limit을 다시 읽는다.
- JSON-RPC error: error text를 상태 문구에 포함한다.
- schema field가 없거나 null이면 해당 row는 `--%`, `reset --`로 표시한다.
- 앱은 app-server를 stdio child process로 실행하고 종료 시 process tree를 정리한다.



