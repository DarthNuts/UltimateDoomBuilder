#region ================== Namespaces

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Compilers;
using CodeImp.DoomBuilder.Config;
using CodeImp.DoomBuilder.Map;

#endregion

namespace CodeImp.DoomBuilder.ZTProject.Mcp
{
	// Инструменты MCP плана §3.1 и §3.4 (ZT/mcp-plan.md): справочник игровой конфигурации, скрипты карты, анализ карты.
	internal sealed partial class McpTools
	{
		#region ================== Registry

		private void RegisterMore()
		{
			Add("config_catalog", "Справочник игровой конфигурации и ресурсов мода: спецы линий (номер, имя, аргументы), эффекты секторов, " +
			    "типы вещей (номер, имя, класс актора — включая акторы мода из DECORATE/ZScript), флаги, UDMF-поля. Поиск по подстроке " +
			    "имени/класса или по номеру.",
			    Schema("kind!", "string", "linedef_specials | sector_effects | thing_types | flags | udmf_fields",
			           "query", "string", "подстрока имени, класса или ключа (без учёта регистра)",
			           "number", "integer", "номер спеца, эффекта или типа вещи",
			           "limit", "integer", "сколько вернуть, по умолчанию 50"), false, a => ToolResult.Json(Catalog(a)));

			Add("scripts_list", "Лампы скриптов открытой карты (SCRIPTS — ACS, DIALOGUE…): размер, число строк, компилятор.",
			    Schema(), false, a => ToolResult.Json(ScriptLumps()));

			Add("script_read", "Текст лампа скрипта карты (как в редакторе скриптов UDB, UTF-8) с номерами строк.",
			    Schema("lump", "string", "имя лампа, по умолчанию SCRIPTS",
			           "from_line", "integer", "с какой строки, по умолчанию 1",
			           "to_line", "integer", "по какую строку, по умолчанию from_line + 199"), false, a => ToolResult.Text(ScriptRead(a)));

			Add("scripts_compile", "Скомпилировать скрипты карты компилятором из конфигурации (ACS — acc), как кнопка Compile редактора " +
			    "скриптов: ошибки с файлом и строкой. Несохранённый текст открытых вкладок берётся из редактора.",
			    Schema(), true, a => ToolResult.Json(CompileScripts()));

			Add("editor_open_file", "Открыть файл (например, src/zscript/ZTDoors.zs рабочей копии) во вкладке редактора скриптов UDB: " +
			    "подсветка по типу (ZScript, DECORATE, ACS, MAPINFO…), хвостовые пробелы при сохранении не срезаются.",
			    Schema("path!", "string", "путь к файлу (абсолютный или от корня рабочей копии)"), false, a =>
			{
				NeedMap();
				string path = Str(a, "path");
				if(!System.IO.Path.IsPathRooted(path)) path = System.IO.Path.Combine(plug.McpStatus()["project_dir"] as string ?? "", path);
				return ZT.ZTMapTools.OpenScriptFile(System.IO.Path.GetFullPath(path), true) ? ToolResult.Text("открыт " + path) : ToolResult.Error("не открыт " + path);
			});

			Add("editor_tabs", "Вкладки редактора скриптов UDB: заголовок, файл (у лампа карты — имя лампа), есть ли несохранённые правки.",
			    Schema(), false, a => ToolResult.Json(ZT.ZTMapTools.ScriptTabs().Select(t => (object)new Dictionary<string, object>
			    {
				    { "title", t.Item1 }, { "file", t.Item2 }, { "changed", t.Item3 }
			    }).ToList()));

			Add("editor_text", "Текст вкладки редактора скриптов по файлу или заголовку; text — заменить (как правка в редакторе, Ctrl+Z " +
			    "откатывает), save — сохранить вкладку (файл — на диск, если его не меняли снаружи; лампу — в карту).",
			    Schema("tab!", "string", "путь файла или заголовок вкладки (SCRIPTS…)",
			           "text", "string", "новый текст",
			           "save", "boolean", "сохранить"), true, a =>
			{
				string tab = Str(a, "tab");
				if(tab != null && (System.IO.Path.IsPathRooted(tab) || tab.Contains("/"))) tab = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(tab) ? tab :
					System.IO.Path.Combine(plug.McpStatus()["project_dir"] as string ?? "", tab));     // пути — как у вкладки
				string r = ZT.ZTMapTools.ScriptTabText(tab, Str(a, "text"), Bool(a, "save", false));
				return r == null ? ToolResult.Error("нет вкладки " + Str(a, "tab")) : ToolResult.Text(r.Length > 20000 ? r.Substring(0, 20000) + "\n…" : r);
			});

			tools.Add(new Tool
			{
				Name = "zt_check_scripts", OwnThread = true,
				Description = "Плагин ZT: проверить скрипты мода (ZScript, DECORATE…) в GZDoom: мод грузится в порядке ZeroTolerance.bat, " +
				              "но с папкой src/ рабочей копии вместо собранного pk3 (-norun, ~7 с без ошибок). Ошибки — с файлом и строкой; " +
				              "они же показываются в редакторе скриптов UDB.",
				Schema = Schema("show", "boolean", "показать ошибки в UDB (по умолчанию да)"),
				Run = a =>
				{
					bool show = Bool(a, "show", true);
					ManualResetEvent done = new ManualResetEvent(false);
					BuilderPlug.CheckResult result = null;
					bool timedout;
					string started = (string)McpServer.OnUi(() => plug.CheckScripts(r =>
					{
						result = r;
						if(show) plug.ShowCheckPublic(r);
						done.Set();
					}), 20000, out timedout);
					if(timedout) return ToolResult.Error("UDB занят (модальное окно?) — ui_windows");
					if(started != null) return ToolResult.Error(started);
					if(!done.WaitOne(240000)) return ToolResult.Error("проверка не закончилась за 4 минуты");
					return ToolResult.Json(new Dictionary<string, object>
					{
						{ "ok", result.Ok }, { "summary", result.Summary },
						{ "errors", result.Errors.Select(e => (object)new Dictionary<string, object> { { "file", e.filename }, { "line", e.linenumber + 1 }, { "text", e.description } }).ToList() }
					});
				}
			});

			Add("render_2d", "Схема области карты в PNG: линии (белые — односторонние, серые — двусторонние, голубые — со спецом), вещи, " +
			    "подсвеченные элементы с номерами (секторы залиты). Вид пользователя в UDB не меняется. Для отчётов и проверки разметки.",
			    Schema("highlight", "object", "что подсветить: {\"sector\": [40], \"linedef\": [12, 13], \"thing\": [5]}",
			           "rect", "number[]", "область [x1, y1, x2, y2]; по умолчанию — вокруг подсвеченного (иначе вся карта)",
			           "margin", "number", "отступ вокруг подсвеченного, по умолчанию 512",
			           "width", "integer", "ширина картинки, по умолчанию 1200",
			           "labels", "boolean", "подписать номера подсвеченного (по умолчанию да)",
			           "things", "boolean", "рисовать вещи (по умолчанию да)",
			           "save_to", "string", "сохранить PNG в файл"), false, a =>
			{
				NeedMap();
				Dictionary<string, List<int>> hl = new Dictionary<string, List<int>>();
				Dictionary<string, object> h = Arg(a, "highlight") as Dictionary<string, object>;
				if(h != null) foreach(KeyValuePair<string, object> kv in h) hl[kv.Key.ToLowerInvariant()] = Ints(h, kv.Key);
				System.Collections.IEnumerable re = Arg(a, "rect") as System.Collections.IEnumerable;
				double[] rect = re != null ? re.Cast<object>().Select(o => Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null;
				object mo = Arg(a, "margin");
				double margin = mo != null ? Convert.ToDouble(mo, System.Globalization.CultureInfo.InvariantCulture) : 512;
				byte[] png = MapRender.Render(hl, rect, margin, Int(a, "width", 1200), Bool(a, "labels", true), Bool(a, "things", true));
				ToolResult r = new ToolResult();
				string path = Str(a, "save_to");
				if(!string.IsNullOrEmpty(path)) { System.IO.File.WriteAllBytes(path, png); r.Content.Add(ToolResult.TextItem("сохранено: " + path)); }
				r.Content.Add(new Dictionary<string, object> { { "type", "image" }, { "mimeType", "image/png" }, { "data", Convert.ToBase64String(png) } });
				return r;
			});

			Add("zt_test_map", "Плагин ZT: тест открытой карты в игре — GZDoom в порядке ZeroTolerance.bat, src/ вместо собранного pk3, " +
			    "карта из UDB со скомпилированными скриптами. Игра открывается на экране пользователя. В ответе — лог GZDoom.",
			    Schema(), true, a =>
			{
				string log;
				string err = plug.TestMap(out log);
				return err != null ? ToolResult.Error(err) : ToolResult.Json(new Dictionary<string, object> { { "started", true }, { "log", log } });
			});

			tools.Add(new Tool
			{
				Name = "map_analysis", OwnThread = true,
				Description = "Встроенный анализ карты UDB (Map Analysis: незакрытые секторы, неизвестные ACS-скрипты, полиобъекты, " +
				              "пропущенные активации, перекрытые линии, неизвестные текстуры…): результаты с элементами. checks — подстроки " +
				              "имён проверок (по умолчанию — отмеченные в UDB); имена проверок — в ответе (available).",
				Schema = Schema("checks", "string[]", "какие проверки, например [\"unknown scripts\", \"polyobjects\"]",
				                "limit", "integer", "сколько результатов вернуть, по умолчанию 200",
				                "keep_open", "boolean", "оставить режим анализа открытым (по умолчанию — вернуть прежний режим)"),
				Run = MapAnalysis
			});
		}

		#endregion

		#region ================== Catalog

		private static bool Like(string text, string query)
		{
			return string.IsNullOrEmpty(query) || (text ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static object Catalog(Dictionary<string, object> a)
		{
			NeedMap();
			GameConfiguration cfg = General.Map.Config;
			string kind = Str(a, "kind"), q = Str(a, "query");
			int number = Int(a, "number", int.MinValue), limit = Int(a, "limit", 50);
			Func<ArgumentInfo[], List<object>> args = list => list == null ? new List<object>() :
				list.Select((x, i) => new { x, i }).Where(p => p.x.Used).Select(p => (object)(p.i + ": " + p.x.Title)).ToList();
			IEnumerable<Dictionary<string, object>> items;
			switch(kind)
			{
				case "linedef_specials":
					items = cfg.SortedLinedefActions.Where(x => (number == int.MinValue || x.Index == number) && (Like(x.Title, q) || Like(x.Category, q)))
						.Select(x => new Dictionary<string, object> { { "number", x.Index }, { "title", x.Title }, { "category", x.Category }, { "args", args(x.Args) } });
					break;
				case "sector_effects":
					items = cfg.SortedSectorEffects.Where(x => (number == int.MinValue || x.Index == number) && Like(x.Title, q))
						.Select(x => new Dictionary<string, object> { { "number", x.Index }, { "title", x.Title } });
					break;
				case "thing_types":
					items = General.Map.Data.ThingTypes.OrderBy(x => x.Index)
						.Where(x => (number == int.MinValue || x.Index == number) && (Like(x.Title, q) || Like(x.ClassName, q)))
						.Select(x => new Dictionary<string, object>
						{
							{ "number", x.Index }, { "title", x.Title }, { "class", x.ClassName },
							{ "category", x.Category != null ? x.Category.Title : null }, { "args", args(x.Args) }
						});
					break;
				case "flags":
					Func<string, IEnumerable<KeyValuePair<string, string>>, IEnumerable<Dictionary<string, object>>> fl = (t, d) =>
						d.Where(f => Like(f.Key, q) || Like(f.Value, q)).Select(f => new Dictionary<string, object> { { "element", t }, { "key", f.Key }, { "title", f.Value } });
					items = fl("linedef", cfg.LinedefFlags)
						.Concat(fl("linedef_activation", cfg.LinedefActivates.Select(x => new KeyValuePair<string, string>(x.Key, x.Title))))
						.Concat(fl("sidedef", cfg.SidedefFlags)).Concat(fl("sector", cfg.SectorFlags)).Concat(fl("thing", cfg.ThingFlags));
					break;
				case "udmf_fields":
					Func<string, List<UniversalFieldInfo>, IEnumerable<Dictionary<string, object>>> uf = (t, l) =>
						l.Where(f => Like(f.Name, q)).Select(f => new Dictionary<string, object> { { "element", t }, { "key", f.Name }, { "type", Enum.GetName(typeof(CodeImp.DoomBuilder.Types.UniversalType), f.Type) ?? f.Type.ToString() }, { "default", f.Default } });
					items = uf("vertex", cfg.VertexFields).Concat(uf("linedef", cfg.LinedefFields)).Concat(uf("sidedef", cfg.SidedefFields))
						.Concat(uf("sector", cfg.SectorFields)).Concat(uf("thing", cfg.ThingFields));
					break;
				default:
					throw new ArgumentException("kind — linedef_specials | sector_effects | thing_types | flags | udmf_fields");
			}
			List<Dictionary<string, object>> all = items.ToList();
			return new Dictionary<string, object> { { "total", all.Count }, { "items", all.Take(limit).ToList() } };
		}

		#endregion

		#region ================== Scripts

		private static IEnumerable<MapLumpInfo> ScriptLumpInfos()
		{
			return General.Map.Config.MapLumps.Values.Where(l => l.ScriptBuild || l.Script != null);
		}

		private static List<object> ScriptLumps()
		{
			NeedMap();
			List<object> list = new List<object>();
			foreach(MapLumpInfo l in ScriptLumpInfos())
			{
				System.IO.MemoryStream data = General.Map.GetLumpData(l.Name);
				byte[] b = data != null ? data.ToArray() : new byte[0];
				list.Add(new Dictionary<string, object>
				{
					{ "lump", l.Name }, { "bytes", b.Length }, { "lines", b.Length == 0 ? 0 : b.Count(x => x == '\n') + 1 },
					{ "compiler", l.ScriptBuild ? General.Map.Options.ScriptCompiler : (l.Script != null ? l.Script.ToString() : null) }
				});
			}
			return list;
		}

		private static string ScriptRead(Dictionary<string, object> a)
		{
			NeedMap();
			string lump = (Str(a, "lump") ?? "SCRIPTS").ToUpperInvariant();
			System.IO.MemoryStream data = General.Map.GetLumpData(lump);
			if(data == null) throw new ArgumentException("у карты нет лампа " + lump);
			string[] lines = Encoding.UTF8.GetString(data.ToArray()).Replace("\r\n", "\n").Split('\n');
			int from = Math.Max(1, Int(a, "from_line", 1)), to = Math.Min(lines.Length, Int(a, "to_line", from + 199));
			StringBuilder sb = new StringBuilder();
			sb.Append(lump).Append(": строки ").Append(from).Append('–').Append(to).Append(" из ").Append(lines.Length).Append('\n');
			for(int i = from; i <= to; i++) sb.Append(i.ToString().PadLeft(5)).Append("  ").Append(lines[i - 1]).Append('\n');
			return sb.ToString();
		}

		private static object CompileScripts()
		{
			NeedMap();
			if(General.Map.IsScriptsWindowOpen) General.Map.ScriptEditor.Editor.ImplicitSave();
			List<CompilerError> errors = new List<CompilerError>();
			List<object> compiled = new List<object>();
			bool success = true;
			foreach(MapLumpInfo l in ScriptLumpInfos())
			{
				ScriptConfiguration sc = l.Script;
				if(sc == null && l.ScriptBuild && General.CompiledScriptConfigs.ContainsKey(General.Map.Options.ScriptCompiler))
					sc = General.CompiledScriptConfigs[General.Map.Options.ScriptCompiler];
				if(sc == null || !General.Map.LumpExists(l.Name)) continue;
				success &= General.Map.TemporaryMapFile.CompileLump(l.Name, sc, errors);
				compiled.Add(l.Name);
			}
			General.Map.LoadACS();
			return new Dictionary<string, object>
			{
				{ "success", success && errors.Count == 0 }, { "compiled", compiled },
				{ "errors", errors.Select(e => (object)new Dictionary<string, object> { { "file", e.filename }, { "line", e.linenumber >= 0 ? (object)(e.linenumber + 1) : null }, { "text", e.description } }).ToList() }
			};
		}

		#endregion

		#region ================== Map analysis

		private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

		private static Form AnalysisForm()
		{
			return Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().Name == "ErrorCheckForm" && f.Visible);
		}

		private static IEnumerable<T> Find<T>(Control parent) where T : Control
		{
			foreach(Control c in parent.Controls)
			{
				if(c is T) yield return (T)c;
				foreach(T x in Find<T>(c)) yield return x;
			}
		}

		private ToolResult MapAnalysis(Dictionary<string, object> a)
		{
			List<string> want = Strs(a, "checks");
			int limit = Int(a, "limit", 200);
			bool keep = Bool(a, "keep_open", false);
			bool timedout;
			string previous = null;
			List<string> available = null;

			// Режим анализа, выбор проверок, «Start Analysis»
			string start = (string)McpServer.OnUi(() =>
			{
				NeedMap();
				previous = General.Editing.Mode != null ? General.Editing.Mode.GetType().Name : null;
				if(previous != "ErrorCheckMode") General.Editing.ChangeMode("ErrorCheckMode");
				Form f = AnalysisForm();
				if(f == null) return "окно Map Analysis не открылось";
				List<CheckBox> boxes = Find<CheckBox>(f).Where(c => c.Tag is Type).ToList();
				available = boxes.Select(c => c.Text).ToList();
				if(want != null)
					foreach(CheckBox c in boxes) c.Checked = want.Any(w => c.Text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
				Button go = Find<Button>(f).FirstOrDefault(b => b.Name == "buttoncheck");
				if(go == null) return "нет кнопки Start Analysis";
				go.PerformClick();
				return null;
			}, 30000, out timedout);
			if(timedout) return ToolResult.Error("UDB занят (модальное окно?) — ui_windows");
			if(start != null) return ToolResult.Error(start);

			// Ждём конца проверок (они идут в потоках UDB)
			DateTime until = DateTime.Now.AddMinutes(5);
			while(true)
			{
				Thread.Sleep(500);
				object running = McpServer.OnUi(() =>
				{
					Form f = AnalysisForm();
					FieldInfo fi = f != null ? f.GetType().GetField("running", Hidden) : null;
					return fi != null && (bool)fi.GetValue(f);
				}, 10000, out timedout);
				if(!timedout && !(bool)running) break;
				if(DateTime.Now > until) return ToolResult.Error("анализ не закончился за 5 минут");
			}

			// Результаты: описание и элементы карты
			object result = McpServer.OnUi(() =>
			{
				Form f = AnalysisForm();
				ListBox lb = f != null ? Find<ListBox>(f).FirstOrDefault(x => x.Name == "results") : null;
				List<object> items = new List<object>();
				int total = 0;
				if(lb != null)
					foreach(object r in lb.Items)
					{
						if(r.GetType().Name == "ResultNoErrors") continue;
						total++;
						if(items.Count >= limit) continue;
						PropertyInfo dp = r.GetType().GetProperty("Description");
						FieldInfo vf = null;
						for(Type t = r.GetType(); t != null && vf == null; t = t.BaseType) vf = t.GetField("viewobjects", Hidden);
						List<object> els = new List<object>();
						IEnumerable objs = vf != null ? vf.GetValue(r) as IEnumerable : null;
						if(objs != null)
							foreach(object o in objs)
							{
								MapElement me = o as MapElement;
								if(me == null) continue;
								string type = me is Vertex ? "vertex" : me is Linedef ? "linedef" : me is Sidedef ? "sidedef" : me is Sector ? "sector" : me is Thing ? "thing" : me.GetType().Name;
								els.Add(type + " " + me.Index);
							}
						items.Add(new Dictionary<string, object>
						{
							{ "check", r.GetType().Name }, { "text", dp != null ? dp.GetValue(r) as string : r.ToString() }, { "elements", els }
						});
					}
				if(!keep && previous != null && previous != "ErrorCheckMode") General.Editing.ChangeMode(previous);
				return new Dictionary<string, object> { { "total", total }, { "results", items }, { "available", available } };
			}, 30000, out timedout);
			if(timedout) return ToolResult.Error("UDB не ответил при чтении результатов");
			return ToolResult.Json(result);
		}

		#endregion
	}
}
