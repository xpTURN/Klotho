### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
KLSG_ECS001 | KlothoGenerator.ECS | Error | Duplicate Component Type ID
KLSG_ECS002 | KlothoGenerator.ECS | Error | Missing IComponent
KLSG_ECS003 | KlothoGenerator.ECS | Error | Component not unmanaged
KLSG_ECS004 | KlothoGenerator.ECS | Warning | Oversized component
KLSG_ECS005 | KlothoGenerator.ECS | Error | Missing partial keyword
KLSG_DA001 | KlothoGenerator.DataAsset | Error | Missing partial keyword
KLSG_DA002 | KlothoGenerator.DataAsset | Error | Missing IDataAsset
KLSG_DA003 | KlothoGenerator.DataAsset | Error | Missing int constructor
KLSG_DA004 | KlothoGenerator.DataAsset | Warning | Ambiguous constructor
KLSG_DA005 | KlothoGenerator.DataAsset | Error | Duplicate DataAsset TypeId
KLOTHO_STRUCT_LAYOUT_MISSING | KlothoGenerator.ECS | Error | Missing [StructLayout(Sequential, Pack=4)] on KlothoComponent (IMP-25 §15 D1)
KLOTHO_INTPTR_FORBIDDEN | KlothoGenerator.ECS | Error | IntPtr/UIntPtr field forbidden in KlothoComponent (platform-variable size)
KLOTHO_EMPTY_STRUCT_SIZE | KlothoGenerator.ECS | Error | Empty KlothoComponent tag struct requires Size=1 (Mono DivideByZero guard)
KLOTHO_ENUM_UNDERLYING | KlothoGenerator.ECS | Warning | Enum field without explicit underlying type
KLOTHO_BOOL_FIELD | KlothoGenerator.ECS | Warning | bool field in KlothoComponent (cross-platform verification)
KLOTHO_CHAR_FIELD | KlothoGenerator.ECS | Warning | char field in KlothoComponent (2B Managed vs Native)
