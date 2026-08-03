# ADOFAI AudioSync

[日本語](README.md) | [English](README.en.md) | [한국어](README.ko.md)

ADOFAIのエディター再生を安定させるUnity Mod Manager用Modです。特に、選択した床から再生したときに音源の開始が遅れる、譜面と音がずれる、開始位置が大きく飛ぶといった問題を抑えます。譜面制作を補助するBPM・位相タップアンカーと、プレイ結果から速度変化を推定する実験機能も含みます。

## 主な機能

- **途中再生の音声同期**
  選択床に対応する音声位置を未来のDSP時刻へ予約し、実際に動き始めたAudioSourceの再生位置を確認してから譜面時計を一度だけ整列します。異常な開始を検出した場合は再試行し、同期を確立できなければADOFAI本体の再生処理へ安全に戻ります。
- **本体の開始処理を維持**
  `scnGame.Play`を抑止・再実行したり、ADOFAIが選んだcheckpointを書き換えたりしません。他Modからも通常どおり1回の再生開始として見える構造です。
- **高BPMカウントダウンの折りたたみ**
  3200 BPMなどの高速区間でも、途中再生の助走を設定した上限BPM以下の見やすい速さへ折りたたみます。音声の開始時刻や選択床は変えず、惑星の助走計算だけに倍率を適用します。
- **Pause / Wait Beatsの分離**
  高BPM用の折りたたみ倍率をPauseイベントのWait Beatsへ適用するか選択できます。既定では通常の譜面速度を維持します。
- **OGGメモリキャッシュ**
  一度読み込んだOGG音源をデコード済みAudioClipとして再利用し、同じ譜面を途中再生するときの再デコードを減らします。デコード前にVorbisの総サンプル数からPCM容量を判定し、上限を超える音源はキャッシュせず本体のストリーミング読込へ戻します。容量上限、LRU削除、手動消去に対応します。
- **BPM・位相タップアンカー**
  選択床を基準に曲へタップすると、タップ列からBPMと拍の位相を推定します。候補を確認し、SetSpeedイベントとして譜面へ適用できます。
- **プレイ誤差からの速度補正（実験機能）**
  手動プレイ中の早遅を区間ごとに解析し、ずれの増減からBPM補正候補を作ります。既定ではOFFで、明示的に有効化した場合だけ記録します。
- **診断表示と失敗ログ**
  予約残差、playhead補正、開始待ち時間、OGGキャッシュ状態などを簡易または詳細表示できます。途中再生に失敗した場合は、原因調査用のまとまったログを出力します。

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
- OGGキャッシュ容量はVorbisの総サンプル数から計算したデコード後PCM容量です。Unity側の一時的なメモリ使用量までは含みません。安全に容量を計算できないOGGはキャッシュ対象外になります。
- 音声やエディター再生へ介入する別Modとの組み合わせは、個別に確認が必要です。

1.0.1向けの回帰項目は[`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md)、変更履歴は[`CHANGELOG.md`](CHANGELOG.md)にあります。

## License

[MIT License](LICENSE)
