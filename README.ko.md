<p align="right"><a href="README.md">🇺🇸 English README</a></p>

# QuotaScope

![Platform](https://img.shields.io/badge/platform-Windows-0078D4?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![UI](https://img.shields.io/badge/UI-WinUI%203-0078D4)
![Providers](https://img.shields.io/badge/providers-Codex%20%7C%20Claude-111827)
![Privacy](https://img.shields.io/badge/privacy-local--only-10B981)
![Status](https://img.shields.io/badge/status-unofficial-6B7280)

QuotaScope는 각 provider의 앱, CLI, 대시보드를 계속 열어두지 않아도 Codex와 Claude의 사용량/rate limit을 빠르게 확인할 수 있는 작은 Windows 트레이 유틸리티입니다.

Codex는 로컬에서 `codex app-server`를 실행해 stdio JSON-RPC로 rate limit 데이터를 읽고, Claude는 로컬 Claude Code 로그인 정보를 이용해 Anthropic 사용량 endpoint를 폴링합니다. 결과는 작은 트레이 팝업에 함께 표시됩니다.

## 스크린샷

<img src="docs/images/quota-scope.png" alt="QuotaScope 사용량 팝업과 설정창" width="900">

| UI 크기 90% 팝업 | 글래스모피즘 + UI 크기 150% |
|---|---|
| <img src="docs/images/quota-scope-90.png" alt="Codex와 Claude 사용량을 표시하는 QuotaScope 팝업" width="330"> | <img src="docs/images/quota-scope-150.png" alt="배경이 비치는 글래스모피즘 적용 팝업" width="330"> |

레이아웃은 행 단위로 지정할 수 있습니다 — 게이지/막대, 순서, 열 수:

<img src="docs/images/quota-scope-settings.png" alt="행별 모양을 지정하는 모양 설정 화면" width="640">

## 설치

1. [최신 릴리스](https://github.com/EvanHexX/quota-scope/releases/latest)에서 `QuotaScope-win-x64.zip`을 내려받습니다.
2. 원하는 위치에 압축을 풉니다.
3. `QuotaScopeWinUI.exe`를 실행합니다.

self-contained 빌드라 .NET이나 Windows App SDK 런타임을 따로 설치할 필요가 없습니다. Windows 11을 권장합니다 (Windows 10에서도 동작하지만 팝업 모서리가 둥글게 처리되지 않습니다).

## 주요 기능

- 트레이 상주 + 컴팩트 사용량 팝업
- Codex와 Claude(Pro/Max) 사용량을 provider별 구분 섹션으로 동시에 표시
- Payload 기반 행 생성: provider가 보고하는 rate limit window마다 1행이라, 새 window(예: 모델별 주간 한도)가 생겨도 앱 업데이트 없이 표시됨
- 선택 표시: secondary 행(GPT-5.3-Codex-Spark, Claude 모델별 window)과 credits 행
- 트레이 아이콘은 전체 사용률을 호 채움(5% 단위)과 3단계 상태 색상으로 표시, 정확한 수치는 툴팁과 팝업에서 제공
- 경고 임계값 진입 시 트레이 알림 (선택)
- 레이아웃: 막대 / 게이지 / 믹스 & 매치(행별 모양 지정 + 1열·2열 강제)
- 테마: 다크, 라이트, 미드나잇, 시스템 테마 따르기, 글래스모피즘(4단계 강도)
- UI 크기 조절(80~150%)과 한국어/영어 인터페이스
- 사용자 지정 전역 단축키: 팝업 토글(기본 `Ctrl+Alt+U`), 전체 새로 고침, 핀 토글
- 독립 설정창, 팝업 고정(핀), Windows 시작 시 자동 실행
- 로컬 설정 저장, 별도 자격증명 불필요

## 동작 방식

Codex:

1. 앱이 `codex app-server`를 child process로 실행합니다.
2. stdio JSON-RPC session을 초기화합니다.
3. `account/rateLimits/read`를 호출합니다.
4. `account/rateLimits/updated` notification을 수신합니다.

Claude:

1. 매 폴링마다 로컬 Claude Code 자격증명 파일(`%USERPROFILE%\.claude\.credentials.json`)에서 access token을 읽습니다. 토큰은 저장/복사/로깅하지 않습니다.
2. Anthropic OAuth 사용량 endpoint(Claude Code `/usage` 커맨드와 동일한 데이터)를 최소 60초 간격으로 폴링합니다.
3. 실패 시 마지막 성공 데이터를 stale 상태로 계속 표시합니다.

provider는 서로 격리되어 있어 한쪽 실패가 다른 쪽에 영향을 주지 않습니다. 텔레메트리는 수집하지 않습니다.

## 요구사항

- Windows 10 이상 (Windows 11 권장)
- `codex` 명령으로 접근 가능한 Codex CLI / Codex app-server (선택, Codex 사용량 표시에만 필요)
- 같은 머신에서 Claude Code 로그인 (선택, Claude 사용량 표시에만 필요)
- 소스 빌드 시에만 .NET 10 SDK

## 사용법

- 트레이 아이콘 좌클릭으로 팝업을 열고 닫습니다.
- `Ctrl+Alt+U`(또는 직접 지정한 단축키)로 팝업을 토글합니다.
- 핀 버튼으로 팝업을 고정합니다.
- 팝업 헤더를 드래그해 위치를 옮깁니다.
- 시간 텍스트를 클릭하면 예정 시각 ↔ 남은 시간이 전환됩니다.
- 트레이 아이콘 우클릭: 새로 고침, 재연결, 설정, 종료.
- 팝업 우클릭에서도 같은 메뉴를 사용할 수 있습니다.

Claude 로그인이 필요한 상태에서 `재연결`을 실행하면 Claude Code 로그인 터미널이 자동으로 열립니다. 브라우저 로그인만 완료하면 사용량 표시가 자동으로 재개됩니다.

## 설정

모든 설정은 설정창(트레이 아이콘 → `설정...`)에서 즉시 적용되며, 실행 파일 옆 `settings.json`에 저장됩니다.

- **일반**: 언어, Windows 시작 시 자동 실행, 팝업 위치("마지막 위치" 포함), 시간 표시, 경고 임계값, 임계값 알림
- **프로바이더**: provider별 사용 여부, 갱신 주기, secondary 행, credits 행, Codex 명령, Claude 자격증명 상태, 재연결
- **모양**: 게이지 모양(막대 / 게이지 / 믹스 & 매치), 행별 모양과 열 수, UI 크기, 테마, 글래스모피즘과 강도, 트레이 아이콘 스타일, 게이지 지표
- **단축키**: 팝업 토글, 전체 새로 고침, 핀 토글 — 조합키를 눌러 지정하며, 충돌 시 인라인 오류로 알리고 저장하지 않습니다
- **정보**: 버전, 면책 문구, 저장소 링크

참고:

- Claude 폴링은 설정값과 무관하게 최소 60초로 제한됩니다.
- 게이지와 퍼센트는 게이지 지표 설정(사용량/잔여량)을 따르며, 상태 색상은 항상 사용률 기준입니다.

## Privacy

QuotaScope는 로컬 유틸리티로 설계되었습니다.

- 로컬 `codex app-server` 프로세스를 실행하고 stdio JSON-RPC로 통신합니다.
- Claude 사용량 표시를 위해 로컬 Claude Code 자격증명 파일을 읽고 Anthropic 사용량 endpoint를 HTTPS로 호출합니다. 앱이 원격 서비스와 통신하는 유일한 지점이며, Claude provider를 끄면 발생하지 않습니다.
- Claude access token은 매 요청 시점에만 읽어 사용하고 저장/복사/로깅하지 않습니다.
- 텔레메트리, 애널리틱스, 원격 로깅이 없습니다.
- 설정은 로컬에만 저장됩니다.

## 현재 제한사항

- Windows 전용입니다.
- Codex app-server의 동작과 제공되는 rate limit field에 의존합니다.
- Claude 사용량은 미문서화 Anthropic endpoint에 의존하며, 예고 없이 변경되거나 중단될 수 있습니다.
- Claude 사용량 표시는 같은 머신의 Claude Code 로그인이 필요합니다.
- cloud dashboard나 analytics product가 아니라 local tray utility입니다.
- 아직 인스톨러와 자동 업데이트가 없으며, 릴리스는 portable zip으로 제공됩니다.

## Provider 범위

- 승인된 provider 범위는 Codex(OpenAI)와 Claude(Anthropic)입니다.
- 두 provider 모두 구현되어 있으며, 그 외 provider는 명시적 승인 없이 추가하지 않습니다.

> 이 프로젝트의 이전 이름은 `Codex Usage Tray`이며, 2026-07-22에 `QuotaScope`로 변경했습니다. 기존 Windows Forms 앱은 `app/`에 남아 있고, 릴리스되는 것은 `app-winui/`의 WinUI 3 앱입니다.

## 소스 빌드

```powershell
dotnet build app-winui/QuotaScope.WinUI.csproj
```

내장 self-test 실행 (rate limit mapper와 단축키 파서):

```powershell
dotnet run --project app-winui/QuotaScope.WinUI.csproj -- --self-test
```

self-contained 빌드 publish:

```powershell
dotnet publish app-winui/QuotaScope.WinUI.csproj -c Release -r win-x64 --self-contained true
```

## Disclaimer

> This project is not affiliated with, endorsed by, or sponsored by OpenAI or Anthropic. Codex is a product/service of OpenAI. Claude is a product/service of Anthropic.

## 라이선스

[MIT](LICENSE)

## 문서

프로젝트 메모는 `docs/` 아래에 있습니다.

- `docs/README.md`: 운영/동작 메모
- `docs/PROJECT_MAP.md`: module과 실제 file path map
- `docs/MODERNIZATION_PLAN.md`: .NET / WinUI modernization plan
- `docs/WINUI3_PARITY.md`: WinUI 3 포팅 메모, parity 체크리스트, 창/백드롭 이슈 기록
- `docs/modules/codex_rate_limits.md`: Codex app-server rate limit schema와 mapping notes
- `docs/modules/claude_rate_limits.md`: Claude 사용량 endpoint schema와 mapping notes
