# NOICE - The Digital Void 🌌 (Speed Up Edition)

動画から「存在（背景）」を消去し、「意思（動き）」だけを抽出するアート・ビデオプロセッサー。

## Features
- **Background Subtraction**: 動きのある部分だけをノイズで可視化。
- **Real-time Streaming**: 低遅延プレビュー。
- **Audio**: オリジナル音声の結合、またはホワイト/ブラウンノイズ（ffmpeg で生成）。
- **High-End Design**: 虚無をテーマにしたプレミアムなUI/UX。

## Tech Stack
- **Backend**: C# / ASP.NET Core 8 + OpenCvSharp4
- **Frontend**: Vanilla JavaScript & CSS (Glassmorphism)
- **Audio muxing**: ffmpeg（PATH に必要）

## Requirements
- .NET 8 SDK
- Windows 10/11（OpenCvSharp4.runtime.win 使用時）
- ダウンロード時の音声ミックス用に **ffmpeg** を PATH にインストール推奨

## Setup
1. `run.bat` を実行（初回は `dotnet build` でビルド）。
2. ブラウザで `http://127.0.0.1:8000` にアクセス。
