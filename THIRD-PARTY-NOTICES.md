# Third-party notices

CamPane 自身のコードは MIT です。ビルドのときに NuGet から取る物と、そのライセンスは次のとおりです(2026-11-24 に各パッケージの nuspec とライセンスの本文で確認)。

## PC 側(ビルドの時だけ使う物。出力には入らない)

| パッケージ | 版 | ライセンス | 役目 |
|---|---|---|---|
| Microsoft.Windows.CsWin32 | 0.3.371 | MIT | Win32 と COM の宣言をビルドの時に生成する |
| Microsoft.Windows.SDK.Win32Metadata | 71.0.45-preview | Microsoft Windows SDK のライセンス | 上が読むメタデータ |
| Microsoft.Windows.WDK.Win32Metadata | 0.13.25-experimental | Microsoft Windows SDK のライセンス | 同上 |
| Microsoft.Windows.SDK.Win32Docs | 0.1.42-alpha | Microsoft Windows SDK のライセンス | 同上(説明文) |

Windows SDK のライセンスの 3 つは、このリポジトリにも、ビルドした exe の隣にも入りません。

## PC 側(テストの時だけ使う物)

| パッケージ | 版 | ライセンス |
|---|---|---|
| xunit(xunit.core / xunit.assert / xunit.analyzers / xunit.extensibility.* を含む) | 2.9.3(analyzers は 1.18.0) | Apache-2.0 |
| xunit.abstractions | 2.0.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk(Microsoft.CodeCoverage / Microsoft.TestPlatform.* を含む) | 17.14.1 | MIT |
| Newtonsoft.Json | 13.0.3 | MIT |

## Android 側

まだありません。
