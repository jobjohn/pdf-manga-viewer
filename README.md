# PDF Manga Viewer

WinUI 3 (C# / .NET 10) 製のシンプルな PDF 漫画ビューワです。
縦長・横長どちらのページでも、アスペクト比を保ったままウィンドウいっぱいに表示します。

## 機能

- PDF / ZIP・CBZ アーカイブ / 画像フォルダを画面いっぱいに表示
- パスワード保護された PDF の解除に対応
- 見開きモード（3種類のページ組み）＋ 横長ページの自動単独表示 ＋ 手動の1ページずらし（`S`）
- 右綴じ（右から左）表示・キー反転・右下ページ番号・画面左右クリックでのページ送りに対応（設定はすべて保存）
- 続きから開く（ファイルごとの読書位置記憶）＆ 最近開いたファイル一覧
- 同じフォルダ内の前後の巻（PDF / ZIP / CBZ / フォルダ）へ移動（最終ページ到達時の次巻案内つき）
- 拡大・縮小（`Ctrl + ホイール` / ダブルクリック / `+`・`-`・`0`）＆ 拡大中のドラッグ移動（パン）
- 90°回転（`R` / `Shift+R`）
- サムネイル一覧（ページ一覧グリッド、`T`）
- ページ番号指定ジャンプ（ページ番号クリック / `Ctrl+G`）＆ シークバー操作中のページ番号プレビュー
- ウィンドウ位置・サイズ・最大化状態の記憶
- ドラッグ＆ドロップ、ファイル／フォルダ選択ダイアログ、コマンドライン引数、Windows の「プログラムから開く」に対応
- 前後ページの先読みレンダリングと LRU キャッシュでページ送りを軽快に
- 高 DPI 対応・ウィンドウサイズ変更時の自動再レンダリング
- `F11` で全画面表示、`Esc` で全画面・拡大・サムネイル一覧を解除

## 動作環境

- Windows 10 1809 (17763) 以降 / Windows 11
- .NET SDK 10.0 以降（ビルドする場合）

Windows App SDK と .NET ランタイムは同梱（self-contained）設定のため、公開したフォルダーを配布先の PC にコピーするだけで起動できます。

## ビルドと実行

Visual Studio は不要で、`dotnet` CLI だけでビルドできます。

```powershell
dotnet build -c Release
.\bin\Release\net10.0-windows10.0.19041.0\win-x64\PdfMangaViewer.exe
```

開発中にそのまま実行する場合:

```powershell
dotnet run
```

PDF を指定して起動することもできます。

```powershell
.\bin\Debug\net10.0-windows10.0.19041.0\win-x64\PdfMangaViewer.exe "D:\books\manga.pdf"
```

配布用フォルダを作る場合:

```powershell
dotnet publish -c Release -r win-x64
```

`bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\` 以下をフォルダごと配布してください。.NET ランタイムと Windows App SDK を含むため約 210MB になりますが、配布先での追加インストールは不要です。

> ARM64 で動かす場合は `PdfMangaViewer.csproj` の `RuntimeIdentifier` を `win-arm64` に変更してください。

## インストーラー

[Releases](https://github.com/jobjohn/pdf-manga-viewer/releases) から `PdfMangaViewer-Setup-<バージョン>.msi` をダウンロードして実行するとインストールできます（自分でビルドした場合は `installer\dist\` に出力されます）。

- インストール先: `%LOCALAPPDATA%\Programs\PdfMangaViewer`
- スタートメニューとデスクトップにショートカットを作成
- アンインストールは Windows の「設定 > アプリ > インストールされているアプリ」から
- ユーザー設定 (`%LOCALAPPDATA%\PdfMangaViewer\settings.json`) はアンインストール後も残ります
- 署名なしの MSI のため、配布方法によっては SmartScreen の警告が表示される場合があります

インストーラーを再ビルドする場合:

```powershell
.\installer\build-installer.ps1
```

`dotnet publish`、インストーラー用ファイル一覧の生成、MSI のビルドをまとめて実行します。バージョンは `PdfMangaViewer.csproj` の `<Version>` が使われます（`-Version 0.0.2` のように指定して上書きもできます）。

### リリース手順

`v0.0.2` のような形式のタグを push すると、GitHub Actions（[.github/workflows/release.yml](.github/workflows/release.yml)）が `windows-latest` で MSI をビルドし、GitHub Release に自動で公開します。バージョンはタグから取られます。

```powershell
git tag v0.0.2
git push origin v0.0.2
```

## キー操作

| キー | 動作 |
| --- | --- |
| ← | 次のページ（「キー反転」オンのとき）／前のページ（オフのとき） |
| → | 前のページ（「キー反転」オンのとき）／次のページ（オフのとき） |
| ↑ / PageUp / Shift+Space | 前のページ |
| ↓ / PageDown / Space | 次のページ |
| Home / End | 最初 / 最後のページ |
| Ctrl+G | ページ番号を指定して移動 |
| S | 見開きを1ページずらす |
| R / Shift+R | 90°時計回り / 反時計回りに回転 |
| T | サムネイル一覧の表示切替 |
| Ctrl+PageUp / Ctrl+PageDown | 同じフォルダ内の前 / 次の巻を開く |
| + / - / 0 / Ctrl+ホイール / ダブルクリック | 拡大・縮小・リセット（拡大中はドラッグで移動、右クリックでリセット） |
| Ctrl+O / Ctrl+Shift+O | ファイルを開く / フォルダを開く |
| F11 / Esc | 全画面表示の切替 / 解除（サムネイル・拡大の解除も可） |

マウスホイールでもページ送りできます（下方向で次ページ）。
下部の「見開き」ボタンから表示方法を選べます。

- **単ページ**: 1 ページずつ表示
- **見開き（表紙のみ単独）**: 1 / 2-3 / 4-5 / …
- **見開き（1ページ目から2枚ずつ）**: 1-2 / 3-4 / 5-6 / …
- **見開き（1・2ページ目のみ単独）**: 1 / 2 / 3-4 / 5-6 / …

選択した見開きモードは保存され、次回起動時も同じモードで開きます。
右綴じがオンのときは、見開きの左右が逆になり、左に後のページ・右に前のページが表示されます（例: 左に P3、右に P2）。
下部の「キー反転」は初期状態でオンになっており、左右キーと左右の矢印ボタンを「← が次ページ」の向きにします。オフにすると「← が前ページ」の従来の向きに戻ります。
下部の「右綴じ」は初期状態でオンで、見開きを右から左に並べます。オフにすると左から右になります。この設定は保存され、次回起動時も引き継ぎます（キーの向きとは独立です）。
操作バーは 1 秒間操作がないと自動で隠れます。表示中にバー以外の場所をクリックしても閉じます。隠れた後は、バー付近にマウスを移動するか、非表示の状態でクリックすると再表示されます（画面内を動かしただけでは表示されません）。

## 実装メモ

- PDF の描画には Windows 標準の `Windows.Data.Pdf` を使用しています（追加のネイティブ DLL は不要）。
- `PdfPage.RenderToStreamAsync` に画面サイズ（物理ピクセル）から計算した `DestinationWidth` / `DestinationHeight` を渡し、`BitmapImage` に変換して表示しています。
- 画像は `Stretch="Uniform"` で配置しているため、ページごとに縦横比が変わっても余白が均等になり、画面からはみ出しません。
- 現在のページに加えて前後 2 ページ程度をバックグラウンドでレンダリングし、LRU キャッシュ（最大 8 ページ）に保持しています。
- 見開き表示では、2 ページの高さを揃えて横に並べ、合計幅が画面に収まるように表示サイズを計算しています。右綴じ時は若いページを右側に配置します。
- 見開きモードと読み方向（右綴じ）の設定は `%LOCALAPPDATA%\PdfMangaViewer\settings.json` に保存しています。
- レンダリング画像の長辺は 2600px を上限にしてメモリ使用量を抑えています。
- WinUI が生成する `.xbf` とアプリ用の `.pri` は `dotnet publish` に含まれないことがあるため、`CopyWinUIResourcesToPublish` ターゲットで公開フォルダーへコピーしています。

## ライセンス

[MIT License](LICENSE)
