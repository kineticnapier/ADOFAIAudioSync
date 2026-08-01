# ADOFAI AudioSync

ADOFAIのエディター再生、特に選択床からの途中再生で発生するランダムな音源ずれを抑えるUnity Mod Manager用Modです。BPM・位相タップアンカーと、プレイ誤差から速度変化を作る実験機能も含みます。

## 主な機能

- ADOFAI本体の`scnGame.Play`を抑止・再実行せず、そのまま1回だけ通す再生経路
- 選択床からの再生を未来のDSP時刻へ予約し、実際のAudioSource playheadに一度だけ整列
- 高BPM区間の途中再生カウントダウンを読みやすい速度へ折りたたみ
- PauseイベントのWait Beatsを通常の譜面速度へ分離可能
- OGG音源をデコード済みのAudioClipとしてメモリキャッシュ
- BPM・位相タップアンカー
- プレイ誤差からの速度補正（実験機能・既定OFF）
- 簡易／詳細の診断表示と、途中再生失敗時の完全ログ

## 対応環境

- Windows版 A Dance of Fire and Ice
- Unity Mod Manager
- ゲーム本体のMono/Managed DLLを参照できる環境

ゲーム更新で本体メソッドの構造が変わると、一部のHarmonyパッチが無効になる場合があります。パッチは個別に導入されるため、1機能の失敗だけでMod全体を解除しません。

## インストール

配布ZIPをゲームの`Mods`フォルダーへ展開します。次の配置になれば完了です。

```text
A Dance of Fire and Ice/
└─ Mods/
   └─ ADOFAIAudioSync/
      ├─ ADOFAIAudioSync.dll
      └─ Info.json
```

配布ZIPにはPDBを含めません。設定はUnity Mod ManagerのMods画面から変更できます。旧版の`Settings.xml`は起動時に移行され、読み込めない場合は日時付きの`.broken-*`へ退避されます。

## ショートカット

| 操作 | 機能 |
|---|---|
| `Ctrl+F9` | 診断表示をOFF→簡易→詳細の順に切替 |
| `Ctrl+F6` | BPM・位相タップウィンドウを開閉 |
| `Ctrl+T` | 選択床を新しいBPM・位相計測点に設定 |
| `F10` | タップ入力（ウィンドウ内で変更可能） |
| `Backspace` | 計測中の直前タップを削除 |
| `Enter` | タップ計測を確定 |
| `Ctrl+Enter` | 解析結果を譜面へ適用 |
| `Escape` | 現在の計測を取り消し |
| `Ctrl+Shift+E` | 実験的なプレイ誤差記録を開始／停止 |

`Ctrl+F9`の変更はその場で保存されます。

## ビルド

必要なもの:

- Windows
- Visual Studio 2022またはBuild Toolsの「.NETデスクトップビルドツール」
- A Dance of Fire and Ice
- Unity Mod Managerが導入されたゲームの`Managed`フォルダー

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

成功すると、DLLは`src\bin\Release`、UMM用ZIPは`artifacts`に生成されます。ビルドスクリプトはZIP内が`ADOFAIAudioSync/ADOFAIAudioSync.dll`と`ADOFAIAudioSync/Info.json`の2ファイルだけであることも検証します。

ゲームのModフォルダーへ同時に配置する場合:

```powershell
.\build.ps1 -DeployDir "C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\ADOFAIAudioSync"
```

`-DeployDir`ではローカル診断用にPDBも配置します。ゲーム本体やUnityのDLLはリポジトリへ含めず、ビルド時にローカルのゲームから参照します。

## 既知の制限

- 主な対象はエディター内の再生です。通常のレベルプレイの音声挙動を変更するModではありません。
- 音源末端で2回のplayhead更新を確認できない場合は、危険なDSP予約を行わずゲーム本体のScrub処理へ戻します。
- OGGキャッシュ容量はデコード後PCMの推定値です。Unity側の一時的なメモリ使用量までは含みません。
- 音声やエディター再生へ介入する別Modとの組み合わせは、個別に確認が必要です。

1.0.0向けの回帰項目は[`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md)、変更履歴は[`CHANGELOG.md`](CHANGELOG.md)にあります。

## License

[MIT License](LICENSE)
