#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ZTMapMerge.Udmf;

#endregion

namespace ZTMapMerge
{
	// Семантическое слияние карт UDMF (TEXTMAP) для git и для ручного запуска (ZT/workbench-plan.md §8.2).
	//   ZTMapMerge merge <база> <наша> <их> [<результат>]  — 3-way слияние; без <результат> пишет в <наша> (драйвер git:
	//                                                         merge.udmf.driver = "dotnet ZTMapMerge.dll merge %O %A %B");
	//                                                         код 0 — без конфликтов, 1 — есть конфликты (взято «у нас»)
	//   ZTMapMerge summary <старая> <новая>                — что изменилось между версиями
	//   ZTMapMerge roundtrip <файл>…                       — чтение и запись совпадают с исходником байт в байт
	internal static class Program
	{
		private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

		private static int Main(string[] args)
		{
			Console.OutputEncoding = Utf8;
			try
			{
				if(args.Length >= 4 && args[0] == "merge") return MergeCmd(args[1], args[2], args[3], args.Length >= 5 ? args[4] : args[2]);
				if(args.Length == 3 && args[0] == "summary") return SummaryCmd(args[1], args[2]);
				if(args.Length >= 2 && args[0] == "roundtrip") return RoundtripCmd(args.Skip(1));
				Console.Error.WriteLine("ZTMapMerge merge <база> <наша> <их> [<результат>] | summary <старая> <новая> | roundtrip <файл>…");
				return 2;
			}
			catch(Exception ex)
			{
				Console.Error.WriteLine("ZTMapMerge: " + ex.Message);
				return 2;
			}
		}

		private static UdmfMap Load(string path)
		{
			return UdmfMap.Parse(File.ReadAllText(path, Utf8));
		}

		private static int MergeCmd(string basePath, string oursPath, string theirsPath, string outPath)
		{
			DateTime start = DateTime.Now;
			MergeResult r = UdmfMerger.Merge(Load(basePath), Load(oursPath), Load(theirsPath));
			File.WriteAllText(outPath, r.Map.Write(), Utf8);
			Console.Error.WriteLine("ZTMapMerge: слияние карты за " + (int)(DateTime.Now - start).TotalMilliseconds + " мс");
			foreach(KeyValuePair<string, MergeStats> s in r.Stats.Where(s => s.Value.Kept > 0 || s.Value.DeletedOurs + s.Value.DeletedTheirs > 0))
				Console.Error.WriteLine("  " + s.Key + ": " + s.Value);
			if(r.Conflicts.Count == 0)
			{
				Console.Error.WriteLine("  конфликтов нет");
				return 0;
			}
			Console.Error.WriteLine("  КОНФЛИКТЫ (" + r.Conflicts.Count + "):");
			foreach(MergeConflict c in r.Conflicts.Take(200)) Console.Error.WriteLine("    " + c);
			if(r.Conflicts.Count > 200) Console.Error.WriteLine("    … и ещё " + (r.Conflicts.Count - 200));
			return 1;
		}

		// Сопоставление элементов тем же UdmfMerger.Correspond, что у перечитывания карты на месте в плагине ZTProject
		private static int SummaryCmd(string oldPath, string newPath)
		{
			foreach(KeyValuePair<string, TypeCorrespondence> s in UdmfMerger.Correspond(Load(oldPath), Load(newPath)))
			{
				int changed = s.Value.Changed.Count(c => c), added = s.Value.BToA.Count(i => i < 0), deleted = s.Value.AToB.Count(i => i < 0);
				if(changed + added + deleted == 0) continue;
				Console.WriteLine(s.Key + ": изменено " + changed + ", добавлено " + added + ", удалено " + deleted);
			}
			return 0;
		}

		private static int RoundtripCmd(IEnumerable<string> files)
		{
			int bad = 0;
			foreach(string f in files)
			{
				byte[] orig = File.ReadAllBytes(f);
				byte[] again = Utf8.GetBytes(UdmfMap.Parse(Utf8.GetString(orig)).Write());
				bool same = orig.SequenceEqual(again);
				if(!same) bad++;
				Console.WriteLine((same ? "OK   " : "DIFF ") + f + (same ? "" : " (" + orig.Length + " -> " + again.Length + " байт)"));
			}
			return bad == 0 ? 0 : 1;
		}
	}
}
