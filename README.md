# Grass Manager 🌱

GitHub 잔디(활동 기록)를 매일 꾸준히 가꿀 수 있도록 도와주는 웹앱입니다.

## 기능

- **프로젝트 관리** — 직접 추가하거나 GitHub 저장소에서 가져오기. 프로젝트마다 작업 요일과 "추가할 기능/할 일" 목록을 관리
- **잔디 연동** — GitHub 아이디로 최근 활동을, Personal Access Token을 넣으면 1년치 공식 잔디를 표시. 현재/최장 스트릭, 1년 기여 수, 이번 주 활동일
- **매니저**
  - 오늘의 추천 미션: 오늘 요일에 배정된 프로젝트의 할 일 → 오래 방치된 프로젝트 → 작은 커밋 아이디어 순
  - 자정까지 남은 시간과 스트릭 위험 경고
  - 주간 작업 계획표와 빈 요일 자동 배정
  - 방치된 프로젝트 / 할 일이 빈 프로젝트 알림
  - 브라우저 리마인더(탭이 열려 있을 때)
- **탭이 닫혀도 알림** — GitHub Actions가 매일 커밋 여부를 확인해 ntfy 푸시 / Discord / GitHub 메일로 알려줌

## 실행

**https://kangnagi.github.io/GrassManager/** 에서 바로 쓸 수 있습니다 (GitHub Pages, `main` 브랜치에 push하면 자동 반영).

로컬에서 열려면 빌드 과정 없는 정적 웹이므로 로컬 서버로 열어 주세요.

```bash
python -m http.server 5174
```

브라우저에서 http://localhost:5174 을 열고 **설정**에서 GitHub 아이디를 입력하세요.
데이터(프로젝트, 할 일, 토큰)는 브라우저 localStorage에만 저장됩니다.

## 탭이 닫혀도 알림 받기 (GitHub Actions)

앱의 **설정 → 탭이 닫혀도 알림 받기**에서 안내하는 대로:

1. 알림용 저장소를 만들고
2. `.github/workflows/grass-reminder.yml`(앱이 생성), `.github/scripts/grass-check.mjs`(이 저장소의 [`reminder/grass-check.mjs`](reminder/grass-check.mjs)), 선택적으로 `grass-plan.json`을 올린 뒤
3. Secret `NTFY_TOPIC`(필수), `GRASS_TOKEN`(권장), `DISCORD_WEBHOOK`(선택)을 등록하고
4. Actions에서 **Run workflow**로 테스트합니다.

## 바탕화면 위젯 (Windows)

이번 주(일~토) 잔디, 연속 기록, 오늘 커밋 여부를 바탕화면에서 바로 보여 주는 작은 위젯입니다.

1. `widget/build.bat`을 더블클릭해 `widget/GrassWidget.exe`를 만듭니다 (Windows 기본 .NET Framework 4 컴파일러 사용, 설치할 것 없음).
2. `GrassWidget.exe`를 실행합니다.
3. 오른쪽 위 **≡ 설정** 메뉴에서 **Windows 시작 시 자동 실행**을 켜면 로그인할 때마다 자동으로 뜹니다.

**≡ 설정 메뉴** (우클릭해도 같은 메뉴)

| 메뉴 | 동작 |
| --- | --- |
| 위치 및 크기 조절 | 편집 모드: 끌어서 이동, 오른쪽 아래 모서리를 끌어 크기 조절(0.75~2배), ✓를 누르면 그 자리에 고정 |
| 위젯 숨기기 | 잔디 매니저 로고 아이콘으로 축소 (점 색: 초록=오늘 커밋함, 주황=아직). 아이콘을 클릭하면 다시 펼침, 끌면 이동 |
| 계정 설정 | 홈페이지(웹앱) 열기 |
| 새로고침 / Windows 시작 시 자동 실행 | 잔디 바로 다시 읽기 / 로그인 시 자동 실행 켜고 끄기 |
| 위젯 종료 | 프로그램 완전히 종료 |

- 평소에는 위치가 고정되어 실수로 끌려가지 않습니다. 칸 위에 마우스를 올리면 날짜별 기여 수, 더블클릭하면 홈페이지가 열립니다.
- 잔디를 볼 GitHub 아이디는 설정 파일의 `user=` 값입니다 (기본 Kangnagi).
- GitHub 공개 잔디 페이지를 30분마다(그리고 자정에) 한 번 읽어 옵니다. 토큰이 필요 없고, 비공개 저장소 기여까지 세려면 GitHub 프로필 설정의 **Private contributions**를 켜 두세요.
- 애니메이션이나 주기적인 다시 그리기가 없어 평소 CPU 사용량은 거의 0%입니다.
- 설정(아이디·위치·크기·숨김 상태)은 `%APPDATA%\GrassWidget\config.ini`에 저장됩니다.

## 파일 구성

| 파일 | 설명 |
| --- | --- |
| `index.html` | 앱 진입점 |
| `style.css` | 스타일 (라이트/다크 모드) |
| `app.js` | 앱 로직: GitHub 동기화, 잔디, 매니저, 설정 |
| `reminder/grass-check.mjs` | GitHub Actions에서 실행되는 커밋 확인·알림 스크립트 |
| `widget/GrassWidget.cs` | 바탕화면 위젯 소스 (C# WinForms) |
| `widget/build.bat` | 위젯 빌드 스크립트 → `widget/GrassWidget.exe` |
