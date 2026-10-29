# 変更点一覧(Camera Roles 版)

- 基準: `TheRealAlexV/WebcamSwitcher` の `main` = コミット **`943fe8e`**
  ("Video delay settings, per-camera vcam switches, and persisted on/off state")。
  作業開始時点で、`origin`(kemushiboy/WebcamSwitcher)の `main` と同一でした。
  タグ `v1.0`(`70e75b6`)はこれより古いため、基準には使っていません。
- 作業ブランチ: `feature/stable-camera-roles`
- ライセンス: MIT(`LICENSE`)と `THIRD_PARTY_NOTICES.md` はそのまま維持しています。

## 1. 元の WebcamSwitcher との主な違い

| 項目 | 元の実装(943fe8e) | 本改修 |
|---|---|---|
| 物理カメラの識別 | 取得は Device ID だが、設定が空なら「最初の 2 台」を自動採用 | **Device ID のみ**。自動採用・フォールバックなし |
| 仮想カメラ名 | `"<FriendlyName> (WebcamSwitcher)"`。同名なら列挙順で `(2)` を付与 | **ロールごとの固定名**(既定 `CAM-01` …)。重複は登録拒否 |
| 映像パイプ | 配列位置で決まる `…Frames.v1.p0`, `.p1` … | **永続 Virtual Camera ID** で決まる `…Frames.v1.r.<ID>` |
| 対応関係の単位 | 配列インデックス(slot) | **Camera Role**(永続 ID がキー) |
| 並べ替え・ロール変更 | 一覧が変わると全仮想カメラ停止 | 変わったロールだけ停止・再登録。他は無停止 |
| カメラの抜け | ストール監視で再起動を試行(デバイスの有無は区別せず) | DeviceWatcher で存在監視。**同じ ID だけ**再接続。状態を区別して表示 |
| 入力が無いときの仮想カメラ | 最後のフレームの繰り返し/黒 | **診断映像**(ロール・状態・最終受信時刻・理由) |
| 状態表示 | vcam on/off のみ | 10 種類の状態+入出力解像度/fps/受信/ドロップ/最終時刻/処理時間/再接続/利用状態 |
| 既定出力 | 1920×1080@30 | **1280×720@30 NV12** |
| アスペクト比の異なる入力 | MF のリサイズ任せ(実質 Stretch) | **Letterbox(既定)/ Crop / Stretch** を選択 |
| Capture Format Auto | 最速の非圧縮を優先(MJPEG は非圧縮が遅いときのみ) | 出力 fps に届くモードを優先。非圧縮が USB 2.0 帯域を圧迫する場合で、カメラが MJPEG を持つときだけ MJPEG(MF デコード) |
| Switcher 仮想カメラ | 既定 ON | **既定 OFF**(機能は維持。ホットキーも維持) |
| 設定保存 | `File.WriteAllText`(上書き) | 一時ファイル+fsync+`File.Replace`、`.bak` 保持、破損時はバックアップから復元、未知項目を保持 |
| ログ | `%TEMP%\WebcamSwitcher.apply.log` に無制限追記 | `%LOCALAPPDATA%\WebcamSwitcher\logs\`、日時+ロールタグ、5 MB×10 世代でローテーション、非同期書き込み |
| 起動オプション | Start with Windows / Start minimized(※最小化起動は未実装だった) | 最小化起動を実装。カメラ自動開始、仮想カメラ自動登録、クラッシュ後の自動再起動、起動時の認識待ち時間を追加 |
| カメラ登録 | Settings のドロップダウン(Friendly Name 表示。同名は区別不能) | **登録画面**: ライブ映像、Device ID 末尾と全文コピー、入力形式、状態、ロール割り当て |
| フレーム毎のアロケーション | ヘッダー用の小さな配列を毎フレーム確保 | 送信経路はアロケーションなし(バッファ再利用) |

## 2. 追加・変更したファイル

### 追加
- `src/WebcamSwitcher.Core/CameraRoles.cs` — ロール名規則、重複検査、スキーマ 1→2 移行、Device ID 末尾表示
- `src/WebcamSwitcher.Core/ConfigStore.cs` — 安全な保存/読み込み/移行
- `src/WebcamSwitcher.Core/RoleChannel.cs` — 1 ロール分のパイプライン(監視、再接続、診断映像、統計)
- `src/WebcamSwitcher.Core/DevicePresenceMonitor.cs` — DeviceWatcher によるデバイス存在監視
- `src/WebcamSwitcher.Core/DiagnosticFrame.cs` — 診断映像(内蔵 5×7 フォントで NV12 に直接描画)
- `src/WebcamSwitcher.Core/PerformanceMonitor.cs` — CPU/メモリ/ロール別 fps の定期ログ
- `src/WebcamSwitcher.Core/LatencyProbe.cs` — 追加遅延の計測用タイムスタンプ(試験専用)
- `src/WebcamSwitcher.Core/SyntheticSource.cs` — 負荷試験用テストパターン(`synthetic:` デバイス、試験専用)
- `src/WebcamSwitcher.App/RegistrationWindow.xaml(.cs)` — カメラ登録画面
- `tools/RoleTest/` — ロール層のヘッドレステスト(66 項目、CI で実行)
- `tools/ChromiumCheck/` — Chromium(Edge/Chrome ヘッドレス)の `enumerateDevices()` 確認
- `tools/soak-monitor.ps1`, `tools/ui-snapshot.ps1`
- `docs/camera-roles.md`(運用ガイド)、`docs/test-report.md`(動作確認・性能)、`docs/config.sample.json`、本ファイル

### 主な変更
- `AppConfig.cs` — スキーマ 2(Role, VirtualCameraName, VirtualCameraId, PhysicalFriendlyName, CaptureFps, ScaleMode, Reconnect, AutoRegisterVirtualCameras, RestartOnCrash, PerformanceLogIntervalSeconds, LogMaxFiles/MB, LatencyProbe、未知項目保持)
- `VirtualCameraService.cs` — 永続 ID をキーにしたスロット管理。リネーム/削除されたロールだけ停止・登録解除。重複はブロック
- `CameraEngine.cs` — ロールチャネルの集合に置き換え。設定変更は変化したロールだけ再構築
- `CameraSource.cs` — デバイス ID 完全一致、失敗原因の分類、Letterbox/Crop の読み取りサイズ算出、fps 優先の Auto、帯域考慮の MJPEG 選択、`MediaCapture.Failed` 監視、統計
- `FramePublisher.cs` — 送信経路のアロケーション除去、ドロップ/送信数カウンタ
- `PipelineController.cs`, `App.xaml.cs`, `MainWindow.*`, `SettingsWindow.*`, `PreviewVm.cs`, `HotkeyService.cs`, `ConfigService.cs` — ロール中心の UI と起動処理
- `WebcamSwitcherProtocol.cs/.h` — 既定出力 1280×720、ロール用パイプ名
- `installer/WebcamSwitcher.iss` — ドキュメント・ライセンス同梱、アンインストール時に実行中アプリ停止と ProgramData のルーティングファイル削除
- `build.ps1` — `regsvr32` の結果を待って HKLM で確認(従来は終了コードが常に 0 に見えて「登録済み」と誤表示していた)
- `tools/CaptureTest` — ロール用統合テストに作り直し(ルーティング・fps・遅延・フレーム保存)
- `tools/VcamTest` — 設定をロール付きで生成。並列起動の時間判定を「ネイティブ層の重なり」判定へ(保存が fsync を伴うため)

## 3. 旧設定からの移行

`%APPDATA%\WebcamSwitcher\config.json` に `SchemaVersion` が無い(=旧形式)場合、起動時に自動で移行し、すぐ保存します。

1. カメラ一覧の**保存されていた順**に `CAM-01`, `CAM-02` … を割り当てます(Windows の列挙順ではありません)。
2. `VirtualCameraName` はロール名、`VirtualCameraId` は新しい GUID を生成します。
3. `FriendlyName` → `PhysicalFriendlyName` に移し、`FriendlyName` は書き出しません。
4. `DeviceId`、`CaptureWidth/Height`、`CaptureFormat`、`DelayMs`、`VirtualCameraEnabled`、ホットキー等はそのまま。
5. Switcher 仮想カメラは OFF にします(本版の方針)。
6. 未知の項目はそのまま残します。移行内容はログに `[Config]` として記録されます。

移行後は ID が固定され、二度と再生成されません(移行処理は冪等)。
仮想カメラ名が `"<名前> (WebcamSwitcher)"` から `CAM-01` などに変わるため、
**既存アプリ側の仮想カメラ選択は一度選び直してください。**

## 4. MediaPipe TouchDesigner プラグインについて

プラグイン本体は変更していません。調査結果:

- `src/state.js` の `changeWebcam()` は `navigator.mediaDevices.enumerateDevices()` の **ラベル完全一致**
  (`device.label == webcam`)でカメラを探し、`getUserMedia({ video: { deviceId: { exact } , width/height: ideal(ウィンドウサイズ) } })`
  で開きます。
- ラベルは `CAM-01 (Windows 仮想カメラ)` のように Windows が付ける接尾辞を含みますが、本アプリはロール名を固定するため、
  同じ PC・同じ言語設定ではラベルは不変です。したがって **仮想カメラ名の選択だけで安定します**。
- 注意点(プラグインの変更ではなく運用で対応): プラグインが要求する解像度は Web Render TOP の
  ウィンドウサイズ(`ideal`)です。仮想カメラは 1280×720 のみを提供するため、ブラウザ側で縮小されることはあっても
  入力は 720p です。720p で処理させたい場合は Web Render TOP の解像度を 1280×720 にしてください。
