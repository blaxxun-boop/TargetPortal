using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Groups;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace TargetPortal;

public static class Map
{
	public static bool Teleporting;
	private static bool PortalAllowsAllItems;
	private static readonly Dictionary<Minimap.PinData, ZDO> activePins = new();
	private static bool shouldPortalsBeVisible = false;
	private static bool[]? visibleIconTypes;
	private static GameObject favoriteList = null!;

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

	delegate bool GetPortal(out Minimap.PinData? closestPin, out ZDO? portalZDO);

	private static void HandlePortalClick(GetPortal getPortal)
	{
		if (TargetPortal.ignoreItemsTeleport.Value != TargetPortal.IgnoreItems.Always && (TargetPortal.ignoreItemsTeleport.Value == TargetPortal.IgnoreItems.Never || !PortalAllowsAllItems) && !Player.m_localPlayer.IsTeleportable())
		{
			Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_noteleport");
			return;
		}

		if (!getPortal(out Minimap.PinData? closestPin, out ZDO? portalZDO))
		{
			return;
		}

		Quaternion rotation = portalZDO!.GetRotation();

		Minimap.instance.SetMapMode(Minimap.MapMode.Small);
		CancelTeleport();

		Player.m_localPlayer.TeleportTo(closestPin!.m_pos + rotation * Vector3.forward + Vector3.up, rotation, true);
	}

	private static bool GetClosestPortal(out Minimap.PinData? closestPin, out ZDO? portalZDO)
	{
		foreach (Minimap.PinData pinData in activePins.Keys)
		{
			pinData.m_save = true;
		}

		Minimap Minimap = Minimap.instance;
		closestPin = Minimap.GetClosestPin(Minimap.ScreenToWorldPoint(Input.mousePosition), Minimap.m_removeRadius * (Minimap.m_largeZoom * 2f));

		foreach (Minimap.PinData pinData in activePins.Keys)
		{
			pinData.m_save = false;
		}

		if (closestPin is null)
		{
			portalZDO = null;
			return false;
		}

		return activePins.TryGetValue(closestPin, out portalZDO);
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
			HandlePortalClick(GetClosestPortal);
			return false;
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapRightClick))]
	private class MapRightClick
	{
		private static void Prefix()
		{
			if (!GetClosestPortal(out _, out ZDO? portalZDO))
			{
				return;
			}
			ToggleFavoritePortal(portalZDO!);
		}
	}

	private static void ToggleFavoritePortal(ZDO portalZDO)
	{
		if (Player.m_localPlayer.m_customData.TryGetValue("TargetPortal Favorites", out string portals))
		{
			List<string> portalList = portals.Split(',').ToList();

			if (!portalList.Remove(portalZDO.m_uid.ToString()))
			{
				portalList.Add(portalZDO.m_uid.ToString());
			}

			Player.m_localPlayer.m_customData["TargetPortal Favorites"] = string.Join(",", portalList);
		}
		else
		{
			Player.m_localPlayer.m_customData.Add("TargetPortal Favorites", portalZDO.m_uid.ToString());
		}
		
		FillFavorites();
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
		private static void Postfix(Minimap __instance)
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

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Awake))]
	private static class AddFavoritePins
	{
		private static void Postfix(Minimap __instance)
		{
			favoriteList = new GameObject("TargetPortal Favorites")
			{
				transform =
				{
					parent = __instance.m_largeRoot.transform,
				},
			};

			RectTransform rect = favoriteList.AddComponent<RectTransform>();
			rect.anchorMin = new Vector2(0, 0.5f);
			rect.anchorMax = new Vector2(0, 0.5f);
			rect.anchoredPosition = new Vector2(15, 0);
			rect.sizeDelta = new Vector2(200, 500);
			rect.pivot = new Vector2(0, 0.5f);
			favoriteList.AddComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
		}
	}

	private static void ClearFavorites()
	{
		for (int i = 0; i < favoriteList.transform.childCount; ++i)
		{
			Object.Destroy(favoriteList.transform.GetChild(i).gameObject);
		}
	}

	private static void FillFavorites()
	{
		ClearFavorites();
		
		if (Player.m_localPlayer.m_customData.TryGetValue("TargetPortal Favorites", out string portals))
		{
			Dictionary<string, Minimap.PinData> pins = activePins.ToDictionary(p => p.Value.m_uid.ToString(), p => p.Key);

			List<string> portalList = portals.Split(',').ToList();

			foreach (string portal in portalList)
			{
				if (pins.TryGetValue(portal, out Minimap.PinData pin))
				{
					GameObject favoriteEntry = Object.Instantiate(Minimap.instance.m_largeRoot.transform.Find("KeyHints/keyboard_hints/AddPin").gameObject, favoriteList.transform);
					favoriteEntry.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
					Transform label = favoriteEntry.transform.Find("Label");
					label.SetAsLastSibling();
					label.GetComponent<TextMeshProUGUI>().text = pin.m_name;
					label.GetComponent<RectTransform>().pivot = new Vector2(0, 0.5f);
					Image portalIcon = favoriteEntry.transform.Find("keyboard_hint").GetComponent<Image>();
					portalIcon.sprite = pin.m_icon;
					portalIcon.gameObject.AddComponent<FavoriteClicked>().Pin = pin;
				}
			}
		}
	}

	private class FavoriteClicked : MonoBehaviour, IPointerClickHandler
	{
		public Minimap.PinData Pin = null!;

		public void OnPointerClick(PointerEventData pointerEventData)
		{
			if (pointerEventData.button == PointerEventData.InputButton.Left)
			{
				HandlePortalClick((out Minimap.PinData? pin, out ZDO? zdo) =>
				{
					pin = Pin;
					return activePins.TryGetValue(pin, out zdo);
				});
			}
			else if (pointerEventData.button == PointerEventData.InputButton.Right)
			{
				if (activePins.TryGetValue(Pin, out ZDO zdo))
				{
					ToggleFavoritePortal(zdo);
				}
			}
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
		bool changedPins = false;
		HashSet<Vector3> existingPins = new(activePins.Keys.Select(p => p.m_pos));

		string myId = UserInfo.GetLocalUser().UserId.ToString();
		foreach (ZDO zdo in TargetPortal.knownPortals)
		{
			TargetPortal.PortalMode mode = (TargetPortal.PortalMode)zdo.GetInt("TargetPortal PortalMode");
			string ownerString = zdo.GetString("TargetPortal PortalOwnerId");
			if (TargetPortal.allowNonPublicPortals.Value == TargetPortal.Toggle.Off || mode == TargetPortal.PortalMode.Public || (mode == TargetPortal.PortalMode.Admin && TargetPortal.configSync.IsAdmin) || ownerString == myId.Replace("Steam_", "") || (mode == TargetPortal.PortalMode.Group && API.GroupPlayers().Contains(PlayerReference.fromPlayerInfo(ZNet.instance.m_players.FirstOrDefault(p => p.m_userInfo.m_id.ToString() == ownerString)))) || (mode == TargetPortal.PortalMode.Guild && Guilds.API.GetOwnGuild() is { } guild && guild.Members.ContainsKey(new Guilds.PlayerReference { id = !ownerString.Contains('_') ? "Steam_" + ownerString : ownerString, name = zdo.GetString("TargetPortal PortalOwnerName") })))
			{
				if (existingPins.Contains(zdo.m_position))
				{
					existingPins.Remove(zdo.m_position);
				}
				else
				{
					activePins.Add(Minimap.instance.AddPin(zdo.m_position, (Minimap.PinType)AddMinimapPortalIcon.pinType, zdo.GetString("tag"), false, false), zdo);
					changedPins = true;
				}
			}
		}

		List<Minimap.PinData> remove = activePins.Keys.Where(p => existingPins.Contains(p.m_pos)).ToList();
		foreach (Minimap.PinData pin in remove)
		{
			Minimap.instance.RemovePin(pin);
			activePins.Remove(pin);
			changedPins = true;
		}

		if (changedPins)
		{
			FillFavorites();
		}
	}

	private static void RemovePortalPins()
	{
		foreach (Minimap.PinData pinData in activePins.Keys)
		{
			Minimap.instance.RemovePin(pinData);
		}
		activePins.Clear();
		
		ClearFavorites();
	}
}
