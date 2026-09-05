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

- `expiresAt`까지 **15분 이하**로 남으면(`ClaudeSessionRenewer.RenewLeadTime`) `claude mcp list`를 헤드리스로 1회 실행한다. read-only 명령이라 **사용량을 소모하지 않는다**(실측 ~2.2초). 갱신이 일어나면 Claude Code가 자격증명 파일을 다시 쓰고, 파일 감시가 그 결과를 반영한다.

> **어떤 명령이 갱신을 유발하는가 (2026-08-13 실측, claude 2.x)**
> 만료 5시간 경과 토큰으로 측정한 결과:
> - `claude auth status` → **갱신 안 함.** 저장된 refresh token 유효성만 보고 `loggedIn: true`를 출력할 뿐 파일을 다시 쓰지 않는다. 이때 `.credentials.json`의 access token은 usage endpoint에서 401을 받는다.
> - `claude mcp list` → **갱신함.** `expiresAt`이 실행 시각 +8h로 이동하고 파일이 다시 쓰인다.
>
> refresh token은 회전하지 않는다: 갱신 전후 `refreshTokenExpiresAt`이 동일한 절대 시각을 유지하므로(2026-09-06 10:38), 반복 갱신이 refresh token 수명을 갉아먹지 않는다.
>
> 이는 문서화된 계약이 아니라 CLI 구현 특성이다. 향후 CLI가 여기서 갱신을 멈추면 아래 실패 경로(쿨다운 → 5회 후 중단 → 수동 로그인 안내)로 degrade한다.
- 401을 이미 받은 뒤(앱이 절전/휴면으로 만료 시점을 지나친 경우)에도 같은 nudge를 시도한 뒤 `Unauthenticated`로 일시 중단한다.
- nudge 실패 판정은 **토큰이 이미 죽었을 때만** 적용된다. 만료 전 선제 nudge가 파일을 안 바꾸는 건 정상일 수 있다 — Claude Code가 자체 기준으로 "아직 갱신 불필요"라고 판단한 경우이고, 그 임계값은 우리가 아는 값이 아니다. 따라서:
  - **선제 nudge**(만료 전) no-op → 실패로 세지 않고 `ProactiveRetry`(2분) 후 재시도. 포기하지 않는다.
  - **복구 nudge**(만료 시각 경과 또는 401 수신) no-op → 실패로 세고 쿨다운 2m → 10m → 30m. 연속 5회면 파일이 바뀔 때까지 중단.
  - 이 구분이 없으면 선제 nudge가 만료 전 15분 동안 실패 카운터를 소진해, 정작 401이 난 순간 30분 쿨다운에 걸려 있게 된다.
- `claude` 실행 파일은 `%USERPROFILE%\.local\bin\claude.exe` 등 알려진 설치 경로를 먼저 찾고, 없으면 `cmd.exe /c claude ...`로 PATH shim을 탄다.
- **자식 프로세스 수명**: nudge는 `claude` CLI를 1회 실행하고 끝난다. 실측(2026-09-02) 결과 직계 자식 1개, exit 0, ~3초, 종료 10초 후 잔존 `claude`/`node` 프로세스 **0개** — MCP 서버를 띄우거나 백그라운드에 남기지 않는다. 다만 앱이 nudge 진행 중에 종료되면 대기 스레드가 프로세스와 함께 죽어 30초 타임아웃 kill이 실행되지 않으므로, `ClaudeSessionRenewer.Dispose()`가 진행 중인 자식을 `Kill(entireProcessTree)`로 회수한다. `ClaudeUsageProvider.Dispose()`가 이를 호출한다. 이게 없으면 멈춘 `claude`가 285MB 실행 파일을 잠근 채 남아 이후 CLI 업데이트를 막을 수 있다.
- 이 경로는 Windows 서비스나 svchost와 무관하다. QuotaScope는 서비스를 등록하지 않고, nudge는 `%USERPROFILE%\.local\bin\claude.exe`(CLI 네이티브 설치)만 실행한다. Claude **데스크톱 앱**은 별개 MSIX 패키지이며 자체 auto-start 서비스(`CoworkVMService`)를 갖는데, 이는 QuotaScope와 아무 관련이 없다.
- `claude mcp list` 출력에는 사용자가 설정한 서버 목록이 들어 있으므로 **읽어서 버리기만 하고 어디에도 기록하지 않는다**. 자식 프로세스는 창 없이 실행하며 30초 후 강제 종료한다.
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
| `extra_usage` | 항상 secondary `Credits` row 1개. `is_enabled`는 읽지 않는다 |

`limits`가 없는 구 응답은 기존 필드 매핑으로 폴백한다: `five_hour`/`seven_day` → primary, `seven_day_<model>` 접두어 동적 매핑 → secondary.

- null인 필드는 row를 만들지 않는다. `extra_usage`는 예외로, 값이 전부 null이어도 `Credits` row는 만든다 (소비 0).
- `Credits` gauge의 분모는 `monthly_limit` > 0이면 그 값, 아니면 provider 설정 `CreditsFullAmount`(기본 2500)다. `utilization`이 오면 그 값이 그대로 usedPercent가 된다.
- `Credits` row의 보조 텍스트(`남은 값 / 기준값`)는 실제로 그려진 usedPercent에서 역산한다. `used_credits`가 없거나 `utilization`과 어긋나도 막대와 텍스트가 어긋나지 않게 하기 위함이다.
- 분모가 없으면(설정값 0 + `monthly_limit` 없음 + `utilization` 없음) gauge 없이 텍스트 row로 떨어진다.
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
