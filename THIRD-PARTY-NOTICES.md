# Third-party notices

CamPane 自身のコードは MIT です。ビルドのときに NuGet と Maven から取る物、配布物に同梱する物と、そのライセンスは次のとおりです(2026-11-24 に確認。PC 側は各パッケージの nuspec とライセンスの本文、Android 側は Gradle の依存の一覧と各プロジェクトが公表しているライセンス)。

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

## PC 側(Windows の zip に同梱する物)

Releases の Windows の zip は、.NET を入れていない PC でも動くように、次のランタイムを同梱しています(自己完結型の発行。版は 10.0.11)。

| 同梱する物 | ライセンス | ライセンスの本文 |
|---|---|---|
| .NET 10 のランタイム(Microsoft.NETCore.App) | MIT | https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| Windows Desktop のランタイム(Microsoft.WindowsDesktop.App。WPF を含む) | MIT | https://github.com/dotnet/wpf/blob/main/LICENSE.TXT |

ランタイムの中に含まれる他者のコードの表示は、各リポジトリの THIRD-PARTY-NOTICES.TXT にあります。

## Android 側(APK に入る物)

版は `cd android; .\gradlew.bat -q :probe:dependencies --configuration releaseRuntimeClasspath` で確かめた物です。

| パッケージ | 版 | ライセンス |
|---|---|---|
| org.jetbrains.kotlin:kotlin-stdlib | 2.0.21 | Apache-2.0 |
| org.jetbrains:annotations | 13.0 | Apache-2.0 |

## Android 側(テストの時だけ使う物。APK には入らない)

| パッケージ | 版 | ライセンス |
|---|---|---|
| junit:junit | 4.13.2 | EPL-1.0 |
| org.hamcrest:hamcrest-core | 1.3 | BSD-3-Clause |

junit(EPL-1.0)は、作者の判断でテスト専用の例外にしました。APK には入りません。
