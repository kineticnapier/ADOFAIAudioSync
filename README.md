# ADOFAI AudioSync

ADOFAI のエディター再生、特に選択床からの途中再生で発生するランダムな音源ずれを抑える Unity Mod Manager 用 Mod です。BPM・位相タップアンカーなどの既存機能も含みます。

## 機能

- エディター再生の開始準備
- 選択床から再生するときの音源同期
- 高BPM区間のカウントダウン速度調整
- OGG音源のメモリキャッシュ
- BPM・位相タップアンカー
- 高速な再開や音源ドリフトの診断

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
