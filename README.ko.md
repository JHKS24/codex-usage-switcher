# Codex Desktop Usage Switcher

Codex Desktop 프로필 전환과 AI 도구 사용량 확인을 처리하는 로컬 도구입니다.
macOS에서는 메뉴바 앱으로, Windows에서는 트레이 아이콘과 커스텀 사용량
팝업으로 동작합니다.

## 빠르게 시작하기

### Windows — 원클릭 설치 (다른 PC에 배포할 때)

빌드 머신에서 배포 ZIP을 한 번 만들고:

```powershell
.\scripts\package-windows.ps1 -SelfContained
```

생성된 `dist\CodexDesktopUsageSwitcher-win-x64.zip`을 대상 PC에 복사한 뒤
**압축을 풀고 `install.cmd`를 더블클릭**하면 끝입니다. 설치 스크립트가
Python 3 확인(없으면 winget 설치 제안), `%LOCALAPPDATA%` 설치, 시작 메뉴 +
로그인 자동 시작 바로가기 생성, 앱 실행까지 처리합니다. .NET 런타임은
self-contained 빌드라 필요 없습니다. 옵션: `install.cmd -NoStartup -NoLaunch`

### 소스에서 설치

Windows:

```powershell
git clone https://github.com/cogusrlchg-wq/codex-desktop-usage-switcher.git
cd codex-desktop-usage-switcher
.\scripts\install-local-windows.ps1
```

Windows 트레이 앱 실행:

```powershell
& "$env:LOCALAPPDATA\CodexDesktopUsageSwitcher\CodexDesktopUsageSwitcher.Windows.exe"
```

배포용 ZIP 생성:

```powershell
.\scripts\package-windows.ps1 -SelfContained
```

macOS:

```bash
git clone https://github.com/cogusrlchg-wq/codex-desktop-usage-switcher.git && cd codex-desktop-usage-switcher && ./scripts/install-local.sh
```

앱 실행:

```bash
open "$HOME/Applications/CodexDesktopMenu.app"
```

Windows 설치 스크립트는 앱을 `%LOCALAPPDATA%\CodexDesktopUsageSwitcher`에
복사하고, 시작 메뉴 바로가기와 CLI shortcut을 만듭니다.

```text
%USERPROFILE%\bin\codex-desktop-switch.cmd
```

macOS 설치 스크립트는 앱을 `~/Applications`에 복사하고 CLI shortcut을 만듭니다.

```text
~/bin/codex-desktop-switch
```

`~/bin`이 shell `PATH`에 들어 있어야 합니다.

## 스크린샷

![익명화된 메뉴 스크린샷](assets/menu-screenshot-redacted.svg)

이미지는 더미 프로필명을 사용한 예시입니다.

## 하는 일

- `~/.codex-switch/profiles/` 아래 Codex 프로필을 전환합니다.
- Codex 5H / Week 남은 사용량을 보여줍니다.
- 선택적으로 Claude 5H / Week 남은 사용량을 보여줍니다.
- Windows에서는 기본적으로 트레이 아이콘만 유지됩니다. 트레이 아이콘을
  좌클릭/우클릭하면 커스텀 사용량 팝업이 열립니다.
- Windows `설정`에서 작업표시줄 알림 영역 숫자 아이콘을 최대 6개까지
  켜고 끌 수 있습니다: Codex 5H / Week, CodexSub 5H / Week,
  Claude 5H / Week.
- 팝업은 열리자마자 현재 Codex 프로필과 5H / Week 남은 사용량을 보여줍니다.
- 팝업의 `새로고침`을 누르면 창을 닫지 않고 내부 값만 갱신합니다.
- 프로필 행을 누르면 선택되고, `Switch profile` 버튼이나 행 더블클릭을
  누르면 확인창을 띄운 뒤 계정을 전환합니다.
- Windows 팝업의 `설정` 창에서 Codex 프로필 로그인 추가, 현재 계정 저장,
  Claude 사용량 로그인, Claude Code 로그인, Doctor, 프로필 폴더 열기를 할 수
  있습니다.

경고 기준:

- Codex와 Claude 모두 남은 사용량 기준으로 표시하므로 `20%` 이하에서
  주황색 경고가 뜹니다.
- Claude 남은 사용량은 `100 - 현재 Claude 사용률`로 계산합니다.

## 지원 환경

- macOS 또는 Windows
- Codex Desktop
- Python 3
- macOS: Xcode command line tools와 `swiftc`
- Windows: 배포용 self-contained ZIP은 .NET 런타임이 필요 없습니다.
  .NET SDK/runtime은 로컬 빌드할 때만 필요합니다.
- `codex` CLI가 `PATH`에 있거나 `CODEX_CLI_PATH`가 설정되어 있어야 함

Linux는 지원하지 않습니다. Windows에서 Codex.exe가 일반 설치 위치에 없으면
`CODEX_DESKTOP_APP_PATH`를 설정하세요.

## Codex ID 추가

```bash
codex-desktop-switch login main
codex-desktop-switch login work
codex-desktop-switch list
```

CLI로 직접 전환할 때는 Codex Desktop을 완전히 종료한 뒤 실행합니다.

```bash
codex-desktop-switch use main
codex-desktop-switch use main --apply
```

메뉴/트레이 앱에서 전환하면 확인창 이후 Codex 종료 요청, 잔여 app-server/tool-session
정리, 전환, Codex 재실행 순서로 처리합니다.

메뉴 표시 순서를 고정하려면 아래 파일을 둡니다.

```json
{
  "profiles": ["main", "work", "personal"],
  "refresh_minutes": 10
}
```

저장 위치: `~/.codex-switch/config.json`

## CLI

```bash
codex-desktop-switch list
codex-desktop-switch current
codex-desktop-switch usage
codex-desktop-switch use main --apply
codex-desktop-switch restore <backup-id> --apply
codex-desktop-switch stop-codex --help
```

`use`는 기본 dry-run입니다. 실제 전환은 `--apply`가 있을 때만 합니다.

## Claude 사용량

```bash
codex-desktop-switch claude-login
codex-desktop-switch claude-usage --json
```

Windows 트레이 메뉴의 `Claude 로그인`은 터미널 창을 열고 interactive
`claude-login`을 실행합니다. 브라우저에서 승인한 뒤 OAuth code만 그 터미널에
붙여넣으세요.

OAuth code 만료 또는 rate limit 오류가 나면 같은 코드를 반복 재시도하지 말고,
pending login 상태를 초기화한 뒤 새 로그인 flow를 시작합니다.

```bash
codex-desktop-switch claude-login-reset
```

## Gatekeeper

이 앱은 직접 빌드한 미서명 앱입니다. 첫 실행 때 macOS가 차단할 수 있습니다.
Finder에서 `CodexDesktopMenu.app` 우클릭 → **열기**를 사용하세요.

터미널 대안:

```bash
xattr -dr com.apple.quarantine "$HOME/Applications/CodexDesktopMenu.app"
open "$HOME/Applications/CodexDesktopMenu.app"
```

## 보안

- `auth.json`, `credentials.json`, profile, backup, session, log, SQLite state를 커밋하지 마세요.
- `~/.codex-switch`는 사용자 본인만 접근 가능한 로컬 영역으로 유지하세요.
- Codex와 Claude 사용량 endpoint는 비공식이며 언제든 바뀔 수 있습니다.
- 자세한 내용은 [SECURITY.md](SECURITY.md)를 참고하세요.

## Credits

프로필 단위 관리 방식과 `import-cdx` 마이그레이션 경로는
[ezpzai/cdx](https://github.com/ezpzai/cdx) (Apache-2.0)에서 영감을 받았습니다.
이 프로젝트는 cdx의 fork가 아니라 로컬 메뉴/트레이용 독립 구현입니다.

사용량 인사이트 — 대시보드 차트, Codex/Claude 트랜스크립트 파싱, 사용량/비용 계산 — 은
[codex-usage-monitor](https://github.com/kimbyungsu/codex-usage-monitor) (MIT)를
C#로 포팅한 것입니다. Claude OAuth 사용량은 **claude-usage-bar**의 로컬 자격증명
포맷·엔드포인트를 따릅니다. 업스트림 라이선스 전문은
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)를 참고하세요.
