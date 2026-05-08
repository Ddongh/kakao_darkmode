# KakaoDark

PC 카카오톡에 다크모드를 입히는 Windows 트레이 유틸리티입니다. 카카오톡이 자체 다크모드를 지원하지 않는 환경에서 외부 오버레이로 다크 효과를 구현합니다.

> **성격**: 카카오톡 프로세스를 변조하지 않고 Windows의 표준 Magnification API를 사용해 다크 효과만 입힙니다. DLL 인젝션·메모리 패치·바이너리 수정 없음.

## 스크린샷

(추가 예정)

## 핵심 기능

| 기능 | 동작 |
|---|---|
| **다중 창 추적** | 메인 채팅 리스트, 채팅방(여러 개), 설정창, 하단 광고 popup 등 카카오톡이 띄우는 모든 가시 창에 자동 적용 |
| **다크 색반전** | 흰 배경 → 어두운 회색, 검정 텍스트 → 밝은 색. 채도 있는 색·이미지·이모티콘은 가능한 보존 |
| **3가지 프리셋** | 단순 반전 / 스마트 인버전(기본) / 부드러운 다크 — 트레이 메뉴에서 즉시 전환 |
| **드래그 시 검정 블록** | 카카오톡 본체 이동 중 매그니파이어가 따라잡지 못하는 lag 동안 검정 사각형(padding 20px)으로 가려 분리 현상 방지 |
| **그룹 드래그** | 메인 + 하단 광고 popup처럼 인접·포함된 창들이 한 그룹으로 함께 처리 |
| **이벤트 기반 갱신** | 평소엔 폴링 없음 (CPU 0%). 마우스 클릭·휠 발생 시 300ms burst로만 매그니파이어 invalidate |
| **클릭 깜빡임 완화** | 헤더 클릭 직전 호스트를 일시 topmost로 부스트해 카카오톡 z-order 변경 1프레임 lag 가림 |
| **마우스 휠 forward** | 호스트가 가로채는 휠 메시지를 카카오톡 자식 컨트롤로 SendMessageTimeout으로 전달 |

## 동작 원리

```
[카카오톡 메인 창]                      [layered + click-through 호스트]
   ├ 메인 채팅 리스트                       ├ 자식 매그니파이어 컨트롤
   ├ 광고 popup                             │   - MagSetWindowSource(카카오 RECT)
   └ 채팅방 (여러 개, 별도 hwnd)            │   - MagSetColorEffect(다크 매트릭스)
                                            │   - MagSetWindowFilterList(자기 자신 제외)
                                            └ 호스트 BackColor=Black
                                              (드래그 중 매그니파이어 hide 시 검정 노출)
```

1. `KakaoTalk.exe` PID에 속한 모든 가시 top-level 창을 추적
2. 각 카카오 창마다 layered + transparent 호스트 윈도우를 정확히 같은 위치에 띄움
3. 호스트 자식으로 매그니파이어 컨트롤이 카카오톡 영역을 캡처해 색반전 적용
4. WinEventHook(`LOCATIONCHANGE`, `FOREGROUND`, `MOVESIZESTART/END`, `REORDER` 등) + 16ms 폴링으로 카카오톡 위치 변경 추적
5. 호스트는 카카오톡 z-order 바로 위에 위치 (다른 앱이 카카오톡 위로 오면 호스트도 같이 가려짐)
6. 마우스 입력은 `WM_NCHITTEST → HTTRANSPARENT`로 카카오톡으로 통과

## 빌드

요구 사항: **.NET 8 SDK** (Windows). [공식 다운로드](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build KakaoDark/KakaoDark.csproj -c Release
```

산출물: `KakaoDark/bin/Release/net8.0-windows/KakaoDark.exe`

단일 파일 배포:
```powershell
dotnet publish KakaoDark -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## 실행

1. 카카오톡 PC를 먼저 실행
2. `KakaoDark.exe` 실행 → 트레이에 검정 배경의 노란 **K** 아이콘 등장
3. 자동으로 카카오톡 창에 다크 적용
4. **트레이 우클릭** → 프리셋 변경, 자동 시작 토글, 종료

기본 프리셋은 **스마트 인버전** (카카오 노랑 등 채도 있는 색을 가능한 보존).

## 알려진 한계

- **프레임 lag**: 카카오톡과 호스트가 다른 hwnd라 OS 합성 파이프라인의 1~2프레임 시차가 본질적으로 있음. 빠른 드래그 시 검정 블록 padding(20px)이 그 영역을 가림.
- **이미지·이모티콘 색감**: 매그니파이어는 픽셀 단위 색반전이라 사진과 UI 픽셀 구분 불가. "부드러운 다크" 프리셋이 이미지 손상 가장 적음.
- **카카오톡 자체 그리기 캐시**: 일부 영역(예: 입력창)은 카카오톡이 invalidate할 때만 다시 그려짐 → 마우스 클릭/휠 시 burst로 따라잡음.
- **원격 데스크톱·일부 가상머신**: Magnification API가 동작 제한될 수 있음.
- **카카오톡 업데이트**: 클래스명·창 구조가 크게 바뀌면 추적 필터([`KakaoWindowTracker.cs`](KakaoDark/Core/KakaoWindowTracker.cs))를 조정해야 할 수 있음.

## 프로젝트 구조

```
kakao_darkmode/
├── KakaoDark.sln
├── KakaoDark/                      메인 프로젝트 (매그니파이어 기반)
│   ├── Program.cs                  단일 인스턴스 진입점
│   ├── TrayContext.cs              트레이 UI + 호스트 풀 관리 + 마우스 hook
│   ├── Core/
│   │   ├── KakaoWindowTracker.cs   카카오 창 enumerate + WinEventHook
│   │   ├── MagnifierHost.cs        layered host + 매그니파이어 컨트롤
│   │   ├── ColorPresets.cs         다크 매트릭스 프리셋 3종
│   │   ├── MouseHook.cs            WH_MOUSE_LL (드래그 감지·휠·클릭)
│   │   └── AutoStart.cs            HKCU\\...\\Run 레지스트리 토글
│   └── Native/                     User32 / Magnification API P/Invoke
├── KakaoDarkHook/                  (보존) C++ 후킹 DLL — 시도했으나 카카오 보안 경고
├── KakaoDarkInject/                (보존) C# 인젝터 — KakaoDarkHook과 한 쌍
└── README.md
```

### 시도했지만 실패한 접근

`KakaoDarkHook/` + `KakaoDarkInject/` 폴더는 **DLL 인젝션 + API 후킹** 방식으로 진짜 다크모드를 만들려던 시도입니다:
- IAT 후킹 → 카카오톡이 GDI를 IAT로 import하지 않아 효과 없음
- Inline 후킹 (gdi32.dll의 `SetTextColor`/`FillRect` 등 5바이트 JMP 패치) → **카카오톡이 무결성 검사로 변조 감지 → 보안 경고 다이얼로그 표시 후 종료**

코드는 학습 자산으로 보존했습니다. 매그니파이어 방식이 안전·안정적인 유일한 외부 다크모드 방법으로 결론.

## 색 팔레트 튜닝

[`Core/ColorPresets.cs`](KakaoDark/Core/ColorPresets.cs)의 5×5 컬러 매트릭스 값을 조정하면 색감을 바꿀 수 있습니다:

```csharp
public static MAGCOLOREFFECT SmartInvert() => Build(new float[]
{
     0.333f, -0.667f, -0.667f, 0, 0,
    -0.667f,  0.333f, -0.667f, 0, 0,
    -0.667f, -0.667f,  0.333f, 0, 0,
     0,       0,       0,      1, 0,
     1,       1,       1,      0, 1
});
```

## 기여

이슈·PR 환영합니다. 카카오톡 빌드 변경으로 깨지는 케이스가 있으면 [`KakaoWindowTracker.cs`](KakaoDark/Core/KakaoWindowTracker.cs)의 클래스명·필터를 조정해주세요.

## 라이선스

MIT (또는 본인이 원하는 라이선스로 수정)

## 면책

- 카카오톡 자체를 변조하지 않으므로 일반 사용에 안전합니다.
- Windows의 표준 Magnification API만 사용하므로 백신·보안 도구의 오탐 가능성도 낮습니다.
- 다만 본 도구는 비공식이며, 카카오톡 약관·OS 환경 변화에 따라 동작이 달라질 수 있습니다.
