using System.Text;
using xpTURN.Klotho.Generator.Model;
using xpTURN.Klotho.Generator.Utils;

namespace xpTURN.Klotho.Generator.Emitters
{
    internal static class EmitHelper
    {
        /// <summary>
        /// Returns size info for a field.
        /// isFixed=true: entire size is compile-time constant.
        /// isFixed=false: size = fixed portion (e.g. count prefix), variableExpr = runtime expression.
        /// </summary>
        public static (bool isFixed, int size, string variableExpr) GetSizeExpression(SerializableFieldInfo field)
        {
            // Direct mapped type
            if (TypeMappings.TryGetMapping(field.TypeFullName, out var mapping))
            {
                if (mapping.Size > 0)
                    return (true, mapping.Size, null);

                // Variable: string or byte[]
                if (IsString(field.TypeFullName))
                    return (false, 4, $"System.Text.Encoding.UTF8.GetByteCount(this.{field.Name} ?? \"\")");

                if (IsByteArray(field.TypeFullName))
                    return (false, 4, $"(this.{field.Name}?.Length ?? 0)");
            }

            // List<T>
            if (field.ElementTypeName != null && field.KeyTypeName == null && IsListType(field.TypeFullName))
            {
                if (TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping) && elemMapping.Size > 0)
                {
                    return (false, 4, $"this.{field.Name}.Count * {elemMapping.Size}");
                }
            }

            // Array T[]
            if (field.ElementTypeName != null && field.KeyTypeName == null)
            {
                if (TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping) && elemMapping.Size > 0)
                {
                    return (false, 4, $"this.{field.Name}.Length * {elemMapping.Size}");
                }
            }

            // Dictionary<TK, TV>
            if (field.KeyTypeName != null && field.ValueTypeName != null)
            {
                if (TypeMappings.TryGetMapping(field.KeyTypeName, out var keyMapping) && keyMapping.Size > 0 &&
                    TypeMappings.TryGetMapping(field.ValueTypeName, out var valMapping) && valMapping.Size > 0)
                {
                    int pairSize = keyMapping.Size + valMapping.Size;
                    return (false, 4, $"this.{field.Name}.Count * {pairSize}");
                }
            }

            // Fallback — should not reach here if validation passes
            return (true, 0, null);
        }

        public static void EmitSerializeField(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            // Direct mapped type
            if (TypeMappings.TryGetMapping(field.TypeFullName, out var mapping))
            {
                if (IsByteArray(field.TypeFullName))
                {
                    sb.AppendLine($"{indent}writer.WriteBytes(this.{field.Name} ?? System.Array.Empty<byte>());");
                }
                else
                {
                    sb.AppendLine($"{indent}writer.{mapping.WriteMethod}(this.{field.Name});");
                }
                return;
            }

            // Array T[]
            if (field.ElementTypeName != null && field.KeyTypeName == null && !IsListType(field.TypeFullName))
            {
                EmitSerializeArray(sb, field, indent);
                return;
            }

            // List<T>
            if (field.ElementTypeName != null && field.KeyTypeName == null && IsListType(field.TypeFullName))
            {
                EmitSerializeList(sb, field, indent);
                return;
            }

            // Dictionary<TK, TV>
            if (field.KeyTypeName != null && field.ValueTypeName != null)
            {
                EmitSerializeDictionary(sb, field, indent);
                return;
            }
        }

        public static void EmitDeserializeField(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            EmitDeserializeField(sb, field, indent, "this");
        }

        public static void EmitDeserializeField(StringBuilder sb, SerializableFieldInfo field, string indent, string instancePrefix)
        {
            // Direct mapped type
            if (TypeMappings.TryGetMapping(field.TypeFullName, out var mapping))
            {
                if (IsByteArray(field.TypeFullName))
                {
                    sb.AppendLine($"{indent}var __bytes_{field.Name} = reader.ReadBytes();");
                    sb.AppendLine($"{indent}{instancePrefix}.{field.Name} = __bytes_{field.Name}.Length > 0 ? __bytes_{field.Name}.ToArray() : null;");
                }
                else
                {
                    sb.AppendLine($"{indent}{instancePrefix}.{field.Name} = reader.{mapping.ReadMethod}();");
                }
                return;
            }

            // Array T[]
            if (field.ElementTypeName != null && field.KeyTypeName == null && !IsListType(field.TypeFullName))
            {
                EmitDeserializeArray(sb, field, indent, instancePrefix);
                return;
            }

            // List<T>
            if (field.ElementTypeName != null && field.KeyTypeName == null && IsListType(field.TypeFullName))
            {
                EmitDeserializeList(sb, field, indent, instancePrefix);
                return;
            }

            // Dictionary<TK, TV>
            if (field.KeyTypeName != null && field.ValueTypeName != null)
            {
                EmitDeserializeDictionary(sb, field, indent, instancePrefix);
                return;
            }
        }

        public static void EmitGetSerializedSize(StringBuilder sb, SerializableTypeInfo info, string indent)
        {
            int baseSize = TypeMappings.GetBaseSize(info.Category);
            var fixedSize = baseSize;
            var variableParts = new StringBuilder();

            foreach (var field in info.Fields)
            {
                var sizeExpr = GetSizeExpression(field);
                if (sizeExpr.isFixed)
                {
                    fixedSize += sizeExpr.size;
                }
                else
                {
                    fixedSize += sizeExpr.size; // fixed portion (e.g., count prefix)
                    variableParts.Append($" + {sizeExpr.variableExpr}");
                }
            }

            if (variableParts.Length == 0)
            {
                sb.AppendLine($"{indent}    public override int GetSerializedSize() => {fixedSize};");
            }
            else
            {
                sb.AppendLine($"{indent}    public override int GetSerializedSize()");
                sb.AppendLine($"{indent}        => {fixedSize}{variableParts};");
            }
        }

        public static string GetHashExpression(SerializableFieldInfo field)
        {
            if (TypeMappings.TryGetMapping(field.TypeFullName, out var mapping) && mapping.HashExpression != null)
            {
                return "hash = " + string.Format(mapping.HashExpression, "hash", $"this.{field.Name}") + ";";
            }
            // Collections don't participate in hash
            return null;
        }

        // --- Array ---

        private static void EmitSerializeArray(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            if (!TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping)) return;
            sb.AppendLine($"{indent}writer.WriteInt32(this.{field.Name}.Length);");
            sb.AppendLine($"{indent}for (int __i = 0; __i < this.{field.Name}.Length; __i++)");
            sb.AppendLine($"{indent}    writer.{elemMapping.WriteMethod}(this.{field.Name}[__i]);");
        }

        private static void EmitDeserializeArray(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            EmitDeserializeArray(sb, field, indent, "this");
        }

        private static void EmitDeserializeArray(StringBuilder sb, SerializableFieldInfo field, string indent, string instancePrefix)
        {
            if (!TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping)) return;
            sb.AppendLine($"{indent}int __count_{field.Name} = reader.ReadInt32();");
            sb.AppendLine($"{indent}{instancePrefix}.{field.Name} = new {field.ElementTypeName}[__count_{field.Name}];");
            sb.AppendLine($"{indent}for (int __i = 0; __i < __count_{field.Name}; __i++)");
            sb.AppendLine($"{indent}    {instancePrefix}.{field.Name}[__i] = reader.{elemMapping.ReadMethod}();");
        }

        // --- List<T> ---

        private static void EmitSerializeList(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            if (!TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping)) return;
            sb.AppendLine($"{indent}writer.WriteInt32(this.{field.Name}.Count);");
            sb.AppendLine($"{indent}for (int __i = 0; __i < this.{field.Name}.Count; __i++)");
            sb.AppendLine($"{indent}    writer.{elemMapping.WriteMethod}(this.{field.Name}[__i]);");
        }

        private static void EmitDeserializeList(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            EmitDeserializeList(sb, field, indent, "this");
        }

        private static void EmitDeserializeList(StringBuilder sb, SerializableFieldInfo field, string indent, string instancePrefix)
        {
            if (!TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping)) return;
            sb.AppendLine($"{indent}int __count_{field.Name} = reader.ReadInt32();");
            sb.AppendLine($"{indent}{instancePrefix}.{field.Name}.Clear();");
            sb.AppendLine($"{indent}if ({instancePrefix}.{field.Name}.Capacity < __count_{field.Name})");
            sb.AppendLine($"{indent}    {instancePrefix}.{field.Name}.Capacity = __count_{field.Name};");
            sb.AppendLine($"{indent}for (int __i = 0; __i < __count_{field.Name}; __i++)");
            sb.AppendLine($"{indent}    {instancePrefix}.{field.Name}.Add(reader.{elemMapping.ReadMethod}());");
        }

        // --- Dictionary<TK, TV> ---

        // NOTE: Dictionary serialization is for editor/conversion time only. Not used on the runtime hot path,
        // so List allocation (GC) for key sorting is acceptable.
        private static void EmitSerializeDictionary(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            if (!TypeMappings.TryGetMapping(field.KeyTypeName, out var keyMapping)) return;
            if (!TypeMappings.TryGetMapping(field.ValueTypeName, out var valMapping)) return;

            sb.AppendLine($"{indent}writer.WriteInt32(this.{field.Name}.Count);");
            sb.AppendLine($"{indent}var __keys_{field.Name} = new System.Collections.Generic.List<{field.KeyTypeName}>(this.{field.Name}.Keys);");
            sb.AppendLine($"{indent}__keys_{field.Name}.Sort();");
            sb.AppendLine($"{indent}for (int __i = 0; __i < __keys_{field.Name}.Count; __i++)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    writer.{keyMapping.WriteMethod}(__keys_{field.Name}[__i]);");
            sb.AppendLine($"{indent}    writer.{valMapping.WriteMethod}(this.{field.Name}[__keys_{field.Name}[__i]]);");
            sb.AppendLine($"{indent}}}");
        }

        private static void EmitDeserializeDictionary(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            EmitDeserializeDictionary(sb, field, indent, "this");
        }

        private static void EmitDeserializeDictionary(StringBuilder sb, SerializableFieldInfo field, string indent, string instancePrefix)
        {
            if (!TypeMappings.TryGetMapping(field.KeyTypeName, out var keyMapping)) return;
            if (!TypeMappings.TryGetMapping(field.ValueTypeName, out var valMapping)) return;

            sb.AppendLine($"{indent}int __count_{field.Name} = reader.ReadInt32();");
            sb.AppendLine($"{indent}{instancePrefix}.{field.Name}.Clear();");
            sb.AppendLine($"{indent}for (int __i = 0; __i < __count_{field.Name}; __i++)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    var __key = reader.{keyMapping.ReadMethod}();");
            sb.AppendLine($"{indent}    var __val = reader.{valMapping.ReadMethod}();");
            sb.AppendLine($"{indent}    {instancePrefix}.{field.Name}[__key] = __val;");
            sb.AppendLine($"{indent}}}");
        }

        // --- Helpers ---

        private static bool IsListType(string typeFullName)
        {
            return typeFullName.StartsWith("System.Collections.Generic.List<");
        }

        private static bool IsByteArray(string typeFullName)
        {
            return typeFullName == "System.Byte[]" || typeFullName == "byte[]";
        }

        private static bool IsString(string typeFullName)
        {
            return typeFullName == "System.String" || typeFullName == "string";
        }
    }
}
