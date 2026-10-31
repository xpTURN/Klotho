# xpTURN.Klotho
[![Unity 2022.3+](https://img.shields.io/badge/unity-2022.3%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![Godot 4.4+ (.NET)](https://img.shields.io/badge/godot-4.4%2B%20(.NET)-478cbf.svg)](https://godotengine.org/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

[English](README.md) | [한국어](README.ko-KR.md) | **日本語** | [简体中文](README.zh-CN.md)

> この文書は英語版 [README](README.md) の翻訳です。内容に差異がある場合は英語版を正とします。

**Unity と Godot のための決定論的マルチプレイヤーシミュレーションフレームワーク**

クライアントサイド予測（CSP）、ロールバック、フレーム同期、Server-Driven モード、リプレイに対応した決定論的シミュレーションフレームワークです。シミュレーションのコアはエンジンに依存しない純粋な C# で、その上に **Unity** と **Godot (.NET)** のアダプターが載ります。浮動小数点を排除し、32.32 固定小数点（`FP64`）と決定論的な乱数生成器（Xorshift128+）だけでシミュレーションを構築するため、プラットフォームやコンパイラが異なっても完全な再現性を保証します。

<img src="klotho.png" alt="Klotho" width="800">

> Klotho はシミュレーションを 1 フレームずつ紡ぎます。

**同期の仕組みを一段落で:** 鍵は決定論です。同じ順序の入力が与えられれば、すべてのピアがバイト単位で同一の状態を計算するため、ネットワークは*入力だけ*を運び、検証はハッシュの比較に帰着します。その上で各ピアは、同じティック軸の上に 2 本のタイムラインを持ちます。**確定（Verified）チェーン**（全プレイヤーの実際の入力が判明しているティック — 不変）と、**予測（Predicted）チェーン**（リモート入力を*推測して*先行実行したティック — 暫定）です。CPU の分岐予測のように、シミュレーションは待たずに予測入力で即座に進みます。実際の入力が推測と食い違うと、エンジンはスナップショットを復元して再シミュレーションします（**ロールバック**）。状態が入力の純粋関数であるため、このコストは小さく抑えられます。入力遅延と適応的なタイミングバッファがロールバックの発生頻度を減らし、決定論が実際に崩れた場合は段階的な回復の階段（ハッシュ確認 → ロールバック → 全状態の再同期 → 補正リセット）が一致を取り戻します。同じ仕組みが **P2P ロックステップ**（ピアが同等の権限を持つ）と **Server-Driven**（サーバーが確定チェーンを所有する）の両方を支えます。詳しい根拠: [Docs/SynchronizationDesign.md](Docs/SynchronizationDesign.md)。

---

## 主な機能

| 領域 | 内容 |
| ---- | ---- |
| **ネットワークモデル** | P2P ロックステップ · ロールバック + クライアント予測 · Server-Driven（権威サーバー） · 専用サーバー · 観戦 · 途中参加（Late Join） · 再接続 |
| **ルームとマッチ設定** | マルチルーム専用サーバー · `IMatchConfigSource` によるルームごとのステージ（`StageId`）+ マッチごとの不透明な設定（`MatchConfigData`） — 1 台のサーバーで異なるステージのルームをホスト · ロビーのルーム予約（接続前予約） · 確定したマッチ結果のロビーへの報告（`IMatchResultProvider`、少なくとも 1 回の配送 + 冪等） |
| **決定論的数学** | `FP64`（32.32 固定小数点） · `FPVector2/3/4` · `FPQuaternion` · `FPMatrix` · LUT/CORDIC ベースの三角関数 · `DeterministicRandom`（Xorshift128+） |
| **物理** | `FPPhysicsWorld` · ブロードフェーズ（SpatialGrid） · ナローフェーズ · CCD（Sweep） · 拘束ソルバー · ジョイント · トリガー · 静的 BVH |
| **ナビゲーション** | `FPNavMesh` · A*（三角形グラフ） · Funnel（SSFA） · ORCA 回避 · ECS 統合の `NavAgentComponent` |
| **ECS** | Sparse-set の `ComponentStorage<T>` · `Frame`（単一の byte[] ヒープ） · `FilterWithout/Filter<T1..T5>` · `FrameRingBuffer` · `SystemRunner` |
| **AI / HFSM** | 決定論的な階層型 FSM · `HFSMBuilder`（fluent） · `HFSMRoot` / `HFSMManager` · `HFSMComponent`（エンティティごと） · `HFSMDecision` / `AIAction` · Unity のステートツリー可視化ツール |
| **シリアライズ / ソースジェネレーター** | `SpanWriter/Reader`（ref struct、GC なし） · `[KlothoComponent]` / `[KlothoSerializable]` / `[KlothoDataAsset]` / `[KlothoSerializableStruct]` による自動コード生成 · ビルド時の `DeterminismAnalyzer`（シミュレーションコード内の float / 非決定論的 API を検出） |
| **データアセット** | `IDataAsset` · `DataAssetRegistry` · `DataAssetRef` · JSON シリアライズ（`xpTURN.Klotho.DataAsset.Json`） |
| **リプレイ** | 記録 / 再生 / シーク / 可変速度 |
| **検証ツール** | `SyncTestRunner`（GGPO 方式の決定論検証） · `DeterminismVerificationRunner` · ベンチマークスイート |
| **Unity 統合** | `USimulationConfig` · `USessionConfig` · ビュー層（`EntityViewFactory` / `EntityViewUpdater` / `EntityView`、`BindBehaviour` / `ViewFlags`、`VerifiedFrameInterpolator`） · `KlothoSessionDriver`（MonoBehaviour） · `KlothoConnectionAsync`（UniTask） |
| **Godot 統合** | `GodotSimulationConfig` · `GodotSessionConfig`（Resource） · ビュー層（`EntityViewFactory` / `EntityViewUpdaterNode` / `EntityViewNode`、`VerifiedFrameInterpolator`） · `GodotSessionDriver`（Node） · `GodotConnectionAsync`（`Task`） · `GodotDebugSink` / `GodotLogSink` |

---

## 向いているジャンル

Klotho の決定論的シミュレーション機能の恩恵を最も受けるジャンルです。

### 最適（主な対象）

- **格闘ゲーム** — フレーム単位で正確な入力 + ロールバックネットコード
- **プラットフォーム格闘 / アリーナ乱闘**
- **2〜4 人の PvP アクション** — 少人数の P2P ロールバックに最適
- **タクティクス / ターン制 SRPG** — 決定論 + リプレイ + 同期検証
- **リアルタイムストラテジー（RTS）** — ロックステップによるフレーム同期
- **MOBA / トップダウンアリーナ** — ECS、物理、ナビゲーションをすべて含む
- **ツインスティックシューター / 協力シューター** — 決定論的な物理 + ORCA 回避
- **オートバトラー** — 決定論的シミュレーション + 共有できるリプレイ

### 適している（構造的に好相性）

- **ローグライク / ローグライト（PvE 協力）** — 共有シードとリプレイのための決定論的乱数（Xorshift128+）
- **カード / ボード / デッキ構築 PvP** — 入力量が少なく、検証とリプレイに強い
- **パズル PvP / 落ち物対戦**
- **レース（小規模）** — 固定小数点物理が小さなグリッドで決定論を保証
- **トップダウンのサバイバルアクション**

---

## アーキテクチャ概要

```
┌─────────────────────────────────────────────────────────────┐
│                    Game Application                         │
│   (ISimulation impl: EcsSimulation or custom Simulation)    │
└───────────────────────────┬─────────────────────────────────┘
                            │
         ┌──────────────────┼──────────────────┐
         ▼                  ▼                  ▼
  ┌───────────────┐   ┌──────────────┐   ┌────────────────────┐
  │ KlothoEngine  │   │ ReplaySystem │   │KlothoNetworkService│
  │ (orchestrator)│◄  │ (record/play)│   │                    │
  └──────┬────────┘   └──────────────┘   └─────────┬──────────┘
         │                                         │
    ┌────┴────┐                              ┌─────┴──────┐
    ▼         ▼                              ▼            ▼
 ISimulation  InputBuffer               INetworkTransport  NetworkMessages
    │
    ├── (ECS) Frame — EntityManager + ComponentStorage[]
    ├── SystemRunner — ISystem[]
    ├── FrameRingBuffer (snapshots / rollback)
    └── DataAssetRegistry
```

3 層に分かれています:
- **Klotho エンジン層** — 純粋な C#、エンジン非依存（`UnityEngine` / Godot への参照なし）。同じバイナリが Unity と Godot (.NET) のアダプターの下で、そしてサーバー側（.NET コンソール / ASP.NET）で動きます。
- **シミュレーション転送層** — `INetworkTransport` 抽象化の上の UDP 転送（Input / InputAck / SyncCheck / Handshake）。既定の参照実装として LiteNetLib を提供しており、他のライブラリに置き換えられます。
- **ゲームサービス層** — ロビー / マッチメイキング / 認証（外部の gRPC など — このプロジェクトの外で統合）。

---

## 技術スタック

- **Unity 2022.3+** — スクリプティングバックエンド: Mono / IL2CPP（AOT セーフ — シリアライズはソース生成で行い、実行時のコード生成 · リフレクションなし）
- **Godot 4.4+ (mono / .NET, net8.0)** — Godot アダプターは `addons/klotho/`（ビルド済みのコア DLL + アダプターのソース）として配布されます。
- **C# 言語レベル** — Unity は **C# 8.0** を対象とし、C# 11 に必要なランタイム属性の代替実装は `xpTURN.Polyfill` パッケージが提供します。Godot アダプターは **net8.0 / `LangVersion latest`** のためポリフィルは不要です（ネイティブの `init` / `required`）。
- **UniTask** — Unity でのみ使う非同期（Cysharp）。**Godot アダプターは標準ライブラリの `Task`** を使います — UniTask への依存なし。
- **xpTURN.Klotho.Logging (IKLogger)** — 自社製の構造化ロギング（外部のロギング依存なし）。`Plugins~/Logging.Mel` サンプルアダプターで MEL と任意に連携できます（`Microsoft.Extensions.Logging.Abstractions` DLL は利用側が用意）。
- **LiteNetLib** — UDP 転送の既定の参照実装（MIT、純粋な C#）。Unity では `Runtime/ThirdParty/LiteNetLib.v2.1.4` 以下に**ソースとして同梱**され、Godot では **NuGet パッケージ `LiteNetLib 2.1.4`** として入ります。転送層は `INetworkTransport` で抽象化されており、置き換えられます。
- **Newtonsoft.Json** — DataAsset の JSON シリアライズ（Unity: `com.unity.nuget.newtonsoft-json` · Godot: NuGet `Newtonsoft.Json 13.0.3`）

詳細: [Docs/BaseLibraries.md](Docs/BaseLibraries.md)

---

## インストール

Klotho はエンジンごとにインストール方法が異なります — 対応するガイドに従ってください。各ガイドは**クライアント**を先に、続いて任意の**専用サーバー**（共有のエンジン非依存コアの上で動く通常の .NET ホスト）を扱います:

- **[インストール — Unity](Docs/Installation.Unity.md)** — UPM の git URL + Polyfill の有効化、続いて専用サーバー用の `Server~` プロジェクト参照。
- **[インストール — Godot (.NET)](Docs/Installation.Godot.md)** — `addons/klotho/` フォルダー + `Klotho.props` を取り込む 1 行、続いて（エンジン非依存の）専用サーバー。

リポジトリ内の [`Samples/`](Samples/) 以下のプロジェクトは、インストールの参考にもなります — サンプルごとの解説は[ドキュメントマップ](#ドキュメントマップ)を参照してください。

---

## リポジトリ構成

Klotho はリポジトリ最上位の `com.xpturn.klotho/` 以下にあります。Unity 利用者は UPM でパッケージをインストールし、Godot 利用者は `addons/klotho/` フォルダーとして梱包された `Godot~/` アダプターを使います（[インストール](#インストール)を参照）。リポジトリ内の Unity サンプルは、同じ最上位パッケージを `file:` の manifest 参照で使います。

```
<リポジトリルート>/
├── README.md  ·  CHANGELOG.md  ·  LICENSE
├── com.xpturn.klotho/             ← ★ フレームワークパッケージ
│   ├── package.json
│   ├── Runtime/                   エンジン非依存のコア
│   │   ├── Core/                  エンジン · セッション · ネットワーク
│   │   ├── Logging/               IKLogger（自社製）
│   │   ├── Gameplay/              組み込みのコンポーネント / システム
│   │   ├── Diagnostics/           フォールトインジェクション · メトリクス
│   │   ├── Input/                 入力バッファー / 予測器
│   │   ├── Network/               転送 · サーバー · 観戦
│   │   ├── State/                 スナップショットマネージャー
│   │   ├── Serialization/         SpanWriter/Reader
│   │   ├── Replay/                記録 / 再生
│   │   ├── ECS/                   Frame · コンポーネント · システム
│   │   ├── Deterministic/         FP64 · 物理 · ナビゲーション
│   │   ├── LiteNetLib/            UDP 転送
│   │   └── ThirdParty/            同梱の外部依存
│   ├── Unity/                     Unity アダプター
│   │   └── Editor/                Unity 専用のエディターツール
│   ├── Godot~/                    Godot (.NET) アダプター
│   │   └── Adapters/Editor/       Godot (.NET) のエディターツール
│   ├── Plugins/Analyzers/         ソースジェネレーター
│   ├── Plugins~/Logging.Mel/      MEL 連携サンプル
│   └── Server~/                   専用サーバーのプロジェクト
│
├── dist/addons/klotho/            ← Godot アドオン
│
├── Samples/                       ← 単体のサンプル
│   ├── Brawler/                   4 人対戦の格闘（Unity）
│   ├── P2pSample/                 最小の P2P（Unity）
│   ├── SdSample/                  最小の Server-Driven（Unity）
│   ├── GodotP2pSample/            最小の P2P（Godot）
│   ├── GodotSdSample/             最小の Server-Driven（Godot）
│   └── LoggingMelConsole/         .NET ロギングのサンプル
│
├── Docs/                          ← ドキュメント
└── Tools/                         ← .NET ツール（内部用）
    ├── KlothoGenerator/           ソースジェネレーター
    ├── KlothoGenerator.Tests/     ジェネレーターのテスト
    ├── DeterminismVerification/   決定論の検証ツール
    ├── PhysicsDeterminismProbe/   FP 決定論のプローブ
    └── gen.sh                     ジェネレーターのビルドスクリプト
```

---

## クイックスタート

4 つのステップ — コンポーネントの定義 → システムの実装 → コールバックの接続 → セッションの作成と駆動 — は両エンジンで同じ形ですが、セッションの駆動とビュー層はエンジンごとに異なります。対応する解説を選んでください（それぞれ完全なコードを含む独立した文書です）:

- **[クイックスタート — Unity](Docs/QuickStart.Unity.md)** — `MonoBehaviour` コントローラー、`KlothoSessionDriver`、`ScriptableObject` の設定、UniTask による参加、`EntityViewFactory` / `EntityViewUpdater` / `EntityView`。
- **[クイックスタート — Godot (.NET)](Docs/QuickStart.Godot.md)** — `Node` コントローラー、`GodotSessionDriver`（`_Process`）、`Resource` の設定、標準の `Task` による参加、`EntityViewNode` / `EntityViewUpdaterNode`（`.tscn`）。

ステップ 1〜3（コンポーネント / システム / コールバック）はエンジン非依存のコアです。`KlothoSessionFlow` は両エンジンで同じエントリーポイント（`StartHostAndListen` / `JoinP2PAsync` / `JoinServerDrivenAsync` / `ReconnectAsync` / `SpectateAsync` / `StartReplayFromFile`）を提供します — 非同期のものを Unity は UniTask で、Godot は標準の `Task` で包みます。セッションの作成は唯一の `IKlothoSessionObserver.OnSessionCreated(session, SessionEntryKind kind)` で観測します — `simCfg.Mode` ではなく `kind` で分岐してください。

詳しい解説: [Docs/GameDevWorkflow.md](Docs/GameDevWorkflow.md)、[Docs/GameDevAPI.md](Docs/GameDevAPI.md)

---

## サンプル

[Samples/Brawler](Samples/Brawler) — 4 人対戦の格闘ゲームのサンプル

- ECS ベースの戦闘 / 移動 / スキル / クールダウン / ノックバック / アイテム / トラップ
- HFSM ベースのボット AI（`BotHFSMRoot` / `BotActions` / `BotDecisions`）
- Unity Cinemachine とのカメラ連携
- P2P と ServerDriven の両モードに対応
- リプレイの記録 / 再生

ドキュメント: [Docs/Samples/Brawler.md](Docs/Samples/Brawler.md)

---

## ドキュメントマップ

以下のドキュメントは英語で書かれています。

| ドキュメント | 内容 |
| ---- | ---- |
| [Docs/Installation.Unity.md](Docs/Installation.Unity.md) · [Docs/Installation.Godot.md](Docs/Installation.Godot.md) | エンジンごとのインストール（クライアント + 専用サーバー） |
| [Docs/QuickStart.Unity.md](Docs/QuickStart.Unity.md) · [Docs/QuickStart.Godot.md](Docs/QuickStart.Godot.md) | エンジンごとの 5 ステップのクイックスタート（コンポーネント → システム → コールバック → セッション → ビュー） |
| [Docs/FEATURES.md](Docs/FEATURES.md) | 機能の全一覧 |
| [Docs/Specification.md](Docs/Specification.md) | エンジン仕様（ステートマシン · 設定 · イベント · メッセージプロトコル · 形式） |
| [Docs/LobbyIntegrationGuide.md](Docs/LobbyIntegrationGuide.md) | ロビー ↔ 専用サーバー ↔ クライアントの連携（モックアップ） — チケットの受け渡し · 検証フック · ID の伝播 · マッチ結果の報告 |
| [Docs/EntitlementLifecycle.md](Docs/EntitlementLifecycle.md) | 信頼できるプレイヤーデータ（エンタイトルメント）のライフサイクルの参照 — 発生源 · 保存 · 保持 · 伝播 · 読み取り · 破棄 · 不変条件 |
| [Docs/SynchronizationDesign.md](Docs/SynchronizationDesign.md) | 同期の設計方針（決定論 · 2 チェーンモデル · 予測/ロールバック · タイミング · 権限モデル · 回復の階段） |
| [Docs/DesyncDiagnostics.md](Docs/DesyncDiagnostics.md) | デシンクの根本原因の特定とログ分析のガイド（診断の漏斗: 分類 → ティック → 層 · ログの読み方 · オンライン Probe の判定 · `DiagnosticHistoryTicks` · 安全性の保証） |
| [Docs/GameDevWorkflow.md](Docs/GameDevWorkflow.md) | ゲーム開発者のワークフロー（ステップごと） |
| [Docs/Cookbook.md](Docs/Cookbook.md) | 「X をしたい」 — 26 の文書にまたがる作業 → エントリーポイントの索引と、4 つのレシピ（弾 · ノックバック · 一回限りのエフェクト · 決定論的な最寄りの敵） |
| [Docs/GameDevAPI.md](Docs/GameDevAPI.md) | ゲーム開発者向け API の現状 |
| [Docs/SimulationConfigGuide.md](Docs/SimulationConfigGuide.md) | SimulationConfig の推奨値ガイド（ジャンル / プラットフォーム別） |
| [Docs/ViewInterpolation.md](Docs/ViewInterpolation.md) | 初心者向けのビュー補間ガイド（なぜ必要か · CSP とスナップショットの経路 · 描画クロック · テレポート · 誤差補正 · トラブルシューティング） |
| [Docs/BaseLibraries.md](Docs/BaseLibraries.md) | 使用している基盤ライブラリの一覧 |
| [Docs/ECS.md](Docs/ECS.md) | ECS ガイド（エンティティ · コンポーネント · システム · フィルター · Frame のスナップショット/ハッシュ · ロールバック） |
| [Docs/ECSMemoryOptimization.md](Docs/ECSMemoryOptimization.md) | ECS のメモリ最適化（フレームヒープの構成 · 実行時の `[Mem]` 分析 · コンポーネントごとの `maxCount` · 未使用の型の除外） |
| [Docs/Serialization.md](Docs/Serialization.md) | シリアライズとソースジェネレーター（`SpanWriter/Reader` · `[KlothoComponent]`/`[KlothoSerializable]`/`[KlothoDataAsset]` のコード生成 · 対応する型 · 診断） |
| [Docs/DeterministicMath.md](Docs/DeterministicMath.md) | 決定論的数学（`FP64` 32.32 固定小数点 · `FPVector*`/`FPQuaternion`/`FPMatrix` · 三角関数 · 幾何 · `DeterministicRandom`） |
| [Docs/Replay.md](Docs/Replay.md) | リプレイ（入力の記録 · 保存/読み込み · 再生/一時停止/シーク/速度 · 決定論の保証） |
| [Docs/Navigation.md](Docs/Navigation.md) | 決定論的ナビゲーション（FPNavMesh · A* · Funnel · ORCA） |
| [Docs/Navigation.Rebake.md](Docs/Navigation.Rebake.md) | 実行時の NavMesh 再ベイク — マッチ中の建物の配置、決定論的な再三角形分割 |
| [Docs/NavMeshVisualizer.Godot.md](Docs/NavMeshVisualizer.Godot.md) | `Godot (.NET)` のエディターツール — シリアライズされた `FPNavMesh`（`.bytes`）を可視化し、3D ビューポートで経路探索 / エージェントのシミュレーションを検証 |
| [Docs/PhysicsWorld.md](Docs/PhysicsWorld.md) | 決定論的物理（剛体 · コライダー · 接触 · トリガー · CCD） |
| [Docs/PhysicsVisualizer.Godot.md](Docs/PhysicsVisualizer.Godot.md) | `Godot (.NET)` の実行時/エディターツール — 動作中の FPPhysics ワールド（ボディ · コライダー · 接触）の描画、HUD インスペクター、静的コライダーのビューアー |
| [Docs/DataAsset.md](Docs/DataAsset.md) | DataAsset の作成ガイド（定義 · JSON の作成 · `.bytes` のビルド · 登録 · 参照） |
| [Docs/HFSM.md](Docs/HFSM.md) | エージェント/ボット AI のための階層型 FSM（`HFSMBuilder` · `HFSMRoot` · `HFSMManager` · 判断/行動） |
| [Docs/Samples/Brawler.md](Docs/Samples/Brawler.md) · [Docs/Samples/P2pSample.md](Docs/Samples/P2pSample.md) · [Docs/Samples/SdSample.md](Docs/Samples/SdSample.md) | `Unity` サンプルの解説 — Brawler、P2pSample、SdSample |
| [Docs/Samples/GodotSdSample.md](Docs/Samples/GodotSdSample.md) · [Docs/Samples/GodotP2pSample.md](Docs/Samples/GodotP2pSample.md) | `Godot (.NET)` サンプルの解説（構成 + Godot 特有の注意点） |
| [Docs/Samples/DevIdentityKeys.md](Docs/Samples/DevIdentityKeys.md) | 開発用 ID キー — SD/P2P サンプルのロビー ID に使う Ed25519 キーペアの生成と更新 |

---

## 設計原則

- **決定論が第一** — float は使わない。シミュレーションの状態はすべて FP64 / 整数 / bool
- **GC ゼロ志向** — ref struct、オブジェクトプール、キャッシュしたフィールド、LINQ なし
- **エンジン非依存** — コアは純粋な C#（`UnityEngine` / `Godot` への参照なし）。エンジン統合（Unity / Godot）は共有コアの上のアダプター層にある
- **最小の帯域** — 入力（コマンド）だけを送る。状態の同期はなし（ハッシュ検証のみ）
- **層の分離** — シミュレーションのコールバック（決定論的）とビューのコールバック（非決定論的）を厳密に分ける
