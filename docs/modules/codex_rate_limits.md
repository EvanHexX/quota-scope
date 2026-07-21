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

## Mapping

- 앱에 표시하는 percent는 `remaining = 100 - usedPercent`다.
- `windowDurationMins <= 1440`인 window 중 가장 짧은 값을 `5시간` row로 표시한다.
- `windowDurationMins >= 8640`인 window 중 가장 긴 값을 `1주` row로 표시한다.
- overall 남은 사용량은 primary/secondary remaining 중 더 낮은 값으로 표시한다.
- Spark rows는 `limitName`/`limitId`에서 `spark`, `bengalfox`, `gpt-5.3-codex`를 찾고, 해당 snapshot의 primary를 `Spark 5h`, secondary를 `Spark 1w`로 표시한다.

## Failure Handling

- app-server 시작 실패: `Codex connection required` 상태를 popup에 표시한다.
- timeout/cancel failure: `Codex connection timed out. Use Settings > Codex Connection > Reconnect.`를 표시한다.
- `Settings > Codex Connection > Reconnect`는 child process를 종료하고 새 app-server process를 initialize한 뒤 rate limit을 다시 읽는다.
- JSON-RPC error: error text를 상태 문구에 포함한다.
- schema field가 없거나 null이면 해당 row는 `--%`, `reset --`로 표시한다.
- 앱은 app-server를 stdio child process로 실행하고 종료 시 process tree를 정리한다.



