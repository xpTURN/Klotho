// Runtime FPPhysics debug visualizer. A Node3D placed in the running scene; subscribes to the live
// IFPPhysicsWorldProvider and rebuilds two ImmediateMesh overlays (static colliders / dynamic
// bodies+contacts) on simulation ticks. Geometry lives as world-space MeshInstance3D children
// (TopLevel=true) that the active Camera3D renders automatically. Rebuild is gated on OnTickExecuted
// (physics changes per tick, not per render frame), coalesced to one rebuild per frame via a dirty flag.
//
// Default Enabled=false: the node is harmless until switched on (no #if compile gate; release-safe).
// Wiring: place this node in the scene BEFORE the session starts so OnSessionCreated is received;
// for dynamic spawn after session start, call Bind(session) (KlothoSession has no current-session accessor).
using System;

using global::Godot;

using xpTURN.Klotho.Core;                      // KlothoSession
using xpTURN.Klotho.Deterministic.Math;        // FPVector3.ToVector3()
using xpTURN.Klotho.Deterministic.Geometry;    // FPBounds3
using xpTURN.Klotho.Deterministic.Physics;     // FPPhysicsBody, FPStaticCollider, FPContact, IFPPhysics*

namespace xpTURN.Klotho.Godot
{
    public partial class GodotFPPhysicsWorldVisualizer : Node3D
    {
        // ---- Inspector ([Export] display toggles + colors) ----

        [ExportGroup("General")]
        [Export] public bool Enabled = false;        // master switch; default OFF
        [Export] public bool AlwaysOnTop = true;     // depth-test off (draw over geometry)

        [ExportGroup("PhysicsBody Display")]
        [Export] public bool ShowBodies = true;
        [Export] public bool ShowBodyShape = true;
        [Export] public bool ShowBodyAABB = false;
        [Export] public bool ShowBodyVelocity = true;
        [Export] public float VelocityArrowScale = 0.2f;

        [ExportGroup("StaticCollider Display")]
        [Export] public bool ShowStaticColliders = true;
        [Export] public bool ShowStaticShape = true;
        [Export] public bool ShowStaticAABB = false;

        [ExportGroup("Collision Visualization")]
        [Export] public bool ShowContacts = true;
        [Export] public bool ShowContactNormals = true;
        [Export] public bool ShowCollisionHighlight = true;
        [Export] public float ContactNormalScale = 0.5f;
        [Export] public float ContactPointRadius = 0.05f;

        [ExportGroup("Colors — Body")]
        [Export] public Color DynamicShapeColor = new Color(1f, 0.8f, 0f, 0.9f);
        [Export] public Color StaticBodyShapeColor = new Color(0.6f, 0.6f, 0.6f, 0.9f);
        [Export] public Color KinematicShapeColor = new Color(0f, 0.6f, 1f, 0.9f);
        [Export] public Color SceneStaticColor = new Color(0f, 1f, 0.5f, 0.9f);
        [Export] public Color AabbColor = new Color(1f, 1f, 1f, 0.25f);
        [Export] public Color VelocityColor = new Color(1f, 0.3f, 0.3f, 1f);

        [ExportGroup("Colors — Collision")]
        [Export] public Color ContactPointColor = new Color(1f, 0f, 0f, 1f);
        [Export] public Color ContactNormalColor = new Color(1f, 0.5f, 0f, 1f);
        [Export] public Color CollisionHighlightColor = new Color(1f, 0f, 0f, 0.5f);
        [Export] public Color TriggerHighlightColor = new Color(1f, 0f, 1f, 0.5f);

        [ExportGroup("Colors — Selection")]
        [Export] public Color SelectedShapeColor = new Color(1f, 0.9f, 0f, 1f);
        [Export] public Color SelectedAABBColor = new Color(1f, 0.9f, 0f, 0.6f);

        // ---- Internal state (read by the debug panel) ----

        public IFPPhysicsWorldProvider Provider { get; private set; }

        public int selectedIndex = 0;
        public bool viewingBodies = true;

        public int bodyCount;
        public int staticCount;
        public FPPhysicsBody[] currentBodies;
        public FPStaticCollider[] currentStatics;

        public FPContact[] currentContacts;
        public int currentContactCount;
        public FPContact[] currentSContacts;
        public int currentSContactCount;

        // copied-vs-total accounting for the three caches above (the debug panel draws copied/total
        // from this).
        public FPContactSnapshotStats currentStats;

        // Truncation warning latch — compared against the previous counter values, not the
        // copied/total pair: this runs once per frame while the snapshot is overwritten every tick.
        bool _warnedSnapshotTruncated;
        int _lastSeenContactTrunc, _lastSeenStaticTrunc, _lastSeenTriggerTrunc;

        // ---- Drawing nodes / drawers ----

        MeshInstance3D _staticMI;
        MeshInstance3D _dynamicMI;
        GodotFPPhysicsImmediateDrawer _staticDrawer;
        GodotFPPhysicsImmediateDrawer _dynamicDrawer;

        bool _dynamicDirty;

        // Collision/trigger marking (reused across frames — no per-frame realloc; GC rule).
        bool[] _collidingMark;
        bool[] _triggerMark;
        bool[] _triggerStaticMark;
        readonly System.Collections.Generic.Dictionary<int, int> _idToIndex = new();
        readonly System.Collections.Generic.Dictionary<int, int> _staticIdToIndex = new();

        KlothoSession _session;
        Action<int> _onTick;

        // ---- Lifecycle ----

        public override void _Ready()
        {
            EnsureNodes();
        }

        public override void _EnterTree()
        {
            EnsureNodes();
            KlothoSession.OnSessionCreated += HandleSessionCreated;
        }

        public override void _ExitTree()
        {
            KlothoSession.OnSessionCreated -= HandleSessionCreated;
            DetachSession();
        }

        void EnsureNodes()
        {
            if (_dynamicMI != null) return;

            var staticMesh = new ImmediateMesh();
            var dynamicMesh = new ImmediateMesh();
            _staticMI = new MeshInstance3D { Mesh = staticMesh, Name = "FPPhysicsOverlayStatic", TopLevel = true };
            _dynamicMI = new MeshInstance3D { Mesh = dynamicMesh, Name = "FPPhysicsOverlayDynamic", TopLevel = true };
            AddChild(_staticMI);
            AddChild(_dynamicMI);
            _staticDrawer = new GodotFPPhysicsImmediateDrawer(staticMesh);
            _dynamicDrawer = new GodotFPPhysicsImmediateDrawer(dynamicMesh);
        }

        // Place-before-session path: static event delivers the session.
        void HandleSessionCreated(KlothoSession session) => Bind(session);

        // Dynamic-spawn path: the game hands the session in explicitly (no current-session accessor exists).
        public void Bind(KlothoSession session)
        {
            if (session == null) return;
            DetachSession();
            _session = session;
            if (session.SimulationCallbacks is IFPPhysicsProviderSource src)
                Provider = src.PhysicsProvider;

            var engine = session.Engine;
            if (engine != null)
            {
                _onTick = _ => _dynamicDirty = true;
                engine.OnTickExecuted += _onTick;
                _dynamicDirty = true;   // first paint
            }
        }

        void DetachSession()
        {
            if (_session?.Engine != null && _onTick != null)
                _session.Engine.OnTickExecuted -= _onTick;
            _onTick = null;
            _session = null;
            Provider = null;
        }

        // ---- Per-frame: rebuild at most once when a tick advanced ----

        public override void _Process(double delta)
        {
            // Visibility follows Enabled every frame: the early-return below only stops *updating* the
            // ImmediateMesh; the baked geometry would otherwise keep rendering when disabled or after the
            // session ends. Hide the overlay nodes instead of relying on stale surfaces (mirrors the debug panel).
            bool show = Enabled && Provider != null;
            if (_staticMI != null) _staticMI.Visible = show;
            if (_dynamicMI != null) _dynamicMI.Visible = show;

            if (!show || !_dynamicDirty) return;
            _dynamicDirty = false;

            Provider.GetBodies(out var bodies, out int bc);
            Provider.GetStaticColliders(out var statics, out int sc);
            Provider.GetContacts(out var contacts, out int cc, out var sContacts, out int scc);
            Provider.GetTriggerPairs(out var triggerPairs, out int tc);
            currentStats = Provider.GetSnapshotStats();
            WarnOnceIfSnapshotTruncated();

            // Cache snapshot for the debug panel.
            currentBodies = bodies; bodyCount = bc;
            currentStatics = statics; staticCount = sc;
            currentContacts = contacts; currentContactCount = cc;
            currentSContacts = sContacts; currentSContactCount = scc;

            if (ShowCollisionHighlight)
            {
                RebuildIdMaps(bodies, bc, statics, sc);
                MarkCollidingBodies(bc, contacts, cc, sContacts, scc);
                MarkTriggerBodies(bc, sc, triggerPairs, tc);
            }

            // Rebuild static every tick alongside dynamic. The original count-gate (rebuild only when
            // staticCount changed) was a premature optimization — static colliders are few, so per-tick
            // rebuild is negligible, and it makes ShowStaticColliders/ShowStaticShape/colors toggles take
            // effect immediately (matching how dynamic toggles already work).
            RebuildStatic(statics, sc);
            RebuildDynamic(bodies, bc, statics, sc, contacts, cc, sContacts, scc);
        }

        void WarnOnceIfSnapshotTruncated()
        {
            bool grew = currentStats.ContactTruncatedCount       != _lastSeenContactTrunc
                     || currentStats.StaticContactTruncatedCount != _lastSeenStaticTrunc
                     || currentStats.TriggerTruncatedCount       != _lastSeenTriggerTrunc;

            _lastSeenContactTrunc = currentStats.ContactTruncatedCount;
            _lastSeenStaticTrunc  = currentStats.StaticContactTruncatedCount;
            _lastSeenTriggerTrunc = currentStats.TriggerTruncatedCount;

            if (!grew || _warnedSnapshotTruncated) return;
            _warnedSnapshotTruncated = true;

            // once per instance; the live numbers are the debug panel's job. "not copied", not
            // "collisions" — the totals include speculative (CCD) contacts, never highlighted.
            GD.PushWarning(
                "[GodotFPPhysicsWorldVisualizer] contact/trigger snapshot truncated — the overlay and "
                + "the debug panel's per-body list are showing less than the simulation holds. "
                + $"Contacts {currentStats.ContactCopied}/{currentStats.ContactTotal}, "
                + $"Static {currentStats.StaticContactCopied}/{currentStats.StaticContactTotal}, "
                + $"Triggers {currentStats.TriggerCopied}/{currentStats.TriggerTotal}. "
                + $"Truncating copies so far: {currentStats.ContactTruncatedCount}/"
                + $"{currentStats.StaticContactTruncatedCount}/{currentStats.TriggerTruncatedCount}.");
        }

        void RebuildStatic(FPStaticCollider[] statics, int sc)
        {
            _staticDrawer.Begin(AlwaysOnTop);
            if (ShowStaticColliders)
            {
                for (int i = 0; i < sc; i++)
                {
                    if (ShowStaticShape)
                        _staticDrawer.DrawStaticColliderShape(ref statics[i], SceneStaticColor);
                    if (ShowStaticAABB)
                        _staticDrawer.DrawAABB(statics[i].collider.GetWorldBounds(statics[i].meshData), AabbColor);
                }
            }
            _staticDrawer.End();
        }

        void RebuildDynamic(FPPhysicsBody[] bodies, int bc,
                            FPStaticCollider[] statics, int sc,
                            FPContact[] contacts, int cc,
                            FPContact[] sContacts, int scc)
        {
            _dynamicDrawer.Begin(AlwaysOnTop);

            if (ShowBodies)
                for (int i = 0; i < bc; i++)
                    DrawBody(ref bodies[i]);

            // Collision/trigger highlight overlay (re-draws marked shapes in highlight colors).
            if (ShowCollisionHighlight
                && _collidingMark != null && _collidingMark.Length >= bc
                && _triggerMark != null && _triggerMark.Length >= bc)
            {
                for (int i = 0; i < bc; i++)
                {
                    if (_collidingMark[i])
                        _dynamicDrawer.DrawBodyShape(ref bodies[i], CollisionHighlightColor);
                    if (_triggerMark[i])
                        _dynamicDrawer.DrawBodyShape(ref bodies[i], TriggerHighlightColor);
                }
                if (_triggerStaticMark != null && _triggerStaticMark.Length >= sc)
                    for (int i = 0; i < sc; i++)
                        if (_triggerStaticMark[i])
                            _dynamicDrawer.DrawStaticColliderShape(ref statics[i], TriggerHighlightColor);
            }

            if (ShowContacts)
            {
                for (int i = 0; i < cc; i++)
                    _dynamicDrawer.DrawContact(ref contacts[i], ContactPointColor, ContactNormalColor,
                        ContactNormalScale, ContactPointRadius, ShowContactNormals);
                for (int i = 0; i < scc; i++)
                    _dynamicDrawer.DrawContact(ref sContacts[i], ContactPointColor, ContactNormalColor,
                        ContactNormalScale, ContactPointRadius, ShowContactNormals);
            }

            // Selected item highlight (final overlay).
            DrawSelectedHighlight(bodies, bc, statics, sc);

            _dynamicDrawer.End();
        }

        void DrawSelectedHighlight(FPPhysicsBody[] bodies, int bc, FPStaticCollider[] statics, int sc)
        {
            // ImmediateMesh lines have no width, so the bright SelectedShapeColor (not a thicker
            // outline) is what distinguishes the selected item.
            if (viewingBodies && selectedIndex >= 0 && selectedIndex < bc)
            {
                _dynamicDrawer.DrawBodyShape(ref bodies[selectedIndex], SelectedShapeColor);
                _dynamicDrawer.DrawAABB(bodies[selectedIndex].collider.GetWorldBounds(bodies[selectedIndex].meshData), SelectedAABBColor);
            }
            else if (!viewingBodies && selectedIndex >= 0 && selectedIndex < sc)
            {
                _dynamicDrawer.DrawStaticColliderShape(ref statics[selectedIndex], SelectedShapeColor);
                _dynamicDrawer.DrawAABB(statics[selectedIndex].collider.GetWorldBounds(statics[selectedIndex].meshData), SelectedAABBColor);
            }
        }

        // ---- Collision / trigger marking (verbatim logic from FPPhysicsWorldVisualizer:265-328) ----

        void MarkCollidingBodies(int bodyCount, FPContact[] contacts, int cCount,
                                 FPContact[] sContacts, int scCount)
        {
            if (_collidingMark == null || _collidingMark.Length < bodyCount)
                _collidingMark = new bool[bodyCount];
            for (int i = 0; i < bodyCount; i++) _collidingMark[i] = false;

            for (int i = 0; i < cCount; i++)
            {
                if (contacts[i].isSpeculative) continue;
                int a = contacts[i].entityA;
                int b = contacts[i].entityB;
                if (a >= 0 && a < bodyCount) _collidingMark[a] = true;
                if (b >= 0 && b < bodyCount) _collidingMark[b] = true;
            }

            for (int i = 0; i < scCount; i++)
            {
                if (sContacts[i].isSpeculative) continue;
                int a = sContacts[i].entityA;
                int b = sContacts[i].entityB;
                if (a >= 0 && a < bodyCount) _collidingMark[a] = true;
                if (b >= 0 && b < bodyCount) _collidingMark[b] = true;
            }
        }

        void MarkTriggerBodies(int bodyCount, int staticCount, (int idA, int idB)[] pairs, int count)
        {
            if (_triggerMark == null || _triggerMark.Length < bodyCount)
                _triggerMark = new bool[bodyCount];
            for (int i = 0; i < bodyCount; i++) _triggerMark[i] = false;

            if (_triggerStaticMark == null || _triggerStaticMark.Length < staticCount)
                _triggerStaticMark = new bool[staticCount];
            for (int i = 0; i < staticCount; i++) _triggerStaticMark[i] = false;

            for (int i = 0; i < count; i++)
            {
                TryMarkTriggerId(pairs[i].Item1, bodyCount, staticCount);
                TryMarkTriggerId(pairs[i].Item2, bodyCount, staticCount);
            }
        }

        void TryMarkTriggerId(int id, int bodyCount, int staticCount)
        {
            if (_idToIndex.TryGetValue(id, out int bi) && bi < bodyCount)
                _triggerMark[bi] = true;
            else if (_staticIdToIndex.TryGetValue(id, out int si) && si < staticCount)
                _triggerStaticMark[si] = true;
        }

        void RebuildIdMaps(FPPhysicsBody[] bodies, int bodyCount, FPStaticCollider[] statics, int staticCount)
        {
            _idToIndex.Clear();
            for (int i = 0; i < bodyCount; i++)
                _idToIndex[bodies[i].id] = i;

            _staticIdToIndex.Clear();
            for (int i = 0; i < staticCount; i++)
                _staticIdToIndex[statics[i].id] = i;
        }

        void DrawBody(ref FPPhysicsBody body)
        {
            Color color = BodyColor(ref body);

            if (ShowBodyShape)
                _dynamicDrawer.DrawBodyShape(ref body, color);
            if (ShowBodyAABB)
                _dynamicDrawer.DrawAABB(body.collider.GetWorldBounds(body.meshData), AabbColor);
            if (ShowBodyVelocity && !body.rigidBody.isStatic && !body.rigidBody.isKinematic)
                _dynamicDrawer.DrawArrowFromVelocity(body.position.ToVector3(),
                    body.rigidBody.velocity.ToVector3(), VelocityArrowScale, VelocityColor);
        }

        Color BodyColor(ref FPPhysicsBody body)
        {
            if (body.isTrigger)
            {
                Color b = body.rigidBody.isStatic ? StaticBodyShapeColor
                        : body.rigidBody.isKinematic ? KinematicShapeColor
                                                     : DynamicShapeColor;
                return new Color(b.R, b.G, b.B, 0.4f);
            }
            if (body.rigidBody.isStatic) return StaticBodyShapeColor;
            if (body.rigidBody.isKinematic) return KinematicShapeColor;
            return DynamicShapeColor;
        }
    }
}
