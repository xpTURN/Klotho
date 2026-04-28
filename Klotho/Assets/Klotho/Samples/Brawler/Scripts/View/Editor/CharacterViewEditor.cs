#if UNITY_EDITOR
namespace Brawler
{
    using UnityEngine;
    using UnityEditor;
    using xpTURN.Klotho.ECS;
    using xpTURN.Klotho.ECS.FSM;

    [CustomEditor(typeof(CharacterView))]
    public class CharacterViewEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (!Application.isPlaying) return;

            var view = (CharacterView)target;
            var engine = view.Engine;
            if (engine == null) return;

            var entity = view.CachedEntity;
            var frame = engine.PredictedFrame.Frame;
            if (frame == null) return;
            if (!entity.IsValid || !frame.Has<CharacterComponent>(entity)) return;

            ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(entity);
            ref readonly var t = ref frame.GetReadOnly<TransformComponent>(entity);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("ECS Debug", EditorStyles.boldLabel);
            EditorGUILayout.IntField("EntityIndex", entity.Index);

            var pos = t.Position;
            EditorGUILayout.Vector3Field("Position", new UnityEngine.Vector3(pos.x.ToFloat(), pos.y.ToFloat(), pos.z.ToFloat()));
            EditorGUILayout.FloatField("RotationY", t.Rotation.ToFloat() * UnityEngine.Mathf.Rad2Deg);

            if (frame.Has<PhysicsBodyComponent>(entity))
            {
                ref readonly var phys = ref frame.GetReadOnly<PhysicsBodyComponent>(entity);
                var vel = phys.RigidBody.velocity;
                EditorGUILayout.Vector3Field("Velocity", new UnityEngine.Vector3(vel.x.ToFloat(), vel.y.ToFloat(), vel.z.ToFloat()));
            }

            EditorGUILayout.IntField("KnockbackPower", c.KnockbackPower);
            EditorGUILayout.IntField("StockCount", c.StockCount);
            EditorGUILayout.Toggle("IsDead", c.IsDead);
            EditorGUILayout.Toggle("IsGrounded", c.IsGrounded);
            EditorGUILayout.Toggle("IsJumping", c.IsJumping);
            var gn = c.GroundNormal;
            EditorGUILayout.Vector3Field("GroundNormal", new UnityEngine.Vector3(gn.x.ToFloat(), gn.y.ToFloat(), gn.z.ToFloat()));
            EditorGUILayout.IntField("CharacterClass", c.CharacterClass);

            var isKnockback = frame.Has<KnockbackComponent>(entity) && frame.GetReadOnly<KnockbackComponent>(entity).BlockInput;
            EditorGUILayout.Toggle("HasKnockback", isKnockback);

            bool isBot = frame.Has<BotComponent>(entity);
            EditorGUILayout.Toggle("IsBot", isBot);
            if (isBot)
            {
                ref readonly var bot = ref frame.GetReadOnly<BotComponent>(entity);
                int leafId = frame.Has<HFSMComponent>(entity)
                    ? HFSMManager.GetLeafStateId(ref frame, entity)
                    : -1;
                string stateName = leafId switch
                {
                    BotStateId.Idle   => "Idle",
                    BotStateId.Chase  => "Chase",
                    BotStateId.Attack => "Attack",
                    BotStateId.Evade  => "Evade",
                    BotStateId.Skill  => "Skill",
                    _ => $"Unknown({leafId})",
                };
                EditorGUILayout.TextField("FSM State", stateName);
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("Copy ECS Debug Info"))
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"EntityIndex: {entity.Index}");
                sb.AppendLine($"PlayerId: {view.PlayerId}");
                sb.AppendLine($"Position: ({pos.x.ToFloat():F4}, {pos.y.ToFloat():F4}, {pos.z.ToFloat():F4})");
                sb.AppendLine($"RotationY: {t.Rotation.ToFloat() * UnityEngine.Mathf.Rad2Deg:F2}");
                if (frame.Has<PhysicsBodyComponent>(entity))
                {
                    ref readonly var phys = ref frame.GetReadOnly<PhysicsBodyComponent>(entity);
                    var vel = phys.RigidBody.velocity;
                    sb.AppendLine($"Velocity: ({vel.x.ToFloat():F4}, {vel.y.ToFloat():F4}, {vel.z.ToFloat():F4})");
                }
                sb.AppendLine($"KnockbackPower: {c.KnockbackPower}");
                sb.AppendLine($"StockCount: {c.StockCount}");
                sb.AppendLine($"IsDead: {c.IsDead}");
                sb.AppendLine($"IsGrounded: {c.IsGrounded}");
                sb.AppendLine($"IsJumping: {c.IsJumping}");
                sb.AppendLine($"GroundNormal: ({c.GroundNormal.x.ToFloat():F4}, {c.GroundNormal.y.ToFloat():F4}, {c.GroundNormal.z.ToFloat():F4})");
                sb.AppendLine($"CharacterClass: {c.CharacterClass}");

                sb.AppendLine($"HasKnockback: {isKnockback}");
                sb.AppendLine($"IsBot: {isBot}");
                if (isBot)
                {
                    ref readonly var bot = ref frame.GetReadOnly<BotComponent>(entity);
                    int lid = frame.Has<HFSMComponent>(entity)
                        ? HFSMManager.GetLeafStateId(ref frame, entity)
                        : -1;
                    sb.AppendLine($"FSM State: {lid} (leafId={lid})");
                    sb.AppendLine($"HasDestination: {bot.HasDestination}");
                    sb.AppendLine($"Destination: ({bot.Destination.x.ToFloat():F4}, {bot.Destination.y.ToFloat():F4}, {bot.Destination.z.ToFloat():F4})");
                }
                EditorGUIUtility.systemCopyBuffer = sb.ToString();
            }

            Repaint();
        }
    }
}
#endif
