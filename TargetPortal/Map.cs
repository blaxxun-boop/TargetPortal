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
	private static readonly List<FavoriteEntry> favorites = new();
	private static int favoriteCycleIndex = -1;

	private class FavoriteEntry
	{
		public Minimap.PinData Pin = null!;
		public TextMeshProUGUI Label = null!;
		public Color LabelColor;
	}

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
		// Runs before the entries are torn down below, while the labels are still alive.
		ClearFavoriteHighlight();

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

	private static bool HandlePortalClick(GetPortal getPortal)
	{
		if (!Teleporting)
		{
			return true;
		}

		if (TargetPortal.ignoreItemsTeleport.Value != TargetPortal.IgnoreItems.Always && (TargetPortal.ignoreItemsTeleport.Value == TargetPortal.IgnoreItems.Never || !PortalAllowsAllItems) && !Player.m_localPlayer.IsTeleportable(false))
		{
			Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_noteleport");
			return false;
		}

		if (!getPortal(out Minimap.PinData? closestPin, out ZDO? portalZDO))
		{
			return false;
		}

		Quaternion rotation = portalZDO!.GetRotation();

		Minimap.instance.SetMapMode(Minimap.MapMode.Small);
		CancelTeleport();

		Player.m_localPlayer.TeleportTo(closestPin!.m_pos + rotation * Vector3.forward + Vector3.up, rotation, true);
		return false;
	}

	// Gamepads have no pointer on the map: the crosshair sits fixed at the center of the screen and
	// the map pans underneath it. ZInput.pointerPosition keeps reporting the idle mouse position, so
	// vanilla targets the screen center for every gamepad map action and we have to match that.
	private static Vector3 GetMapCursorWorldPoint()
	{
		Vector3 screenPoint = ZInput.IsExclusiveGamepadActive()
			? new Vector3(Screen.width / 2f, Screen.height / 2f)
			: Input.mousePosition;
		return Minimap.instance.ScreenToWorldPoint(screenPoint);
	}

	private static bool GetClosestPortal(out Minimap.PinData? closestPin, out ZDO? portalZDO)
	{
		foreach (Minimap.PinData pinData in activePins.Keys)
		{
			pinData.m_save = true;
		}

		Minimap Minimap = Minimap.instance;
		closestPin = Minimap.GetClosestPin(GetMapCursorWorldPoint(), Minimap.m_removeRadius * (Minimap.m_largeZoom * 2f));

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
			return HandlePortalClick(GetClosestPortal);
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.RemovePinUnderPointer))]
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
		string portalIdentifier = portalZDO.GetPosition().ToString();
		if (Player.m_localPlayer.m_customData.TryGetValue("TargetPortal Favorites", out string portals))
		{
			List<string> portalList = portals.Split('|').ToList();

			if (!portalList.Remove(portalIdentifier))
			{
				portalList.Add(portalIdentifier);
			}

			Player.m_localPlayer.m_customData["TargetPortal Favorites"] = string.Join("|", portalList);
		}
		else
		{
			Player.m_localPlayer.m_customData.Add("TargetPortal Favorites", portalIdentifier);
		}
		
		FillFavorites();
	}

	// The favorite list sits outside the map pane, so the gamepad crosshair - which is locked to the
	// center of the screen and samples the map surface - can never be aimed at it. Instead of making
	// the list selectable, move the map so the favorite lands under the crosshair, which leaves the
	// existing teleport path to do the rest.
	private static void CycleToNextFavorite()
	{
		if (favorites.Count == 0)
		{
			return;
		}

		favoriteCycleIndex = (favoriteCycleIndex + 1) % favorites.Count;

		Vector3 offset = favorites[favoriteCycleIndex].Pin.m_pos - Player.m_localPlayer.transform.position;
		Minimap.instance.m_mapOffset = new Vector3(offset.x, 0f, offset.z);
		Minimap.instance.m_pinUpdateRequired = true;

		UpdateFavoriteHighlight();
	}

	// Panning by hand moves the crosshair off whatever was jumped to, so the highlight stops being true.
	private static void ClearFavoriteHighlight()
	{
		if (favoriteCycleIndex < 0)
		{
			return;
		}

		favoriteCycleIndex = -1;
		UpdateFavoriteHighlight();
	}

	private static void UpdateFavoriteHighlight()
	{
		for (int i = 0; i < favorites.Count; ++i)
		{
			favorites[i].Label.color = i == favoriteCycleIndex ? Color.yellow : favorites[i].LabelColor;
		}
	}

	// A gamepad never produces the pointer events that drive OnMapLeftClick / RemovePinUnderPointer,
	// so the portal selection has to be read off the buttons directly.
	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Update))]
	private static class GamepadPortalSelection
	{
		private static bool ButtonDown(TargetPortal.GamepadButton button) => TargetPortal.GamepadButtonName(button) is { } name && ZInput.GetButtonDown(name);

		// Mirrors the takeInput check Minimap.Update makes before handling any map input.
		private static bool TakeInput() =>
			(Chat.instance == null || !Chat.instance.HasFocus())
			&& !global::Console.IsVisible()
			&& !TextInput.IsVisible()
			&& !Menu.IsActive()
			&& !InventoryGui.IsVisible()
			&& (Hud.instance == null || !Hud.instance.m_buildUi.SearchFieldFocused);

		private static void Prefix(Minimap __instance)
		{
			if (!Teleporting || __instance.m_mode != Minimap.MapMode.Large || !ZInput.IsGamepadActive())
			{
				return;
			}

			// The pin name dialog cannot be open here, since every route into it is blocked while
			// Teleporting, so InTextInput covers the remaining text entry cases on its own.
			if (ZInput.VirtualKeyboardOpen || Minimap.InTextInput() || !TakeInput())
			{
				return;
			}

			if (ButtonDown(TargetPortal.gamepadTeleportButton.Value))
			{
				HandlePortalClick(GetClosestPortal);
			}
			else if (ButtonDown(TargetPortal.gamepadFavoriteButton.Value) && GetClosestPortal(out _, out ZDO? portalZDO))
			{
				ToggleFavoritePortal(portalZDO!);
			}
			else if (ButtonDown(TargetPortal.gamepadCycleFavoritesButton.Value))
			{
				CycleToNextFavorite();
			}
			else if (Mathf.Abs(ZInput.GetJoyLeftStickX()) > 0.1f || Mathf.Abs(ZInput.GetJoyLeftStickY()) > 0.1f)
			{
				ClearFavoriteHighlight();
			}
		}
	}

	// While a portal is being picked, the map is a portal selector and not a pin editor. The mouse
	// paths are already blocked by MapAlternativeClick, but the gamepad reaches these directly from
	// Minimap.UpdateMap: A opens the pin name dialog and RB deletes the pin under the crosshair.
	[HarmonyPatch(typeof(Minimap), nameof(Minimap.ShowPinNameInput))]
	private static class BlockPinCreationWhileTeleporting
	{
		private static bool Prefix() => !Teleporting;
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.RemovePin), typeof(Vector3), typeof(float))]
	private static class BlockPinRemovalWhileTeleporting
	{
		private static bool Prefix(ref bool __result)
		{
			__result = false;
			return !Teleporting;
		}
	}

	[HarmonyPatch]
	private class MapAlternativeClick
	{
		private static IEnumerable<MethodInfo> TargetMethods() => new[]
		{
			AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.OnMapDblClick)),
			AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.RemovePinUnderPointer)),
			AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.OnMapMiddleClick)),
		};

		private static bool Prefix()
		{
			return !Teleporting;
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Start))]
	private static class RefereshPortalPins
	{
		private static void Postfix(Minimap __instance)
		{
			IEnumerator Update()
			{
				yield return null;
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

		favorites.Clear();
		favoriteCycleIndex = -1;
	}

	private static void FillFavorites()
	{
		ClearFavorites();
		
		if (Player.m_localPlayer.m_customData.TryGetValue("TargetPortal Favorites", out string portals))
		{
			Dictionary<string, Minimap.PinData> pins = activePins.ToDictionary(p => p.Value.m_position.ToString(), p => p.Key);

			List<string> portalList = portals.Split('|').ToList();

			foreach (string portal in portalList)
			{
				if (pins.TryGetValue(portal, out Minimap.PinData pin))
				{
					GameObject favoriteEntry = Object.Instantiate(Minimap.instance.m_largeRoot.transform.Find("KeyHints/keyboard_hints/AddPin").gameObject, favoriteList.transform);
					// The template lives under the keyboard hint group, which is switched off while a
					// gamepad is in use; the clone has to be shown on its own regardless.
					favoriteEntry.SetActive(true);
					favoriteEntry.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
					Transform label = favoriteEntry.transform.Find("Label");
					label.SetAsLastSibling();
					TextMeshProUGUI labelText = label.GetComponent<TextMeshProUGUI>();
					labelText.text = pin.m_name;
					label.GetComponent<RectTransform>().pivot = new Vector2(0, 0.5f);
					favorites.Add(new FavoriteEntry { Pin = pin, Label = labelText, LabelColor = labelText.color });
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
			if ((TargetPortal.allowIconToggleWithoutMap.Value == TargetPortal.Toggle.On ? Minimap.instance.m_mode != Minimap.MapMode.None : Minimap.instance.m_mode == Minimap.MapMode.Large) && TargetPortal.mapPortalIconKey.Value.IsDown() && Player.m_localPlayer.GetComponent<PlayerController>().TakeInput())
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
			if (TargetPortal.allowNonPublicPortals.Value == TargetPortal.Toggle.Off || mode == TargetPortal.PortalMode.Public || (mode == TargetPortal.PortalMode.Admin && TargetPortal.configSync.IsAdmin) || ownerString == myId.Replace("Steam_", "") || (mode == TargetPortal.PortalMode.Group && API.GroupPlayers().Contains(PlayerReference.fromPlayerInfo(ZNet.instance.m_players.FirstOrDefault(p => p.m_userInfo.m_id.ToString().Replace("Steam_", "") == ownerString)))) || (mode == TargetPortal.PortalMode.Guild && Guilds.API.GetOwnGuild() is { } guild && guild.Members.ContainsKey(new Guilds.PlayerReference { id = !ownerString.Contains('_') ? "Steam_" + ownerString : ownerString, name = zdo.GetString("TargetPortal PortalOwnerName") })))
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
