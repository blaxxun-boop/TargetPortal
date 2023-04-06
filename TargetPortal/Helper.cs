using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TargetPortal;

public static class Helper
{
	private static byte[] ReadEmbeddedFileBytes(string name)
	{
		using MemoryStream stream = new();
		Assembly.GetExecutingAssembly().GetManifestResourceStream("TargetPortal." + name)?.CopyTo(stream);
		return stream.ToArray();
	}

	private static Texture2D loadTexture(string name)
	{
		Texture2D texture = new(0, 0);
		texture.LoadImage(ReadEmbeddedFileBytes(name));
		return texture;
	}

	public static Sprite loadSprite(string name, int width, int height) => Sprite.Create(loadTexture(name), new Rect(0, 0, width, height), Vector2.zero);

	public static bool GetAllZDOsWithPrefabIterative(this ZDOMan zdoman, List<string> prefabs, List<ZDO> zdos, ref int index)
	{
		HashSet<int> stableHashCodes = new(prefabs.Select(p => p.GetStableHashCode()));
		if (index >= zdoman.m_objectsBySector.Length)
		{
			zdos.AddRange(zdoman.m_objectsByOutsideSector.Values.SelectMany(v => v).Where(zdo => stableHashCodes.Contains(zdo.m_prefab)));
			zdos.RemoveAll(ZDOMan.InvalidZDO);
			return true;
		}
		int num = 0;
		while (index < zdoman.m_objectsBySector.Length)
		{
			if (zdoman.m_objectsBySector[index] is { } zdoList)
			{
				zdos.AddRange(zdoList.Where(zdo => stableHashCodes.Contains(zdo.m_prefab)));
				if (++num > 400)
				{
					break;
				}
			}
			++index;
		}
		return false;
	}
}
