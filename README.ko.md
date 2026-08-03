# ADOFAI AudioSync

[日本語](README.md) | [English](README.en.md) | [한국어](README.ko.md)

ADOFAI AudioSync는 A Dance of Fire and Ice 에디터의 재생을 안정화하는 Unity Mod Manager 모드입니다. 특히 선택한 타일에서 재생할 때 발생하는 오디오 시작 지연, 채보와 오디오의 싱크 어긋남, 의도하지 않은 위치로 크게 이동하는 문제를 줄입니다. 채보 제작을 돕는 BPM·위상 탭 앵커와 플레이 오차로부터 속도 변화를 추정하는 실험적 기능도 포함되어 있습니다.

## 주요 기능

- **선택한 타일부터 오디오 동기화**
  요청한 오디오 위치를 미래의 DSP 시각에 예약합니다. 실제 AudioSource의 재생 헤드가 움직이기 시작한 것을 확인한 뒤, 관측한 위치에 맞춰 채보 시계를 한 번만 정렬합니다. 비정상적인 시작은 재시도할 수 있으며, 동기화를 확립하지 못하면 ADOFAI 기본 재생 처리로 안전하게 돌아갑니다.
- **게임의 기본 시작 처리 유지**
  `scnGame.Play`를 억제하거나 다시 호출하지 않으며, ADOFAI가 선택한 체크포인트도 변경하지 않습니다. 따라서 다른 모드에서도 일반적인 재생 시작이 한 번만 발생한 것으로 인식됩니다.
- **고BPM 카운트다운 폴딩**
  3200 BPM과 같은 매우 빠른 구간에서 재생을 시작해도, 시각적 준비 구간을 설정한 최대 BPM 이하의 읽기 쉬운 속도로 접을 수 있습니다. 배율은 행성의 준비 이동 계산에만 적용되며, 선택한 타일과 오디오 시작 시각은 바뀌지 않습니다.
- **Pause / Wait Beats 타이밍 분리**
  고BPM 카운트다운 배율을 Pause 이벤트의 Wait Beats에도 적용할지 선택할 수 있습니다. 기본값에서는 Wait Beats가 채보의 원래 속도를 유지합니다.
- **OGG 메모리 캐시**
  이전에 불러온 OGG 파일을 디코딩된 AudioClip으로 재사용하여 같은 채보를 다시 중간 재생할 때 반복 디코딩을 줄입니다. 디코딩 전에 Vorbis의 전체 샘플 수로 PCM 크기를 계산하며, 제한을 초과하는 파일은 캐시하지 않고 ADOFAI의 기본 스트리밍 경로로 불러옵니다. 용량 제한, LRU 제거, 수동 삭제를 지원합니다.
- **BPM·위상 탭 앵커**
  타일을 선택하고 음악에 맞춰 탭하면 BPM과 박자의 위상을 함께 추정합니다. 결과를 확인한 뒤 SetSpeed 이벤트로 채보에 적용할 수 있습니다.
- **플레이 오차 기반 속도 보정(실험적 기능)**
  수동 플레이에서 구간별 빠름/느림 오차를 분석하고, 오차의 변화량으로 BPM 보정 후보를 생성합니다. 기본값은 꺼짐이며 명시적으로 활성화한 경우에만 데이터를 기록합니다.
- **진단 표시 및 실패 로그**
  예약 잔차, 재생 헤드 보정, 시작 대기 시간, OGG 캐시 상태 등을 간단 또는 상세 오버레이로 표시합니다. 선택한 타일부터 재생하는 데 실패하면 원인 조사에 필요한 진단 로그를 한 묶음으로 출력합니다.

## 요구 사항

- Windows 버전 A Dance of Fire and Ice
- Unity Mod Manager
- 게임의 Mono/Managed DLL을 참조할 수 있는 빌드 환경

게임 업데이트로 패치 대상 메서드의 구조가 바뀌면 해당 Harmony 패치가 비활성화될 수 있습니다. 각 패치는 독립적으로 설치되므로, 지원되지 않는 기능 하나 때문에 모드 전체가 비활성화되지는 않습니다.

## 설치

배포 ZIP을 게임의 `Mods` 폴더에 압축 해제합니다. 최종 폴더 구조는 다음과 같아야 합니다.

```text
A Dance of Fire and Ice/
└─ Mods/
   └─ ADOFAIAudioSync/
      ├─ ADOFAIAudioSync.dll
      └─ Info.json
```

배포 패키지에는 PDB가 포함되지 않습니다. 설정은 Unity Mod Manager의 Mods 화면에서 변경할 수 있습니다. 이전 버전의 `Settings.xml`은 시작 시 이전되며, 읽을 수 없는 경우 타임스탬프가 포함된 `.broken-*` 접미사를 붙여 보존됩니다.

## 단축키

| 입력 | 기능 |
|---|---|
| `Ctrl+F9` | 진단 표시를 끄기, 간단, 상세 순서로 전환 |
| `Ctrl+F6` | BPM·위상 탭 창 열기 또는 닫기 |
| `Ctrl+T` | 선택한 타일을 새 BPM·위상 측정 앵커로 설정 |
| `F10` | 탭 입력(탭 창에서 변경 가능) |
| `Backspace` | 가장 최근의 탭 삭제 |
| `Enter` | 현재 탭 측정 완료 |
| `Ctrl+Enter` | 분석 결과를 채보에 적용 |
| `Escape` | 현재 측정 취소 |
| `Ctrl+Shift+E` | 실험적 플레이 오차 기록 시작 또는 중지 |

`Ctrl+F9`로 변경한 설정은 즉시 저장됩니다.

## 빌드

필요한 항목:

- Windows
- .NET 데스크톱 빌드 도구 워크로드가 설치된 Visual Studio 2022 또는 Build Tools
- A Dance of Fire and Ice
- Unity Mod Manager가 설치된 게임의 `Managed` 폴더

Steam 기본 설치 경로를 사용하는 경우 저장소 루트에서 다음 명령을 실행합니다.

```powershell
.\build.ps1
```

다른 Steam 라이브러리에 설치한 경우:

```powershell
.\build.ps1 -GameManagedDir "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed"
```

환경 변수로 경로를 지정할 수도 있습니다.

```powershell
$env:ADOFAI_GAME_MANAGED_DIR = "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed"
.\build.ps1
```

빌드가 성공하면 DLL은 `src\bin\Release`에, Unity Mod Manager용 ZIP은 `artifacts`에 생성됩니다. 빌드 스크립트는 ZIP에 `ADOFAIAudioSync/ADOFAIAudioSync.dll`과 `ADOFAIAudioSync/Info.json`만 포함되어 있으며 경로 구분자로 ZIP 표준인 `/`를 사용하는지도 확인합니다.

빌드와 동시에 로컬 Mods 폴더에 배포하려면:

```powershell
.\build.ps1 -DeployDir "C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\ADOFAIAudioSync"
```

`-DeployDir`은 로컬 진단을 위한 PDB도 복사합니다. 게임 및 Unity DLL은 빌드할 때 로컬 설치에서 참조하며 저장소에는 포함하지 않습니다.

## 알려진 제한 사항

- 주 대상은 에디터 재생입니다. 이 모드는 일반 레벨 플레이의 오디오 동작을 변경하지 않습니다.
- 오디오 파일 끝부분에서 서로 다른 재생 헤드 업데이트를 두 번 확인할 수 없으면, 위험한 DSP 예약을 피하고 기본 Scrub 처리로 돌아갑니다.
- OGG 캐시 크기는 Vorbis의 전체 샘플 수로 계산한 디코딩 후 PCM 크기이며 Unity가 만드는 모든 임시 메모리 할당을 포함하지 않습니다. 크기를 안전하게 계산할 수 없는 OGG는 캐시하지 않습니다.
- 오디오 또는 에디터 재생을 패치하는 다른 모드와의 호환성은 개별적으로 확인해야 합니다.

릴리스 회귀 검사 항목은 [`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md), 버전 변경 내역은 [`CHANGELOG.md`](CHANGELOG.md)에서 확인할 수 있습니다.

## 라이선스

[MIT License](LICENSE)
