using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace hexi.valheim.faststruct
{
    [BepInPlugin(ID, "Fast Structure Support Calculation", "0.2.0")]
    public class FastStructureSupport : BaseUnityPlugin
    {
        private const string ID = "hexi.FastStructure";

        private Harmony harmony;

        [UsedImplicitly]
        private void Awake()
        {
            harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), ID);
        }

        [UsedImplicitly]
        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
        }
    }

    public class WearNTearTracked
    {
        public WearNTear Wear;
        public float LastSupport, MaxSupport;
        public StructureData Structure;
    }

    public class StructureData
    {
        public int IdleTicks;
        public bool Sleeping;
        public readonly List<WearNTearTracked> Pieces = new List<WearNTearTracked>();
    }

    public static class WearNTearManager
    {
        private static int NumTicksBeforeSleep = 50; // needs to be high, ticking is weird
        
        public static readonly List<StructureData> Structures = new List<StructureData>();
        public static readonly List<WearNTear> Ticking = new List<WearNTear>();
        
        public static readonly List<WearNTear> AwaitingInit = new List<WearNTear>();
        public static readonly List<WearNTear> AddNow = new List<WearNTear>();
        
        public static readonly List<WearNTearTracked> MaxSupport = new List<WearNTearTracked>();

        public static WearNTearTracked GetStructure(WearNTear piece)
        {
            foreach (var structure in Structures)
            {
                var found = structure.Pieces.FirstOrDefault(p => p.Wear == piece);
                if (found != null) return found;
            }

            return null;
        }

        public static void AddEntry(WearNTear piece)
        {
            AwaitingInit.Add(piece);
        }

        public static void FullyAddEntry(WearNTear piece)
        {
            AwaitingInit.Remove(piece);
            
            // precaution: make sure the piece isnt destroyed
            if (piece == null)
            {
                Debug.Log("[FastStructure] Removing destroyed piece before it can pollute list.");
                RemoveEntry(piece);
                return;
            }
            
            piece.GetMaterialProperties(out float maxSupport, out var _, out var _, out var _);
            WearNTearTracked tracked = new WearNTearTracked
            {
                Wear = piece,
                LastSupport = piece.m_support,
                MaxSupport = maxSupport
            };
            
            List<StructureData> nearbyStructures = new List<StructureData>();
            
            // get neighbours
            foreach (var collider in piece.m_supportColliders)
            {
                WearNTear t = collider.GetComponentInParent<WearNTear>();
                if (!(t is null)) // horrid jank to avoid unity lifetime check
                {
                    WearNTearTracked structure = GetStructure(t);
                    if (structure != null && !nearbyStructures.Contains(structure.Structure)) nearbyStructures.Add(structure.Structure);
                }
            }

            if (nearbyStructures.Count == 0)
            {
                // new structure
                StructureData structure = new StructureData();
                structure.Pieces.Add(tracked);
                Structures.Add(structure);
                tracked.Structure = structure;
            }
            else if (nearbyStructures.Count == 1)
            {
                // existing structure
                nearbyStructures[0].Pieces.Add(tracked);
                nearbyStructures[0].IdleTicks = 0;
                nearbyStructures[0].Sleeping = false;
                tracked.Structure = nearbyStructures[0];
            }
            else
            {
                // connecting multiple structures
                StructureData mainStructure = nearbyStructures.First();
                nearbyStructures.RemoveAt(0);
                foreach (var structure in nearbyStructures)
                {
                    mainStructure.Pieces.AddRange(structure.Pieces);
                    Structures.Remove(structure);
                }

                mainStructure.Pieces.Add(tracked);
                mainStructure.IdleTicks = 0;
                mainStructure.Sleeping = false;
                tracked.Structure = mainStructure;
            }
        }

        public static void RemoveEntry(WearNTear piece)
        {
            AwaitingInit.Remove(piece);
            AddNow.Remove(piece);
            
            WearNTearTracked structure = GetStructure(piece);
            if (structure != null)
            {
                structure.Structure.Pieces.Remove(structure);
                structure.Structure.IdleTicks = 0;
                structure.Structure.Sleeping = false;
                if (structure.Structure.Pieces.Count == 0) Structures.Remove(structure.Structure);
            }
        }

        public static void Tick()
        {
            Ticking.Clear();
            foreach (var piece in AwaitingInit)
            {
                Ticking.Add(piece);
            }
            
            foreach (var structure in Structures)
            {
                if (structure.Sleeping)
                {
                    // make sure terrain-supported pieces always tick (should also cover other unusual cases like being supported by mineables)
                    
                    // NOTE: this will also cause pieces supported by more structurally sound pieces to always tick (wood on stone)
                    // should have minimal impact, pieces already have early-exit code when their support is unchanged
                    foreach (var piece in structure.Pieces)
                    {
                        bool contains = MaxSupport.Contains(piece);
                        if (piece.Wear.m_support == piece.MaxSupport) {
                            Ticking.Add(piece.Wear); // needs to tick to check for no longer being supported
                            if (!contains) MaxSupport.Add(piece);
                        }
                        else if (contains)
                        {
                            // max support piece no longer has max support, probably lost terrain support; building needs to wake
                            piece.LastSupport = piece.Wear.m_support;
                            structure.Sleeping = false;
                            structure.IdleTicks = 0;
                            Ticking.Add(piece.Wear);
                            MaxSupport.Remove(piece);
                        }
                    }
                }
                else {
                    foreach (var piece in structure.Pieces)
                    {
                        float diff = piece.Wear.m_support - piece.LastSupport;
                        
                        // only update prior support if it went up OR it went down at least .1%
                        // only update idle ticker if support went down
                        if (diff > 0) piece.LastSupport = piece.Wear.m_support;
                        else if (diff < -.001f)
                        {
                            piece.LastSupport = piece.Wear.m_support;
                            structure.IdleTicks = 0;
                        }
                        
                        Ticking.Add(piece.Wear);
                        
                        // needs to be here because updates dont go through all pieces
                        if (piece == structure.Pieces.Last()) structure.IdleTicks++;
                        
                        bool contains = MaxSupport.Contains(piece);
                        if (piece.LastSupport == piece.MaxSupport)
                        {
                            if (!contains) MaxSupport.Add(piece);
                        }
                        else if (contains)
                        {
                            MaxSupport.Remove(piece);
                            structure.IdleTicks = 0;
                        }
                    }

                    if (structure.IdleTicks >= NumTicksBeforeSleep) structure.Sleeping = true;
                }
            }
        }
    }

    [HarmonyPatch(typeof(WearNTearUpdater), "UpdateWearNTear")]
    public class WearNTearManagerUpdate
    {
        public static bool DidTick = false;
        public static bool Prefix(WearNTearUpdater __instance)
        {
            DidTick = false;
            WearNTearManager.Tick();
            return true;
        }

        public static void Postfix(WearNTearUpdater __instance)
        {
            foreach (var piece in WearNTearManager.AddNow) WearNTearManager.FullyAddEntry(piece);
            WearNTearManager.AddNow.Clear();
        }
    }

    [HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
    public class WearNTearUpdateSupport
    {
        public static bool Prefix(WearNTear __instance)
        {
            if (!WearNTearManager.Ticking.Contains(__instance))
            {
                if (!WearNTearManagerUpdate.DidTick)
                {
                    WearNTearManagerUpdate.DidTick = true;
                    WearNTearManager.Tick();
                    if (!WearNTearManager.Ticking.Contains(__instance)) return false;
                }
                else return false;
            }
            
            WearNTearManager.Ticking.Remove(__instance);
            if (WearNTearManager.AwaitingInit.Contains(__instance)) WearNTearManager.AddNow.Add(__instance);
            
            return true;
        }
    }

    [HarmonyPatch(typeof(WearNTear), "Awake")]
    public class WearNTearAwake
    {
        public static void Postfix(WearNTear __instance)
        {
            WearNTearManager.AddEntry(__instance);
        }
    }
    
    [HarmonyPatch(typeof(WearNTear), "OnDestroy")]
    public class WearNTearDestroy
    {
        public static bool Prefix(WearNTear __instance)
        {
            WearNTearManager.RemoveEntry(__instance);
            return true;
        }
    }
}