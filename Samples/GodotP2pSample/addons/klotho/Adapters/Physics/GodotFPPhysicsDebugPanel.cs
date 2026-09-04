// Runtime on-screen FPPhysics debug HUD. The editor inspector cannot follow a separately-running game's
// live state, so the body/contact inspector is a runtime CanvasLayer + Control HUD that reads the
// visualizer's cached snapshot (currentBodies/currentContacts/...) and drives its selectedIndex/
// viewingBodies. Built entirely in code (no .tscn); docked to a screen corner via Corner.
using System.Text;

using global::Godot;

using xpTURN.Klotho.Deterministic.Math;        // FP64.ToFloat()
using xpTURN.Klotho.Deterministic.Geometry;    // FPContact, FPBounds3
using xpTURN.Klotho.Deterministic.Physics;     // FPPhysicsBody, FPStaticCollider

namespace xpTURN.Klotho.Godot
{
    public partial class GodotFPPhysicsDebugPanel : CanvasLayer
    {
        public enum PanelCorner { TopLeft, TopRight, BottomLeft, BottomRight }

        [Export] public bool Enabled = false;
        [Export] public GodotFPPhysicsWorldVisualizer Visualizer;

        // Which screen corner the HUD docks to (game UI usually occupies the top-left).
        [Export]
        public PanelCorner Corner
        {
            get => _corner;
            set { _corner = value; if (_root != null) ApplyCorner(); }
        }
        PanelCorner _corner = PanelCorner.TopRight;

        Label _header;
        Button _bodiesTab;
        Button _staticTab;
        Button _prev;
        Button _next;
        Label _navLabel;
        Label _detail;
        Control _root;

        public override void _Ready()
        {
            BuildUi();
        }

        void BuildUi()
        {
            if (_root != null) return;

            var panel = new PanelContainer { Name = "FPPhysicsDebugPanel" };
            panel.CustomMinimumSize = new Vector2(340, 0);
            AddChild(panel);
            _root = panel;
            ApplyCorner();

            var vbox = new VBoxContainer();
            panel.AddChild(vbox);

            _header = new Label { Text = "FPPhysics" };
            vbox.AddChild(_header);

            var tabs = new HBoxContainer();
            vbox.AddChild(tabs);
            _bodiesTab = new Button { Text = "Bodies", ToggleMode = true, ButtonPressed = true };
            _staticTab = new Button { Text = "StaticColliders", ToggleMode = true };
            _bodiesTab.Pressed += () => SetViewing(true);
            _staticTab.Pressed += () => SetViewing(false);
            tabs.AddChild(_bodiesTab);
            tabs.AddChild(_staticTab);

            var nav = new HBoxContainer();
            vbox.AddChild(nav);
            _prev = new Button { Text = "<", CustomMinimumSize = new Vector2(30, 0) };
            _next = new Button { Text = ">", CustomMinimumSize = new Vector2(30, 0) };
            _navLabel = new Label { Text = "0 / 0", HorizontalAlignment = HorizontalAlignment.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _prev.Pressed += () => Step(-1);
            _next.Pressed += () => Step(+1);
            nav.AddChild(_prev);
            nav.AddChild(_navLabel);
            nav.AddChild(_next);

            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 220) };
            vbox.AddChild(scroll);
            _detail = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            scroll.AddChild(_detail);

            var copy = new Button { Text = "Copy to Clipboard" };
            copy.Pressed += () => DisplayServer.ClipboardSet(_detail.Text);
            vbox.AddChild(copy);
        }

        // Dock the panel to the chosen corner: collapse all anchors to that corner point, then grow
        // inward (away from the edges) with content. Robust for every corner (no negative-size cases).
        void ApplyCorner()
        {
            if (_root == null) return;
            const float m = 10f;
            bool right = _corner == PanelCorner.TopRight || _corner == PanelCorner.BottomRight;
            bool bottom = _corner == PanelCorner.BottomLeft || _corner == PanelCorner.BottomRight;

            float ax = right ? 1f : 0f;
            float ay = bottom ? 1f : 0f;
            _root.AnchorLeft = _root.AnchorRight = ax;
            _root.AnchorTop = _root.AnchorBottom = ay;

            float ox = right ? -m : m;
            float oy = bottom ? -m : m;
            _root.OffsetLeft = _root.OffsetRight = ox;
            _root.OffsetTop = _root.OffsetBottom = oy;

            _root.GrowHorizontal = right ? Control.GrowDirection.Begin : Control.GrowDirection.End;
            _root.GrowVertical = bottom ? Control.GrowDirection.Begin : Control.GrowDirection.End;
        }

        void SetViewing(bool bodies)
        {
            if (Visualizer == null) return;
            Visualizer.viewingBodies = bodies;
            Visualizer.selectedIndex = 0;
            _bodiesTab.ButtonPressed = bodies;
            _staticTab.ButtonPressed = !bodies;
        }

        void Step(int delta)
        {
            if (Visualizer == null) return;
            Visualizer.selectedIndex += delta;
        }

        public override void _Process(double delta)
        {
            bool show = Enabled && Visualizer != null;
            if (_root != null) _root.Visible = show;
            if (!show) return;

            var vis = Visualizer;
            int total = vis.viewingBodies ? vis.bodyCount : vis.staticCount;
            if (total > 0)
                vis.selectedIndex = Mathf.Clamp(vis.selectedIndex, 0, total - 1);
            else
                vis.selectedIndex = 0;

            _header.Text = $"FPPhysics   Bodies: {vis.bodyCount}   StaticColliders: {vis.staticCount}";
            // copied/total for the three snapshot views, suppressed until the visualizer has run a
            // frame (the contact caches are filled by _Process, not here) — "0/0" in that state
            // would assert "nothing was truncated" where we know nothing.
            if (vis.currentContacts != null)
            {
                var st = vis.currentStats;
                _header.Text += $"\n   Contacts: {st.ContactCopied}/{st.ContactTotal}"
                    + $"   Static: {st.StaticContactCopied}/{st.StaticContactTotal}"
                    + $"   Triggers: {st.TriggerCopied}/{st.TriggerTotal}"
                    + (st.AnyTruncated ? "   [TRUNCATED — showing less than the simulation holds]" : "");
            }
            _bodiesTab.ButtonPressed = vis.viewingBodies;
            _staticTab.ButtonPressed = !vis.viewingBodies;
            _navLabel.Text = $"{(total == 0 ? 0 : vis.selectedIndex + 1)} / {total}";
            _prev.Disabled = vis.selectedIndex <= 0;
            _next.Disabled = vis.selectedIndex >= total - 1;
            _detail.Text = BuildDetailText(vis);
        }

        // ---- Detail text (used for both the panel label and clipboard) ----

        static string BuildDetailText(GodotFPPhysicsWorldVisualizer vis)
        {
            if (vis.viewingBodies)
            {
                if (vis.currentBodies == null || vis.selectedIndex >= vis.bodyCount) return string.Empty;
                return BuildBodyText(vis);
            }
            if (vis.currentStatics == null || vis.selectedIndex >= vis.staticCount) return string.Empty;
            return BuildStaticText(vis);
        }

        static string BuildBodyText(GodotFPPhysicsWorldVisualizer vis)
        {
            FPPhysicsBody b = vis.currentBodies[vis.selectedIndex];
            var rb = b.rigidBody;
            var sb = new StringBuilder();
            sb.AppendLine($"EntityIndex: {b.id}");
            sb.AppendLine($"Type: {BodyTypeStr(b)}");
            sb.AppendLine($"Position: {FmtV3(b.position)}");
            sb.AppendLine($"Rotation: {FmtEuler(b.rotation)}");
            sb.AppendLine($"Shape: {b.collider.type}");
            sb.AppendLine($"Mass: {rb.mass.ToFloat():F2}  invM: {rb.inverseMass.ToFloat():F2}");
            sb.AppendLine($"Velocity: {FmtV3(rb.velocity)}");
            sb.AppendLine($"AngVelocity: {FmtV3(rb.angularVelocity)}");
            sb.AppendLine($"Damping: lin={rb.linearDamping.ToFloat():F2}  ang={rb.angularDamping.ToFloat():F2}");
            sb.AppendLine($"Material: rest={rb.restitution.ToFloat():F2}  fric={rb.friction.ToFloat():F2}");
            sb.AppendLine($"Flags: Static={rb.isStatic}  Kin={rb.isKinematic}  Trigger={b.isTrigger}");
            AppendContactList(sb, vis);
            return sb.ToString();
        }

        static string BuildStaticText(GodotFPPhysicsWorldVisualizer vis)
        {
            FPStaticCollider sc = vis.currentStatics[vis.selectedIndex];
            var bounds = sc.collider.GetWorldBounds(sc.meshData);
            var sb = new StringBuilder();
            sb.AppendLine($"Type: id={sc.id}  Shape: {sc.collider.type}");
            sb.AppendLine($"Material: rest={sc.restitution.ToFloat():F2}  fric={sc.friction.ToFloat():F2}");
            sb.AppendLine($"Trigger: {sc.isTrigger}");
            sb.AppendLine($"AABB center: {FmtV3(bounds.center)}");
            sb.AppendLine($"AABB size: {FmtV3(bounds.size)}");
            return sb.ToString();
        }

        static void AppendContactList(StringBuilder sb, GodotFPPhysicsWorldVisualizer vis)
        {
            int bodyIdx = vis.selectedIndex;
            int dynCount = 0, staCount = 0;

            if (vis.currentContacts != null)
                for (int i = 0; i < vis.currentContactCount; i++)
                    if (vis.currentContacts[i].entityA == bodyIdx || vis.currentContacts[i].entityB == bodyIdx)
                        dynCount++;
            if (vis.currentSContacts != null)
                for (int i = 0; i < vis.currentSContactCount; i++)
                    if (vis.currentSContacts[i].entityA == bodyIdx || vis.currentSContacts[i].entityB == bodyIdx)
                        staCount++;

            // truncation has to be considered, not just the tally: a body whose contacts were all
            // dropped reads as zero, and that is exactly the body someone came to inspect.
            bool incomplete = vis.currentStats.ContactListMayBeIncomplete;
            if (!vis.currentStats.ShouldDrawContactList(dynCount, staCount)) return;

            sb.AppendLine();
            // the header text itself has to say it — a note beside "Dyn:0 Static:0" still leaves the
            // body of the list asserting "no contacts".
            sb.AppendLine(incomplete
                ? $"Contacts  Dyn:{dynCount}  Static:{staCount}  — truncated (this body's contacts may not be shown)"
                : $"Contacts  Dyn:{dynCount}  Static:{staCount}");

            if (vis.currentContacts != null)
            {
                for (int i = 0; i < vis.currentContactCount; i++)
                {
                    FPContact c = vis.currentContacts[i];
                    if (c.entityA != bodyIdx && c.entityB != bodyIdx) continue;
                    int other = c.entityA == bodyIdx ? c.entityB : c.entityA;
                    string tag = c.isSpeculative ? " [CCD]" : "";
                    string peer = (other >= 0 && other < vis.bodyCount) ? $"entity={vis.currentBodies[other].id}" : "?";
                    sb.AppendLine($"  {peer}  d={Mathf.Abs(c.depth.ToFloat()):F3}  n={FmtV3(c.normal)}{tag}");
                }
            }

            if (vis.currentSContacts != null)
            {
                for (int i = 0; i < vis.currentSContactCount; i++)
                {
                    FPContact c = vis.currentSContacts[i];
                    if (c.entityA != bodyIdx && c.entityB != bodyIdx) continue;
                    string tag = c.isSpeculative ? " [CCD]" : "";
                    string peer = c.entityB < 0
                        ? $"static[{~c.entityB}]"
                        : (c.entityB >= 0 && c.entityB < vis.bodyCount ? $"entity={vis.currentBodies[c.entityB].id}" : "?");
                    sb.AppendLine($"  {peer}  d={Mathf.Abs(c.depth.ToFloat()):F3}  n={FmtV3(c.normal)}{tag}");
                }
            }
        }

        // ---- Format helpers (port of FPPhysicsWorldVisualizer.FmtV3/FmtEuler/BodyTypeStr) ----

        static string FmtV3(FPVector3 v)
            => $"({v.x.ToFloat():F2}, {v.y.ToFloat():F2}, {v.z.ToFloat():F2})";

        static string FmtEuler(FPQuaternion q)
        {
            var e = new Quaternion(q.x.ToFloat(), q.y.ToFloat(), q.z.ToFloat(), q.w.ToFloat()).GetEuler();
            return $"({Mathf.RadToDeg(e.X):F0}°, {Mathf.RadToDeg(e.Y):F0}°, {Mathf.RadToDeg(e.Z):F0}°)";
        }

        static string BodyTypeStr(FPPhysicsBody b)
        {
            if (b.rigidBody.isStatic) return "Static";
            if (b.rigidBody.isKinematic) return "Kinematic";
            return "Dynamic";
        }
    }
}
