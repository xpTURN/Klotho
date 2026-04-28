using System.Collections.Generic;

namespace xpTURN.Klotho.Generator.Utils
{
    internal static class TypeMappings
    {
        internal sealed class TypeMapping
        {
            public string WriteMethod { get; set; }
            public string ReadMethod { get; set; }
            public int Size { get; set; }
            public string HashExpression { get; set; } // null = no hash support
        }

        private static readonly Dictionary<string, TypeMapping> Mappings = new Dictionary<string, TypeMapping>
        {
            // CLR names
            ["System.Boolean"] = new TypeMapping { WriteMethod = "WriteBool", ReadMethod = "ReadBool", Size = 1, HashExpression = "FPHash.Hash({0}, {1})" },
            ["System.Byte"] = new TypeMapping { WriteMethod = "WriteByte", ReadMethod = "ReadByte", Size = 1, HashExpression = "FPHash.Hash({0}, {1})" },
            ["System.Int16"] = new TypeMapping { WriteMethod = "WriteInt16", ReadMethod = "ReadInt16", Size = 2, HashExpression = "FPHash.Hash({0}, {1})" },
            ["System.UInt16"] = new TypeMapping { WriteMethod = "WriteUInt16", ReadMethod = "ReadUInt16", Size = 2, HashExpression = "FPHash.Hash({0}, {1})" },
            ["System.Int32"] = new TypeMapping { WriteMethod = "WriteInt32", ReadMethod = "ReadInt32", Size = 4, HashExpression = "FPHash.Hash({0}, {1})" },
            ["System.UInt32"] = new TypeMapping { WriteMethod = "WriteUInt32", ReadMethod = "ReadUInt32", Size = 4, HashExpression = "FPHash.Hash({0}, {1})" },
            ["System.Int64"] = new TypeMapping { WriteMethod = "WriteInt64", ReadMethod = "ReadInt64", Size = 8, HashExpression = "FPHash.Hash({0}, {1})" },
            ["System.UInt64"] = new TypeMapping { WriteMethod = "WriteUInt64", ReadMethod = "ReadUInt64", Size = 8, HashExpression = "FPHash.Hash({0}, {1})" },
            // C# keyword aliases (ToDisplayString returns these)
            ["bool"] = new TypeMapping { WriteMethod = "WriteBool", ReadMethod = "ReadBool", Size = 1, HashExpression = "FPHash.Hash({0}, {1})" },
            ["byte"] = new TypeMapping { WriteMethod = "WriteByte", ReadMethod = "ReadByte", Size = 1, HashExpression = "FPHash.Hash({0}, {1})" },
            ["short"] = new TypeMapping { WriteMethod = "WriteInt16", ReadMethod = "ReadInt16", Size = 2, HashExpression = "FPHash.Hash({0}, {1})" },
            ["ushort"] = new TypeMapping { WriteMethod = "WriteUInt16", ReadMethod = "ReadUInt16", Size = 2, HashExpression = "FPHash.Hash({0}, {1})" },
            ["int"] = new TypeMapping { WriteMethod = "WriteInt32", ReadMethod = "ReadInt32", Size = 4, HashExpression = "FPHash.Hash({0}, {1})" },
            ["uint"] = new TypeMapping { WriteMethod = "WriteUInt32", ReadMethod = "ReadUInt32", Size = 4, HashExpression = "FPHash.Hash({0}, {1})" },
            ["long"] = new TypeMapping { WriteMethod = "WriteInt64", ReadMethod = "ReadInt64", Size = 8, HashExpression = "FPHash.Hash({0}, {1})" },
            ["ulong"] = new TypeMapping { WriteMethod = "WriteUInt64", ReadMethod = "ReadUInt64", Size = 8, HashExpression = "FPHash.Hash({0}, {1})" },
            ["string"] = new TypeMapping { WriteMethod = "WriteString", ReadMethod = "ReadString", Size = -1, HashExpression = null },
            ["byte[]"] = new TypeMapping { WriteMethod = "WriteBytes", ReadMethod = "ReadBytes", Size = -1, HashExpression = null },
            // FP types
            ["xpTURN.Klotho.Deterministic.Math.FP64"] = new TypeMapping { WriteMethod = "WriteFP", ReadMethod = "ReadFP64", Size = 8, HashExpression = "FPHash.Hash({0}, {1})" },
            ["xpTURN.Klotho.Deterministic.Math.FPVector2"] = new TypeMapping { WriteMethod = "WriteFP", ReadMethod = "ReadFPVector2", Size = 16, HashExpression = "FPHash.Hash({0}, {1})" },
            ["xpTURN.Klotho.Deterministic.Math.FPVector3"] = new TypeMapping { WriteMethod = "WriteFP", ReadMethod = "ReadFPVector3", Size = 24, HashExpression = "FPHash.Hash({0}, {1})" },
            ["xpTURN.Klotho.Deterministic.Math.FPVector4"] = new TypeMapping { WriteMethod = "WriteFP", ReadMethod = "ReadFPVector4", Size = 32, HashExpression = "FPHash.Hash({0}, {1})" },
            ["xpTURN.Klotho.Deterministic.Math.FPQuaternion"] = new TypeMapping { WriteMethod = "WriteFP", ReadMethod = "ReadFPQuaternion", Size = 32, HashExpression = "FPHash.Hash({0}, {1})" },
            // CLR collection types
            ["System.String"] = new TypeMapping { WriteMethod = "WriteString", ReadMethod = "ReadString", Size = -1, HashExpression = null },
            ["System.Byte[]"] = new TypeMapping { WriteMethod = "WriteBytes", ReadMethod = "ReadBytes", Size = -1, HashExpression = null },
            // ECS types
            ["xpTURN.Klotho.ECS.EntityRef"] = new TypeMapping { WriteMethod = "WriteEntityRef", ReadMethod = "ReadEntityRef", Size = 8, HashExpression = "FPHash.Hash({0}, {1}.ToId())" },
            ["xpTURN.Klotho.ECS.DataAssetRef"] = new TypeMapping { WriteMethod = "WriteDataAssetRef", ReadMethod = "ReadDataAssetRef", Size = 4, HashExpression = "FPHash.Hash({0}, {1}.Id)" },
            // Composite physics types
            ["xpTURN.Klotho.Deterministic.Physics.FPRigidBody"] = new TypeMapping { WriteMethod = "WriteFPRigidBody", ReadMethod = "ReadFPRigidBody", Size = 146, HashExpression = "FPHash.Hash({0}, {1})" },
            ["xpTURN.Klotho.Deterministic.Physics.FPCollider"] = new TypeMapping { WriteMethod = "WriteFPCollider", ReadMethod = "ReadFPCollider", Size = 81, HashExpression = "FPHash.Hash({0}, {1})" },
        };

        public static bool TryGetMapping(string typeFullName, out TypeMapping mapping)
        {
            return Mappings.TryGetValue(typeFullName, out mapping);
        }

        public static bool IsFixedSizeType(string typeFullName)
        {
            return Mappings.TryGetValue(typeFullName, out var m) && m.Size > 0;
        }

        public static int GetBaseSize(Model.TypeCategory category)
        {
            switch (category)
            {
                case Model.TypeCategory.Entity: return 64;
                case Model.TypeCategory.Command: return 12;
                case Model.TypeCategory.Message: return 1;
                case Model.TypeCategory.Event: return 0;
                default: return 0;
            }
        }
    }
}
