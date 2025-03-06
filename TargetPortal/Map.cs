using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Groups;
using HarmonyLib;
using UnityEngine;

namespace TargetPortal;

public static class Map
{
	public static bool Teleporting;
	private static bool PortalAllowsAllItems;
	private static readonly Dictionary<Minimap.PinData, ZDO> activePins = new();
	private static bool shouldPortalsBeVisible = false;
	private static bool[]? visibleIconTypes;

	[HarmonyPatch(typeof(TeleportWorldTrigger), nameof(TeleportWorldTrigger.OnTriggerEnter))]
	private class OpenMapOnPortalEnter
	{
		private static bool Prefix(TeleportWorldTrigger __instance, Collider colliderIn)
		{
			if (colliderIn.GetComponent<Player>() != Player.m_localPlayer)
			{
				return false;
			}

			if (TargetPortal.limitToVanillaPortals.Value == TargetPortal.Toggle.On && Utils.GetPrefabName(__instance.transform.parent.gameObject) is not "portal_wood" and not "portal_stone")
			{
				return true;
			}

			bool origNoMap = Game.m_noMap;
			Game.m_noMap = false;

			PortalAllowsAllItems = __instance.m_teleportWorld.m_allowAllItems;
			Teleporting = true;
			Minimap.instance.ShowPointOnMap(__instance.transform.position);

			Game.m_noMap = origNoMap;

			if (!shouldPortalsBeVisible)
			{
				AddPortalPins();
			}

			if (InventoryGui.IsVisible())
			{
				InventoryGui.instance.Hide();
			}

			if (TargetPortal.hidePinsDuringPortal.Value == TargetPortal.Toggle.On && visibleIconTypes == null)
			{
				visibleIconTypes = new bool[Minimap.instance.m_visibleIconTypes.Length];
				Array.Copy(Minimap.instance.m_visibleIconTypes, visibleIconTypes, Minimap.instance.m_visibleIconTypes.Length);
				ToggleIconFilters(true);
			}

			return false;
		}
	}

	private static void ToggleIconFilters(bool force = false)
	{
		if (visibleIconTypes == null)
		{
			return;
		}

		HashSet<Sprite> locationSprites = new(Minimap.instance.m_locationIcons.Select(l => l.m_icon));
		HashSet<int> visiblePins = new(Minimap.instance.m_pins.Where(p => locationSprites.Contains(p.m_icon)).Select(p => (int)p.m_type))
		{
			AddMinimapPortalIcon.pinType,
		};

		if (TargetPortal.showPlayersDuringPortal.Value == TargetPortal.Toggle.On)
		{
			visiblePins.Add((int)Minimap.PinType.Player);
		}


		for (int i = 0; i < visibleIconTypes.Length; ++i)
		{
			if (visiblePins.Contains(i))
			{
				continue;
			}

			if (visibleIconTypes[i] && (!Minimap.instance.m_visibleIconTypes[i] || force))
			{
				Minimap.instance.ToggleIconFilter((Minimap.PinType)i);
			}
		}
	}

	public static void CancelTeleport()
	{
		Teleporting = false;

		if (!shouldPortalsBeVisible)
		{
			RemovePortalPins();
		}

		if (TargetPortal.hidePinsDuringPortal.Value == TargetPortal.Toggle.On)
		{
			ToggleIconFilters();
			visibleIconTypes = null;
		}
	}

	private static void HandlePortalClick()
	{
		if (TargetPortal.ignoreItemsTeleport.Value != TargetPortal.IgnoreItems.Always && (TargetPortal.ignoreItemsTeleport.Value == TargetPortal.IgnoreItems.Never || !PortalAllowsAllItems) && !Player.m_localPlayer.IsTeleportable())
		{
			Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_noteleport");
			return;
		}

		foreach (Minimap.PinData pinData in activePins.Keys)
		{
			pinData.m_save = true;
		}

		Minimap Minimap = Minimap.instance;
		Minimap.PinData? closestPin = Minimap.GetClosestPin(Minimap.ScreenToWorldPoint(Input.mousePosition), Minimap.m_removeRadius * (Minimap.m_largeZoom * 2f));

		foreach (Minimap.PinData pinData in activePins.Keys)
		{
			pinData.m_save = false;
		}

		if (closestPin is null)
		{
			return;
		}

		if (!activePins.TryGetValue(closestPin, out ZDO portalZDO))
		{
			return;
		}

		Quaternion rotation = portalZDO.GetRotation();

		Minimap.SetMapMode(Minimap.MapMode.Small);
		CancelTeleport();

		Player.m_localPlayer.TeleportTo(closestPin.m_pos + rotation * Vector3.forward + Vector3.up, rotation, true);
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.SetMapMode))]
	public class LeavePortalModeOnMapClose
	{
		private static void Postfix(Minimap.MapMode mode)
		{
			if (mode != Minimap.MapMode.Large)
			{
				CancelTeleport();
			}
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Start))]
	public class AddMinimapPortalIcon
	{
		public static int pinType;

		private static void Postfix(Minimap __instance)
		{
			pinType = __instance.m_visibleIconTypes.Length;
			bool[] visibleIcons = new bool[pinType + 1];
			Array.Copy(__instance.m_visibleIconTypes, visibleIcons, pinType);
			__instance.m_visibleIconTypes = visibleIcons;

			__instance.m_icons.Add(new Minimap.SpriteData
			{
				m_name = (Minimap.PinType)pinType,
				m_icon = TargetPortal.portalIcon,
			});
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapLeftClick))]
	private class MapLeftClick
	{
		private static bool Prefix()
		{
			if (!Teleporting)
			{
				return true;
			}
			HandlePortalClick();
			return false;
		}
	}

	[HarmonyPatch]
	private class MapAlternativeClick
	{
		private static IEnumerable<MethodInfo> TargetMethods() => new[]
		{
			AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.OnMapDblClick)),
			AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.OnMapRightClick)),
			AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.OnMapMiddleClick)),
		};

		private static bool Prefix()
		{
			return !Teleporting;
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Awake))]
	private static class RefereshPortalPins
	{
		private static void Prefix(Minimap __instance)
		{
			IEnumerator Update()
			{
				while (true)
				{
					if (shouldPortalsBeVisible && !Teleporting)
					{
						AddPortalPins();
					}
					yield return new WaitForSeconds(1);
				}
			}
			__instance.StartCoroutine(Update());
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Update))]
	private static class TogglePortalIcons
	{
		private static void Prefix(Minimap __instance)
		{
			if ((TargetPortal.allowIconToggleWithoutMap.Value == TargetPortal.Toggle.On ? Minimap.instance.m_mode != Minimap.MapMode.None : Minimap.instance.m_mode == Minimap.MapMode.Large) && TargetPortal.mapPortalIconKey.Value.IsDown())
			{
				if (!Teleporting)
				{
					if (shouldPortalsBeVisible)
					{
						RemovePortalPins();
					}
					else
					{
						AddPortalPins();
					}
				}
				shouldPortalsBeVisible = !shouldPortalsBeVisible;
			}

			if (Teleporting && TargetPortal.showPlayersDuringPortal.Value == TargetPortal.Toggle.On)
			{
				__instance.UpdatePlayerPins(Time.deltaTime);
			}
		}
	}

	private static void AddPortalPins()
	{
		HashSet<Vector3> existingPins = new(activePins.Keys.Select(p => p.m_pos));

		string? myId = UserInfo.GetLocalUser().UserId.m_userID;
        foreach (ZDO zdo in TargetPortal.knownPortals)
		{
			TargetPortal.PortalMode mode = (TargetPortal.PortalMode)zdo.GetInt("TargetPortal PortalMode");
			string ownerString = zdo.GetString("TargetPortal PortalOwnerId");
			if (TargetPortal.allowNonPublicPortals.Value == TargetPortal.Toggle.Off || mode == TargetPortal.PortalMode.Public || (mode == TargetPortal.PortalMode.Admin && TargetPortal.configSync.IsAdmin) || ownerString == myId || (mode == TargetPortal.PortalMode.Group && API.GroupPlayers().Contains(PlayerReference.fromPlayerInfo(ZNet.instance.m_players.FirstOrDefault(p => p.m_userInfo.m_id.m_userID == ownerString)))) || (mode == TargetPortal.PortalMode.Guild && Guilds.API.GetOwnGuild() is { } guild && guild.Members.ContainsKey(new Guilds.PlayerReference { id = !ownerString.Contains('_') ? "Steam_" + ownerString : ownerString, name = zdo.GetString("TargetPortal PortalOwnerName") })))
			{
				if (existingPins.Contains(zdo.m_position))
				{
					existingPins.Remove(zdo.m_position);
				}
				else
				{
					activePins.Add(Minimap.instance.AddPin(zdo.m_position, (Minimap.PinType)AddMinimapPortalIcon.pinType, zdo.GetString("tag"), false, false), zdo);
				}
			}
		}

		List<Minimap.PinData> remove = activePins.Keys.Where(p => existingPins.Contains(p.m_pos)).ToList();
		foreach (Minimap.PinData pin in remove)
		{
			Minimap.instance.RemovePin(pin);
			activePins.Remove(pin);
		}
	}

	private static void RemovePortalPins()
	{
		foreach (Minimap.PinData pinData in activePins.Keys)
		{
			Minimap.instance.RemovePin(pinData);
		}
		activePins.Clear();
	}
}
