using System.Collections.Generic;
using HarmonyLib;

namespace TargetPortal;

public static class ZDODataBuffer
{
	private static readonly List<ZPackage> packageBuffer = new();

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
	private class StartBufferingOnNewConnection
	{
		private static void Postfix(ZNet __instance, ZNetPeer peer)
		{
			if (!__instance.IsServer())
			{
				peer.m_rpc.Register<ZPackage>("ZDOData", (_, package) => packageBuffer.Add(package));
			}
		}
	}

	[HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
	private class ClearPackageBufferOnShutdown
	{
		private static void Postfix() => packageBuffer.Clear();
	}

	[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddPeer))]
	private class EvaluateBufferedPackages
	{
		private static void Postfix(ZDOMan __instance, ZNetPeer netPeer)
		{
			foreach (ZPackage package in packageBuffer)
			{
				__instance.RPC_ZDOData(netPeer.m_rpc, package);
			}
			packageBuffer.Clear();
		}
	}
}
