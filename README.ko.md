# QuotaScope

QuotaScope는 Codex 앱, CLI, 대시보드를 계속 열어두지 않아도 Codex app-server의 rate limit 상태를 빠르게 확인할 수 있는 작은 Windows 트레이 유틸리티입니다.

현재 릴리즈 목표는 가벼운 Windows Forms 버전입니다. WinForms 버전을 안정화해 릴리즈한 뒤, 이후 WinUI 3 구현으로 포팅할 계획입니다.

> 현재 제품 방향: WinForms 버전은 작고 명확한 실사용 도구로 유지합니다. 릴리즈 전 실험적인 Glassmorphism theme은 제거하고, 이 WinForms 릴리즈를 WinUI 3 포팅의 기능 기준선으로 사용합니다.

## 무엇을 하는 앱인가

QuotaScope는 로컬에서 `codex app-server`를 실행하고, stdio JSON-RPC를 통해 현재 Codex rate limit 데이터를 읽은 뒤, 그 결과를 작은 트레이 팝업에 표시합니다.

Codex를 자주 사용하는 사용자가 작업 중 남은 사용량과 reset 상태를 빠르게 확인하기 위한 도구입니다.

## 주요 기능

- Windows system tray 유틸리티
- Codex 사용량 window를 보여주는 compact popup
- Payload 기반 사용량 gauge: provider가 보고하는 rate limit window마다 1행
- 선택 가능한 GPT-5.3-Codex-Spark 사용량 행
- 선택 가능한 credits 잔액 행
- Pinned popup mode
- Global hotkey: `Ctrl+Alt+U`
- 수동 refresh 및 reconnect control
- 위치, 시간 표시 방식, shape theme, color theme 설정
- 로컬 `settings.json` 저장
- 별도 OpenAI API key 불필요

## 현재 UI

현재 WinForms UI는 원형 사용량 card를 가진 compact dark tray popup입니다.

표시 행은 payload 기반으로 동적으로 생성됩니다. provider가 보고하는 rate limit window만 행이 되며, 라벨은 window 길이에서 유도됩니다 (예: 300분 -> `5h`, 10080분 -> `1w`).

선택 표시 항목:

- `Spark <window>` (GPT-5.3-Codex-Spark)
- `Credits` (잔액)

현재 UI는 의도적으로 단순하게 유지합니다. WinUI 3 포팅 전 기능 기준선으로 사용하기 위한 버전입니다.

## 동작 방식

1. 앱이 `codex app-server`를 child process로 실행합니다.
2. stdio JSON-RPC session을 초기화합니다.
3. `account/rateLimits/read`를 호출합니다.
4. `account/rateLimits/updated` notification을 수신합니다.
5. 새 rate limit 데이터가 들어오면 tray popup을 갱신합니다.

이 앱은 로컬 Codex runtime session을 사용합니다. 별도의 OpenAI API key를 요구하거나 저장하지 않습니다.

## 요구사항

- Windows
- `codex` 명령으로 접근 가능한 Codex CLI / Codex app-server
- 로컬 개발용 .NET 10 SDK

현재 프로젝트 target:

```text
net10.0-windows
Windows Forms
```

## 로컬 실행

Repository root에서 실행:

```powershell
dotnet run --project app/QuotaScope.csproj
```

또는 app directory에서 실행:

```powershell
cd app
dotnet run
```

내장 mapper self-test 실행:

```powershell
dotnet run --project app/QuotaScope.csproj -- --self-test
```

## 설정

앱은 실행 output folder 옆에 로컬 설정 파일을 저장합니다.

```text
settings.json
```

설정 예시:

```json
{
  "Hotkey": "Ctrl+Alt+U",
  "WarningThresholdPercent": 20,
  "PopupGraph": "half-circle",
  "PopupPosition": "BottomRight",
  "ShapeTheme": "Bars",
  "ColorTheme": "DarkBluePurple",
  "TimeDisplayMode": "ClockTime",
  "IsPinned": false,
  "Providers": {
    "codex": {
      "Enabled": true,
      "RefreshSeconds": 60,
      "ShowSecondaryRows": false,
      "ShowCredits": false,
      "Command": "codex"
    }
  }
}
```

참고:

- `TimeDisplayMode`는 `ClockTime` 또는 `RemainingTime`을 사용할 수 있습니다.
- Provider별 옵션은 `Providers` 아래에 provider 단위로 저장됩니다.
- `ShowSecondaryRows`를 켜면 secondary model 행(GPT-5.3-Codex-Spark)이 표시됩니다.
- `ShowCredits`를 켜면 credits 잔액 행이 표시됩니다.
- `Hotkey` 설정은 파일에 존재하지만, 현재 실제 등록된 hotkey는 `Ctrl+Alt+U`로 고정되어 있습니다.

## 현재 제한사항

- Windows 전용입니다.
- 현재 UI는 WinUI 3가 아니라 Windows Forms입니다.
- Codex app-server의 동작과 제공되는 rate limit field에 의존합니다.
- cloud dashboard나 analytics product가 아니라 local tray utility입니다.
- 설정은 앱 output folder에 로컬 파일로 저장됩니다.

## Roadmap

단기 작업:

1. 실험적인 Glassmorphism theme 제거
2. 현재 Windows Forms 버전 릴리즈 정리
3. README, release notes, screenshot 보강
4. WinForms 기준선 release tag 생성

다음 큰 단계:

1. UI를 WinUI 3로 포팅
2. Codex app-server integration과 rate limit mapping 동작은 유지
3. 유지보수하기 좋은 native Windows app 구조로 UI 재구성
4. WinUI 3 포팅 후 packaging/distribution 방식 재검토

Provider 범위:

- 승인된 provider 범위는 Codex(OpenAI)와 Claude(Anthropic)입니다.
- 현재 구현은 Codex만 지원하며, Claude 지원은 예정되어 있습니다.

> 이 프로젝트의 이전 이름은 `Codex Usage Tray`이며, 2026-07-22에 `QuotaScope`로 이름을 변경했습니다.

## Disclaimer

> This project is not affiliated with, endorsed by, or sponsored by OpenAI or Anthropic. Codex is a product/service of OpenAI. Claude is a product/service of Anthropic.

## 문서

프로젝트 메모는 `docs/` 아래에 있습니다.

- `docs/README.md`: 운영/동작 메모
- `docs/PROJECT_MAP.md`: module과 실제 file path map
- `docs/MODERNIZATION_PLAN.md`: .NET / WinUI modernization plan
- `docs/modules/codex_rate_limits.md`: Codex app-server rate limit schema와 mapping notes

## English README

English README is available at [`README.md`](README.md).
