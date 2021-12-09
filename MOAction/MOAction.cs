﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Hooking;
using Dalamud.Plugin;
using MOAction.Configuration;
using Dalamud.Logging;

namespace MOAction
{
    public class MOAction
    {
        public delegate ulong OnRequestActionDetour(long param_1, uint param_2, ulong param_3, long param_4,
                       uint param_5, uint param_6, int param_7);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate ulong ResolvePlaceholderActor(long param1, string param2, byte param3, byte param4);
        private ResolvePlaceholderActor PlaceholderResolver;

        public delegate void OnSetUiMouseoverEntityId(long param1, long param2);

        private readonly MOActionAddressResolver Address;
        private readonly MOActionConfiguration Configuration;

        private Hook<OnRequestActionDetour> requestActionHook;
        private Hook<OnSetUiMouseoverEntityId> uiMoEntityIdHook;

        public Dictionary<uint, List<StackEntry>> Stacks { get; set; }
        private DalamudPluginInterface pluginInterface;
        private IEnumerable<Lumina.Excel.GeneratedSheets.Action> RawActions;

        public IntPtr fieldMOLocation;
        public IntPtr focusTargLocation;
        public IntPtr regularTargLocation;
        public IntPtr uiMoEntityId = IntPtr.Zero;
        public IntPtr MagicStructInfo = IntPtr.Zero;
        private IntPtr MagicUiObject;
        private HashSet<uint> UnorthodoxFriendly;
        private HashSet<uint> UnorthodoxHostile;

        public HashSet<ulong> enabledActions;

        public bool IsGuiMOEnabled = false;
        public bool IsFieldMOEnabled = false;

        public MOAction(SigScanner scanner, ClientState clientState, MOActionConfiguration configuration, ref DalamudPluginInterface plugin, IEnumerable<Lumina.Excel.GeneratedSheets.Action> rawActions)
        {
            fieldMOLocation = scanner.GetStaticAddressFromSig("E8 ?? ?? ?? ?? 83 BF ?? ?? ?? ?? ?? 0F 84 ?? ?? ?? ?? 48 8D 4C 24 ??", 0x283);
            focusTargLocation = scanner.GetStaticAddressFromSig("48 8B 0D ?? ?? ?? ?? 48 89 5C 24 ?? BB ?? ?? ?? ?? 48 89 7C 24 ??", 0);
            regularTargLocation = scanner.GetStaticAddressFromSig("F3 0F 11 05 ?? ?? ?? ?? EB 27", 0) + 0x4;
            MagicStructInfo = scanner.GetStaticAddressFromSig("48 8B 0D ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? 48 89 05 ?? ?? ?? ?? 48 85 C9 74 0C", 0);
            

            Configuration = configuration;

            Address = new MOActionAddressResolver();
            Address.Setup(scanner);

            pluginInterface = plugin;
            RawActions = rawActions;

            Stacks = new Dictionary<uint, List<StackEntry>>();

            PluginLog.Log("===== M O A C T I O N =====");
            PluginLog.Log("RequestAction address {IsIconReplaceable}", Address.RequestAction);
            PluginLog.Log("SetUiMouseoverEntityId address {SetUiMouseoverEntityId}", Address.SetUiMouseoverEntityId);

            requestActionHook = new Hook<OnRequestActionDetour>(Address.RequestAction, new OnRequestActionDetour(HandleRequestAction), this);
            uiMoEntityIdHook = new Hook<OnSetUiMouseoverEntityId>(Address.SetUiMouseoverEntityId, new OnSetUiMouseoverEntityId(HandleUiMoEntityId), this);
            PlaceholderResolver = Marshal.GetDelegateForFunctionPointer<ResolvePlaceholderActor>(Address.ResolvePlaceholderText);
            MagicUiObject = IntPtr.Zero;

            enabledActions = new HashSet<ulong>();
            UnorthodoxFriendly = new HashSet<uint>();
            UnorthodoxHostile = new HashSet<uint>();
            UnorthodoxHostile.Add(3575);
            UnorthodoxFriendly.Add(17055);
            UnorthodoxFriendly.Add(7443);
        }

        public void Enable()
        {
            requestActionHook.Enable();
            uiMoEntityIdHook.Enable();
        }

        public void Dispose()
        {
            requestActionHook.Dispose();
            uiMoEntityIdHook.Dispose();
        }

        private void HandleUiMoEntityId(long param1, long param2)
        {
            //Log.Information("UI MO: {0}", param2);
            uiMoEntityId = (IntPtr)param2;
            uiMoEntityIdHook.Original(param1, param2);
        }

        private ulong HandleRequestAction(long param_1, uint param_2, ulong param_3, long param_4,
                       uint param_5, uint param_6, int param_7)
        {
            var (action, target) = GetActionTarget((uint)param_3);
            if (action != 0 && target != 0)
                return this.requestActionHook.Original(param_1, param_2, action, target, param_5, param_6, param_7);
            return this.requestActionHook.Original(param_1, param_2, param_3, param_4, param_5, param_6, param_7);
        }

        private (uint action, uint target) GetActionTarget(uint ActionID)
        {
            if (Stacks.ContainsKey(ActionID))
            {
                List<StackEntry> stack = Stacks[ActionID];
                foreach (StackEntry t in stack)
                {
                    if (CanUseAction(t)) return (t.actionID, t.target.GetTargetActorId());
                }
            }
            return (0, 0);
        }

        private bool CanUseAction(StackEntry targ)
        {
            if (targ.target == null || targ.actionID == 0) return false;
            var action = RawActions.SingleOrDefault(row => (ulong)row.RowId == targ.actionID);
            

            for (var i = 0; i < this.pluginInterface.ClientState.Actors.Length; i++)
            {
                var a = this.pluginInterface.ClientState.Actors[i];
                if (a != null && a.ActorId == targ.target.GetTargetActorId())
                {
                    if (Configuration.RangeCheck)
                    {
                        if (UnorthodoxFriendly.Contains((uint)action.RowId))
                        {
                            if (a.YalmDistanceX > 30) return false;
                        }
                        else if ((byte)action.Range < a.YalmDistanceX) return false;
                    }
                    if (a is PlayerCharacter) return action.CanTargetFriendly || action.CanTargetParty 
                            || action.CanTargetSelf
                            || action.RowId == 17055 || action.RowId == 7443;
                    if (a is BattleNpc)
                    {
                        BattleNpc b = (BattleNpc)a;
                        if (b.BattleNpcKind != BattleNpcSubKind.Enemy) return action.CanTargetFriendly || action.CanTargetParty
                                || action.CanTargetSelf
                                || UnorthodoxFriendly.Contains((uint)action.RowId);
                    }
                    return action.CanTargetHostile || UnorthodoxHostile.Contains((uint)action.RowId);
                }
            }
            return false;
        }

        public IntPtr GetGuiMoPtr()
        {
            return uiMoEntityId;
        }
        public IntPtr GetFieldMoPtr()
        {
            return fieldMOLocation;
        }
        public IntPtr GetFocusPtr()
        {
            return Marshal.ReadIntPtr(focusTargLocation);
        }
        public IntPtr GetRegTargPtr()
        {
            return regularTargLocation;
        }

        public IntPtr GetPartyMember(int pos)
        {
            var member = pluginInterface.ClientState.PartyList[pos];
            if (member == null || member.Actor == null) return IntPtr.Zero;
            return member.Actor.Address;
        }
        public void SetupPlaceholderResolver()
        {
            while (MagicUiObject == IntPtr.Zero)
            {
                try
                {
                    IntPtr step2 = Marshal.ReadIntPtr(MagicStructInfo) + 8;
                    MagicUiObject = Marshal.ReadIntPtr(step2) + 0xe780 + 0x50;
                }
                catch(Exception e)
                {
                    MagicUiObject = IntPtr.Zero;
                    continue;
                }
            }
        }

        public IntPtr GetActorFromPlaceholder(string placeholder)
        {
            return (IntPtr)PlaceholderResolver((long)MagicUiObject, placeholder, 1, 0);
        }
    }
}