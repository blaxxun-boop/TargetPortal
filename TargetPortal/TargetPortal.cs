﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using Groups;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;

namespace TargetPortal;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
[BepInDependency("org.bepinex.plugins.groups", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("org.bepinex.plugins.guilds", BepInDependency.DependencyFlags.SoftDependency)]
public class TargetPortal : BaseUnityPlugin
{
	private const string ModName = "TargetPortal";
	private const string ModVersion = "1.1.21";
	private const string ModGUID = "org.bepinex.plugins.targetportal";

	public static HashSet<ZDO> knownPortals = new();
	public static Sprite portalIcon = null!;

	public static readonly ConfigSync configSync = new(ModName) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	public static ConfigEntry<Toggle> allowNonPublicPortals = null!;
	public static ConfigEntry<Toggle> limitToVanillaPortals = null!;
	public static ConfigEntry<Toggle> hidePinsDuringPortal = null!;
	public static ConfigEntry<Toggle> showPlayersDuringPortal = null!;
	private static ConfigEntry<Toggle> portalAnimation = null!;
	private static ConfigEntry<int> portalNameLength = null!;
	private static ConfigEntry<int> maximumNumberOfPortals = null!;
	public static ConfigEntry<IgnoreItems> ignoreItemsTeleport = null!;
	private static ConfigEntry<KeyboardShortcut> portalModeToggleModifierKey = null!;
	public static ConfigEntry<KeyboardShortcut> mapPortalIconKey = null!;
	private static ConfigEntry<PortalMode> defaultPortalMode = null!;
	public static ConfigEntry<Toggle> allowIconToggleWithoutMap = null!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	public enum Toggle
	{
		On = 1,
		Off = 0,
	}

	public enum PortalMode
	{
		Public = 0,
		Private = 1,
		Group = 2,
		Admin = 3,
		Guild = 4,
	}

	public enum IgnoreItems
	{
		Never,
		Default,
		Always,
	}

	public void Awake()
	{
		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		allowNonPublicPortals = config("1 - General", "Allow non public portals", Toggle.On, "If on, players can set their portals to private, group (requires Groups) or guild (requires Guilds).");
		limitToVanillaPortals = config("1 - General", "Limit to vanilla portals", Toggle.Off, "If on, the mod ignores non-vanilla portals.");
		portalModeToggleModifierKey = config("1 - General", "Modifier key for toggle", new KeyboardShortcut(KeyCode.LeftShift), "Modifier key that has to be pressed while interacting with a portal, to toggle its mode.", false);
		hidePinsDuringPortal = config("1 - General", "Hide map pins", Toggle.On, "If on, all map pins will be hidden on the map that lets you select a target portal.", false);
		showPlayersDuringPortal = config("1 - General", "Show player pins", Toggle.On, "If on, all player map pins will be shown on the map that lets you select a target portal.", false);
		portalAnimation = config("1 - General", "Portal Animation", Toggle.On, "If on, portals will display their whirling animation while a player is infront of them.", false);
		mapPortalIconKey = config("1 - General", "Hotkey map icons", new KeyboardShortcut(KeyCode.P), "Hotkey to press while the map is open to toggle portal icons.", false);
		portalNameLength = config("1 - General", "Maximum length for portal names", 10, new ConfigDescription("Maximum length for portal names.", new AcceptableValueRange<int>(5, 100)));
		maximumNumberOfPortals = config("1 - General", "Maximum number of portals", 0, new ConfigDescription("Sets the maximum number of portals allowed in the world. Use 0 for no limit."));
		ignoreItemsTeleport = config("1 - General", "Ignore item teleport restrictions", IgnoreItems.Default, new ConfigDescription("Never: Do not allow teleportation of restricted items.\nDefault: Keep vanilla behavior for portals.\nAlways: Ignore item restrictions on portals."));
		defaultPortalMode = config("1 - General", "Default Portal mode", PortalMode.Private, new ConfigDescription("Sets the default mode for newly built portals."), false);
		allowIconToggleWithoutMap = config("1 - General", "Allow Icon toggle map closed", Toggle.Off, new ConfigDescription("If on, the portal icons can be toggled on and off with the hotkey, even if the map is not opened."), false);

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);

		portalIcon = Helper.loadSprite("portalicon.png", 64, 64);
		portalIcon.name = "TargetPortalIcon";
	}

	[HarmonyPatch(typeof(Game), nameof(Game.Start))]
	public class StartPortalFetching
	{
		private static void Postfix()
		{
			knownPortals.Clear();
			Game.instance.StartCoroutine(FetchPortals());
			ZRoutedRpc.instance.Register<ZDOID, int, string, string>("TargetPortals ChangePortalMode", OnPortalModeChange);
		}

		private static void OnPortalModeChange(long sender, ZDOID portalId, int portalMode, string ownerId, string ownerName)
		{
			if (ZDOMan.instance.m_objectsByID.TryGetValue(portalId, out ZDO zdo))
			{
				SetPortalMode(zdo, portalMode, ownerId, ownerName);
			}
		}
	}

	private static void SetPortalMode(ZDO zdo, int portalMode, string ownerId, string ownerName)
	{
		zdo.Set("TargetPortal PortalMode", portalMode);
		zdo.Set("TargetPortal PortalOwnerId", ownerId);
		zdo.Set("TargetPortal PortalOwnerName", ownerName);
	}

	[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
	public class AddMapClosingComponent
	{
		[HarmonyPriority(Priority.Last)]
		private static void Postfix(ZNetScene __instance)
		{
			foreach (GameObject portal in __instance.m_prefabs.Where(p => p.GetComponent<TeleportWorld>()))
			{
				portal.transform.Find("TELEPORT")?.gameObject.AddComponent<CloseMap>();
			}
		}
	}

	private static readonly HashSet<int> vanillaPortalPrefabs = new() { "portal_wood".GetStableHashCode(), "portal_stone".GetStableHashCode() };

	private static IEnumerator FetchPortals()
	{
		while (true)
		{
			List<ZDO> portalList = ZDOMan.instance.GetPortals();
			HashSet<ZDO> foundPortals = limitToVanillaPortals.Value == Toggle.On ? new HashSet<ZDO>(portalList.Where(z => vanillaPortalPrefabs.Contains(z.m_prefab))) : new HashSet<ZDO>(portalList);

			if (ZNet.instance.IsServer())
			{
				foreach (ZDO zdo in foundPortals.Except(knownPortals))
				{
					ZDOMan.instance.ForceSendZDO(zdo.m_uid);
				}
			}

			knownPortals = foundPortals;

			yield return new WaitForSeconds(5f);
		}
		// ReSharper disable once IteratorNeverReturns
	}

	[HarmonyPatch(typeof(Game), nameof(Game.ConnectPortals))]
	private static class SkipPortalConnecting
	{
		private static readonly MethodInfo PortalGetter = AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.GetPortals));

		private static List<ZDO> FilterPortals(List<ZDO> portals)
		{
			List<ZDO> filtered = new();
			if (limitToVanillaPortals.Value == Toggle.On)
			{
				filtered.AddRange(portals.Where(z => !vanillaPortalPrefabs.Contains(z.m_prefab)));
			}
			return filtered;
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				yield return instruction;
				if (instruction.Calls(PortalGetter))
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(SkipPortalConnecting), nameof(FilterPortals)));
				}
			}
		}
	}

	[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddPeer))]
	public static class SendKnownPortalsOnConnect
	{
		[UsedImplicitly]
		private static void Postfix(ZDOMan __instance, ZNetPeer netPeer)
		{
			if (ZNet.instance.IsServer())
			{
				foreach (ZDO zdo in knownPortals)
				{
					__instance.ForceSendZDO(netPeer.m_uid, zdo.m_uid);
				}
			}
		}
	}

	[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.HaveTarget))]
	public static class SetPortalsConnected
	{
		private static bool Prefix(out bool __result)
		{
			__result = true;

			return false;
		}
	}

	[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.GetHoverText))]
	private class OverrideHoverText
	{
		public static void Postfix(TeleportWorld __instance, ref string __result)
		{
			if (portalModeToggleModifierKey.Value.MainKey is KeyCode.None || allowNonPublicPortals.Value == Toggle.Off || (limitToVanillaPortals.Value == Toggle.On && Utils.GetPrefabName(__instance.gameObject) is not "portal_wood" and not "portal_stone"))
			{
				return;
			}

			PortalMode mode = (PortalMode)__instance.m_nview.GetZDO().GetInt("TargetPortal PortalMode");
			if ((mode == PortalMode.Group && !API.IsLoaded()) || (mode == PortalMode.Guild && !Guilds.API.IsLoaded()))
			{
				mode = PortalMode.Private;
			}

			__result = __result.Replace(Localization.instance.Localize("$piece_portal_connected"), mode + (mode is PortalMode.Public or PortalMode.Admin ? "" : $" (Owner: {__instance.m_nview.GetZDO().GetString("TargetPortal PortalOwnerName")})")) + $"\n[<b><color=yellow>{portalModeToggleModifierKey.Value}</color> + <color=yellow>{Localization.instance.Localize("$KEY_Use")}</color></b>] Toggle Mode";
		}
	}

	private static int SquashPortalMode(int mode)
	{
		if (mode == (int)PortalMode.Group && !API.IsLoaded())
		{
			++mode;
		}
		if (mode == (int)PortalMode.Admin && !configSync.IsAdmin)
		{
			++mode;
		}
		if (mode == (int)PortalMode.Guild && !Guilds.API.IsLoaded())
		{
			++mode;
		}
		if (mode > (int)PortalMode.Guild)
		{
			mode = (int)PortalMode.Public;
		}
		return mode;
	}

	[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Awake))]
	private static class SetInitialPortalMode
	{
		private static void Prefix(TeleportWorld __instance)
		{
			if (__instance.GetComponent<Piece>() is { } piece && piece.m_nview.GetZDO() is { } zdo && !piece.IsPlacedByPlayer() && zdo.GetInt("TargetPortal PortalMode", -1) == -1)
			{
				SetPortalMode(zdo, SquashPortalMode((int)defaultPortalMode.Value), UserInfo.GetLocalUser().UserId.ToString().Replace("Steam_", ""), Player.m_localPlayer.GetHoverName());
			}
		}
	}

	[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Interact))]
	private class TogglePortalMode
	{
		public static bool Prefix(TeleportWorld __instance, bool hold)
		{
			if (hold || allowNonPublicPortals.Value == Toggle.Off || (limitToVanillaPortals.Value == Toggle.On && Utils.GetPrefabName(__instance.gameObject) is not "portal_wood" and not "portal_stone"))
			{
				return true;
			}

			if (Input.GetKey(portalModeToggleModifierKey.Value.MainKey) && portalModeToggleModifierKey.Value.Modifiers.All(Input.GetKey))
			{
				int mode = __instance.m_nview.GetZDO().GetInt("TargetPortal PortalMode");
				++mode;
				mode = SquashPortalMode(mode);

				ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "TargetPortals ChangePortalMode", __instance.m_nview.GetZDO().m_uid, mode, UserInfo.GetLocalUser().UserId.ToString().Replace("Steam_", ""), Player.m_localPlayer.GetHoverName());

				return false;
			}

			return true;
		}
	}

	[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Interact))]
	private class EditPortalTagCharacterLimit
	{
		private static int CharacterLimit() => portalNameLength.Value;

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructionList)
		{
			List<CodeInstruction> instructions = instructionList.ToList();
			for (int i = 0; i < instructions.Count; ++i)
			{
				if (instructions[i].opcode == OpCodes.Ldc_I4_S && instructions[i + 1].Calls(AccessTools.Method(typeof(TextInput), nameof(TextInput.RequestText))))
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EditPortalTagCharacterLimit), nameof(CharacterLimit)));
				}
				else
				{
					yield return instructions[i];
				}
			}
		}
	}

	[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.HaveTarget))]
	private static class ControlPortalAnimationHaveTarget
	{
		private static bool Prefix(out bool __result)
		{
			__result = true;
			return false;
		}
	}

	[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.TargetFound))]
	private static class ControlPortalAnimationTargetFound
	{
		private static bool Prefix(out bool __result)
		{
			__result = portalAnimation.Value == Toggle.On;
			return false;
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
	private static class PreventBuildingOverLimit
	{
		private static bool Prefix(Player __instance)
		{
			if (maximumNumberOfPortals.Value > 0 && knownPortals.Count >= maximumNumberOfPortals.Value)
			{
				__instance.Message(MessageHud.MessageType.Center, $"You cannot place more than {maximumNumberOfPortals.Value} portals in this world.");
				return false;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Awake))]
	private static class AddNewPortalToList
	{
		private static void Postfix(TeleportWorld __instance)
		{
			if (__instance.GetComponent<WearNTear>() is { } wear && !ZNetView.m_forceDisableInit && (limitToVanillaPortals.Value == Toggle.Off || Utils.GetPrefabName(__instance.gameObject) is "portal_wood" or "portal_stone"))
			{
				knownPortals.Add(__instance.m_nview.GetZDO());
				Action? onDestroy = wear.m_onDestroyed;
				wear.m_onDestroyed = () =>
				{
					onDestroy?.Invoke();
					knownPortals.Remove(__instance.m_nview.GetZDO());
				};
			}
		}
	}
}
