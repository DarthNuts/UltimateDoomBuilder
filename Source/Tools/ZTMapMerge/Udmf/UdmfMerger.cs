#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

#endregion

namespace ZTMapMerge.Udmf
{
	public sealed class MergeConflict
	{
		public string Type;      // thing / vertex / linedef / sidedef / sector
		public int Index;        // номер элемента в результате (-1 — элемента в результате нет)
		public int BaseIndex = -1, OursIndex = -1, TheirsIndex = -1;   // номера в исходных версиях (-1 — нет в версии)
		public string Key;       // поле; null — конфликт «изменён с одной стороны, удалён с другой»
		public string Base, Ours, Theirs;
		public string Note;

		public override string ToString()
		{
			string where = Type + " " + (Index >= 0 ? Index.ToString(CultureInfo.InvariantCulture) : "—");
			if(Key == null) return where + ": " + Note;
			return where + "." + Key + ": база " + (Base ?? "нет") + ", у нас " + (Ours ?? "нет") + ", у них " + (Theirs ?? "нет") +
			       (Note != null ? " (" + Note + ")" : "");
		}
	}

	public sealed class MergeStats
	{
		public int Kept, ChangedOurs, ChangedTheirs, ChangedBoth, AddedOurs, AddedTheirs, DeletedOurs, DeletedTheirs;

		public override string ToString()
		{
			return string.Format(CultureInfo.InvariantCulture,
				"элементов {0}; изменено: у нас {1}, у них {2}, с обеих сторон {3}; добавлено: у нас {4}, у них {5}; удалено: у нас {6}, у них {7}",
				Kept, ChangedOurs, ChangedTheirs, ChangedBoth, AddedOurs, AddedTheirs, DeletedOurs, DeletedTheirs);
		}
	}

	public sealed class MergeResult
	{
		public UdmfMap Map;
		public List<MergeConflict> Conflicts = new List<MergeConflict>();
		public Dictionary<string, MergeStats> Stats = new Dictionary<string, MergeStats>();
	}

	// Соответствие элементов одного типа между версиями A и B (UdmfMerger.Correspond)
	public sealed class TypeCorrespondence
	{
		public int[] AToB;       // номер в B для каждого элемента A (-1 — в B его нет)
		public int[] BToA;       // номер в A для каждого элемента B (-1 — добавлен в B)
		public bool[] Changed;   // по номерам A: элемент есть в обеих версиях, но поля различаются (ссылки — по смыслу)
	}

	// Семантическое 3-way слияние TEXTMAP (ZT/workbench-plan.md §8.2). Элементы сопоставляются не по номерам, а по смыслу:
	// вершины — по координатам; линии — по координатам концов; стороны — через свои линии; секторы — по большинству своих
	// сторон; вещи — по содержимому, затем по типу и месту, по типу и id, по типу в порядке файла. Затем слияние по полям:
	// поле, изменённое с одной стороны, берётся; изменённое по-разному — конфликт. Ссылки (v1/v2, sidefront/sideback,
	// sector) сравниваются по сопоставленным элементам и в результате перенумеровываются.
	public static class UdmfMerger
	{
		private const int B = 0, O = 1, T = 2;
		private static readonly string[] Versions = { "база", "у нас", "у них" };

		// Сопоставленный элемент: номер в каждой версии (-1 — нет в этой версии)
		private sealed class Ident
		{
			public readonly int[] At = { -1, -1, -1 };
		}

		private sealed class TypeState
		{
			public string Type;
			public List<UdmfBlock>[] Blocks = new List<UdmfBlock>[3];
			public List<Ident> Idents = new List<Ident>();
			public int[][] IdOf = new int[3][];                 // номер в версии -> номер Ident
			public List<KeyValuePair<string, string>>[] Result;  // слитые поля по Ident (null — удалён)
			public int[] FinalIndex;                             // Ident -> номер в результате (-1 — нет)
		}

		#region ================== Entry

		public static MergeResult Merge(UdmfMap baseMap, UdmfMap ours, UdmfMap theirs)
		{
			UdmfMap[] maps = { baseMap, ours, theirs };
			MergeResult result = new MergeResult();
			List<string> types;
			Dictionary<string, TypeState> st = MatchAll(maps, out types);
			foreach(string t in types) MergeFields(st[t], st, result);
			ResurrectReferenced(st, result);
			result.Map = Build(st, types, maps, result);
			return result;
		}

		// Соответствие элементов двух версий карты по тем же правилам, что при слиянии (A — база и «у нас», B — «у них»).
		// Нужен для перечитывания карты на месте: что из A осталось в B, что изменилось, что добавлено и удалено.
		public static Dictionary<string, TypeCorrespondence> Correspond(UdmfMap a, UdmfMap b)
		{
			List<string> types;
			Dictionary<string, TypeState> st = MatchAll(new[] { a, a, b }, out types);
			Dictionary<string, TypeCorrespondence> result = new Dictionary<string, TypeCorrespondence>();
			foreach(string t in types)
			{
				TypeState s = st[t];
				TypeCorrespondence c = new TypeCorrespondence
				{
					AToB = Fill(s.Blocks[B].Count),
					BToA = Fill(s.Blocks[T].Count),
					Changed = new bool[s.Blocks[B].Count]
				};
				foreach(Ident id in s.Idents)
				{
					if(id.At[B] < 0 || id.At[T] < 0) continue;
					c.AToB[id.At[B]] = id.At[T];
					c.BToA[id.At[T]] = id.At[B];
					c.Changed[id.At[B]] = !Same(Norm(s, B, id.At[B], st), Norm(s, T, id.At[T], st));
				}
				result[t] = c;
			}
			return result;
		}

		// Сопоставление элементов трёх версий: сначала геометрия (от вершин к секторам), затем вещи и прочие блоки
		private static Dictionary<string, TypeState> MatchAll(UdmfMap[] maps, out List<string> types)
		{
			Dictionary<string, TypeState> st = new Dictionary<string, TypeState>();
			types = new List<string>(UdmfMap.KnownTypes);
			foreach(UdmfMap m in maps) foreach(string t in m.TypeOrder) if(!types.Contains(t)) types.Add(t);
			foreach(string t in types)
			{
				TypeState s = new TypeState { Type = t };
				for(int v = 0; v < 3; v++) s.Blocks[v] = maps[v].Blocks.ContainsKey(t) ? maps[v].Blocks[t] : new List<UdmfBlock>();
				st[t] = s;
			}

			MatchVertices(st["vertex"]);
			MatchLinedefs(st["linedef"], st["vertex"]);
			MatchSidedefs(st["sidedef"], st["linedef"]);
			MatchSectors(st["sector"], st["sidedef"]);
			MatchThings(st["thing"]);
			foreach(string t in types) if(Array.IndexOf(UdmfMap.KnownTypes, t) < 0) MatchByContent(st[t]);
			return st;
		}

		#endregion

		#region ================== Matching helpers

		private static string Num(string v)
		{
			double d;
			if(v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d.ToString("R", CultureInfo.InvariantCulture);
			return v ?? "";
		}

		private static string Content(UdmfBlock b)
		{
			StringBuilder sb = new StringBuilder();
			foreach(UdmfField f in b.Fields.OrderBy(f => f.Key, StringComparer.Ordinal)) sb.Append(f.Key).Append('=').Append(f.Value).Append(';');
			return sb.ToString();
		}

		// Сопоставление по ключам: каждый ключ X получает первый свободный элемент A с тем же ключом (в порядке файла)
		private static void MatchKeyed(Func<int, string> aKey, int na, bool[] aFree, Func<int, string> xKey, int nx, bool[] xFree, int[] xToA)
		{
			Dictionary<string, Queue<int>> q = new Dictionary<string, Queue<int>>();
			for(int i = 0; i < na; i++)
			{
				if(!aFree[i]) continue;
				string k = aKey(i);
				if(k == null) continue;
				Queue<int> list;
				if(!q.TryGetValue(k, out list)) q[k] = list = new Queue<int>();
				list.Enqueue(i);
			}
			for(int i = 0; i < nx; i++)
			{
				if(!xFree[i]) continue;
				string k = xKey(i);
				Queue<int> list;
				if(k == null || !q.TryGetValue(k, out list) || list.Count == 0) continue;
				int a = list.Dequeue();
				xToA[i] = a;
				xFree[i] = false;
				aFree[a] = false;
			}
		}

		private static bool[] AllFree(int n) { bool[] r = new bool[n]; for(int i = 0; i < n; i++) r[i] = true; return r; }

		private static int[] Fill(int n) { int[] r = new int[n]; for(int i = 0; i < n; i++) r[i] = -1; return r; }

		// Идентичности по результатам сопоставлений: база с сопоставленными O и T, затем добавленные O и T
		// (добавленные с обеих сторон и сопоставленные между собой — одна идентичность)
		private static void BuildIdents(TypeState s, int[] oToB, int[] tToB, int[] tToO)
		{
			int nb = s.Blocks[B].Count, no = s.Blocks[O].Count, nt = s.Blocks[T].Count;
			for(int v = 0; v < 3; v++) s.IdOf[v] = Fill(s.Blocks[v].Count);
			for(int b = 0; b < nb; b++) { Ident id = new Ident(); id.At[B] = b; s.IdOf[B][b] = s.Idents.Count; s.Idents.Add(id); }
			for(int o = 0; o < no; o++)
			{
				if(oToB[o] >= 0) { s.Idents[s.IdOf[B][oToB[o]]].At[O] = o; s.IdOf[O][o] = s.IdOf[B][oToB[o]]; }
				else { Ident id = new Ident(); id.At[O] = o; s.IdOf[O][o] = s.Idents.Count; s.Idents.Add(id); }
			}
			for(int t = 0; t < nt; t++)
			{
				int idx = tToB[t] >= 0 ? s.IdOf[B][tToB[t]] : (tToO != null && tToO[t] >= 0 ? s.IdOf[O][tToO[t]] : -1);
				if(idx >= 0) { s.Idents[idx].At[T] = t; s.IdOf[T][t] = idx; }
				else { Ident id = new Ident(); id.At[T] = t; s.IdOf[T][t] = s.Idents.Count; s.Idents.Add(id); }
			}
		}

		// Сопоставление добавленных с обеих сторон (O и T, не сопоставленных с базой) по тому же ключу
		private static int[] MatchAdded(TypeState s, int[] oToB, int[] tToB, Func<int, int, string> key)
		{
			int no = s.Blocks[O].Count, nt = s.Blocks[T].Count;
			bool[] oFree = new bool[no], tFree = new bool[nt];
			for(int i = 0; i < no; i++) oFree[i] = oToB[i] < 0;
			for(int i = 0; i < nt; i++) tFree[i] = tToB[i] < 0;
			int[] tToO = Fill(nt);
			MatchKeyed(i => key(O, i), no, oFree, i => key(T, i), nt, tFree, tToO);
			return tToO;
		}

		#endregion

		#region ================== Matching by type

		private static string VertexKey(TypeState vs, int v, int i)
		{
			if(i < 0 || i >= vs.Blocks[v].Count) return null;
			UdmfBlock b = vs.Blocks[v][i];
			return Num(b.Get("x")) + "," + Num(b.Get("y"));
		}

		private static void MatchVertices(TypeState s)
		{
			Func<int, int, string> key = (v, i) => VertexKey(s, v, i);
			int[][] toB = new int[3][];
			for(int v = O; v <= T; v++)
			{
				toB[v] = Fill(s.Blocks[v].Count);
				int vv = v;
				MatchKeyed(i => key(B, i), s.Blocks[B].Count, AllFree(s.Blocks[B].Count), i => key(vv, i), s.Blocks[vv].Count, AllFree(s.Blocks[vv].Count), toB[vv]);
			}
			BuildIdents(s, toB[O], toB[T], MatchAdded(s, toB[O], toB[T], key));
		}

		private static string LineKey(TypeState ls, TypeState vs, int v, int i)
		{
			UdmfBlock b = ls.Blocks[v][i];
			return VertexKey(vs, v, b.GetInt("v1", -1)) + ">" + VertexKey(vs, v, b.GetInt("v2", -1));
		}

		private static void MatchLinedefs(TypeState s, TypeState vs)
		{
			Func<int, int, string> key = (v, i) => LineKey(s, vs, v, i);
			int[][] toB = new int[3][];
			for(int v = O; v <= T; v++)
			{
				toB[v] = Fill(s.Blocks[v].Count);
				int vv = v;
				MatchKeyed(i => key(B, i), s.Blocks[B].Count, AllFree(s.Blocks[B].Count), i => key(vv, i), s.Blocks[vv].Count, AllFree(s.Blocks[vv].Count), toB[vv]);
			}
			BuildIdents(s, toB[O], toB[T], MatchAdded(s, toB[O], toB[T], key));
		}

		// Стороны — через линии: у сопоставленных линий сопоставляются передние и задние стороны
		private static void MatchSidedefs(TypeState s, TypeState ls)
		{
			int[][] toB = { null, Fill(s.Blocks[O].Count), Fill(s.Blocks[T].Count) };
			int[] tToO = Fill(s.Blocks[T].Count);
			foreach(Ident line in ls.Idents)
			{
				foreach(string side in new[] { "sidefront", "sideback" })
				{
					int[] sd = new int[3];
					for(int v = 0; v < 3; v++) sd[v] = line.At[v] >= 0 ? ls.Blocks[v][line.At[v]].GetInt(side, -1) : -1;
					for(int v = 0; v < 3; v++) if(sd[v] >= s.Blocks[v].Count) sd[v] = -1;
					for(int v = O; v <= T; v++) if(sd[B] >= 0 && sd[v] >= 0 && toB[v][sd[v]] < 0) toB[v][sd[v]] = sd[B];
					if(sd[B] < 0 && sd[O] >= 0 && sd[T] >= 0 && tToO[sd[T]] < 0) tToO[sd[T]] = sd[O];
				}
			}
			BuildIdents(s, toB[O], toB[T], tToO);
		}

		// Сектор X сопоставляется с сектором A, к которому относится большинство его сопоставленных сторон
		private static int[] MatchSectorsByVotes(TypeState s, TypeState ss, int a, int x, bool[] aFree, bool[] xFree)
		{
			int nx = s.Blocks[x].Count;
			int[] xToA = Fill(nx);
			Dictionary<int, Dictionary<int, int>> votes = new Dictionary<int, Dictionary<int, int>>();
			int[] sides = new int[nx];
			foreach(Ident sd in ss.Idents)
			{
				if(sd.At[x] < 0) continue;
				int xs = ss.Blocks[x][sd.At[x]].GetInt("sector", -1);
				if(xs < 0 || xs >= nx) continue;
				sides[xs]++;
				if(sd.At[a] < 0) continue;
				int asec = ss.Blocks[a][sd.At[a]].GetInt("sector", -1);
				if(asec < 0 || asec >= s.Blocks[a].Count || !aFree[asec]) continue;
				Dictionary<int, int> d;
				if(!votes.TryGetValue(xs, out d)) votes[xs] = d = new Dictionary<int, int>();
				int c;
				d.TryGetValue(asec, out c);
				d[asec] = c + 1;
			}
			// жадно, начиная с самых уверенных пар
			List<Tuple<double, int, int>> pairs = new List<Tuple<double, int, int>>();
			foreach(KeyValuePair<int, Dictionary<int, int>> kv in votes)
				foreach(KeyValuePair<int, int> av in kv.Value)
					if(xFree[kv.Key] && av.Value * 2 >= sides[kv.Key]) pairs.Add(Tuple.Create((double)av.Value / sides[kv.Key], kv.Key, av.Key));
			foreach(Tuple<double, int, int> p in pairs.OrderByDescending(p => p.Item1).ThenBy(p => p.Item2))
			{
				if(!xFree[p.Item2] || !aFree[p.Item3]) continue;
				xToA[p.Item2] = p.Item3;
				xFree[p.Item2] = false;
				aFree[p.Item3] = false;
			}
			return xToA;
		}

		private static void MatchSectors(TypeState s, TypeState ss)
		{
			int[][] toB = new int[3][];
			for(int v = O; v <= T; v++)
			{
				bool[] aFree = AllFree(s.Blocks[B].Count), xFree = AllFree(s.Blocks[v].Count);
				toB[v] = MatchSectorsByVotes(s, ss, B, v, aFree, xFree);
				// секторы без сопоставленных сторон — по одинаковому содержимому
				int vv = v;
				int[] extra = Fill(s.Blocks[v].Count);
				MatchKeyed(i => Content(s.Blocks[B][i]), s.Blocks[B].Count, aFree, i => Content(s.Blocks[vv][i]), s.Blocks[vv].Count, xFree, extra);
				for(int i = 0; i < extra.Length; i++) if(extra[i] >= 0) toB[v][i] = extra[i];
			}
			bool[] oFree = new bool[s.Blocks[O].Count], tFree = new bool[s.Blocks[T].Count];
			for(int i = 0; i < oFree.Length; i++) oFree[i] = toB[O][i] < 0;
			for(int i = 0; i < tFree.Length; i++) tFree[i] = toB[T][i] < 0;
			BuildIdents(s, toB[O], toB[T], MatchSectorsByVotes(s, ss, O, T, oFree, tFree));
		}

		private static void MatchThings(TypeState s)
		{
			List<Func<UdmfBlock, string>> passes = new List<Func<UdmfBlock, string>>
			{
				Content,                                                                         // та же вещь целиком
				b => b.Get("type") + "@" + Num(b.Get("x")) + "," + Num(b.Get("y")),              // тот же тип на том же месте
				b => b.Get("id") != null ? b.Get("type") + "#" + b.Get("id") : null,             // тот же тип и id
				b => b.Get("type")                                                               // тот же тип, по порядку файла
			};
			int[][] toB = new int[3][];
			for(int v = O; v <= T; v++)
			{
				toB[v] = Fill(s.Blocks[v].Count);
				bool[] aFree = AllFree(s.Blocks[B].Count), xFree = AllFree(s.Blocks[v].Count);
				int vv = v;
				foreach(Func<UdmfBlock, string> k in passes)
					MatchKeyed(i => k(s.Blocks[B][i]), s.Blocks[B].Count, aFree, i => k(s.Blocks[vv][i]), s.Blocks[vv].Count, xFree, toB[vv]);
			}
			// добавленные с обеих сторон — только одинаковые целиком или того же типа на том же месте
			int no = s.Blocks[O].Count, nt = s.Blocks[T].Count;
			bool[] oFree = new bool[no], tFree = new bool[nt];
			for(int i = 0; i < no; i++) oFree[i] = toB[O][i] < 0;
			for(int i = 0; i < nt; i++) tFree[i] = toB[T][i] < 0;
			int[] tToO = Fill(nt);
			for(int p = 0; p < 2; p++)
				MatchKeyed(i => passes[p](s.Blocks[O][i]), no, oFree, i => passes[p](s.Blocks[T][i]), nt, tFree, tToO);
			BuildIdents(s, toB[O], toB[T], tToO);
		}

		private static void MatchByContent(TypeState s)
		{
			int[][] toB = new int[3][];
			for(int v = O; v <= T; v++)
			{
				toB[v] = Fill(s.Blocks[v].Count);
				int vv = v;
				MatchKeyed(i => Content(s.Blocks[B][i]), s.Blocks[B].Count, AllFree(s.Blocks[B].Count), i => Content(s.Blocks[vv][i]), s.Blocks[vv].Count, AllFree(s.Blocks[vv].Count), toB[vv]);
			}
			BuildIdents(s, toB[O], toB[T], MatchAdded(s, toB[O], toB[T], (v, i) => Content(s.Blocks[v][i])));
		}

		#endregion

		#region ================== Field merge

		// Ссылочные поля: (тип блока, поле) -> тип, на который ссылается
		private static string RefTarget(string type, string key)
		{
			if(type == "linedef" && (key == "v1" || key == "v2")) return "vertex";
			if(type == "linedef" && (key == "sidefront" || key == "sideback")) return "sidedef";
			if(type == "sidedef" && key == "sector") return "sector";
			return null;
		}

		private const char REF = '\u0001';

		// Поля элемента версии v со ссылками, заменёнными на номера Ident (так их можно сравнивать между версиями)
		private static List<KeyValuePair<string, string>> Norm(TypeState s, int v, int index, Dictionary<string, TypeState> st)
		{
			List<KeyValuePair<string, string>> r = new List<KeyValuePair<string, string>>();
			foreach(UdmfField f in s.Blocks[v][index].Fields)
			{
				string target = RefTarget(s.Type, f.Key);
				string value = f.Value;
				int n;
				if(target != null && int.TryParse(f.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0)
				{
					TypeState ts = st[target];
					value = n < ts.IdOf[v].Length ? REF + ts.IdOf[v][n].ToString(CultureInfo.InvariantCulture) : f.Value;
				}
				r.Add(new KeyValuePair<string, string>(f.Key, value));
			}
			return r;
		}

		private static string Find(List<KeyValuePair<string, string>> fields, string key)
		{
			if(fields == null) return null;
			foreach(KeyValuePair<string, string> f in fields) if(f.Key == key) return f.Value;
			return null;
		}

		private static bool Same(List<KeyValuePair<string, string>> a, List<KeyValuePair<string, string>> b)
		{
			if(a == null || b == null) return a == b;
			if(a.Count != b.Count) return false;
			foreach(KeyValuePair<string, string> f in a) if(Find(b, f.Key) != f.Value) return false;
			return true;
		}

		private static MergeStats StatsOf(MergeResult r, string type)
		{
			MergeStats s;
			if(!r.Stats.TryGetValue(type, out s)) r.Stats[type] = s = new MergeStats();
			return s;
		}

		private static void MergeFields(TypeState s, Dictionary<string, TypeState> st, MergeResult result)
		{
			MergeStats stats = StatsOf(result, s.Type);
			s.Result = new List<KeyValuePair<string, string>>[s.Idents.Count];
			for(int i = 0; i < s.Idents.Count; i++)
			{
				Ident id = s.Idents[i];
				List<KeyValuePair<string, string>>[] f = new List<KeyValuePair<string, string>>[3];
				for(int v = 0; v < 3; v++) f[v] = id.At[v] >= 0 ? Norm(s, v, id.At[v], st) : null;

				if(f[B] == null)
				{
					// добавлен с одной или с обеих сторон
					if(f[O] != null && f[T] == null) { s.Result[i] = f[O]; stats.AddedOurs++; }
					else if(f[T] != null && f[O] == null) { s.Result[i] = f[T]; stats.AddedTheirs++; }
					else { s.Result[i] = MergeOne(s, i, null, f[O], f[T], result); stats.AddedOurs++; }
					continue;
				}
				if(f[O] == null || f[T] == null)
				{
					// удалён хотя бы с одной стороны
					List<KeyValuePair<string, string>> other = f[O] ?? f[T];
					if(other == null) { stats.DeletedOurs++; stats.DeletedTheirs++; continue; }
					if(Same(other, f[B])) { if(f[O] == null) stats.DeletedOurs++; else stats.DeletedTheirs++; continue; }
					// изменён с одной стороны, удалён с другой — сохраняем изменённый, это конфликт
					s.Result[i] = other;
					result.Conflicts.Add(new MergeConflict { Type = s.Type, Index = i, Note = f[O] == null ? "у нас удалён, у них изменён — оставлен изменённый" : "у нас изменён, у них удалён — оставлен изменённый" });
					continue;
				}
				bool co = !Same(f[O], f[B]), ct = !Same(f[T], f[B]);
				if(co && ct) stats.ChangedBoth++; else if(co) stats.ChangedOurs++; else if(ct) stats.ChangedTheirs++;
				s.Result[i] = !co ? f[T] : (!ct ? f[O] : MergeOne(s, i, f[B], f[O], f[T], result));
			}
			stats.Kept = s.Result.Count(r => r != null);
		}

		// Слияние полей элемента: порядок — как у нас, поля, появившиеся только у них, — в конце
		private static List<KeyValuePair<string, string>> MergeOne(TypeState s, int ident, List<KeyValuePair<string, string>> b,
			List<KeyValuePair<string, string>> o, List<KeyValuePair<string, string>> t, MergeResult result)
		{
			List<string> keys = o.Select(f => f.Key).ToList();
			foreach(KeyValuePair<string, string> f in t) if(!keys.Contains(f.Key)) keys.Add(f.Key);
			if(b != null) foreach(KeyValuePair<string, string> f in b) if(!keys.Contains(f.Key)) keys.Add(f.Key);
			List<KeyValuePair<string, string>> r = new List<KeyValuePair<string, string>>();
			foreach(string k in keys)
			{
				string bv = Find(b, k), ov = Find(o, k), tv = Find(t, k);
				string v;
				if(ov == tv) v = ov;
				else if(ov == bv) v = tv;
				else if(tv == bv) v = ov;
				else
				{
					v = ov;
					result.Conflicts.Add(new MergeConflict { Type = s.Type, Index = ident, Key = k, Base = bv, Ours = ov, Theirs = tv, Note = "оставлено «у нас»" });
				}
				if(v != null) r.Add(new KeyValuePair<string, string>(k, v));
			}
			return r;
		}

		// Элемент, на который ссылается оставшийся элемент, не должен пропасть (например, у нас удалили вершину, а у них
		// поправили линию на ней): такой элемент возвращается из версии, где он есть, и это записывается как конфликт
		private static void ResurrectReferenced(Dictionary<string, TypeState> st, MergeResult result)
		{
			bool changed = true;
			while(changed)
			{
				changed = false;
				foreach(TypeState s in st.Values)
				{
					for(int i = 0; i < s.Idents.Count; i++)
					{
						if(s.Result[i] == null) continue;
						foreach(KeyValuePair<string, string> f in s.Result[i])
						{
							string target = RefTarget(s.Type, f.Key);
							if(target == null || f.Value.Length == 0 || f.Value[0] != REF) continue;
							TypeState ts = st[target];
							int tid = int.Parse(f.Value.Substring(1), CultureInfo.InvariantCulture);
							if(ts.Result[tid] != null) continue;
							Ident id = ts.Idents[tid];
							int v = id.At[O] >= 0 ? O : (id.At[T] >= 0 ? T : B);
							ts.Result[tid] = Norm(ts, v, id.At[v], st);
							result.Conflicts.Add(new MergeConflict { Type = target, Index = tid, Note = "удалён с одной стороны, но на него ссылается " + s.Type + " — возвращён (" + Versions[v] + ")" });
							changed = true;
						}
					}
				}
			}
		}

		#endregion

		#region ================== Building

		// Порядок результата: элементы «у нас» — в нашем порядке; добавленные «у них» — в конце, в их порядке; затем
		// возвращённые из базы. Ссылки перенумеровываются по новым номерам.
		private static UdmfMap Build(Dictionary<string, TypeState> st, List<string> types, UdmfMap[] maps, MergeResult result)
		{
			foreach(TypeState s in st.Values)
			{
				s.FinalIndex = Fill(s.Idents.Count);
				List<int> order = new List<int>();
				for(int o = 0; o < s.Blocks[O].Count; o++) order.Add(s.IdOf[O][o]);
				for(int t = 0; t < s.Blocks[T].Count; t++) if(s.Idents[s.IdOf[T][t]].At[O] < 0) order.Add(s.IdOf[T][t]);
				for(int b = 0; b < s.Blocks[B].Count; b++) { Ident id = s.Idents[s.IdOf[B][b]]; if(id.At[O] < 0 && id.At[T] < 0) order.Add(s.IdOf[B][b]); }
				int n = 0;
				foreach(int id in order) if(s.Result[id] != null && s.FinalIndex[id] < 0) s.FinalIndex[id] = n++;
			}
			foreach(MergeConflict c in result.Conflicts)
			{
				if(c.Index < 0) continue;
				Ident id = st[c.Type].Idents[c.Index];
				c.BaseIndex = id.At[B];
				c.OursIndex = id.At[O];
				c.TheirsIndex = id.At[T];
				c.Index = st[c.Type].FinalIndex[c.Index];
			}
			foreach(MergeConflict c in result.Conflicts)
			{
				c.Base = Denorm(c.Base, c.Type, c.Key, st);
				c.Ours = Denorm(c.Ours, c.Type, c.Key, st);
				c.Theirs = Denorm(c.Theirs, c.Type, c.Key, st);
			}

			UdmfMap map = new UdmfMap();
			map.Globals = maps[O].Globals.Select(g => new UdmfField(g.Key, g.Value)).ToList();
			foreach(UdmfField g in maps[T].Globals) if(!map.Globals.Any(x => x.Key == g.Key)) map.Globals.Add(new UdmfField(g.Key, g.Value));
			map.TypeOrder = new List<string>(maps[O].TypeOrder);
			foreach(string t in types)
			{
				TypeState s = st[t];
				UdmfBlock[] blocks = new UdmfBlock[s.Idents.Count];
				int count = 0;
				for(int id = 0; id < s.Idents.Count; id++)
				{
					if(s.FinalIndex[id] < 0) continue;
					UdmfBlock b = new UdmfBlock(t);
					foreach(KeyValuePair<string, string> f in s.Result[id]) b.Fields.Add(new UdmfField(f.Key, Denorm(f.Value, t, f.Key, st)));
					blocks[s.FinalIndex[id]] = b;
					count++;
				}
				if(count > 0) map.Of(t).AddRange(blocks.Take(count));
			}
			return map;
		}

		private static string Denorm(string value, string type, string key, Dictionary<string, TypeState> st)
		{
			if(value == null || value.Length == 0 || value[0] != REF) return value;
			TypeState ts = st[RefTarget(type, key)];
			int fi = ts.FinalIndex[int.Parse(value.Substring(1), CultureInfo.InvariantCulture)];
			return fi.ToString(CultureInfo.InvariantCulture);
		}

		#endregion
	}
}
