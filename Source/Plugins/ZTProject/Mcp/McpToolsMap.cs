#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CodeImp.DoomBuilder.Geometry;
using CodeImp.DoomBuilder.Map;
using ZTMapMerge.Udmf;

#endregion

namespace CodeImp.DoomBuilder.ZTProject.Mcp
{
	// Инструменты MCP для разведки карты (ZT/mcp-plan.md §3.1, §3.4): сектор в точке, элемент с соседями, полиобъекты,
	// линейные порталы, ссылки линий и вещей на ACS-скрипты. Номера и поля — как в TEXTMAP.
	internal sealed partial class McpTools
	{
		#region ================== Registry

		// ACS_Execute, ACS_LockedExecute, ACS_ExecuteWithResult, ACS_LockedExecuteDoor, ACS_ExecuteAlways
		private static readonly int[] AcsSpecials = { 80, 83, 84, 85, 226 };

		private void RegisterMap()
		{
			Add("sector_at", "Сектор в точке карты (x, y): номер, высоты пола и потолка в этой точке (с учётом наклона), текстуры, свет, " +
			    "тег, спец.", Schema("x!", "number", "x", "y!", "number", "y"), false, a =>
			{
				NeedMap();
				double x = Num(a, "x"), y = Num(a, "y");
				Sector s = General.Map.Map.GetSectorByCoordinates(new Vector2D(x, y));
				if(s == null) return ToolResult.Json(new Dictionary<string, object> { { "sector", null }, { "note", "точка вне секторов" } });
				return ToolResult.Json(SectorInfo(s, new Vector2D(x, y)));
			});

			Add("element_info", "Элемент карты с соседями: сектор — стороны и линии контура, соседние секторы, вещи внутри, рамка; линия — " +
			    "вершины, длина, угол, стороны и их секторы; сторона — линия и сектор; вершина — линии; вещь — сектор под ней.",
			    Schema("type!", "string", "vertex | linedef | sidedef | sector | thing", "index!", "integer", "номер",
			           "limit", "integer", "сколько соседей каждого вида вернуть, по умолчанию 100"), false, a =>
			{
				NeedMap();
				string type = Type(a);
				int i = Int(a, "index", -1), limit = Int(a, "limit", 100);
				MapSet m = General.Map.Map;
				Dictionary<string, object> d = new Dictionary<string, object> { { "type", type }, { "index", i }, { "revision", Revision } };
				switch(type)
				{
					case "sector":
					{
						Sector s = m.GetSectorByIndex(i);
						if(s == null) return ToolResult.Error("нет сектора " + i);
						d["info"] = SectorInfo(s, null);
						d["sidedefs"] = s.Sidedefs.Take(limit).Select(sd => (object)sd.Index).ToList();
						d["linedefs"] = s.Sidedefs.Select(sd => sd.Line.Index).Distinct().Take(limit).Select(x => (object)x).ToList();
						d["neighbours"] = s.Sidedefs.Select(sd => sd.Other).Where(o => o != null && o.Sector != null && o.Sector != s)
							.Select(o => o.Sector.Index).Distinct().OrderBy(x => x).Take(limit).Select(x => (object)x).ToList();
						d["things"] = m.Things.Where(t => { t.DetermineSector(); return t.Sector == s; }).Take(limit)
							.Select(t => (object)new Dictionary<string, object> { { "index", t.Index }, { "type", t.Type }, { "x", t.Position.x }, { "y", t.Position.y } }).ToList();
						break;
					}
					case "linedef":
					{
						Linedef l = m.GetLinedefByIndex(i);
						if(l == null) return ToolResult.Error("нет линии " + i);
						d["v1"] = Pt(l.Start); d["v2"] = Pt(l.End);
						d["length"] = Math.Round(l.Length, 3);
						d["angle"] = Math.Round(Angle2D.RadToDeg(l.Angle), 2);
						d["special"] = l.Action;
						d["front"] = SideInfo(l.Front);
						d["back"] = SideInfo(l.Back);
						break;
					}
					case "sidedef":
					{
						Sidedef sd = m.GetSidedefByIndex(i);
						if(sd == null) return ToolResult.Error("нет стороны " + i);
						d["linedef"] = sd.Line.Index; d["front"] = sd.IsFront; d["sector"] = sd.Sector != null ? (object)sd.Sector.Index : null;
						d["other_sector"] = sd.Other != null && sd.Other.Sector != null ? (object)sd.Other.Sector.Index : null;
						break;
					}
					case "vertex":
					{
						Vertex v = m.GetVertexByIndex(i);
						if(v == null) return ToolResult.Error("нет вершины " + i);
						d["x"] = v.Position.x; d["y"] = v.Position.y;
						d["linedefs"] = v.Linedefs.Take(limit).Select(l => (object)l.Index).ToList();
						break;
					}
					case "thing":
					{
						Thing t = m.GetThingByIndex(i);
						if(t == null) return ToolResult.Error("нет вещи " + i);
						t.DetermineSector();
						d["x"] = t.Position.x; d["y"] = t.Position.y; d["z"] = t.Position.z; d["thingtype"] = t.Type; d["angle"] = t.AngleDoom;
						d["sector"] = t.Sector != null ? (object)t.Sector.Index : null;
						break;
					}
				}
				UdmfMap snap = Snapshot();
				List<UdmfBlock> list = snap.Of(type);
				if(i >= 0 && i < list.Count) d["fields"] = Fields(list[i], null);
				return ToolResult.Json(d);
			});

			Add("list_polyobjects", "Полиобъекты карты: номер, стартовые линии (Polyobj_StartLine / ExplicitLine), якорь (вещь 9300) и точка " +
			    "старта (9301–9303), зеркало; проблемы — нет якоря или точки, дубли.", Schema(), false, a =>
			{
				NeedMap();
				MapSet m = General.Map.Map;
				Dictionary<int, Dictionary<string, object>> po = new Dictionary<int, Dictionary<string, object>>();
				Func<int, Dictionary<string, object>> get = n =>
				{
					Dictionary<string, object> e;
					if(!po.TryGetValue(n, out e))
						po[n] = e = new Dictionary<string, object> { { "number", n }, { "lines", new List<object>() }, { "anchors", new List<object>() }, { "spots", new List<object>() } };
					return e;
				};
				foreach(Linedef l in m.Linedefs)
				{
					if(l.Action != 1 && l.Action != 5) continue;
					Dictionary<string, object> e = get(l.Args[0]);
					((List<object>)e["lines"]).Add(new Dictionary<string, object> { { "index", l.Index }, { "special", l.Action == 1 ? "StartLine" : "ExplicitLine" } });
					if(l.Action == 1 && l.Args[1] != 0) e["mirror"] = l.Args[1];
					if(l.Action == 5 && l.Args[2] != 0) e["mirror"] = l.Args[2];
				}
				foreach(Thing t in m.Things)
				{
					if(t.Type < 9300 || t.Type > 9303) continue;
					Dictionary<string, object> e = get(t.AngleDoom);
					object pt = new Dictionary<string, object> { { "index", t.Index }, { "type", t.Type }, { "x", t.Position.x }, { "y", t.Position.y } };
					((List<object>)e[t.Type == 9300 ? "anchors" : "spots"]).Add(pt);
				}
				foreach(Dictionary<string, object> e in po.Values)
				{
					List<string> issues = new List<string>();
					int nl = ((List<object>)e["lines"]).Count, na = ((List<object>)e["anchors"]).Count, ns = ((List<object>)e["spots"]).Count;
					if(nl == 0) issues.Add("нет стартовой линии");
					if(na == 0) issues.Add("нет якоря 9300");
					if(ns == 0) issues.Add("нет точки старта 9301–9303 — мёртвый: GZDoom его не создаст");
					if(na > 1) issues.Add("якорей " + na);
					if(ns > 1) issues.Add("точек старта " + ns);
					e["issues"] = issues;
				}
				List<Dictionary<string, object>> all = po.Values.OrderBy(e => (int)e["number"]).ToList();
				return ToolResult.Json(new Dictionary<string, object> { { "total", all.Count }, { "with_issues", all.Count(e => ((List<string>)e["issues"]).Count > 0) }, { "polyobjects", all } });
			});

			Add("list_portals", "Линейные порталы (Line_SetPortal, спец 156): линия, её id, id линии-цели и найденные цели, тип портала.",
			    Schema(), false, a =>
			{
				NeedMap();
				MapSet m = General.Map.Map;
				string[] kinds = { "visual", "teleporter", "interactive", "static" };
				List<object> list = new List<object>();
				foreach(Linedef l in m.Linedefs.Where(x => x.Action == 156))
				{
					int target = l.Args[0];
					List<object> found = m.Linedefs.Where(x => x != l && x.Tags.Contains(target) && target != 0).Select(x => (object)x.Index).ToList();
					list.Add(new Dictionary<string, object>
					{
						{ "linedef", l.Index }, { "id", l.Tag }, { "target_id", target }, { "targets", found },
						{ "type", l.Args[2] >= 0 && l.Args[2] < kinds.Length ? kinds[l.Args[2]] : l.Args[2].ToString(CultureInfo.InvariantCulture) },
						{ "issue", target != 0 && found.Count == 0 ? "нет линии с id цели" : null }
					});
				}
				return ToolResult.Json(new Dictionary<string, object> { { "total", list.Count }, { "portals", list } });
			});

			Add("check_script_refs", "Ссылки линий и вещей на ACS-скрипты (спецы ACS_Execute 80, LockedExecute 83, ExecuteWithResult 84, " +
			    "LockedExecuteDoor 85, ExecuteAlways 226): вызов несуществующего скрипта по имени (arg0str) или по номеру, вызов по номеру " +
			    "на карте с именованными скриптами (случай кабины 500 MAP02). Имена — из скомпилированных скриптов карты и библиотек UDB.",
			    Schema("limit", "integer", "сколько проблем вернуть, по умолчанию 200"), false, a =>
			{
				NeedMap();
				int limit = Int(a, "limit", 200);
				UdmfMap snap = Snapshot();
				// Имена и номера скриптов — из текста SCRIPTS карты (разбор UDB на скриптах мода падает и очищает списки), плюс
				// то, что знает UDB (библиотеки)
				HashSet<string> names; HashSet<int> numbers;
				ParseScripts(out names, out numbers);
				foreach(string k in General.Map.NamedScripts.Keys) names.Add(k.ToLowerInvariant());
				foreach(int k in General.Map.NumberedScripts.Keys) numbers.Add(k);
				bool named = names.Count > 0, numbered = numbers.Count > 0;
				List<object> issues = new List<object>();
				int calls = 0;
				foreach(string type in new[] { "linedef", "thing" })
				{
					List<UdmfBlock> list = snap.Of(type);
					for(int i = 0; i < list.Count; i++)
					{
						int special = list[i].GetInt("special", 0);
						if(Array.IndexOf(AcsSpecials, special) < 0) continue;
						calls++;
						string name = list[i].Get("arg0str");
						string problem = null, target;
						if(name != null)
						{
							name = name.Trim('"');
							target = "\"" + name + "\"";
							if(!names.Contains(name.ToLowerInvariant())) problem = "нет скрипта с именем " + target;
						}
						else
						{
							int num = list[i].GetInt("arg0", 0);
							target = "№" + num;
							if(!numbers.Contains(num))
								problem = "нет скрипта " + target + (named && !numbered ? " — на карте только именованные скрипты (вызов должен быть по имени, arg0str)" : "");
						}
						if(problem != null && issues.Count < limit)
							issues.Add(new Dictionary<string, object> { { "element", type + " " + i }, { "special", special }, { "script", target }, { "problem", problem } });
					}
				}
				return ToolResult.Json(new Dictionary<string, object>
				{
					{ "calls", calls }, { "named_scripts", names.Count }, { "numbered_scripts", numbers.Count },
					{ "issues", issues }
				});
			});
		}

		#endregion

		#region ================== Helpers

		private static readonly System.Text.RegularExpressions.Regex ScriptDecl =
			new System.Text.RegularExpressions.Regex(@"\bscript\s+(?:""(?<name>[^""]+)""|(?<num>\d+))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

		// Объявления скриптов в SCRIPTS карты: «script "Имя"» и «script 12» (комментарии и строки вне объявлений не в счёт)
		private static void ParseScripts(out HashSet<string> names, out HashSet<int> numbers)
		{
			names = new HashSet<string>(); numbers = new HashSet<int>();
			System.IO.MemoryStream data = General.Map.GetLumpData("SCRIPTS");
			if(data == null) return;
			string text = System.Text.Encoding.UTF8.GetString(data.ToArray());
			text = System.Text.RegularExpressions.Regex.Replace(text, @"/\*.*?\*/", " ",System.Text.RegularExpressions.RegexOptions.Singleline);
			text = System.Text.RegularExpressions.Regex.Replace(text, @"//[^\n]*", " ");
			foreach(System.Text.RegularExpressions.Match m in ScriptDecl.Matches(text))
			{
				if(m.Groups["name"].Success) names.Add(m.Groups["name"].Value.ToLowerInvariant());
				else numbers.Add(int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture));
			}
		}

		private static double Num(Dictionary<string, object> a, string key)
		{
			object v = Arg(a, key);
			if(v == null) throw new ArgumentException("нужно " + key);
			return Convert.ToDouble(v, CultureInfo.InvariantCulture);
		}

		private static Dictionary<string, object> Pt(Vertex v)
		{
			return new Dictionary<string, object> { { "index", v.Index }, { "x", v.Position.x }, { "y", v.Position.y } };
		}

		private static object SideInfo(Sidedef sd)
		{
			if(sd == null) return null;
			return new Dictionary<string, object>
			{
				{ "sidedef", sd.Index }, { "sector", sd.Sector != null ? (object)sd.Sector.Index : null },
				{ "upper", sd.HighTexture }, { "middle", sd.MiddleTexture }, { "lower", sd.LowTexture }
			};
		}

		private static Dictionary<string, object> SectorInfo(Sector s, Vector2D? at)
		{
			Dictionary<string, object> d = new Dictionary<string, object>
			{
				{ "sector", s.Index }, { "floor", s.FloorHeight }, { "ceiling", s.CeilHeight },
				{ "floor_texture", s.FloorTexture }, { "ceiling_texture", s.CeilTexture }, { "light", s.Brightness },
				{ "tag", s.Tag }, { "special", s.Effect },
				{ "bbox", new[] { (object)s.BBox.Left, s.BBox.Top, s.BBox.Right, s.BBox.Bottom } }
			};
			if(at.HasValue)
			{
				d["floor_at_point"] = Math.Round(Sector.GetFloorPlane(s).GetZ(at.Value), 3);
				d["ceiling_at_point"] = Math.Round(Sector.GetCeilingPlane(s).GetZ(at.Value), 3);
			}
			return d;
		}

		#endregion
	}
}
