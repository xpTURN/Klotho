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
            // Nested [KlothoSerializableStruct] — runtime size via its generated GetSerializedSize.
            if (field.IsNestedSerializable)
                return (false, 0, $"this.{field.Name}.GetSerializedSize()");

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
                // Nested [KlothoSerializableStruct] element — value-independent constant size per element
                // (unmanaged struct). Must return here: the Array branch below lacks an !IsListType guard.
                if (field.ElementIsNestedSerializable)
                    return (false, 4, $"(this.{field.Name}?.Count ?? 0) * default({field.ElementTypeName}).GetSerializedSize()");

                if (TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping) && elemMapping.Size > 0)
                {
                    return (false, 4, $"(this.{field.Name}?.Count ?? 0) * {elemMapping.Size}");
                }
                // Variable-size element (string): 4-byte count prefix + per-element (4-byte length prefix + UTF8 bytes).
                // GetSerializedSize is a single expression and the runtime is zero-GC (no LINQ), so the per-element
                // sum is computed by a hand-written runtime helper. The helper includes the count prefix, so the
                // fixed portion here is 0.
                if (IsString(field.ElementTypeName))
                {
                    return (false, 0, $"xpTURN.Klotho.Serialization.SerializationSize.StringList(this.{field.Name})");
                }
            }

            // Array T[]
            if (field.ElementTypeName != null && field.KeyTypeName == null)
            {
                // Nested [KlothoSerializableStruct] element — value-independent constant size per element.
                if (field.ElementIsNestedSerializable)
                    return (false, 4, $"(this.{field.Name}?.Length ?? 0) * default({field.ElementTypeName}).GetSerializedSize()");

                if (TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping) && elemMapping.Size > 0)
                {
                    return (false, 4, $"(this.{field.Name}?.Length ?? 0) * {elemMapping.Size}");
                }
                // Variable-size element (string): mirrors the List<string> branch above. The helper
                // includes the 4-byte count prefix, so the fixed portion here is 0.
                if (IsString(field.ElementTypeName))
                {
                    return (false, 0, $"xpTURN.Klotho.Serialization.SerializationSize.StringArray(this.{field.Name})");
                }
            }

            // Dictionary<TK, TV>
            if (field.KeyTypeName != null && field.ValueTypeName != null)
            {
                if (TypeMappings.TryGetMapping(field.KeyTypeName, out var keyMapping) && keyMapping.Size > 0 &&
                    TypeMappings.TryGetMapping(field.ValueTypeName, out var valMapping) && valMapping.Size > 0)
                {
                    int pairSize = keyMapping.Size + valMapping.Size;
                    return (false, 4, $"(this.{field.Name}?.Count ?? 0) * {pairSize}");
                }
            }

            // Fallback — should not reach here if validation passes
            return (true, 0, null);
        }

        public static void EmitSerializeField(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            // Nested [KlothoSerializableStruct] — delegate to its generated codec.
            if (field.IsNestedSerializable)
            {
                sb.AppendLine($"{indent}this.{field.Name}.Serialize(ref writer);");
                return;
            }

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
            // Nested [KlothoSerializableStruct] — delegate to its generated codec (field is addressable).
            if (field.IsNestedSerializable)
            {
                sb.AppendLine($"{indent}{instancePrefix}.{field.Name}.Deserialize(ref reader);");
                return;
            }

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
            // Nested [KlothoSerializableStruct] — fold via its generated GetHash.
            if (field.IsNestedSerializable)
                return $"hash = this.{field.Name}.GetHash(hash);";

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
            if (field.ElementIsNestedSerializable)
            {
                sb.AppendLine($"{indent}int __n_{field.Name} = this.{field.Name}?.Length ?? 0;");
                sb.AppendLine($"{indent}writer.WriteInt32(__n_{field.Name});");
                sb.AppendLine($"{indent}for (int __i = 0; __i < __n_{field.Name}; __i++)");
                sb.AppendLine($"{indent}    this.{field.Name}[__i].Serialize(ref writer);");
                return;
            }
            if (!TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping)) return;
            sb.AppendLine($"{indent}int __n_{field.Name} = this.{field.Name}?.Length ?? 0;");
            sb.AppendLine($"{indent}writer.WriteInt32(__n_{field.Name});");
            sb.AppendLine($"{indent}for (int __i = 0; __i < __n_{field.Name}; __i++)");
            sb.AppendLine($"{indent}    writer.{elemMapping.WriteMethod}(this.{field.Name}[__i]);");
        }

        private static void EmitDeserializeArray(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            EmitDeserializeArray(sb, field, indent, "this");
        }

        private static void EmitDeserializeArray(StringBuilder sb, SerializableFieldInfo field, string indent, string instancePrefix)
        {
            if (field.ElementIsNestedSerializable)
            {
                sb.AppendLine($"{indent}int __count_{field.Name} = reader.ReadInt32();");
                EmitCountGuard(sb, field, indent);
                sb.AppendLine($"{indent}{instancePrefix}.{field.Name} = __count_{field.Name} == 0 ? System.Array.Empty<{field.ElementTypeName}>() : new {field.ElementTypeName}[__count_{field.Name}];");
                sb.AppendLine($"{indent}for (int __i = 0; __i < __count_{field.Name}; __i++)");
                sb.AppendLine($"{indent}    {instancePrefix}.{field.Name}[__i].Deserialize(ref reader);");   // array element is addressable
                return;
            }
            if (!TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping)) return;
            sb.AppendLine($"{indent}int __count_{field.Name} = reader.ReadInt32();");
            EmitCountGuard(sb, field, indent);
            sb.AppendLine($"{indent}{instancePrefix}.{field.Name} = __count_{field.Name} == 0 ? System.Array.Empty<{field.ElementTypeName}>() : new {field.ElementTypeName}[__count_{field.Name}];");
            sb.AppendLine($"{indent}for (int __i = 0; __i < __count_{field.Name}; __i++)");
            sb.AppendLine($"{indent}    {instancePrefix}.{field.Name}[__i] = reader.{elemMapping.ReadMethod}();");
        }

        // --- List<T> ---

        private static void EmitSerializeList(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            if (field.ElementIsNestedSerializable)
            {
                sb.AppendLine($"{indent}int __n_{field.Name} = this.{field.Name}?.Count ?? 0;");
                sb.AppendLine($"{indent}writer.WriteInt32(__n_{field.Name});");
                sb.AppendLine($"{indent}for (int __i = 0; __i < __n_{field.Name}; __i++)");
                sb.AppendLine($"{indent}    this.{field.Name}[__i].Serialize(ref writer);");
                return;
            }
            if (!TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping)) return;
            sb.AppendLine($"{indent}int __n_{field.Name} = this.{field.Name}?.Count ?? 0;");
            sb.AppendLine($"{indent}writer.WriteInt32(__n_{field.Name});");
            sb.AppendLine($"{indent}for (int __i = 0; __i < __n_{field.Name}; __i++)");
            sb.AppendLine($"{indent}    writer.{elemMapping.WriteMethod}(this.{field.Name}[__i]);");
        }

        private static void EmitDeserializeList(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            EmitDeserializeList(sb, field, indent, "this");
        }

        private static void EmitDeserializeList(StringBuilder sb, SerializableFieldInfo field, string indent, string instancePrefix)
        {
            if (field.ElementIsNestedSerializable)
            {
                sb.AppendLine($"{indent}int __count_{field.Name} = reader.ReadInt32();");
                EmitCountGuard(sb, field, indent);
                EmitListReset(sb, field, indent, instancePrefix);
                sb.AppendLine($"{indent}for (int __i = 0; __i < __count_{field.Name}; __i++)");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    var __e_{field.Name} = default({field.ElementTypeName});");   // List indexer returns a copy — deserialize into a local, then Add
                sb.AppendLine($"{indent}    __e_{field.Name}.Deserialize(ref reader);");
                sb.AppendLine($"{indent}    {instancePrefix}.{field.Name}.Add(__e_{field.Name});");
                sb.AppendLine($"{indent}}}");
                return;
            }
            if (!TypeMappings.TryGetMapping(field.ElementTypeName, out var elemMapping)) return;
            sb.AppendLine($"{indent}int __count_{field.Name} = reader.ReadInt32();");
            EmitCountGuard(sb, field, indent);
            EmitListReset(sb, field, indent, instancePrefix);
            sb.AppendLine($"{indent}for (int __i = 0; __i < __count_{field.Name}; __i++)");
            sb.AppendLine($"{indent}    {instancePrefix}.{field.Name}.Add(reader.{elemMapping.ReadMethod}());");
        }

        // A null List is created at the read count (assignable members only); an existing one is reused.
        private static void EmitListReset(StringBuilder sb, SerializableFieldInfo field, string indent, string instancePrefix)
        {
            string f = $"{instancePrefix}.{field.Name}";
            string c = $"__count_{field.Name}";
            if (field.IsAssignable)
            {
                sb.AppendLine($"{indent}if ({f} == null)");
                sb.AppendLine($"{indent}    {f} = new {field.TypeFullName}({c});");
                sb.AppendLine($"{indent}else");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    {f}.Clear();");
                sb.AppendLine($"{indent}    if ({f}.Capacity < {c})");
                sb.AppendLine($"{indent}        {f}.Capacity = {c};");
                sb.AppendLine($"{indent}}}");
                return;
            }
            sb.AppendLine($"{indent}{f}.Clear();");
            sb.AppendLine($"{indent}if ({f}.Capacity < {c})");
            sb.AppendLine($"{indent}    {f}.Capacity = {c};");
        }

        // --- Dictionary<TK, TV> ---

        // NOTE: Dictionary serialization is for editor/conversion time only. Not used on the runtime hot path,
        // so List allocation (GC) for key sorting is acceptable.
        private static void EmitSerializeDictionary(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            if (!TypeMappings.TryGetMapping(field.KeyTypeName, out var keyMapping)) return;
            if (!TypeMappings.TryGetMapping(field.ValueTypeName, out var valMapping)) return;

            sb.AppendLine($"{indent}int __n_{field.Name} = this.{field.Name}?.Count ?? 0;");
            sb.AppendLine($"{indent}writer.WriteInt32(__n_{field.Name});");
            sb.AppendLine($"{indent}if (__n_{field.Name} > 0)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    var __keys_{field.Name} = new System.Collections.Generic.List<{field.KeyTypeName}>(this.{field.Name}.Keys);");
            sb.AppendLine($"{indent}    __keys_{field.Name}.Sort();");
            sb.AppendLine($"{indent}    for (int __i = 0; __i < __keys_{field.Name}.Count; __i++)");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        writer.{keyMapping.WriteMethod}(__keys_{field.Name}[__i]);");
            sb.AppendLine($"{indent}        writer.{valMapping.WriteMethod}(this.{field.Name}[__keys_{field.Name}[__i]]);");
            sb.AppendLine($"{indent}    }}");
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
            EmitCountGuard(sb, field, indent);
            if (field.IsAssignable)
            {
                sb.AppendLine($"{indent}if ({instancePrefix}.{field.Name} == null)");
                sb.AppendLine($"{indent}    {instancePrefix}.{field.Name} = new {field.TypeFullName}(__count_{field.Name});");
                sb.AppendLine($"{indent}else");
                sb.AppendLine($"{indent}    {instancePrefix}.{field.Name}.Clear();");
            }
            else
            {
                sb.AppendLine($"{indent}{instancePrefix}.{field.Name}.Clear();");
            }
            sb.AppendLine($"{indent}for (int __i = 0; __i < __count_{field.Name}; __i++)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    var __key = reader.{keyMapping.ReadMethod}();");
            sb.AppendLine($"{indent}    var __val = reader.{valMapping.ReadMethod}();");
            sb.AppendLine($"{indent}    {instancePrefix}.{field.Name}[__key] = __val;");
            sb.AppendLine($"{indent}}}");
        }

        // --- Wire count guard ---

        // A wire count is untrusted: reject a negative count, or one whose elements cannot fit in the
        // bytes left (every element writes at least MinWireBytes), before anything is allocated.
        private static void EmitCountGuard(StringBuilder sb, SerializableFieldInfo field, string indent)
        {
            string c = $"__count_{field.Name}";
            sb.AppendLine($"{indent}if ({c} < 0 || (long){c} * {GetMinWireBytes(field)} > reader.Remaining)");
            sb.AppendLine($"{indent}    throw new System.IO.InvalidDataException(\"{field.Name}: count \" + {c} + \" exceeds the \" + reader.Remaining + \" remaining bytes\");");
        }

        private static string GetMinWireBytes(SerializableFieldInfo field)
        {
            if (field.KeyTypeName != null && field.ValueTypeName != null)
                return (MinWireBytes(field.KeyTypeName) + MinWireBytes(field.ValueTypeName)).ToString();
            if (field.ElementIsNestedSerializable)
                return $"System.Math.Max(1, default({field.ElementTypeName}).GetSerializedSize())";
            return MinWireBytes(field.ElementTypeName).ToString();
        }

        // Fixed-size mapped types write exactly Size; the variable ones (string, byte[]) write a 4-byte length prefix first.
        private static int MinWireBytes(string typeName)
            => TypeMappings.TryGetMapping(typeName, out var m) && m.Size > 0 ? m.Size : 4;

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
