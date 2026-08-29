// GodotP2pSample bootstrap — standalone, one instance = one peer (counterpart of the Unity
// P2pGameController). The menu drives Host/Join/Ready/Stop on a single session. A GodotSessionDriver
// pumps the transport + session each frame; the EntityViewUpdaterNode self-drives view interpolation;
// joins go through GodotSessionFlowAsync; views are pooled. Run two instances (Host + Join) to play.
using System;
using System.Threading.Tasks;
using global::Godot;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Godot;
using xpTURN.Klotho.LiteNetLib;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;

namespace xpTURN.Samples.P2pSample
{
	public partial class P2pGameNode : Node
	{
		private const string ConnectionKey = "xpTURN.P2pSample";

		private IKLogger            _logger;
		private IDataAssetRegistry  _registry;
		private LiteNetLibTransport _transport;
		private P2pInputCapture     _input;
		private KlothoSessionFlow   _flow;
		private KlothoSession       _session;
		private GodotP2pViewCallbacks _viewCallbacks;
		private EntityViewUpdaterNode _view;
		private P2pEntityViewFactory  _factory;
		private DefaultGodotEntityViewPool _pool;
		private GodotSessionDriver    _driver;
		private GodotP2pMenu          _menu;
		private GodotP2pHud           _hud;

		private ISimulationConfig     _simCfg;
		private ISessionConfig        _sesCfg;
		private Task<KlothoSession>   _joinTask;
		private bool _joining;

		// Host-chosen stage (CLI: -- stage=N). The host authors SimulationConfig, so it stamps StageId here;
		// the guest receives it via SimulationConfig (multi-stage demo: host stage=1 → 6×6 ground).
		private int  _stageId;

		// Headless self-test hook (CLI: -- host | -- join). 0=interactive (menu-driven).
		private int  _autoMode;       // 1=host, 2=join
		private bool _autoReadySent;
		private bool _verified;
		private const int VerifyTick = 60;

		public override void _Ready()
		{
			WarmupRegistry.RunAll();

			_logger = CreateLogger();
			_registry = LoadAssetRegistry();
			// Error correction on: this sample is where the rollback smoothing is meant to be seen.
			// Everything else stays at the adapter defaults on purpose. InputDelayTicks used to be 0 here,
			// which is a measurement stimulus rather than a setting: with no delay every remote input is
			// late, so the client rolls back on practically every tick and the correction deltas are easy
			// to watch. It is a bad thing to inherit by copying this sample (the default is 4, and the
			// interface documents 2~6) — set it to 0 temporarily if you want that view (IMP105 C-14).
			_simCfg   = new SimulationConfig { EnableErrorCorrection = true };
			_sesCfg   = new SessionConfig { MaxPlayers = 2, MinPlayers = 2, CountdownDurationMs = 3000 };
			_input    = new P2pInputCapture();
			_transport = new LiteNetLibTransport(_logger, connectionKey: ConnectionKey);

			_menu = GetNode<GodotP2pMenu>("UILayer/Menu");
			_hud  = GetNode<GodotP2pHud>("UILayer/Hud");
			_viewCallbacks = new GodotP2pViewCallbacks(_hud);

			// s = SimulationConfig for this session: the host's own config (host authors it, see OnHost) or
			// the config received from the host (guest). s.StageId selects the stage both sides build.
			var flowBuilder = new KlothoFlowSetupBuilder((s, ss) =>
						new SessionCallbacks(new P2pSimulationCallbacks(_input, s.StageId), _viewCallbacks))
					.WithLogger(_logger)
					.WithTransport(_transport)
					.WithAssetRegistry(_registry)
					.WithGodotDefaults();
#if DEBUG
			// Dev lobby identity (signed Ed25519 ticket). Debug/dev only (P2pDevIdentity is build-gated).
			// Built once for host+guest; the validator is unused on a guest (host-only verification).
			flowBuilder = flowBuilder
				.WithLobbyIdentity(P2pDevIdentity.CreateProvider())
				.WithIdentityValidator(P2pDevIdentity.CreateValidator());
#endif
			_flow = new KlothoSessionFlow(flowBuilder.Build());

			var playerScene = GD.Load<PackedScene>("res://player.tscn");
			_factory = new P2pEntityViewFactory(playerScene);
			_pool = new DefaultGodotEntityViewPool();
			_pool.Prewarm(playerScene, _sesCfg.MaxPlayers);
			_view = new EntityViewUpdaterNode();
			AddChild(_view);

			_driver = new GodotSessionDriver();
			AddChild(_driver);
			_driver.BindTransport(_transport);
			_driver.PreSessionUpdate += (s, dt) => { if (s.State == KlothoState.Running) _input.CaptureInput(); };

			_menu.OnHostClicked  += OnHost;
			_menu.OnJoinClicked  += OnJoin;
			_menu.OnReadyClicked += OnReady;
			_menu.OnStopClicked  += OnStop;
			_menu.SetInitialHost("127.0.0.1", 7777);
			_menu.SetReadyEnabled(false);
			_menu.SetStopEnabled(false);

			SetupView3D();

			foreach (var a in OS.GetCmdlineUserArgs())
			{
				if (a.StartsWith("stage=", StringComparison.Ordinal) && int.TryParse(a.Substring("stage=".Length), out int st))
					_stageId = st;
			}
			foreach (var a in OS.GetCmdlineUserArgs())
			{
				if (a == "host") { _autoMode = 1; OnHost(); }
				else if (a == "join") { _autoMode = 2; OnJoin(); }
			}
		}

		private void OnHost()
		{
			if (_session != null) return;
			// Host authors the SimulationConfig — stamp the chosen stage so it propagates to guests.
			if (_simCfg is SimulationConfig sc) sc.StageId = _stageId;
			_session = _flow.StartHostAndListen(_simCfg, _sesCfg, "Game", _menu.Host, _menu.Port);
			if (_session == null) return; // listen failure — framework already tore down
			OnSessionReady();
		}

		private void OnJoin()
		{
			if (_session != null || _joining) return;
			_joining = true;
			_joinTask = _flow.JoinP2PAsync(_transport, _menu.Host, _menu.Port, _sesCfg);
		}

		private void OnReady()
		{
			if (_session == null) return;
			_hud.SetLocalReady(true);
			_session.SetReady(true);
		}

		private void OnStop()
		{
			if (_session == null) return;
			_driver.DetachAndStop();
			_view.Cleanup();
			_viewCallbacks.Cleanup();
			_session = null;
			_menu.SetReadyEnabled(false);
			_menu.SetStopEnabled(false);
			_hud.SetLocalReady(false);
		}

		private void OnSessionReady()
		{
			_view.Initialize(_session.Engine, _factory, _pool);
			_driver.Attach(_session);
			_hud.SetPhase(_session.Phase);
			_menu.SetReadyEnabled(true);
			_menu.SetStopEnabled(true);
		}

		// The driver pumps transport + session; the view updater self-drives interpolation. This node only
		// resolves the async join, mirrors the phase to the HUD, and runs the headless self-test.
		public override void _Process(double delta)
		{
			if (_transport == null) return; // _Ready failed to initialize.

			if (_joining && _joinTask != null)
			{
				if (_joinTask.IsFaulted)
				{
					_logger.KError($"[P2p] join failed (host running?): {_joinTask.Exception?.GetBaseException().Message}");
					_joining = false; _joinTask = null;
				}
				else if (_joinTask.IsCompleted)
				{
					_session = _joinTask.Result;
					_joining = false; _joinTask = null;
					OnSessionReady();
				}
			}

			if (_session == null) return;

			_hud.SetPhase(_session.Phase);

			if (_autoMode != 0) AutoTestStep();
		}

		// Headless self-test: auto-Ready once synchronized, then verify view nodes and quit.
		private void AutoTestStep()
		{
			if (!_autoReadySent && _session.Phase == SessionPhase.Synchronized)
			{
				OnReady();
				_autoReadySent = true;
				_logger.KInformation($"[P2p] auto({_autoMode}) ready sent.");
			}

			if (!_verified && _session.State == KlothoState.Running && _session.Engine.CurrentTick >= VerifyTick)
			{
				_verified = true;
				int n = _view.GetChildCount();
				_logger.KInformation($"[P2p] auto({_autoMode}) tick={_session.Engine.CurrentTick} viewNodes={n}");
				if (n >= 2) _logger.KInformation($"=== P2P STANDALONE OK ===");
				else        _logger.KError($"=== P2P STANDALONE FAILED (viewNodes={n}) ===");
				if (DisplayServer.GetName() == "headless") GetTree().Quit(n >= 2 ? 0 : 1);
			}
		}

		private static IKLogger CreateLogger()
			// Default level, like the sibling SD sample. Debug was pinned here to capture the per-tick
			// [EC][DIAG] / [ViewLife] lines while the correction work was being measured; leaving it on
			// ships a sample that writes those to file on every tick (IMP105 C-14). Pass level: KLogLevel.Debug
			// when you need them.
			=> GodotKlothoLogger.CreateDefault(filePrefix: "P2p", categoryName: "P2p", timestampFormat: "HH:mm:ss.fff");

		// Configure the 3D view in code (LookAt avoids hand-written, easy-to-mistake basis matrices in .tscn).
		private void SetupView3D()
		{
			// Top-down: camera high above the origin looking straight down; screen-up = world -Z.
			var cam = GetNodeOrNull<Camera3D>("Camera3D");
			if (cam != null)
			{
				cam.LookAtFromPosition(new Vector3(0, 7, 0), Vector3.Zero, new Vector3(0, 0, -1));
				// Background + ambient so everything is visible even where the directional light doesn't reach.
				cam.Environment = new global::Godot.Environment
				{
					BackgroundMode      = global::Godot.Environment.BGMode.Color,
					BackgroundColor     = new Color(0.12f, 0.13f, 0.18f),
					AmbientLightSource  = global::Godot.Environment.AmbientSource.Color,
					AmbientLightColor   = new Color(0.5f, 0.5f, 0.5f),
					AmbientLightEnergy  = 1.0f,
				};
			}

			// Sun from above, slightly angled, so the cube tops (seen by the top-down camera) are lit.
			var light = GetNodeOrNull<DirectionalLight3D>("DirectionalLight3D");
			light?.LookAtFromPosition(new Vector3(4, 10, 4), Vector3.Zero, Vector3.Up);
		}

		private IDataAssetRegistry LoadAssetRegistry()
		{
			// Self-contained: load the copied P2pAssets.bytes from res://Data. Use Godot FileAccess so it
			// works both in the editor and in an exported .pck (System.IO cannot read res:// inside a .pck).
			byte[] bytes = global::Godot.FileAccess.GetFileAsBytes("res://Data/P2pAssets.bytes");
			if (bytes == null || bytes.Length == 0)
			{
				var err = global::Godot.FileAccess.GetOpenError();
				throw new System.IO.FileNotFoundException($"res://Data/P2pAssets.bytes not found (err={err})");
			}
			var assets = DataAssetReader.LoadMixedCollectionFromBytes(bytes);
			IDataAssetRegistryBuilder builder = new DataAssetRegistry();
			builder.RegisterRange(assets);
			return builder.Build();
		}

		public override void _ExitTree()
		{
			if (_session != null) { _driver?.DetachAndStop(); _session = null; }
			_view?.Cleanup();
			_viewCallbacks?.Cleanup();
			_pool?.Dispose();
			_input?.Dispose();
		}
	}
}
