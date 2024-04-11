using System;
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
	private static readonly Dictionary<Minimap.PinData, ZDO> activePins = new();
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

			if (TargetPortal.limitToVanillaPortals.Value == TargetPortal.Toggle.On && Utils.GetPrefabName(__instance.transform.parent.gameObject) != "portal_wood")
			{
				return true;
			}

			Teleporting = true;
			Minimap.instance.ShowPointOnMap(__instance.transform.position);

			AddPortalPins();

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
		HashSet<int> locationPins = new(Minimap.instance.m_pins.Where(p => locationSprites.Contains(p.m_icon)).Select(p => (int)p.m_type))
		{
			AddMinimapPortalIcon.pinType,
		};

		for (int i = 0; i < visibleIconTypes.Length; ++i)
		{
			if (locationPins.Contains(i))
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

		RemovePortalPins();

		if (TargetPortal.hidePinsDuringPortal.Value == TargetPortal.Toggle.On)
		{
			ToggleIconFilters();
			visibleIconTypes = null;
		}
	}

	private static void HandlePortalClick()
	{
		if (!Player.m_localPlayer.IsTeleportable())
		{
			Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_noteleport");
			return;
		}

		foreach (Minimap.PinData pinData in activePins.Keys)
		{
			pinData.m_save = true;
		}

		Minimap Minimap = Minimap.instance;
		Vector3 ScreenToWorldPoint = Minimap.ScreenToWorldPoint(Input.mousePosition);

        Minimap.PinData? closestPin = Minimap.GetClosestPin(ScreenToWorldPoint, Minimap.m_removeRadius * (Minimap.m_largeZoom * 2f));

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

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Update))]
	private static class ShowPortalIcons
	{
		private static void Prefix()
		{
			if (Minimap.instance.m_mode == Minimap.MapMode.Large && TargetPortal.mapPortalIconKey.Value.IsDown())
			{
				if (activePins.Count == 0)
				{
					AddPortalPins();
				}
				else
				{
					RemovePortalPins();
				}
			}
		}
	}

	private static void AddPortalPins()
	{
		string? myId = PrivilegeManager.GetNetworkUserId();
		foreach (ZDO zdo in TargetPortal.knownPortals)
		{
			TargetPortal.PortalMode mode = (TargetPortal.PortalMode)zdo.GetInt("TargetPortal PortalMode");
			string ownerString = zdo.GetString("TargetPortal PortalOwnerId");
			if (TargetPortal.allowNonPublicPortals.Value == TargetPortal.Toggle.Off || mode == TargetPortal.PortalMode.Public || (mode == TargetPortal.PortalMode.Admin && TargetPortal.configSync.IsAdmin) || ownerString == myId.Replace("Steam_", "") || (mode == TargetPortal.PortalMode.Group && API.GroupPlayers().Contains(PlayerReference.fromPlayerInfo(ZNet.instance.m_players.FirstOrDefault(p => p.m_host == ownerString)))) || (mode == TargetPortal.PortalMode.Guild && Guilds.API.GetOwnGuild() is { } guild && guild.Members.ContainsKey(new Guilds.PlayerReference { id = !ownerString.Contains('_') ? "Steam_" + ownerString : ownerString, name = zdo.GetString("TargetPortal PortalOwnerName") })))
			{
				activePins.Add(Minimap.instance.AddPin(zdo.m_position, (Minimap.PinType)AddMinimapPortalIcon.pinType, zdo.GetString("tag"), false, false), zdo);
			}
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
