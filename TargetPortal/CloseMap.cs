using UnityEngine;

namespace TargetPortal;

public class CloseMap : MonoBehaviour
{
	private void OnTriggerExit(Collider other)
	{
		if (Map.Teleporting && other.GetComponent<Player>() == Player.m_localPlayer)
		{
			Minimap.instance.m_dragView = false;
			Minimap.instance.SetMapMode(Minimap.MapMode.Small);
			Map.CancelTeleport();
		}
	}
}
