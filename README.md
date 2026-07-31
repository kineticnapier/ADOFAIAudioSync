# ADOFAI AudioSync

ADOFAI のエディター再生、特に選択床からの途中再生で発生するランダムな音源ずれを抑える Unity Mod Manager 用 Mod です。BPM・位相タップアンカーなどの既存機能も含みます。

## v0.9.11 の同期修正

- `timeSamples - requestedSample` をそのまま開始誤差にしていた処理を修正しました。
- `PlayScheduled` の予約時刻から、その観測時点で本来到達しているはずのサンプルを算出し、実サンプルとの差だけを予約残差として判定します。
- 正常な開始確認に必要な2～3フレーム分を、開始ずれとして誤検出しなくなりました。
- 正常時の `dspTimeSong` は、フレーム依存の `dspTime` / `timeSamples` の組ではなく、既知のDSP予約時刻から決定します。
- 毎フレーム `ScrubToFloorNumber` を呼んでいた追加助走を廃止しました。高い床での走査・状態書き換え・カクつきを発生させません。

## ビルド

必要なもの:

- Windows
- Visual Studio 2022 または Build Tools（`.NET デスクトップ ビルド ツール`）
- A Dance of Fire and Ice
- Unity Mod Manager が導入されたゲームの `Managed` フォルダー

通常のSteam既定パスなら、リポジトリ直下で次を実行します。

```powershell
.\build.ps1
```

別のSteamライブラリにある場合:

```powershell
.\build.ps1 -GameManagedDir "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed"
```

環境変数でも指定できます。

```powershell
$env:ADOFAI_GAME_MANAGED_DIR = "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed"
.\build.ps1
```

成功すると、DLLは `src\bin\Release`、導入用ZIPは `artifacts` に生成されます。ゲームのModフォルダーへ同時に配置する場合:

```powershell
.\build.ps1 -DeployDir "C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\ADOFAIAudioSync"
```

ゲーム本体やUnityのDLLはリポジトリへ含めません。ビルド時にローカルのゲームから参照します。
