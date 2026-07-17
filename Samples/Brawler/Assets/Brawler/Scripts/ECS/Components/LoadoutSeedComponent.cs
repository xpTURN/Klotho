using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    // Per-player tick-0 entitlement loadout seed.
    //
    // Written in OnInitializeWorld (before SaveSnapshot(0)) from each peer's independently-verified
    // entitlement (engine.GetPlayerEntitlement). Because every peer holds the same lobby-signed bytes and
    // the decode is a deterministic pure function, the seeded masks are identical across peers — so the
    // tick-0 snapshot matches and no host authority is needed. HandleSpawn copies the per-class slice into
    // the spawned CharacterComponent; the seed entity itself rides the snapshot, so late-joiners adopt it
    // via full-state.
    //
    // Components must be unmanaged structs, so ownership is carried as fixed-width bitmasks, never byte[].
    [KlothoComponent(107, MaxCount = 16)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct LoadoutSeedComponent : IComponent
    {
        public int PlayerId;
        // Acquired skills across all classes: bit (classIdx*2 + slot). HandleSpawn extracts the 2 bits
        // for the player's chosen class. 4 classes * 2 slots = 8 bits.
        public int OwnedSkillMask;
        // Owned consumables: consumable-id offset -> bit (id 100 -> bit0).
        public int OwnedConsumableMask;
    }
}
