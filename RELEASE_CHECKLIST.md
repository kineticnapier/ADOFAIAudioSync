# v1.0.0 Release Verification

v1.0.0をReleaseビルドし、同じDLLで次を確認します。問題が出た場合は、ゲームログ、再現に使った音源形式、開始床、操作順を残します。

## Build and package

- [ ] `build.ps1`のReleaseビルドが警告・エラーなしで完了する
- [ ] `artifacts/ADOFAIAudioSync-v1.0.0.zip`が生成される
- [ ] ZIP内が`ADOFAIAudioSync/ADOFAIAudioSync.dll`と`ADOFAIAudioSync/Info.json`だけである
- [ ] `Info.json`とオーバーレイの版が`1.0.0`で一致する
- [ ] 旧`Settings.xml`を残した更新でも起動する

## Playback

- [ ] WAVで曲頭から再生できる
- [ ] WAVで選択床から再生できる
- [ ] OGGで曲頭から再生できる
- [ ] OGGの初回・キャッシュ再利用後とも選択床から再生できる
- [ ] 途中再生を短時間に連打しても無音・二重再生・大幅な位置ずれが起きない
- [ ] エディターへ戻った直後に別の床から開始しても、選択床が後方へ飛ばず音声付きで開始する
- [ ] 添付再現譜面の床420前後と床895付近を連続して開始しても、選択したcheckpointが書き換わらない
- [ ] 曲末端付近ではtimeoutを繰り返さず本体Scrubへ戻る
- [ ] 高BPM区間の途中再生カウントダウンが設定上限へ折りたたまれる
- [ ] 3200 BPM区間の床77付近から開始しても、床786前後へ飛ばず、コンボが残留せず`scnGame.Play`が1回だけ観測される
- [ ] PauseイベントのWait Beatsが設定どおり独立する

## Lifecycle

- [ ] 再生中にエディターへ戻ると音が止まる
- [ ] ゲームオーバー後に音が残らない
- [ ] リスタート後に古い音が残らない
- [ ] 予約待機中にModをOFFにしても本体の再生状態が失われない
- [ ] 別譜面を開いたとき、前の計測・補正状態が誤適用されない

## Cache and tools

- [ ] OGGキャッシュOFFで本体読込へ戻る
- [ ] キャッシュ消去後に件数と実際の参照が解放される
- [ ] 容量超過時にLRU削除が働く
- [ ] `Ctrl+F9`の変更が再起動後も保存される
- [ ] BPM・位相タップ計測、Take保存、適用、取消が動作する
- [ ] 実験的プレイ誤差補正が既定OFFで、明示的にONにした場合だけ記録する

## Failure diagnostics

途中再生が失敗した場合、次の区切りを含む範囲をそのまま保存します。

```text
=== ADOFAI AudioSync v1.0.0 checkpoint schedule failure ===
...
=== end checkpoint schedule failure ===
```

正式配布前に、未確認の項目を実ゲーム上で完了させます。
