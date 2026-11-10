#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Карта проекта Zero Tolerance: лампы карты лежат файлами в wad-src/<КАРТА>/ рабочей копии git (порядок и имена —
	// wad-src/wad_structure.json, тот же манифест, что у build-wad.py). Для UDB из них собирается временный WAD;
	// после сохранения лампы раскладываются обратно в файлы.
	internal sealed class ProjectMap
	{
		#region ================== Constants

		public const string MANIFEST = "wad-src/wad_structure.json";

		#endregion

		#region ================== Types

		// Ламп карты в манифесте: TEXTMAP/DIALOGUE — файл; SCRIPTS — исходник (лампы SCRIPTS) и скомпилированный BEHAVIOR
		private sealed class Entry
		{
			public string Name;
			public string File;       // для обычных лампов
			public string Source;     // SCRIPTS: .acs
			public string Behavior;   // SCRIPTS: BEHAVIOR.lmp
		}

		#endregion

		#region ================== Variables

		private readonly List<Entry> entries = new List<Entry>();

		#endregion

		#region ================== Properties

		public string ProjectDir { get; private set; }
		public string MapName { get; private set; }
		public string WadPath { get; private set; }

		// Папка карты в рабочей копии относительно её корня, например wad-src/MAP01/
		public string MapFolder { get { return "wad-src/" + MapName + "/"; } }

		#endregion

		#region ================== Constructor

		private ProjectMap() { }

		#endregion

		#region ================== Manifest

		public static bool IsProjectDir(string dir)
		{
			return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, MANIFEST));
		}

		private static List<Dictionary<string, object>> ReadManifest(string projectdir)
		{
			string json = File.ReadAllText(Path.Combine(projectdir, MANIFEST), Encoding.UTF8);
			JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
			return js.Deserialize<List<Dictionary<string, object>>>(json);
		}

		private static string Str(Dictionary<string, object> d, string key)
		{
			object v;
			return d.TryGetValue(key, out v) && v != null ? v.ToString() : null;
		}

		// Карты проекта в порядке манифеста
		public static List<string> ListMaps(string projectdir)
		{
			return ReadManifest(projectdir).Where(e => Str(e, "kind") == "map").Select(e => Str(e, "name")).ToList();
		}

		public static ProjectMap Load(string projectdir, string mapname)
		{
			ProjectMap pm = new ProjectMap();
			pm.ProjectDir = Path.GetFullPath(projectdir);
			pm.MapName = mapname.ToUpperInvariant();
			foreach(Dictionary<string, object> e in ReadManifest(projectdir))
			{
				if(Str(e, "kind") != "lump" || !string.Equals(Str(e, "map"), pm.MapName, StringComparison.OrdinalIgnoreCase)) continue;
				pm.entries.Add(new Entry { Name = Str(e, "name"), File = Str(e, "file"), Source = Str(e, "src"), Behavior = Str(e, "behavior") });
			}
			if(pm.entries.Count == 0) throw new InvalidDataException("В " + MANIFEST + " нет лампов карты " + pm.MapName);

			// Временный WAD — вне рабочей копии (там же UDB хранит .dbs с настройками карты)
			string key = Path.GetFileName(pm.ProjectDir.TrimEnd('\\', '/')) + "-" + ShortHash(pm.ProjectDir.ToLowerInvariant());
			string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZTWorkbench", key);
			Directory.CreateDirectory(dir);
			pm.WadPath = Path.Combine(dir, pm.MapName + ".wad");
			return pm;
		}

		private static string ShortHash(string s)
		{
			using(SHA1 sha = SHA1.Create())
			{
				byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
				return BitConverter.ToString(h, 0, 4).Replace("-", "").ToLowerInvariant();
			}
		}

		private string Abs(string wadsrcrelative)
		{
			return Path.Combine(ProjectDir, "wad-src", wadsrcrelative.Replace('/', Path.DirectorySeparatorChar));
		}

		#endregion

		#region ================== WAD

		// Лампы карты из файлов wad-src/ в порядке build-wad.py (без маркера карты и ENDMAP)
		public List<KeyValuePair<string, byte[]>> ReadLumps()
		{
			List<KeyValuePair<string, byte[]>> lumps = new List<KeyValuePair<string, byte[]>>();
			foreach(Entry e in entries)
			{
				if(e.Name == "SCRIPTS")
				{
					string beh = e.Behavior != null ? Abs(e.Behavior) : null;
					string src = e.Source != null ? Abs(e.Source) : null;
					lumps.Add(new KeyValuePair<string, byte[]>("BEHAVIOR", beh != null && File.Exists(beh) ? File.ReadAllBytes(beh) : new byte[0]));
					lumps.Add(new KeyValuePair<string, byte[]>("SCRIPTS", src != null && File.Exists(src) ? File.ReadAllBytes(src) : new byte[0]));
				}
				else if(e.File != null)
				{
					lumps.Add(new KeyValuePair<string, byte[]>(e.Name, File.ReadAllBytes(Abs(e.File))));
				}
			}
			return lumps;
		}

		// Собирает временный WAD карты из файлов wad-src/
		public void BuildWad()
		{
			List<KeyValuePair<string, byte[]>> lumps = new List<KeyValuePair<string, byte[]>>();
			lumps.Add(new KeyValuePair<string, byte[]>(MapName, new byte[0]));
			lumps.AddRange(ReadLumps());
			lumps.Add(new KeyValuePair<string, byte[]>("ENDMAP", new byte[0]));
			WadFile.Write(WadPath, lumps);
		}

		// Раскладывает лампы карты из временного WAD обратно в файлы wad-src/. Пишет только изменившиеся файлы.
		// Возвращает пути изменённых файлов относительно корня рабочей копии.
		public List<string> ExportFromWad()
		{
			List<KeyValuePair<string, byte[]>> all = WadFile.Read(WadPath);
			int start = all.FindIndex(l => string.Equals(l.Key, MapName, StringComparison.OrdinalIgnoreCase));
			if(start < 0) throw new InvalidDataException("Во временном WAD нет карты " + MapName);

			Dictionary<string, byte[]> lumps = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
			for(int i = start + 1; i < all.Count; i++)
			{
				if(string.Equals(all[i].Key, "ENDMAP", StringComparison.OrdinalIgnoreCase)) break;
				if(!lumps.ContainsKey(all[i].Key)) lumps[all[i].Key] = all[i].Value;
			}

			List<string> changed = new List<string>();
			foreach(Entry e in entries)
			{
				byte[] data;
				if(e.Name == "SCRIPTS")
				{
					if(e.Source != null && lumps.TryGetValue("SCRIPTS", out data) && WriteIfChanged(Abs(e.Source), data)) changed.Add("wad-src/" + e.Source);
					if(e.Behavior != null && lumps.TryGetValue("BEHAVIOR", out data) && data.Length > 0 && WriteIfChanged(Abs(e.Behavior), data)) changed.Add("wad-src/" + e.Behavior);
				}
				else if(e.File != null && lumps.TryGetValue(e.Name, out data))
				{
					if(WriteIfChanged(Abs(e.File), data)) changed.Add("wad-src/" + e.File);
				}
			}
			return changed;
		}

		private static bool WriteIfChanged(string path, byte[] data)
		{
			if(File.Exists(path))
			{
				byte[] old = File.ReadAllBytes(path);
				if(old.Length == data.Length && old.SequenceEqual(data)) return false;
			}
			File.WriteAllBytes(path, data);
			return true;
		}

		// Затронута ли карта списком изменённых файлов (пути относительно корня рабочей копии, прямые слеши)
		public bool IsTouchedBy(IEnumerable<string> changedpaths)
		{
			return changedpaths.Any(p => p.StartsWith(MapFolder, StringComparison.OrdinalIgnoreCase) ||
			                             string.Equals(p, MANIFEST, StringComparison.OrdinalIgnoreCase));
		}

		#endregion
	}

	// Минимальное чтение и запись PWAD (каталог лампов в конце файла, как у build-wad.py)
	internal static class WadFile
	{
		public static List<KeyValuePair<string, byte[]>> Read(string path)
		{
			List<KeyValuePair<string, byte[]>> result = new List<KeyValuePair<string, byte[]>>();
			// UDB держит WAD карты открытым на чтение с FileShare.ReadWrite — открываем так же
			using(FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			using(BinaryReader r = new BinaryReader(fs))
			{
				string magic = Encoding.ASCII.GetString(r.ReadBytes(4));
				if(magic != "PWAD" && magic != "IWAD") throw new InvalidDataException("Не WAD: " + path);
				int count = r.ReadInt32();
				int diroffset = r.ReadInt32();
				for(int i = 0; i < count; i++)
				{
					fs.Position = diroffset + i * 16;
					int pos = r.ReadInt32();
					int size = r.ReadInt32();
					string name = Encoding.ASCII.GetString(r.ReadBytes(8)).TrimEnd('\0');
					fs.Position = pos;
					result.Add(new KeyValuePair<string, byte[]>(name, size > 0 ? r.ReadBytes(size) : new byte[0]));
				}
			}
			return result;
		}

		public static void Write(string path, List<KeyValuePair<string, byte[]>> lumps)
		{
			// Перезапись на месте с FileShare.ReadWrite: открытый в UDB WAD (ресурс карты) не мешает
			using(FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
			using(BinaryWriter w = new BinaryWriter(fs))
			{
				int dataSize = lumps.Sum(l => l.Value.Length);
				w.Write(Encoding.ASCII.GetBytes("PWAD"));
				w.Write(lumps.Count);
				w.Write(12 + dataSize);
				List<int> offsets = new List<int>();
				int pos = 12;
				foreach(KeyValuePair<string, byte[]> l in lumps)
				{
					offsets.Add(pos);
					w.Write(l.Value);
					pos += l.Value.Length;
				}
				for(int i = 0; i < lumps.Count; i++)
				{
					w.Write(lumps[i].Value.Length > 0 ? offsets[i] : 0);
					w.Write(lumps[i].Value.Length);
					byte[] name = new byte[8];
					byte[] src = Encoding.ASCII.GetBytes(lumps[i].Key.ToUpperInvariant());
					Array.Copy(src, name, Math.Min(8, src.Length));
					w.Write(name);
				}
			}
		}
	}
}
