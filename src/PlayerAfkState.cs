using System;
using UnityEngine;

namespace AFKManager
{
    [Flags]
    internal enum ActivityReason
    {
        None = 0,
        Movement = 1,
        Look = 2,
        Emote = 4,
        Equipment = 8,
        CraftingStation = 16,
        EnterBed = 32
    }

    /// <summary>In-memory state of one connected session (keyed by ZNetPeer.m_uid). Never persisted.</summary>
    internal sealed class PlayerAfkState
    {
        internal long PeerUid;
        internal string PlayerName = "";

        internal bool Initialized;        // a usable player ZDO has been seen at least once
        internal bool NeedsBaseline;      // dead / loading / character missing: next usable poll only re-baselines
        internal float LastActivityTime;
        internal float LastObservedTime;  // last usable poll; the AFK timer is paused from here while dead / loading / missing
        internal bool IsAfk;

        // comparison baselines (values at the previous usable poll; look = value at last look activity)
        internal ZDOID CharacterId;
        internal ZDOID Parent;            // SyncTransform parent (ship, bed, chair) or None
        internal Vector3 LastPosition;    // world position, or parent-relative position while parented
        internal Vector3 LastWorldPosition; // always world space (only used to place the announcement chat bubble)
        internal Vector3 LookReference;
        internal bool HasLookReference;
        internal int LastEmoteId;
        internal int LastEquipmentHash;
        internal int LastCrafting;
        internal bool WasInBed;

        internal int SeenGeneration;      // disconnect reconciliation
    }
}
