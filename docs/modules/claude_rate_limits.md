# Claude Rate Limits

## Purpose

QuotaScope는 Anthropic의 미문서화 OAuth usage endpoint를 폴링해 Claude Pro/Max 사용량을 읽는다. Claude Code의 `/usage` 슬래시 커맨드가 사용하는 것과 동일한 서버 측 데이터이며, 커뮤니티 도구(claude-code-statusline 등)가 같은 endpoint를 사용 중이다.

**주의: 비공식 endpoint다. 예고 없이 변경되거나 중단될 수 있다.**

## Request

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <claudeAiOauth.accessToken>
anthropic-beta: oauth-2025-04-20
User-Agent: claude-code/<version>
```

- `User-Agent`는 필수다. `claude-code/` 형태가 아니면 지속적으로 429가 반환된다. 현재 앱은 `claude-code/2.0.0`을 보낸다.

## Token

- Windows: `%USERPROFILE%\.claude\.credentials.json` → `claudeAiOauth.accessToken`
- 앱은 토큰을 **저장/복사/로깅하지 않는다**. 매 폴링마다 파일을 읽어 요청에 쓰고 버린다.
- 토큰 refresh는 Claude Code가 담당한다. 앱은 refresh grant를 직접 호출하지 않고, 자격증명 파일에 쓰지도 않는다.
- access token TTL은 **8시간**이다 (`claudeAiOauth.expiresAt`, epoch ms). Claude Code를 그동안 한 번도 실행하지 않으면 토큰이 만료되어 401이 난다.
- 401 이후에는 자격증명 파일의 수정 시각이 바뀔 때까지(=Claude Code가 재로그인/refresh로 파일을 다시 쓸 때까지) 폴링을 일시 중단한다.
- WinUI 앱에서 로그인이 필요한 상태로 재연결을 실행하면 `cmd /k claude /login` 터미널을 자동으로 띄운다. 사용자는 브라우저 로그인만 완료하면 되고, 파일 변화 감지로 폴링이 자동 재개된다.

## Session Auto-Renew

만료 8시간마다 수동 재연결을 요구하지 않기 위해, 앱은 **갱신을 Claude Code에 위임**한다. 앱이 refresh token을 쓰거나 저장하는 일은 없다.

- `expiresAt`까지 **15분 이하**로 남으면(`ClaudeSessionRenewer.RenewLeadTime`) `claude auth status`를 헤드리스로 1회 실행한다. read-only 명령이라 **사용량을 소모하지 않는다**. 갱신이 일어나면 Claude Code가 자격증명 파일을 다시 쓰고, 파일 감시가 그 결과를 반영한다.
- 401을 이미 받은 뒤(앱이 절전/휴면으로 만료 시점을 지나친 경우)에도 같은 nudge를 시도한 뒤 `Unauthenticated`로 일시 중단한다.
- 자격증명 파일이 안 바뀐 nudge는 실패로 보고 쿨다운을 2m → 10m → 30m로 늘린다. 연속 5회 실패하면 파일이 바뀔 때까지(수동 로그인/재연결) 프로세스 실행을 멈춘다.
- `claude` 실행 파일은 `%USERPROFILE%\.local\bin\claude.exe` 등 알려진 설치 경로를 먼저 찾고, 없으면 `cmd.exe /c claude ...`로 PATH shim을 탄다.
- `claude auth status` 출력에는 계정 식별 정보가 들어 있으므로 **읽어서 버리기만 하고 어디에도 기록하지 않는다**. 자식 프로세스는 창 없이 실행하며 30초 후 강제 종료한다.
- per-provider 설정 `AutoRenewSession`(기본 `true`)으로 끌 수 있다. 끄면 종전 동작 그대로 만료 후 수동 재연결이 필요하다.

## Session Visibility

- 자격증명 파일에 `FileSystemWatcher`를 걸어, Claude Code가 파일을 다시 쓰면 다음 폴링을 기다리지 않고 즉시 재조회 후 `UsageUpdated`로 밀어 올린다 (2초 디바운스, 중복 이벤트 1건으로 병합). watcher를 못 걸어도 기존 폴링 복구 경로가 그대로 동작한다.
- 정상 상태의 `StatusText`는 세션 상태를 반영한다: 갱신 중이면 `Renewing Claude session`, 만료가 임박하면 `Session expires in 12m`, 그 외에는 `Claude rate limit`. 임박 기준은 auto-renew가 켜져 있으면 15분, 꺼져 있으면 60분이다.

## Response

```json
{
  "five_hour":        { "utilization": 33.0, "resets_at": "2026-04-11T07:00:00.528743+00:00" },
  "seven_day":        { "utilization": 13.0, "resets_at": "2026-04-17T00:59:59.951713+00:00" },
  "seven_day_opus":   null,
  "seven_day_sonnet": { "utilization": 1.0,  "resets_at": "..." },
  "extra_usage":      { "is_enabled": false, "monthly_limit": null, "used_credits": null, "utilization": null }
}
```

- `utilization`: 사용률 0~100 (소수 가능, 활성 window 없으면 0)
- `resets_at`: ISO 8601 UTC 문자열. Codex의 epoch 초/밀리초와 다르므로 **파서를 공유하지 않는다**.

## Row Mapping

**신규 스키마 (2026-07-22 실측):** 응답에 `limits` 배열이 추가되었고, 구 `seven_day_<model>` 필드들은 null로 온다. `limits`가 존재하면 그것이 단일 진실 소스다:

```json
"limits": [
  { "kind": "session",       "group": "session", "percent": 20, "resets_at": "...", "scope": null, "is_active": false },
  { "kind": "weekly_all",    "group": "weekly",  "percent": 28, "resets_at": "...", "scope": null, "is_active": false },
  { "kind": "weekly_scoped", "group": "weekly",  "percent": 55, "resets_at": "...",
    "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null }, "is_active": true }
]
```

| limits 항목 | UsageRow |
|---|---|
| `scope == null`, group `session` | primary, `5h` |
| `scope == null`, group `weekly` | primary, `7d` |
| scoped (model/surface) | `7d <이름>` — **`is_active == true`면 primary(항상 표시 + overall 반영)**, 아니면 secondary(모델별 행 토글) |
| `extra_usage` | `is_enabled == true`일 때만 secondary, `Credits` |

`limits`가 없는 구 응답은 기존 필드 매핑으로 폴백한다: `five_hour`/`seven_day` → primary, `seven_day_<model>` 접두어 동적 매핑 → secondary.

- null인 필드는 row를 만들지 않는다.
- overall 수치는 primary row(5h, 7d)의 usedPercent 중 최댓값이다 (전 계층 usedPercent 통일).
- secondary row와 Credits row 표시는 per-provider 옵션(`ShowSecondaryRows`, `ShowCredits`)을 따른다.

## Reliability

- 폴링 간격 하한 60초. 설정값이 더 짧아도 provider가 60초로 clamp한다.
- 마지막 성공 응답을 캐시한다. 네트워크/파싱 실패 시 캐시를 `Stale` 상태로 계속 표시한다 (빈 화면 금지).
- 429: 지수 백오프 60s → 5m → 15m(상한), `RateLimited` 상태로 캐시 표시.
- 401: `Unauthenticated` 상태, 자격증명 파일이 바뀔 때까지 폴링 중단.
- 자격증명 파일 부재: `Unauthenticated`, 안내 문구 "Run Claude Code and sign in".
- Claude provider의 실패는 Codex provider나 앱 전체에 영향을 주지 않는다 (per-provider 격리).

## Files

- `app/Providers/Claude/ClaudeUsageProvider.cs`: 폴링/캐시/백오프/상태 머신 + 자격증명 파일 감시
- `app/Providers/Claude/ClaudeCredentialReader.cs`: 자격증명 파일 읽기 (토큰 비보존, `expiresAt` 조회)
- `app/Providers/Claude/ClaudeSessionRenewer.cs`: 세션 자동 갱신 nudge/쿨다운 + `RunSelfTest()`
- `app/Providers/Claude/ClaudeCommandResolver.cs`: `claude` CLI 경로 해석
- `app/Providers/Claude/ClaudeUsageMapper.cs`: 응답 → `ProviderUsage` 매핑 + `RunSelfTest()`
