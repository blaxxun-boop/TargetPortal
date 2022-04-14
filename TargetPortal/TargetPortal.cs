using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;

namespace TargetPortal;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class TargetPortal : BaseUnityPlugin
{
	private const string ModName = "TargetPortal";
	private const string ModVersion = "1.0.0";
	private const string ModGUID = "org.bepinex.plugins.targetportal";

	public static List<ZDO> knownPortals = new();
	public static Sprite portalIcon = null!;

	private static readonly ConfigSync configSync = new(ModName) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0
	}

	public void Awake()
	{
		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);

		portalIcon = Helper.loadSprite("portalicon.png", 64, 64);
	}

	[HarmonyPatch(typeof(Game), nameof(Game.Start))]
	public class StartPortalFetching
	{
		private static void Postfix()
		{
			knownPortals.Clear();
			Game.instance.StartCoroutine(FetchPortals());
		}
	}

	[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
	public class AddMapClosingComponent
	{
		private static void Postfix(ZNetScene __instance)
		{
			__instance.GetPrefab("portal_wood").transform.Find("TELEPORT").gameObject.AddComponent<CloseMap>();
		}
	}

	private static IEnumerator FetchPortals()
	{
		while (true)
		{
			List<ZDO> portalList = new();
			int index = 0;
			while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative("portal_wood", portalList, ref index))
			{
				yield return null;
			}

			if (ZNet.instance.IsServer())
			{
				foreach (ZDO zdo in portalList.Except(knownPortals))
				{
					ZDOMan.instance.ForceSendZDO(zdo.m_uid);
				}
			}

			knownPortals = portalList;

			yield return new WaitForSeconds(5f);
		}
		// ReSharper disable once IteratorNeverReturns
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
}
