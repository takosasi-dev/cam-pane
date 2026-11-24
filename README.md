# CamPane

Android 端末のカメラの映像を、Windows PC の 1 枚のウィンドウに映すソフトです。[English](README.en.md)

> **開発中です。まだ使える物はありません。**
> いまリポジトリにあるのは、設計を決めるための測定用ツール(Probe)の PC 側と Android 側だけです。アプリ本体は、これから作ります。

## これは何をするアプリか

**端末のカメラの映像を PC に送るアプリです。** 通話や画面共有で手元(楽器、紙に書いた図、ボードゲームの盤面)を見せるために、机の上の端末のカメラを使います。PC 側は普通のウィンドウに映すだけで、Discord の画面共有や録画のソフトでは、そのウィンドウを選んで使います。

カメラを扱うので、次を守る設計にしています。

- カメラが動いている間、端末の画面に必ず「送信中」と、送り先の PC の名前を出します。
- 映すのは、端末のアプリが画面に出ている間だけです。裏に回したり画面を消したりすると、カメラを止めます。
- カメラを開けるのは、端末の画面の「映す」ボタンだけです。PC 側から端末のカメラを開かせる指示はありません。
- 映像を保存しません。保存するのは、PC 側で「撮る」を押した 1 枚だけです。
- 音は扱いません。端末のマイクの権限を求めません。
- 仮想カメラにはしません。ドライバを作らず、入れません。PC 側は管理者で動かしません。
- 通信は TLS で、ペアリングした端末だけを通します。インターネットへは通信しません。

### 止め方

- 端末の「止める」ボタンを押す、または端末のアプリを閉じる。
- PC 側の映像のウィンドウを閉じる(端末のカメラも止まります)。

## 開発の状況

| 段階 | 中身 | 状態 |
|---|---|---|
| Phase 0 | 測定用ツール(Probe) | PC 側と Android 側を作った。dtab(Android 7.0)で圧縮と PC での復号まで確認。`show` と画面共有の確認はこれから |
| Phase 1〜3 | 本体 | 未着手 |

## ダウンロード

[Releases](https://github.com/takosasi-dev/cam-pane/releases) の v0.0.1(プレリリース)に、Probe の APK と Windows の zip を置いています。

- **Windows の zip**: .NET 10 のランタイムごと入れてあるので、.NET を入れなくても動きます。展開して、中の `CamPane.Probe.exe` を PowerShell から動かします。
- **APK**: **デバッグ用の鍵で署名した仮の物**です。正式な鍵で署名した版に変えるときは、いったん消してから入れ直す必要があります。

## いまある物: PC 側の Probe

動作環境: Windows 11(x64)、.NET 10 SDK。

```powershell
dotnet build pc\CamPane.sln -c Release
```

`CamPane.Probe.exe` を引数なしで動かすと、使い方が出ます。

| サブコマンド | やること |
|---|---|
| `decoders` | H.264 のデコーダーの一覧を出す |
| `decode <ファイル>` | 生の H.264(Annex B)のファイルを復号し、フレームの数と大きさを出す。v0.0.2 から、入れ方を 4 通り((a) そのまま (b) 低遅延の設定 (c) 1 枚の絵を 1 サンプルに (d) SPS に「並べ替えなし」を書き足す)で比べ、絵が出るまでの溜めを出す |
| `show <ファイル>` | 同じファイルを復号して、ウィンドウに繰り返し映す(画面共有で取り込めるかを見るため) |

Probe は、復号した絵をファイルに保存しません。読ませる映像のファイルは、確かめた後に消してください(`.gitignore` で、映像のファイルはリポジトリに入らないようにしてあります)。

## いまある物: Android 側の Probe

端末のカメラと H.264 のエンコーダーの一覧を画面に出し、カメラの映像を 5 秒だけ圧縮して、生の H.264(Annex B)のファイルに保存します。画面に出す行は、logcat(タグ `CamPaneProbe`)にも出します。

- 権限は CAMERA だけです。通信はしません(INTERNET の権限もありません)。
- 圧縮の経路は 2 つあります。既定は GL 経由です。端末によっては直接の経路ではフレームが出ないため(カメラの側が弾く)、GL 経由を既定にしています。
- 圧縮するのはアプリが画面に出ている間だけです。裏に回すと、カメラとエンコーダーを閉じて止めます。

ビルド(JDK 17 と Android SDK。`android\local.properties` に `sdk.dir` を書いておく):

```powershell
cd android; .\gradlew.bat assembleDebug
```

使い方:

- 画面の「5 秒圧縮して保存」ボタンを押す(GL 経由)。
- adb から動かすとき:

```powershell
adb shell am start -n io.github.takosasidev.campane.probe/.MainActivity --es action encode         # GL 経由
adb shell am start -n io.github.takosasidev.campane.probe/.MainActivity --es action encode-direct  # 直接
```

保存先は `/storage/emulated/0/Android/data/io.github.takosasidev.campane.probe/files/`(`campane_probe_<日時>.h264`)です。PC へ写して、PC 側の Probe に渡します。

```powershell
adb pull /storage/emulated/0/Android/data/io.github.takosasidev.campane.probe/files/campane_probe_<日時>.h264
CamPane.Probe.exe decode campane_probe_<日時>.h264
CamPane.Probe.exe show campane_probe_<日時>.h264
```

**映像には部屋が映ります。確かめた後は、端末の側と PC の側の両方のファイルを消してください**(端末の側は `adb shell rm <パス>`、またはアプリを消すと一緒に消えます)。

## ライセンス

MIT([LICENSE](LICENSE))。使っているライブラリのライセンスは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) にあります。
