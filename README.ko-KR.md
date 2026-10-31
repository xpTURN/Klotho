# xpTURN.Klotho
[![Unity 2022.3+](https://img.shields.io/badge/unity-2022.3%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![Godot 4.4+ (.NET)](https://img.shields.io/badge/godot-4.4%2B%20(.NET)-478cbf.svg)](https://godotengine.org/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

[English](README.md) | **한국어** | [日本語](README.ja-JP.md) | [简体中文](README.zh-CN.md)

> 이 문서는 영어 [README](README.md) 의 번역입니다. 내용이 다르면 영어판을 기준으로 합니다.

**Unity 와 Godot 을 위한 결정론적 멀티플레이 시뮬레이션 프레임워크**

클라이언트 측 예측(CSP), 롤백, 프레임 동기화, Server-Driven 모드, 리플레이를 지원하는 결정론적 시뮬레이션 프레임워크입니다. 시뮬레이션 코어는 엔진에 묶이지 않은 순수 C# 이고, 그 위에 **Unity** 와 **Godot (.NET)** 어댑터가 얹힙니다. 부동소수점을 배제하고 32.32 고정소수점(`FP64`)과 결정론적 난수 생성기(Xorshift128+)만으로 시뮬레이션을 구성하므로, 플랫폼과 컴파일러가 달라도 완전한 재현성을 보장합니다.

<img src="klotho.png" alt="Klotho" width="800">

> Klotho 는 시뮬레이션을 한 프레임씩 자아냅니다.

**동기화가 동작하는 방식을 한 문단으로:** 결정론이 핵심입니다. 같은 순서의 입력이 주어지면 모든 피어가 바이트 단위로 같은 상태를 계산하므로, 네트워크는 *입력만* 나르고 검증은 해시 비교로 줄어듭니다. 그 위에서 각 피어는 같은 틱 축 위에 타임라인 둘을 둡니다. **확정(Verified) 체인**(모든 플레이어의 실제 입력이 알려진 틱 — 바뀌지 않음)과 **예측(Predicted) 체인**(원격 입력을 *추측해* 앞서 실행한 틱 — 잠정적)입니다. CPU 의 분기 예측처럼, 시뮬레이션은 기다리지 않고 예측한 입력으로 곧바로 나아갑니다. 실제 입력이 추측과 어긋나면 엔진은 스냅숏을 되돌리고 다시 시뮬레이션합니다(**롤백**). 상태가 입력의 순수 함수이므로 이 비용은 작습니다. 입력 지연과 적응형 타이밍 버퍼가 롤백이 일어나는 횟수를 줄이고, 결정론이 실제로 깨지면 단계별 복구 사다리(해시 확인 → 롤백 → 전체 상태 재동기화 → 교정 리셋)가 일치를 되찾습니다. 같은 장치가 **P2P 락스텝**(피어가 같은 권한을 가짐)과 **Server-Driven**(서버가 확정 체인을 소유함) 모두에 쓰입니다. 자세한 근거: [Docs/SynchronizationDesign.md](Docs/SynchronizationDesign.md).

---

## 주요 기능

| 영역 | 내용 |
| ---- | ---- |
| **네트워크 모델** | P2P 락스텝 · 롤백 + 클라이언트 예측 · Server-Driven(권한 서버) · 전용 서버 · 관전 · 늦은 참가(Late Join) · 재접속 |
| **방 · 매치 설정** | 다중 방 전용 서버 · `IMatchConfigSource` 를 통한 방별 스테이지(`StageId`) + 매치별 불투명 설정(`MatchConfigData`) — 서버 하나가 서로 다른 스테이지의 방을 호스팅 · 로비 방 예약(접속 전 예약) · 확정된 매치 결과를 로비에 보고(`IMatchResultProvider`, 최소 한 번 전달 + 멱등) |
| **결정론적 수학** | `FP64`(32.32 고정소수점) · `FPVector2/3/4` · `FPQuaternion` · `FPMatrix` · LUT/CORDIC 기반 삼각함수 · `DeterministicRandom`(Xorshift128+) |
| **물리** | `FPPhysicsWorld` · 광역 단계(SpatialGrid) · 세부 단계 · CCD(Sweep) · 제약 솔버 · 조인트 · 트리거 · 정적 BVH |
| **내비게이션** | `FPNavMesh` · A*(삼각형 그래프) · Funnel(SSFA) · ORCA 회피 · ECS 통합 `NavAgentComponent` |
| **ECS** | Sparse-set `ComponentStorage<T>` · `Frame`(단일 byte[] 힙) · `FilterWithout/Filter<T1..T5>` · `FrameRingBuffer` · `SystemRunner` |
| **AI / HFSM** | 결정론적 계층형 FSM · `HFSMBuilder`(fluent) · `HFSMRoot` / `HFSMManager` · `HFSMComponent`(엔티티별) · `HFSMDecision` / `AIAction` · Unity 상태 트리 시각화 도구 |
| **직렬화 / 소스 생성기** | `SpanWriter/Reader`(ref struct, GC 없음) · `[KlothoComponent]` / `[KlothoSerializable]` / `[KlothoDataAsset]` / `[KlothoSerializableStruct]` 를 통한 자동 코드 생성 · 빌드 시점 `DeterminismAnalyzer`(시뮬레이션 코드의 float / 비결정론적 API 를 짚어 냄) |
| **데이터 에셋** | `IDataAsset` · `DataAssetRegistry` · `DataAssetRef` · JSON 직렬화(`xpTURN.Klotho.DataAsset.Json`) |
| **리플레이** | 녹화 / 재생 / 탐색 / 가변 속도 |
| **검증 도구** | `SyncTestRunner`(GGPO 방식 결정론 검증) · `DeterminismVerificationRunner` · 벤치마크 모음 |
| **Unity 통합** | `USimulationConfig` · `USessionConfig` · 뷰 계층(`EntityViewFactory` / `EntityViewUpdater` / `EntityView`, `BindBehaviour` / `ViewFlags`, `VerifiedFrameInterpolator`) · `KlothoSessionDriver`(MonoBehaviour) · `KlothoConnectionAsync`(UniTask) |
| **Godot 통합** | `GodotSimulationConfig` · `GodotSessionConfig`(Resource) · 뷰 계층(`EntityViewFactory` / `EntityViewUpdaterNode` / `EntityViewNode`, `VerifiedFrameInterpolator`) · `GodotSessionDriver`(Node) · `GodotConnectionAsync`(`Task`) · `GodotDebugSink` / `GodotLogSink` |

---

## 적합한 장르

Klotho 의 결정론적 시뮬레이션 기능에서 가장 큰 이득을 얻는 장르입니다.

### 가장 잘 맞음(핵심 대상)

- **격투 게임** — 프레임 단위로 정확한 입력 + 롤백 넷코드
- **플랫폼 격투 / 아레나 난투**
- **2–4인 PvP 액션** — 소수 인원 P2P 롤백에 최적
- **택틱스 / 턴제 SRPG** — 결정론 + 리플레이 + 동기 검증
- **실시간 전략(RTS)** — 락스텝 프레임 동기화
- **MOBA / 탑다운 아레나** — ECS, 물리, 내비게이션을 모두 포함
- **트윈스틱 슈터 / 협동 슈터** — 결정론적 물리 + ORCA 회피
- **오토 배틀러** — 결정론적 시뮬레이션 + 공유할 수 있는 리플레이

### 잘 맞음(구조적으로 적합)

- **로그라이크 / 로그라이트(PvE 협동)** — 공유 시드와 리플레이를 위한 결정론적 난수(Xorshift128+)
- **카드 / 보드 / 덱빌더 PvP** — 입력량이 적고, 검증과 리플레이가 강함
- **퍼즐 PvP / 낙하 블록 대전**
- **레이싱(소규모)** — 고정소수점 물리가 작은 그리드에서 결정론을 보장
- **탑다운 서바이벌 액션**

---

## 아키텍처 개요

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

세 계층으로 나뉩니다:
- **Klotho 엔진 계층** — 순수 C#, 엔진 독립(`UnityEngine` / Godot 참조 없음). 같은 바이너리가 Unity 와 Godot (.NET) 어댑터 아래에서, 그리고 서버 쪽(.NET 콘솔 / ASP.NET)에서 돕니다.
- **시뮬레이션 전송 계층** — `INetworkTransport` 추상화 위의 UDP 전송(Input / InputAck / SyncCheck / Handshake). 기본 참조 구현으로 LiteNetLib 를 제공하며, 다른 라이브러리로 바꿀 수 있습니다.
- **게임 서비스 계층** — 로비 / 매치메이킹 / 인증(외부 gRPC 등 — 이 프로젝트 밖에서 통합).

---

## 기술 스택

- **Unity 2022.3+** — 스크립팅 백엔드: Mono / IL2CPP(AOT 안전 — 직렬화는 소스 생성으로 하며, 런타임 코드 생성 · 리플렉션이 없음)
- **Godot 4.4+ (mono / .NET, net8.0)** — Godot 어댑터는 `addons/klotho/`(미리 빌드한 코어 DLL + 어댑터 소스)로 배포됩니다.
- **C# 언어 수준** — Unity 는 **C# 8.0** 을 대상으로 하며, C# 11 에 필요한 런타임 특성의 대체 구현은 `xpTURN.Polyfill` 패키지가 공급합니다. Godot 어댑터는 **net8.0 / `LangVersion latest`** 라 폴리필이 필요 없습니다(네이티브 `init` / `required`).
- **UniTask** — Unity 에서만 쓰는 비동기(Cysharp). **Godot 어댑터는 표준 라이브러리 `Task`** 를 씁니다 — UniTask 의존 없음.
- **xpTURN.Klotho.Logging (IKLogger)** — 자체 구조화 로깅(외부 로깅 의존 없음). `Plugins~/Logging.Mel` 샘플 어댑터로 MEL 과 선택적으로 연동할 수 있습니다(`Microsoft.Extensions.Logging.Abstractions` DLL 은 사용하는 쪽이 제공).
- **LiteNetLib** — UDP 전송의 기본 참조 구현(MIT, 순수 C#). Unity 에서는 `Runtime/ThirdParty/LiteNetLib.v2.1.4` 아래에 **소스로 포함**되고, Godot 에서는 **NuGet 패키지 `LiteNetLib 2.1.4`** 로 들어옵니다. 전송 계층은 `INetworkTransport` 로 추상화되어 있어 바꿀 수 있습니다.
- **Newtonsoft.Json** — DataAsset JSON 직렬화(Unity: `com.unity.nuget.newtonsoft-json` · Godot: NuGet `Newtonsoft.Json 13.0.3`)

자세히: [Docs/BaseLibraries.md](Docs/BaseLibraries.md)

---

## 설치

Klotho 는 엔진마다 설치 방법이 다릅니다 — 맞는 안내를 따르세요. 각 안내는 **클라이언트**를 먼저, 이어서 선택 사항인 **전용 서버**(공유하는 엔진 독립 코어 위의 일반 .NET 호스트)를 다룹니다:

- **[설치 — Unity](Docs/Installation.Unity.md)** — UPM git URL + Polyfill 활성화, 이어서 전용 서버를 위한 `Server~` 프로젝트 참조.
- **[설치 — Godot (.NET)](Docs/Installation.Godot.md)** — `addons/klotho/` 폴더 + `Klotho.props` 가져오기 한 줄, 이어서 (엔진 독립) 전용 서버.

저장소의 [`Samples/`](Samples/) 아래 프로젝트들은 설치 참고용으로도 쓰입니다 — 샘플별 안내는 [문서 지도](#문서-지도)를 보세요.

---

## 저장소 구성

Klotho 는 저장소 최상위의 `com.xpturn.klotho/` 아래에 있습니다. Unity 사용자는 UPM 으로 패키지를 설치하고, Godot 사용자는 `addons/klotho/` 폴더로 포장한 `Godot~/` 어댑터를 씁니다([설치](#설치) 참고). 저장소 안의 Unity 샘플은 같은 최상위 패키지를 `file:` manifest 참조로 씁니다.

```
<저장소 루트>/
├── README.md  ·  CHANGELOG.md  ·  LICENSE
├── com.xpturn.klotho/             ← ★ 프레임워크 패키지
│   ├── package.json
│   ├── Runtime/                   엔진 독립 코어
│   │   ├── Core/                  엔진 · 세션 · 네트워크
│   │   ├── Logging/               IKLogger(자체)
│   │   ├── Gameplay/              내장 컴포넌트 / 시스템
│   │   ├── Diagnostics/           결함 주입 · 지표
│   │   ├── Input/                 입력 버퍼 / 예측기
│   │   ├── Network/               전송 · 서버 · 관전
│   │   ├── State/                 스냅숏 관리자
│   │   ├── Serialization/         SpanWriter/Reader
│   │   ├── Replay/                녹화 / 재생
│   │   ├── ECS/                   Frame · 컴포넌트 · 시스템
│   │   ├── Deterministic/         FP64 · 물리 · 내비게이션
│   │   ├── LiteNetLib/            UDP 전송
│   │   └── ThirdParty/            포함한 외부 의존물
│   ├── Unity/                     Unity 어댑터
│   │   └── Editor/                Unity 전용 에디터 도구
│   ├── Godot~/                    Godot (.NET) 어댑터
│   │   └── Adapters/Editor/       Godot (.NET) 에디터 도구
│   ├── Plugins/Analyzers/         소스 생성기
│   ├── Plugins~/Logging.Mel/      MEL 연동 샘플
│   └── Server~/                   전용 서버 프로젝트
│
├── dist/addons/klotho/            ← Godot 애드온
│
├── Samples/                       ← 독립 샘플
│   ├── Brawler/                   4인 격투(Unity)
│   ├── P2pSample/                 최소 P2P(Unity)
│   ├── SdSample/                  최소 Server-Driven(Unity)
│   ├── GodotP2pSample/            최소 P2P(Godot)
│   ├── GodotSdSample/             최소 Server-Driven(Godot)
│   └── LoggingMelConsole/         .NET 로깅 샘플
│
├── Docs/                          ← 문서
└── Tools/                         ← .NET 도구(내부용)
    ├── KlothoGenerator/           소스 생성기
    ├── KlothoGenerator.Tests/     생성기 시험
    ├── DeterminismVerification/   결정론 검증기
    ├── PhysicsDeterminismProbe/   FP 결정론 탐침
    └── gen.sh                     생성기 빌드 스크립트
```

---

## 빠른 시작

네 단계 — 컴포넌트 정의 → 시스템 구현 → 콜백 연결 → 세션 생성과 구동 — 는 두 엔진에서 같은 꼴이지만, 세션 구동과 뷰 계층은 엔진마다 다릅니다. 맞는 안내를 고르세요(각각 전체 코드를 담은 독립 문서입니다):

- **[빠른 시작 — Unity](Docs/QuickStart.Unity.md)** — `MonoBehaviour` 컨트롤러, `KlothoSessionDriver`, `ScriptableObject` 설정, UniTask 참가, `EntityViewFactory` / `EntityViewUpdater` / `EntityView`.
- **[빠른 시작 — Godot (.NET)](Docs/QuickStart.Godot.md)** — `Node` 컨트롤러, `GodotSessionDriver`(`_Process`), `Resource` 설정, 표준 `Task` 참가, `EntityViewNode` / `EntityViewUpdaterNode`(`.tscn`).

1–3 단계(컴포넌트 / 시스템 / 콜백)는 엔진 독립 코어입니다. `KlothoSessionFlow` 는 두 엔진에서 같은 진입점(`StartHostAndListen` / `JoinP2PAsync` / `JoinServerDrivenAsync` / `ReconnectAsync` / `SpectateAsync` / `StartReplayFromFile`)을 내놓습니다 — 비동기인 것을 Unity 는 UniTask 로, Godot 는 표준 `Task` 로 감쌉니다. 세션 생성은 하나뿐인 `IKlothoSessionObserver.OnSessionCreated(session, SessionEntryKind kind)` 로 관찰합니다 — `simCfg.Mode` 가 아니라 `kind` 로 분기하세요.

자세한 안내: [Docs/GameDevWorkflow.md](Docs/GameDevWorkflow.md), [Docs/GameDevAPI.md](Docs/GameDevAPI.md)

---

## 샘플

[Samples/Brawler](Samples/Brawler) — 4인 격투 게임 샘플

- ECS 기반 전투 / 이동 / 스킬 / 쿨다운 / 넉백 / 아이템 / 함정
- HFSM 기반 봇 AI(`BotHFSMRoot` / `BotActions` / `BotDecisions`)
- Unity Cinemachine 카메라 연동
- P2P 와 ServerDriven 모드 모두 지원
- 리플레이 녹화 / 재생

문서: [Docs/Samples/Brawler.md](Docs/Samples/Brawler.md)

---

## 문서 지도

아래 문서는 영어로 쓰여 있습니다.

| 문서 | 내용 |
| ---- | ---- |
| [Docs/Installation.Unity.md](Docs/Installation.Unity.md) · [Docs/Installation.Godot.md](Docs/Installation.Godot.md) | 엔진별 설치(클라이언트 + 전용 서버) |
| [Docs/QuickStart.Unity.md](Docs/QuickStart.Unity.md) · [Docs/QuickStart.Godot.md](Docs/QuickStart.Godot.md) | 엔진별 다섯 단계 빠른 시작(컴포넌트 → 시스템 → 콜백 → 세션 → 뷰) |
| [Docs/FEATURES.md](Docs/FEATURES.md) | 전체 기능 목록 |
| [Docs/Specification.md](Docs/Specification.md) | 엔진 명세(상태 머신 · 설정 · 이벤트 · 메시지 프로토콜 · 형식) |
| [Docs/LobbyIntegrationGuide.md](Docs/LobbyIntegrationGuide.md) | 로비 ↔ 전용 서버 ↔ 클라이언트 연동(목업) — 티켓 전달 · 검증 훅 · 신원 전파 · 매치 결과 보고 |
| [Docs/EntitlementLifecycle.md](Docs/EntitlementLifecycle.md) | 신뢰할 수 있는 플레이어 데이터(엔타이틀먼트)의 수명 주기 참고 — 출처 · 저장 · 보존 · 전파 · 읽기 · 폐기 · 불변식 |
| [Docs/SynchronizationDesign.md](Docs/SynchronizationDesign.md) | 동기화 설계 방향(결정론 · 두 체인 모델 · 예측/롤백 · 타이밍 · 권한 모델 · 복구 사다리) |
| [Docs/DesyncDiagnostics.md](Docs/DesyncDiagnostics.md) | 디싱크 근본 원인 국소화와 로그 분석 안내(진단 깔때기: 분류 → 틱 → 계층 · 로그 읽기 · 온라인 Probe 판정 · `DiagnosticHistoryTicks` · 안전 보장) |
| [Docs/GameDevWorkflow.md](Docs/GameDevWorkflow.md) | 게임 개발자 작업 흐름(단계별) |
| [Docs/Cookbook.md](Docs/Cookbook.md) | 『X 를 하고 싶다』 — 26개 문서에 걸친 작업 → 진입점 색인, 그리고 레시피 넷(발사체 · 넉백 · 일회성 효과 · 결정론적 최근접 적) |
| [Docs/GameDevAPI.md](Docs/GameDevAPI.md) | 게임 개발자 API 현황 |
| [Docs/SimulationConfigGuide.md](Docs/SimulationConfigGuide.md) | SimulationConfig 권장값 안내(장르 / 플랫폼별) |
| [Docs/ViewInterpolation.md](Docs/ViewInterpolation.md) | 입문자를 위한 뷰 보간 안내(왜 필요한가 · CSP 와 스냅숏 경로 · 렌더 시계 · 순간이동 · 오차 보정 · 문제 해결) |
| [Docs/BaseLibraries.md](Docs/BaseLibraries.md) | 사용하는 기반 라이브러리 목록 |
| [Docs/ECS.md](Docs/ECS.md) | ECS 안내(엔티티 · 컴포넌트 · 시스템 · 필터 · Frame 스냅숏/해시 · 롤백) |
| [Docs/ECSMemoryOptimization.md](Docs/ECSMemoryOptimization.md) | ECS 메모리 최적화(프레임 힙 구성 · 런타임 `[Mem]` 분석 · 컴포넌트별 `maxCount` · 쓰지 않는 형식 가지치기) |
| [Docs/Serialization.md](Docs/Serialization.md) | 직렬화와 소스 생성기(`SpanWriter/Reader` · `[KlothoComponent]`/`[KlothoSerializable]`/`[KlothoDataAsset]` 코드 생성 · 지원 형식 · 진단) |
| [Docs/DeterministicMath.md](Docs/DeterministicMath.md) | 결정론적 수학(`FP64` 32.32 고정소수점 · `FPVector*`/`FPQuaternion`/`FPMatrix` · 삼각함수 · 기하 · `DeterministicRandom`) |
| [Docs/Replay.md](Docs/Replay.md) | 리플레이(입력 녹화 · 저장/불러오기 · 재생/일시정지/탐색/속도 · 결정론 보장) |
| [Docs/Navigation.md](Docs/Navigation.md) | 결정론적 내비게이션(FPNavMesh · A* · Funnel · ORCA) |
| [Docs/Navigation.Rebake.md](Docs/Navigation.Rebake.md) | 런타임 NavMesh 재베이크 — 매치 도중 건물 배치, 결정론적 재삼각분할 |
| [Docs/NavMeshVisualizer.Godot.md](Docs/NavMeshVisualizer.Godot.md) | `Godot (.NET)` 에디터 도구 — 직렬화된 `FPNavMesh`(`.bytes`)를 시각화하고 3D 뷰포트에서 길찾기 / 에이전트 시뮬레이션을 검증 |
| [Docs/PhysicsWorld.md](Docs/PhysicsWorld.md) | 결정론적 물리(강체 · 콜라이더 · 접촉 · 트리거 · CCD) |
| [Docs/PhysicsVisualizer.Godot.md](Docs/PhysicsVisualizer.Godot.md) | `Godot (.NET)` 런타임/에디터 도구 — 실행 중인 FPPhysics 세계(바디 · 콜라이더 · 접촉) 그리기, HUD 인스펙터, 정적 콜라이더 뷰어 |
| [Docs/DataAsset.md](Docs/DataAsset.md) | DataAsset 작성 안내(정의 · JSON 작성 · `.bytes` 빌드 · 등록 · 조회) |
| [Docs/HFSM.md](Docs/HFSM.md) | 에이전트/봇 AI 를 위한 계층형 FSM(`HFSMBuilder` · `HFSMRoot` · `HFSMManager` · 결정/행동) |
| [Docs/Samples/Brawler.md](Docs/Samples/Brawler.md) · [Docs/Samples/P2pSample.md](Docs/Samples/P2pSample.md) · [Docs/Samples/SdSample.md](Docs/Samples/SdSample.md) | `Unity` 샘플 안내 — Brawler, P2pSample, SdSample |
| [Docs/Samples/GodotSdSample.md](Docs/Samples/GodotSdSample.md) · [Docs/Samples/GodotP2pSample.md](Docs/Samples/GodotP2pSample.md) | `Godot (.NET)` 샘플 안내(구조 + Godot 특유의 함정) |
| [Docs/Samples/DevIdentityKeys.md](Docs/Samples/DevIdentityKeys.md) | 개발용 신원 키 — SD/P2P 샘플의 로비 신원에 쓰는 Ed25519 키 쌍의 생성과 교체 |

---

## 설계 원칙

- **결정론 우선** — float 없음. 모든 시뮬레이션 상태는 FP64 / 정수 / bool
- **GC 0 지향** — ref struct, 객체 풀, 캐시한 필드, LINQ 없음
- **엔진 독립** — 코어는 순수 C#(`UnityEngine` / `Godot` 참조 없음). 엔진 통합(Unity / Godot)은 공유 코어 위의 어댑터 계층에 있음
- **최소 대역폭** — 입력(커맨드)만 보냄. 상태 동기화 없음(해시 검증만)
- **계층 분리** — 시뮬레이션 콜백(결정론적)과 뷰 콜백(비결정론적)을 엄격히 나눔
