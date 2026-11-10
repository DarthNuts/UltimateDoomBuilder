#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeImp.DoomBuilder.Map;
using CodeImp.DoomBuilder.ZT;
using ZTMapMerge.Udmf;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Перечитывание открытой карты на месте (ZT/workbench-plan.md §9.2, решение 8). Новая версия TEXTMAP читается в
	// отдельный MapSet, её элементы сопоставляются с картой UDB по правилам семантического слияния (ZTMapMerge), и в карту
	// одним шагом отмены переносится только разница: поля, добавленные и удалённые элементы, затем порядок элементов —
	// как в файле (иначе следующее сохранение перенумеровало бы карту). Вид, режим и выделение остаются. Остальные лампы
	// карты (SCRIPTS, BEHAVIOR, DIALOGUE) заменяются во временном WAD UDB. Итог проверяется: карта, записанная из памяти,
	// должна совпасть байт в байт с новой версией, прочитанной UDB. Не сошлось — карту надо открыть заново.
	internal sealed class MapReload
	{
		#region ================== Variables

		// Латиница-1 переводит байты в символы один к одному: тексты TEXTMAP сравниваются и разбираются без перекодировки
		private static readonly Encoding Bytes = Encoding.GetEncoding(28591);

		public int Changed, Added, Deleted, Moved;
		public bool MapChanged;
		public bool Started;     // шаг отмены уже создан (перенос начался)
		public readonly List<string> Lumps = new List<string>();

		#endregion

		#region ================== Report

		public string Describe()
		{
			List<string> parts = new List<string>();
			if(MapChanged) parts.Add("изменено " + Changed + ", добавлено " + Added + ", удалено " + Deleted);
			if(Lumps.Count > 0) parts.Add("лампы " + string.Join(", ", Lumps));
			return parts.Count > 0 ? string.Join("; ", parts) : "без изменений";
		}

		#endregion

		#region ================== Apply

		// Перенести в открытую карту лампы карты из файлов. force — заменить и одинаковые лампы (вкладка скрипта с
		// несохранённым текстом перечитывается). Возвращает null, если на месте не вышло (причина — в why): тогда карту
		// надо открыть заново.
		public static MapReload Apply(List<KeyValuePair<string, byte[]>> lumps, string description, bool force, out string why)
		{
			why = null;
			MapReload r = new MapReload();
			byte[] textmap = null;

			// Сначала — можно ли заменить остальные лампы: новый ламп во временный WAD на место не встанет
			List<KeyValuePair<string, byte[]>> replace = new List<KeyValuePair<string, byte[]>>();
			HashSet<string> differ = new HashSet<string>();
			foreach(KeyValuePair<string, byte[]> l in lumps)
			{
				if(l.Key == "TEXTMAP") { textmap = l.Value; continue; }
				MemoryStream cur = General.Map.GetLumpData(l.Key);
				if(cur == null)
				{
					if(l.Value.Length == 0) continue;
					why = "во временном WAD UDB нет лампа " + l.Key;
					return null;
				}
				if(!cur.ToArray().SequenceEqual(l.Value)) differ.Add(l.Key);
				if(force || differ.Contains(l.Key)) replace.Add(l);
			}
			if(textmap == null) { why = "у карты нет TEXTMAP"; return null; }

			MapSet incoming = ZTMapTools.ReadUdmf(textmap);
			try
			{
				byte[] incbytes = ZTMapTools.WriteUdmf(incoming);
				byte[] curbytes = ZTMapTools.WriteUdmf(General.Map.Map);
				if(!curbytes.SequenceEqual(incbytes) && !ApplyTextmap(r, General.Map.Map, incoming, curbytes, incbytes, description, out why))
					return null;
			}
			finally
			{
				ZTMapTools.Discard(incoming);
			}

			foreach(KeyValuePair<string, byte[]> l in replace)
			{
				ZTMapTools.ReplaceMapLump(l.Key, l.Value);
				if(differ.Contains(l.Key)) r.Lumps.Add(l.Key);
			}
			// Карта в памяти теперь та же, что в файлах
			ZTMapTools.MarkSaved();
			return r;
		}

		// Правка карты по образцу TEXTMAP (MCP set_fields): разница — одним шагом отмены, карта остаётся изменённой.
		// Не вышло после начала переноса — шаг отменяется. null — не вышло (причина — в why).
		public static MapReload ApplyText(byte[] textmap, string description, out string why)
		{
			why = null;
			MapReload r = new MapReload();
			MapSet incoming = ZTMapTools.ReadUdmf(textmap);
			try
			{
				byte[] incbytes = ZTMapTools.WriteUdmf(incoming);
				byte[] curbytes = ZTMapTools.WriteUdmf(General.Map.Map);
				if(curbytes.SequenceEqual(incbytes)) return r;
				if(!ApplyTextmap(r, General.Map.Map, incoming, curbytes, incbytes, description, out why))
				{
					if(r.Started) General.Map.UndoRedo.PerformUndo();
					return null;
				}
				return r;
			}
			finally
			{
				ZTMapTools.Discard(incoming);
			}
		}

		// Разница TEXTMAP — в карту одним шагом отмены. Писатель UDB пишет элементы по порядку номеров, поэтому номер блока
		// в тексте — это номер элемента в MapSet (и у карты UDB, и у прочитанной новой версии).
		private static bool ApplyTextmap(MapReload r, MapSet map, MapSet inc, byte[] curbytes, byte[] incbytes, string description, out string why)
		{
			why = null;
			UdmfMap a = UdmfMap.Parse(Bytes.GetString(curbytes));
			UdmfMap b = UdmfMap.Parse(Bytes.GetString(incbytes));
			if(!SameExtras(a, b)) { why = "изменились глобальные поля или нестандартные блоки TEXTMAP"; return false; }

			Vertex[] oV = map.Vertices.ToArray(), iV = inc.Vertices.ToArray();
			Linedef[] oL = map.Linedefs.ToArray(), iL = inc.Linedefs.ToArray();
			Sidedef[] oSd = map.Sidedefs.ToArray(), iSd = inc.Sidedefs.ToArray();
			Sector[] oS = map.Sectors.ToArray(), iS = inc.Sectors.ToArray();
			Thing[] oT = map.Things.ToArray(), iT = inc.Things.ToArray();
			if(Count(a, "vertex") != oV.Length || Count(a, "linedef") != oL.Length || Count(a, "sidedef") != oSd.Length ||
			   Count(a, "sector") != oS.Length || Count(a, "thing") != oT.Length ||
			   Count(b, "vertex") != iV.Length || Count(b, "linedef") != iL.Length || Count(b, "sidedef") != iSd.Length ||
			   Count(b, "sector") != iS.Length || Count(b, "thing") != iT.Length)
			{
				why = "число элементов в тексте и в карте не совпало";
				return false;
			}

			Dictionary<string, TypeCorrespondence> c = UdmfMerger.Correspond(a, b);
			TypeCorrespondence cv = c["vertex"], cl = c["linedef"], cd = c["sidedef"], cs = c["sector"], ct = c["thing"];
			Dictionary<Sidedef, int> oldside = new Dictionary<Sidedef, int>();
			for(int i = 0; i < oSd.Length; i++) oldside[oSd[i]] = i;

			bool ok = false;
			ZTMapTools.BeginExternalChange(description);
			r.Started = true;
			bool autoremove = ZTMapTools.SetAutoRemove(map, false);
			try
			{
				// Вершины
				Vertex[] nV = new Vertex[iV.Length];
				for(int j = 0; j < iV.Length; j++)
				{
					int ai = cv.BToA[j];
					if(ai >= 0)
					{
						nV[j] = oV[ai];
						if(cv.Changed[ai]) { ZTMapTools.CopyProperties(iV[j], nV[j]); nV[j].Marked = true; r.Changed++; }
					}
					else
					{
						nV[j] = Made(map.CreateVertex(iV[j].Position), "вершин");
						ZTMapTools.CopyProperties(iV[j], nV[j]);
						nV[j].Marked = true;
						r.Added++;
					}
				}

				// Секторы
				Sector[] nS = new Sector[iS.Length];
				for(int j = 0; j < iS.Length; j++)
				{
					int ai = cs.BToA[j];
					if(ai >= 0)
					{
						nS[j] = oS[ai];
						if(cs.Changed[ai]) { ZTMapTools.CopyProperties(iS[j], nS[j]); nS[j].Marked = true; r.Changed++; }
					}
					else
					{
						nS[j] = Made(map.CreateSector(), "секторов");
						ZTMapTools.CopyProperties(iS[j], nS[j]);
						nS[j].Marked = true;
						r.Added++;
					}
				}

				// Линии и их стороны
				Linedef[] nL = new Linedef[iL.Length];
				Sidedef[] nSd = new Sidedef[iSd.Length];
				for(int j = 0; j < iL.Length; j++)
				{
					Linedef src = iL[j];
					Vertex sv = nV[src.Start.Index], ev = nV[src.End.Index];
					int ai = cl.BToA[j];
					Linedef dst;
					if(ai >= 0)
					{
						dst = oL[ai];
						bool changed = cl.Changed[ai];
						if(dst.Start != sv) { dst.SetStartVertex(sv); changed = true; }
						if(dst.End != ev) { dst.SetEndVertex(ev); changed = true; }
						if(cl.Changed[ai]) ZTMapTools.CopyProperties(src, dst);
						if(changed) { dst.Marked = true; r.Changed++; }
					}
					else
					{
						dst = Made(map.CreateLinedef(sv, ev), "линий");
						ZTMapTools.CopyProperties(src, dst);
						dst.Marked = true;
						r.Added++;
					}
					nL[j] = dst;
					SyncSide(r, map, dst, true, src.Front, nS, nSd, cd, oldside);
					SyncSide(r, map, dst, false, src.Back, nS, nSd, cd, oldside);
				}

				// Вещи
				Thing[] nT = new Thing[iT.Length];
				for(int j = 0; j < iT.Length; j++)
				{
					int ai = ct.BToA[j];
					if(ai >= 0)
					{
						nT[j] = oT[ai];
						if(ct.Changed[ai]) { ZTMapTools.CopyProperties(iT[j], nT[j]); nT[j].Marked = true; r.Changed++; }
					}
					else
					{
						nT[j] = Made(map.CreateThing(), "вещей");
						ZTMapTools.CopyProperties(iT[j], nT[j]);
						nT[j].Marked = true;
						r.Added++;
					}
				}

				// Удалённые: вещи; линии вместе со сторонами; затем секторы и вершины — на них уже ничто не ссылается
				for(int i = 0; i < oT.Length; i++) if(ct.AToB[i] < 0) { oT[i].Dispose(); r.Deleted++; }
				for(int i = 0; i < oL.Length; i++)
				{
					if(cl.AToB[i] >= 0) continue;
					if(oL[i].Front != null) oL[i].Front.Dispose();
					if(oL[i].Back != null) oL[i].Back.Dispose();
					oL[i].Dispose();
					r.Deleted++;
				}
				for(int i = 0; i < oS.Length; i++)
				{
					if(cs.AToB[i] >= 0) continue;
					if(oS[i].Sidedefs.Count > 0) throw new InvalidOperationException("удаляемый сектор " + i + " ещё связан со сторонами");
					oS[i].Dispose();
					r.Deleted++;
				}
				for(int i = 0; i < oV.Length; i++)
				{
					if(cv.AToB[i] >= 0) continue;
					if(oV[i].Linedefs.Count > 0) throw new InvalidOperationException("удаляемая вершина " + i + " ещё связана с линиями");
					oV[i].Dispose();
					r.Deleted++;
				}

				// Порядок — как в файле
				if(map.Vertices.Count != nV.Length || map.Linedefs.Count != nL.Length || map.Sidedefs.Count != nSd.Length ||
				   map.Sectors.Count != nS.Length || map.Things.Count != nT.Length || nSd.Any(s => s == null))
					throw new InvalidOperationException("после переноса число элементов не совпало с файлом");
				r.Moved += Reorder(nV, e => e.Index, (e, k) => e.ChangeIndex(k));
				r.Moved += Reorder(nL, e => e.Index, (e, k) => e.ChangeIndex(k));
				r.Moved += Reorder(nSd, e => e.Index, (e, k) => e.ChangeIndex(k));
				r.Moved += Reorder(nS, e => e.Index, (e, k) => e.ChangeIndex(k));
				r.Moved += Reorder(nT, e => e.Index, (e, k) => e.ChangeIndex(k));
				ok = true;
			}
			catch(Exception ex)
			{
				why = "перенос не удался: " + ex.Message;
			}
			finally
			{
				ZTMapTools.SetAutoRemove(map, autoremove);
				try { ZTMapTools.EndExternalChange(); }
				catch(Exception ex) { if(ok) why = "обновление карты после переноса не удалось: " + ex.Message; ok = false; }
			}
			if(!ok) return false;

			// Проверка: карта из памяти — та же, что новая версия, прочитанная UDB
			byte[] after = ZTMapTools.WriteUdmf(map);
			if(!after.SequenceEqual(incbytes))
			{
				why = "после переноса карта не совпала с файлом (" + FirstDifference(after, incbytes) + ")";
				return false;
			}
			r.MapChanged = true;
			General.WriteLogLine("ZT: карта перечитана на месте: " + r.Describe() + ", переставлено " + r.Moved);
			return true;
		}

		// Сторона линии dst по образцу src (null — стороны быть не должно). Сторону сопоставленной линии сохраняем тем же
		// объектом и правим, у новой линии — создаём.
		private static void SyncSide(MapReload r, MapSet map, Linedef dst, bool front, Sidedef src, Sector[] nS, Sidedef[] nSd,
			TypeCorrespondence cd, Dictionary<Sidedef, int> oldside)
		{
			Sidedef cur = front ? dst.Front : dst.Back;
			if(src == null)
			{
				if(cur != null) { cur.Dispose(); dst.Marked = true; r.Deleted++; }
				return;
			}
			Sector sec = nS[src.Sector.Index];
			if(cur == null)
			{
				cur = Made(map.CreateSidedef(dst, front, sec), "сторон");
				ZTMapTools.CopyProperties(src, cur);
				dst.Marked = true;
				r.Added++;
			}
			else
			{
				int ai;
				bool matched = oldside.TryGetValue(cur, out ai) && cd.AToB[ai] == src.Index;
				bool changed = !matched || cd.Changed[ai];
				if(changed) ZTMapTools.CopyProperties(src, cur);
				if(cur.Sector != sec) { cur.SetSector(sec); changed = true; }
				if(changed) { dst.Marked = true; r.Changed++; }
			}
			nSd[src.Index] = cur;
		}

		private static T Made<T>(T element, string what) where T : class
		{
			if(element == null) throw new InvalidOperationException("в карте не осталось места для " + what);
			return element;
		}

		#endregion

		#region ================== Order

		// Переставить элементы так, чтобы target[i] получил номер i. Двигаются только элементы вне наибольшей возрастающей
		// подпоследовательности текущих номеров — таких перестановок меньше всего. ChangeIndex — «вынуть и вставить»:
		// сдвинутый элемент встаёт сразу за своим предшественником в target, и к концу порядок совпадает целиком.
		private static int Reorder<T>(T[] target, Func<T, int> index, Action<T, int> move)
		{
			int n = target.Length;
			int[] p = new int[n];
			for(int j = 0; j < n; j++) p[j] = index(target[j]);
			bool[] keep = Lis(p);
			int moves = 0;
			for(int j = 0; j < n; j++)
			{
				if(keep[j]) continue;
				int cur = index(target[j]);
				int dest = 0;
				if(j > 0)
				{
					int prev = index(target[j - 1]);
					dest = cur > prev ? prev + 1 : prev;
				}
				if(dest != cur) { move(target[j], dest); moves++; }
			}
			return moves;
		}

		// Наибольшая возрастающая подпоследовательность, O(n log n); true — элемент в неё входит
		private static bool[] Lis(int[] p)
		{
			int n = p.Length, len = 0;
			int[] tails = new int[n], prev = new int[n];
			for(int i = 0; i < n; i++)
			{
				int lo = 0, hi = len;
				while(lo < hi)
				{
					int mid = (lo + hi) / 2;
					if(p[tails[mid]] < p[i]) lo = mid + 1; else hi = mid;
				}
				prev[i] = lo > 0 ? tails[lo - 1] : -1;
				tails[lo] = i;
				if(lo == len) len++;
			}
			bool[] keep = new bool[n];
			for(int i = len > 0 ? tails[len - 1] : -1; i >= 0; i = prev[i]) keep[i] = true;
			return keep;
		}

		#endregion

		#region ================== Helpers

		private static int Count(UdmfMap m, string type)
		{
			List<UdmfBlock> list;
			return m.Blocks.TryGetValue(type, out list) ? list.Count : 0;
		}

		// Глобальные поля и нестандартные блоки TEXTMAP (их UDB хранит как есть) на месте не переносятся
		private static bool SameExtras(UdmfMap a, UdmfMap b)
		{
			if(!SameFields(a.Globals, b.Globals)) return false;
			foreach(string t in a.TypeOrder.Union(b.TypeOrder))
			{
				if(Array.IndexOf(UdmfMap.KnownTypes, t) >= 0) continue;
				List<UdmfBlock> x, y;
				a.Blocks.TryGetValue(t, out x);
				b.Blocks.TryGetValue(t, out y);
				int nx = x != null ? x.Count : 0, ny = y != null ? y.Count : 0;
				if(nx != ny) return false;
				for(int i = 0; i < nx; i++) if(!SameFields(x[i].Fields, y[i].Fields)) return false;
			}
			return true;
		}

		private static bool SameFields(List<UdmfField> x, List<UdmfField> y)
		{
			if(x.Count != y.Count) return false;
			for(int i = 0; i < x.Count; i++) if(x[i].Key != y[i].Key || x[i].Value != y[i].Value) return false;
			return true;
		}

		// Первая различающаяся строка двух текстов TEXTMAP — для журнала
		private static string FirstDifference(byte[] x, byte[] y)
		{
			string[] a = Bytes.GetString(x).Split('\n'), b = Bytes.GetString(y).Split('\n');
			for(int i = 0; i < Math.Min(a.Length, b.Length); i++)
				if(a[i] != b[i]) return "строка " + (i + 1) + ": «" + a[i].Trim() + "» вместо «" + b[i].Trim() + "»";
			return "строк " + a.Length + " вместо " + b.Length;
		}

		#endregion
	}
}
