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
- 토큰 refresh는 Claude Code가 담당한다. 앱은 refresh를 시도하지 않는다.
- 401 이후에는 자격증명 파일의 수정 시각이 바뀔 때까지(=Claude Code가 재로그인/refresh로 파일을 다시 쓸 때까지) 폴링을 일시 중단한다.

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

| 응답 필드 | UsageRow |
|---|---|
| `five_hour` | primary, `5h` |
| `seven_day` | primary, `7d` |
| `seven_day_<model>` (sonnet/opus/fable 등) | secondary, `7d <Model>` — **키를 하드코딩하지 않고 `seven_day_` 접두어를 동적 매핑**하므로 새 모델 창이 추가돼도 코드 변경 없이 표시된다 |
| `extra_usage` | `is_enabled == true`일 때만 secondary, `Credits` (utilization 게이지, 없으면 used/limit 텍스트) |

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

- `app/Providers/Claude/ClaudeUsageProvider.cs`: 폴링/캐시/백오프/상태 머신
- `app/Providers/Claude/ClaudeCredentialReader.cs`: 자격증명 파일 읽기 (토큰 비보존)
- `app/Providers/Claude/ClaudeUsageMapper.cs`: 응답 → `ProviderUsage` 매핑 + `RunSelfTest()`
