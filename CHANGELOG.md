# Changelog

この形は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に合わせています。

## [Unreleased]

## [0.0.2] - 2026-11-24

### Added

- PC の `decode`: 入れ方を 4 通りで比べる。(a) 今のまま (b) 低遅延の設定(`CODECAPI_AVLowLatencyMode`) (c) 低遅延のまま 1 枚の絵を 1 サンプルにまとめる (d) (c) に加えて SPS の VUI に `max_num_reorder_frames=0` を書き足す。最初の 1 枚までと、各絵が出るまでの溜めを出す。VUI の無い映像(dtab のエンコーダーの出力)では、(a)〜(c) で約 17〜35 枚溜まり、(d) で 1 枚になった。

## [0.0.1] - 2026-11-24

### Added

- Phase 0: PC 側の測定用ツール `CamPane.Probe` を追加(decoders / decode / show)。
- Phase 0: Android 側の測定用ツール(probe)を追加。カメラと H.264 のエンコーダーの一覧を出し、5 秒圧縮して生の H.264(Annex B)で保存する。経路は GL 経由(既定)と直接の 2 つ。権限は CAMERA だけで、通信しない。

[Unreleased]: https://github.com/takosasi-dev/cam-pane/compare/v0.0.2...HEAD
[0.0.2]: https://github.com/takosasi-dev/cam-pane/releases/tag/v0.0.2
[0.0.1]: https://github.com/takosasi-dev/cam-pane/releases/tag/v0.0.1
