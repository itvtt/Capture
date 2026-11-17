# Capture

화면 캡처 · 스크롤 캡처 · GIF · 영상 녹화. 트레이에 상주하고, 찍은 것은 바로 복사되며 File Shared 로 링크를 만들어 공유합니다.

## 쓰기

| 단축키 (기본값) | 하는 일 |
|---|---|
| `PrtSc` | 영역 · 창 · 전체 화면 캡처 |
| `Ctrl+PrtSc` | 스크롤 캡처 (긴 화면 한 장) |
| `Shift+PrtSc` | GIF 녹화 |
| `Ctrl+Shift+PrtSc` | 영상(MP4) 녹화 |

- 단축키는 큰 창 아래 **단축키 바꾸기**(트레이 메뉴에도 있음)에서 바꿉니다. 칸을 누르고 새 조합을 누르면 바로 적용되고, `Backspace` 는 단축키 없애기, `Esc` 는 그대로 두기. 글자 · 숫자 키는 Ctrl 이나 Alt 와 함께, `PrtSc` · `F1`~`F24` · `Pause` 는 혼자도 됩니다. 다른 모드와 같거나 다른 프로그램이 쓰는 조합이면 알려 주고 그대로 둡니다. **기본값으로**를 누르면 위 표대로.
- 단축키를 누르면 화면이 멈추고 위에 **영역 · 스크롤 · GIF · 영상 · 전체 화면** 막대가 뜹니다. 끌면 영역, 누르기만 하면 마우스 아래 창, `Enter` 는 그 모니터 전체, `Esc`·오른쪽 클릭은 취소. 돋보기로 화소 단위로 맞출 수 있습니다.
- **캡처**: 저장 폴더에 PNG 로 저장하고 클립보드에 복사한 뒤 편집 창을 엽니다. 편집 창에서 사각형(R) · 화살표(A) · 펜(P) · 형광펜(H) · 글자(T) · 모자이크(M) · 자르기(C), 색 7가지 · 굵기 3가지, 되돌리기(Ctrl+Z · Ctrl+Y). 고친 내용은 닫을 때 같은 파일에 저장됩니다. `Esc` 로 닫기, `Ctrl+C` 복사, `Ctrl+Shift+S` 다른 이름으로 저장, `Ctrl+휠` 확대 · 축소.
- **스크롤 캡처**: 고른 영역 가운데에 마우스를 두고 휠을 굴리며 이어 붙입니다. 끝까지 내려가거나, 막대의 **끝**을 누르거나, 단축키를 다시 누르거나, 마우스를 움직이면 멈춥니다 (최대 높이 20,000 화소). 위아래 고정된 머리글 · 바닥글은 한 번만 들어가고, 글자 가장자리의 미세한 차이는 같은 줄로 봅니다.
- **GIF · 영상**: 영역 둘레에 빨간 테두리와 막대(시간 · 멈춤 · 취소)가 뜹니다. 둘 다 녹화에는 찍히지 않습니다 (Windows 10 2004 이상). 같은 단축키를 다시 누르거나 **멈춤**을 누르면 저장하고 알림에서 공유 · 복사(파일) · 폴더를 고릅니다. GIF 는 초당 15장(바뀐 부분만 저장), 영상은 초당 30장 H.264 (Windows 에 들어 있는 인코더, 소리 없음). 마우스 포인터도 녹화됩니다.
- **공유**: 이 PC 의 File Shared(포트 5000)에 올리고 짧은 링크(`http://<이 PC IP>:5000/s/<코드>`)를 클립보드에 복사합니다. File Shared 가 없거나 꺼져 있으면 알려 줍니다.
- 큰 창: 네 가지 캡처 단추, 최근 캡처(누르면 그림은 편집 창, GIF · 영상은 기본 프로그램, 오른쪽 클릭 = 열기 · 복사 · 공유 · 폴더에서 보기 · 휴지통으로), 저장 위치 바꾸기, Windows 시작할 때 실행. 닫으면 트레이로 가고 단축키는 계속 됩니다. 끝내려면 트레이 아이콘 → 종료.

Windows 11 에서 "Print Screen 키로 화면 캡처 열기"가 켜져 있으면 `PrtSc` 를 Windows 캡처 도구가 먼저 씁니다. 큰 창에 안내와 **Windows 설정** · **단축키 바꾸기** 단추가 나옵니다. 설정을 끄고 돌아오면 다시 등록하고, 아니면 다른 단축키로 바꾸면 됩니다.

## 저장 위치 · 설정

- 캡처 · 녹화: `사진\Capture` (`Capture_yyyyMMdd_HHmmss.png|gif|mp4`). 큰 창 아래 **바꾸기**로 바꿉니다.
- 설정(저장 폴더 · 바꾼 단축키): `%LOCALAPPDATA%\Capture\settings.json` (설치 폴더 밖이라 업데이트해도 남음). 시험할 때는 환경 변수 `CAPTURE_DATA_DIR` 로 다른 폴더를 씁니다.
- 처음 실행할 때 "Windows 시작할 때 실행"을 켭니다 (설치된 사본일 때만, `--tray` 로 트레이에서 시작). 큰 창 아래에서 끌 수 있습니다.

## 빌드 · 확인

```powershell
dotnet build Capture\Capture.csproj -c Release
dotnet run --project Capture.Checks -c Release
```

`Capture.Checks` 는 GIF(색 · 바뀐 부분 · 시간 · LZW), 스크롤 이어 붙이기(머리글 · 바닥글 · 스크롤 막대 · 빈 줄), MP4(Windows 재생기로 열어 위아래 확인), 파일 이름, 단축키(이름 · 허용 키 · 저장), 편집 창 합치기를 확인합니다. 인자로 더 할 수 있습니다:

| 인자 | 하는 일 |
|---|---|
| `--snapshot <폴더>` | 큰 창 · 편집 창 · 고르기 화면 · 녹화 막대 · 알림을 PNG 로 |
| `--screen` | 실제 화면 읽기와 "캡처에서 빼기" 확인 (화면에 잠깐 자홍색 네모) |
| `--live <폴더>` | 실제로 GIF · MP4 녹화 1.5초, 120줄 창 스크롤 캡처 (마우스가 움직임) |
| `--e2e <Capture.exe> <폴더>` | 앱을 띄워 키보드 · 마우스로 캡처 · GIF · 영상까지 (따로 만든 설정 · 저장 폴더) |
| `--hotkey-dialog <폴더>` | 단축키 창에서 실제로 키를 눌러 바꾸기 · 없애기 · 겹침 거절 · 바꾼 단축키가 듣는지 (`CAPTURE_DATA_DIR` 을 따로 정해 두고) |
| `--grab <png>` | 주 모니터를 그대로 저장 |
| `--share <파일>` | 이 PC 의 File Shared 에 실제로 올려 링크 확인 |

원격 데스크톱 창이 최소화되어 있거나 화면이 잠겨 있으면 화면을 읽을 수 없어 `--screen` · `--live` · `--e2e` 는 실패합니다.

## 릴리스

`main` 에 push 하면 self-hosted 러너가 다음 버전을 골라 `Capture-win-x64.zip` 을 릴리스합니다 (i-luncher AppKit, `.github\workflows\release.yml`). 설정 키는 `.env.sample`, 값은 프로필 폴더(`APPS_ENV_DIR`)의 `Capture.env` 에 둡니다.

## 구조

| 파일 | 내용 |
|---|---|
| `App.xaml.cs` | 하나만 실행, 트레이, 자체 업데이트 |
| `Services\Hotkeys.cs` · `HotkeyWindow.xaml` | 전역 단축키 (기본값 · 저장 · 등록), 단축키 바꾸기 창 |
| `CaptureController.cs` | 고르기 → 저장 · 복사 · 편집 / 스크롤 / 녹화 → 알림, 공유 |
| `SelectionWindow.xaml` | 모니터마다 멈춘 화면 · 어둡게 · 창 표시 · 돋보기 · 모드 막대 |
| `EditorWindow.xaml` | 편집 창 |
| `MainWindow.xaml` | 큰 창 |
| `RecordBar.xaml` · `Toast.xaml` | 녹화 막대 · 테두리, 알림 |
| `Core\ScreenGrabber.cs` | 화면 읽기(BitBlt) · 모니터 · 창 목록 · 마우스 |
| `Core\ScrollStitcher.cs` · `ScrollCapture.cs` | 스크롤 이어 붙이기 |
| `Core\GifWriter.cs` · `Mp4Writer.cs` · `Recorder.cs` | GIF 인코더(LZW · 미디언 컷) · MP4(Media Foundation) · 녹화 스레드 |
| `Services\FileShared.cs` | File Shared 업로드 |
