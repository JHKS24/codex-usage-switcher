# Codex Desktop Usage Switcher

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D6.svg)](#지원-환경)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](#빌드--실행소스에서)

Codex Desktop 계정을 전환하고 Codex·Claude 사용량을 보여 주는 **Windows 전용
시스템 트레이 앱**입니다. **.NET 10 WinForms + WebView2** 기반의 네이티브 C#
애플리케이션으로, macOS 앱도 Python도 명령줄 도구도 없습니다.

## 하는 일

### Codex Desktop 계정을 안전하게 전환

- `~/.codex-switch/profiles`에 저장된 프로필에서 `~/.codex/auth.json`을 교체해
  계정·프로필을 전환합니다.
- 전환할 때마다 **타임스탬프가 찍힌 백업**을 만들고 **롤백**을 지원합니다.
- **Codex Desktop 또는 app-server가 실행 중이면 전환을 거부**하며, 실행 중인
  프로세스를 확인할 수 없을 때도 거부합니다. 그래서 전환이 실행 중인 세션을
  손상시키는 일이 없습니다.

### 사용량 팝업

트레이 아이콘을 좌클릭하면 Codex와 Claude 사용량을 나란히 보여 주는 팝업이
열립니다.

- 두 제공자의 5시간 / 주간 **남은 %**.
- 다음 한도 구간까지의 **리셋 카운트다운**.
- **프로필별 quota**.
- 선택한 프로필을 **원클릭으로 전환**.

### 대시보드 창

- **14일 모델 누적 토큰 막대 그래프**.
- 활동량 **요일 × 시간 히트맵**.
- **턴별 토큰 통계**.
- Codex와 Claude **5시간 / 주간 한도 도넛 차트(실시간)**.
- 자동 갱신되는 모델 가격표로 계산한 **예상 비용 요약**. 가격은 번들된 스냅샷에
  담긴 공식 OpenAI / Anthropic 요율을 사용하고 주기적으로 갱신되며, 알 수 없는
  모델은 가격을 매기지 않고 남겨 둡니다.

### 로그인 (모두 GUI 동작)

- **Claude 사용량 로그인** — 앱 내장 **OAuth (PKCE)** flow입니다. 브라우저가
  열리고, 일회용 코드를 다이얼로그에 붙여넣습니다.
- **Codex 프로필 로그인**과 **Claude Code 로그인** — 해당 CLI를 실행하는
  터미널을 엽니다.
- 현재 **Codex 로그인을 프로필로 저장**할 수도 있습니다.

### 이중 언어 UI

영어 / 한국어, **설정**에서 언어를 전환합니다. UI는 기본적으로 시스템 언어를
따르며 선택을 저장합니다.

## 빌드 & 실행(소스에서)

**.NET 10 SDK**가 필요합니다. 레포 루트에서:

```powershell
./build-windows.ps1          # 단일 파일 exe 빌드
./build-windows.ps1 -Test    # 단위 테스트를 먼저 실행한 뒤 빌드
```

이 명령은 대상 머신에 .NET 런타임도 Python도 필요 없는 **self-contained** 단일
실행 파일(~56 MB)을 만듭니다.

```text
build/win-x64/CodexDesktopUsageSwitcher.Windows.exe
```

이 exe를 더블클릭하면 실행됩니다. 앱은 **시스템 트레이**에 상주합니다.

- 트레이 아이콘 **좌클릭** → 사용량 팝업.
- 트레이 아이콘 **우클릭** → 열기 / 종료 메뉴.

보조 스크립트는 `scripts/` 아래에 있습니다(`install-local-windows.ps1`,
`package-windows.ps1`, `test-windows.ps1`, `installer/install.cmd` /
`installer/install.ps1`). 단일 빌드 진입점은 레포 루트의
`./build-windows.ps1`입니다.

## 지원 환경

- **Windows 10 1809+**(10.0.17763) 또는 Windows 11.
- **Microsoft Edge WebView2 런타임**(최신 Windows 10/11에는 기본 설치).
- **Codex 프로필 로그인**용: Codex Desktop과 `codex` CLI. `codex`가 `PATH`에
  없으면 `CODEX_CLI_PATH` 환경 변수를 설정하세요.
- **Claude Code 로그인**용: Claude Code `claude` CLI.

## 보안

- 앱은 **토큰 내용을 출력·로그·표시하지 않습니다**.
- `auth.json` / 자격증명은 **백업과 함께 원자적으로** 복사됩니다.
- 계정 전환은 **Codex Desktop이 완전히 종료되어 있어야** 가능합니다.
- OAuth는 항상 **사람과 브라우저**가 필요합니다.
- 실제 `~/.codex`, `~/.codex-switch`, `~/.config/claude-usage-bar`는 **레포
  밖에 있으며 절대 커밋되지 않습니다**.

자세한 내용은 [SECURITY.md](SECURITY.md)를 참고하세요.

## 레포 & 라이선스

- 레포: <https://github.com/JHKS24/codex-usage-switcher>
- 라이선스: **MIT** © 2026 JHK24. [LICENSE](LICENSE) 참고.

## Credits

이 프로젝트는 여러 업스트림 오픈소스 프로젝트의 작업을 기반으로 합니다.

- **사용량 인사이트** 로직 — 대시보드 차트, Codex/Claude 트랜스크립트 파싱,
  사용량/비용 계산 — 은
  [codex-usage-monitor](https://github.com/kimbyungsu/codex-usage-monitor)
  (MIT)에서 포팅했습니다.
- **Claude OAuth 사용량** 방식은 **claude-usage-bar**를 따릅니다.
- 프로필 단위 관리 방식과 `import-cdx` 마이그레이션 경로는 업스트림
  [ezpzai/cdx](https://github.com/ezpzai/cdx) (Apache-2.0) 계보를 따릅니다. 이
  프로젝트는 fork가 아니라 독립 구현입니다.

업스트림 라이선스 전문은 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)를
참고하세요.
