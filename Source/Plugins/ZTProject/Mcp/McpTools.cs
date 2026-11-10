#region ================== Namespaces

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Editing;
using CodeImp.DoomBuilder.Map;
using CodeImp.DoomBuilder.ZT;
using ZTMapMerge.Udmf;

#endregion

namespace CodeImp.DoomBuilder.ZTProject.Mcp
{
	// Ответ инструмента MCP: текст, JSON (как текст) или картинка
	internal sealed class ToolResult
	{
		public readonly List<object> Content = new List<object>();
		public bool IsError;

		public static ToolResult Text(string text) { ToolResult r = new ToolResult(); r.Content.Add(TextItem(text)); return r; }
		public static ToolResult Error(string text) { ToolResult r = Text(text); r.IsError = true; return r; }
		public static ToolResult Json(object value) { return Text(new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value)); }

		public static Dictionary<string, object> TextItem(string text)
		{
			return new Dictionary<string, object> { { "type", "text" }, { "text", text } };
		}

		public Dictionary<string, object> ToJson()
		{
			return new Dictionary<string, object> { { "content", Content }, { "isError", IsError } };
		}
	}

	// Инструменты MCP-сервера UDB. Чтение карты — по снимку TEXTMAP, записанному тем же писателем, что при сохранении:
	// номера и имена полей — как в файле. Правки и действия — только в режиме «чтение и правка».
	internal sealed partial class McpTools
	{
		#region ================== Types

		private sealed class Tool
		{
			public string Name, Description;
			public Dictionary<string, object> Schema;
			public bool Writes;
			public int Timeout = 30000;
			public Func<Dictionary<string, object>, ToolResult> Run;
			public bool OwnThread;     // сам решает, что делать в потоке интерфейса (действия, ожидание окон)
		}

		#endregion

		#region ================== Variables

		public const string Instructions =
			"Сервер Ultimate Doom Builder (форк Zero Tolerance). Карта, открытая в UDB: map_info, get_elements, query_elements, " +
			"get_selection, select; правка полей — set_fields; справочник — config_catalog; скрипты — scripts_list, script_read, " +
			"scripts_compile; проверки — map_analysis. Окна UDB: ui_windows, ui_click, ui_select_rows, ui_set_text, screenshot. Действия UDB и плагина " +
			"ZT: run_action (например ztproject_ztget — «Получить»), undo/redo, zt_set_project, zt_open_map, zt_reload_map. " +
			"Номера элементов и имена полей — как в TEXTMAP; значения — литералы TEXTMAP (строки в кавычках). " +
			"Действие, открывшее модальное окно, продолжается в UDB: смотрите ui_windows и нажимайте кнопки ui_click.";

		private static readonly Encoding Bytes = Encoding.GetEncoding(28591);

		// Счётчик изменений карты: растёт от любого шага отмены, отката, повтора и открытия карты
		public static int Revision;

		private readonly BuilderPlug plug;
		private readonly List<Tool> tools = new List<Tool>();

		#endregion

		#region ================== Constructor

		public McpTools(BuilderPlug plug)
		{
			this.plug = plug;
			Register();
			RegisterMore();
		}

		#endregion

		#region ================== Registry

		public List<object> List(McpMode mode)
		{
			return tools.Where(t => !t.Writes || mode == McpMode.ReadWrite).Select(t => (object)new Dictionary<string, object>
			{
				{ "name", t.Name }, { "description", t.Description }, { "inputSchema", t.Schema }
			}).ToList();
		}

		// Куда инструменты могут писать картинки (save_to): только .png во временной папке или в ZTWorkbench — иначе инструмент
		// «только для чтения» (разрешённый Claude в чате заранее) мог бы перезаписать любой файл пользователя
		private static string SafeOut(string path)
		{
			string full = Path.GetFullPath(path);
			string[] roots = { Path.GetFullPath(Path.GetTempPath()), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZTWorkbench") };
			if(!string.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase) ||
			   !roots.Any(r => full.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
				throw new ArgumentException("save_to — только .png во временной папке (" + roots[0] + ") или в " + roots[1]);
			Directory.CreateDirectory(Path.GetDirectoryName(full));
			return full;
		}

		// Инструменты только для чтения (чат: разрешены Claude заранее, без вопроса)
		public IEnumerable<string> ReadOnlyNames() { return tools.Where(t => !t.Writes).Select(t => t.Name); }

		public ToolResult Call(string name, Dictionary<string, object> args, McpMode mode)
		{
			Tool t = tools.FirstOrDefault(x => x.Name == name);
			if(t == null) return ToolResult.Error("нет инструмента " + name);
			if(t.Writes && mode != McpMode.ReadWrite) return ToolResult.Error("сервер в режиме «только чтение» (ZT → MCP-сервер)");
			try
			{
				if(t.OwnThread) return t.Run(args);
				bool timedout;
				object r = McpServer.OnUi(() => t.Run(args), t.Timeout, out timedout);
				if(timedout) return ToolResult.Error("UDB не ответил за " + t.Timeout / 1000 + " с (занят или открыто модальное окно — ui_windows)");
				return (ToolResult)r;
			}
			catch(Exception ex)
			{
				return ToolResult.Error((ex is TargetException ? ex.InnerException : ex).Message);
			}
		}

		private static Dictionary<string, object> Schema(params object[] props)
		{
			Dictionary<string, object> p = new Dictionary<string, object>();
			List<object> required = new List<object>();
			for(int i = 0; i + 2 < props.Length; i += 3)
			{
				string name = (string)props[i];
				bool req = name.EndsWith("!");
				if(req) { name = name.TrimEnd('!'); required.Add(name); }
				Dictionary<string, object> d = new Dictionary<string, object> { { "description", props[i + 2] } };
				string type = (string)props[i + 1];
				if(type.EndsWith("[]")) { d["type"] = "array"; d["items"] = new Dictionary<string, object> { { "type", type.TrimEnd('[', ']') } }; }
				else d["type"] = type;
				p[name] = d;
			}
			Dictionary<string, object> s = new Dictionary<string, object> { { "type", "object" }, { "properties", p } };
			if(required.Count > 0) s["required"] = required;
			return s;
		}

		private void Add(string name, string description, Dictionary<string, object> schema, bool writes, Func<Dictionary<string, object>, ToolResult> run)
		{
			tools.Add(new Tool { Name = name, Description = description, Schema = schema, Writes = writes, Run = run });
		}

		#endregion

		#region ================== Arguments

		private static object Arg(Dictionary<string, object> a, string key) { object v; return a.TryGetValue(key, out v) ? v : null; }
		private static string Str(Dictionary<string, object> a, string key) { object v = Arg(a, key); return v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }
		private static int Int(Dictionary<string, object> a, string key, int def) { object v = Arg(a, key); return v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : def; }
		private static bool Bool(Dictionary<string, object> a, string key, bool def) { object v = Arg(a, key); return v != null ? Convert.ToBoolean(v, CultureInfo.InvariantCulture) : def; }

		private static List<int> Ints(Dictionary<string, object> a, string key)
		{
			IEnumerable e = Arg(a, key) as IEnumerable;
			if(e == null || e is string) { object v = Arg(a, key); return v != null ? new List<int> { Convert.ToInt32(v, CultureInfo.InvariantCulture) } : new List<int>(); }
			return e.Cast<object>().Select(o => Convert.ToInt32(o, CultureInfo.InvariantCulture)).ToList();
		}

		private static List<string> Strs(Dictionary<string, object> a, string key)
		{
			IEnumerable e = Arg(a, key) as IEnumerable;
			if(e == null || e is string) { string v = Str(a, key); return v != null ? new List<string> { v } : null; }
			return e.Cast<object>().Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).ToList();
		}

		private static string Type(Dictionary<string, object> a)
		{
			string t = (Str(a, "type") ?? "").ToLowerInvariant();
			if(Array.IndexOf(MapNav.Types, t) < 0) throw new ArgumentException("type — один из: " + string.Join(", ", MapNav.Types));
			return t;
		}

		#endregion

		#region ================== Map snapshot

		private static void NeedMap()
		{
			if(General.Map == null) throw new InvalidOperationException("в UDB не открыта карта");
		}

		private static UdmfMap Snapshot()
		{
			NeedMap();
			return UdmfMap.Parse(Bytes.GetString(ZTMapTools.WriteUdmf(General.Map.Map)));
		}

		private static Dictionary<string, object> Fields(UdmfBlock b, List<string> only)
		{
			Dictionary<string, object> d = new Dictionary<string, object>();
			foreach(UdmfField f in b.Fields) if(only == null || only.Contains(f.Key)) d[f.Key] = f.Value;
			return d;
		}

		// Значение из JSON -> литерал TEXTMAP
		private static string Literal(object v)
		{
			if(v is bool) return (bool)v ? "true" : "false";
			if(v is string) return "\"" + ((string)v).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
			return Convert.ToString(v, CultureInfo.InvariantCulture);
		}

		// Сравнение значения TEXTMAP с заданным: числа — численно, строки — без кавычек
		private static bool Matches(string literal, object wanted)
		{
			if(wanted == null) return literal == null;
			if(literal == null) return false;
			string w = Convert.ToString(wanted, CultureInfo.InvariantCulture);
			double a, b;
			if(double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
			   double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return a == b;
			return string.Equals(literal.Trim('"'), w.Trim('"'), StringComparison.OrdinalIgnoreCase);
		}

		#endregion

		#region ================== Tools

		private void Register()
		{
			Add("map_info", "Открытая карта: имя, файл, игровая конфигурация, число элементов, есть ли несохранённые правки, режим, " +
			    "выделение, история отмены (последние шаги), map_revision; плагин ZT: папка проекта, карта проекта, ветка.",
			    Schema(), false, a => ToolResult.Json(MapInfo()));

			Add("get_elements", "Элементы карты по номерам — все поля как в TEXTMAP (ссылки v1/v2/sidefront/sideback/sector — номерами).",
			    Schema("type!", "string", "vertex | linedef | sidedef | sector | thing",
			           "indices!", "integer[]", "номера элементов",
			           "fields", "string[]", "только эти поля"), false, a =>
			{
				string type = Type(a);
				List<string> only = Strs(a, "fields");
				List<UdmfBlock> list = Snapshot().Of(type);
				List<object> r = new List<object>();
				foreach(int i in Ints(a, "indices"))
					r.Add(i >= 0 && i < list.Count ? (object)new Dictionary<string, object> { { "index", i }, { "fields", Fields(list[i], only) } }
					                               : new Dictionary<string, object> { { "index", i }, { "error", "нет такого элемента" } });
				return ToolResult.Json(new Dictionary<string, object> { { "revision", Revision }, { "elements", r } });
			});

			Add("query_elements", "Поиск элементов по значениям полей TEXTMAP (все условия сразу; null — поля нет). Числа сравниваются численно, " +
			    "строки — без кавычек и без учёта регистра.",
			    Schema("type!", "string", "vertex | linedef | sidedef | sector | thing",
			           "where", "object", "поле -> значение, например {\"id\": 10} или {\"texturefloor\": \"FLAT1\"}",
			           "fields", "string[]", "какие поля вернуть (по умолчанию — только номера)",
			           "limit", "integer", "сколько вернуть, по умолчанию 100"), false, a =>
			{
				string type = Type(a);
				Dictionary<string, object> where = Arg(a, "where") as Dictionary<string, object> ?? new Dictionary<string, object>();
				List<string> only = Strs(a, "fields");
				int limit = Int(a, "limit", 100);
				List<UdmfBlock> list = Snapshot().Of(type);
				List<object> r = new List<object>();
				int total = 0;
				for(int i = 0; i < list.Count; i++)
				{
					if(!where.All(w => Matches(list[i].Get(w.Key), w.Value))) continue;
					total++;
					if(r.Count >= limit) continue;
					if(only == null) r.Add(i);
					else r.Add(new Dictionary<string, object> { { "index", i }, { "fields", Fields(list[i], only) } });
				}
				return ToolResult.Json(new Dictionary<string, object> { { "revision", Revision }, { "total", total }, { "elements", r } });
			});

			Add("get_selection", "Что выделено на карте: номера вершин, линий, секторов и вещей.", Schema(), false, a =>
			{
				NeedMap();
				MapSet m = General.Map.Map;
				Func<IEnumerable<int>, List<object>> lim = e => e.Take(1000).Select(i => (object)i).ToList();
				return ToolResult.Json(new Dictionary<string, object>
				{
					{ "mode", General.Editing.Mode != null ? General.Editing.Mode.GetType().Name : null },
					{ "vertex", lim(m.GetSelectedVertices(true).Select(e => e.Index)) },
					{ "linedef", lim(m.GetSelectedLinedefs(true).Select(e => e.Index)) },
					{ "sector", lim(m.GetSelectedSectors(true).Select(e => e.Index)) },
					{ "thing", lim(m.GetSelectedThings(true).Select(e => e.Index)) }
				});
			});

			Add("select", "Выделить элементы на карте и показать их (режим переключается по типу: вершины, линии, секторы, вещи; " +
			    "сторона — своей линией). Карту не меняет.",
			    Schema("type!", "string", "vertex | linedef | sidedef | sector | thing",
			           "indices!", "integer[]", "номера элементов",
			           "focus", "boolean", "показать выделенное (по умолчанию да)"), false, a =>
			{
				NeedMap();
				int n = MapNav.Select(Type(a), Ints(a, "indices"), Bool(a, "focus", true));
				return ToolResult.Text("выделено: " + n);
			});

			Add("set_fields", "Задать поля TEXTMAP у элементов (одним шагом отмены, Ctrl+Z откатывает). Элементы — по номерам " +
			    "(нужен expect_revision из последнего ответа: карта могла измениться) или по условию where. Значения: строка — " +
			    "строковое поле (\"FLAT1\"), число, true/false; null — удалить поле. Ссылки v1/v2/sidefront/sideback/sector не меняются.",
			    Schema("type!", "string", "vertex | linedef | sidedef | sector | thing",
			           "fields!", "object", "поле -> значение, например {\"lightlevel\": 160, \"user_ztdoor\": null}",
			           "indices", "integer[]", "номера элементов",
			           "expect_revision", "integer", "revision из ответа, по которому выбраны номера",
			           "where", "object", "условие, как у query_elements"), true, a =>
			{
				string type = Type(a);
				Dictionary<string, object> fields = Arg(a, "fields") as Dictionary<string, object>;
				if(fields == null || fields.Count == 0) throw new ArgumentException("fields — какие поля задать");
				foreach(string k in fields.Keys)
					if(k == "v1" || k == "v2" || k == "sidefront" || k == "sideback" || k == "sector")
						throw new ArgumentException("ссылку " + k + " этим инструментом не менять");
				UdmfMap snap = Snapshot();
				List<UdmfBlock> list = snap.Of(type);
				List<int> targets;
				Dictionary<string, object> where = Arg(a, "where") as Dictionary<string, object>;
				if(Arg(a, "indices") != null)
				{
					if(Arg(a, "expect_revision") == null || Int(a, "expect_revision", -1) != Revision)
						throw new InvalidOperationException("карта изменилась (revision " + Revision + ") — перезапросите номера");
					targets = Ints(a, "indices");
					foreach(int i in targets) if(i < 0 || i >= list.Count) throw new ArgumentException("нет " + type + " " + i);
				}
				else if(where != null)
					targets = Enumerable.Range(0, list.Count).Where(i => where.All(w => Matches(list[i].Get(w.Key), w.Value))).ToList();
				else throw new ArgumentException("нужны indices (с expect_revision) или where");
				if(targets.Count == 0) return ToolResult.Json(new Dictionary<string, object> { { "changed", 0 }, { "revision", Revision } });

				foreach(int i in targets)
					foreach(KeyValuePair<string, object> f in fields)
						if(f.Value == null) list[i].Remove(f.Key); else list[i].Set(f.Key, Literal(f.Value));
				string why;
				MapReload r = MapReload.ApplyText(Bytes.GetBytes(snap.Write()), "MCP: set_fields " + type, out why);
				if(r == null) return ToolResult.Error("правка не применена: " + why);
				return ToolResult.Json(new Dictionary<string, object>
				{
					{ "targets", targets.Count }, { "changed", r.Changed }, { "revision", Revision }, { "changed_map", General.Map.IsChanged }
				});
			});

			Add("udmf_schema", "Порядок полей TEXTMAP, как их пишет UDB при сохранении (UniversalStreamWriter): по типам элементов — " +
			    "обычные поля по порядку (с типом и значением по умолчанию, которое UDB не пишет), затем флаги в порядке игровой " +
			    "конфигурации (пишутся только true), затем пользовательские поля — в порядке файла. Для скриптов, которые пишут TEXTMAP.",
			    Schema(), false, a =>
			{
				NeedMap();
				CodeImp.DoomBuilder.Config.GameConfiguration cfg = General.Map.Config;
				Func<object[][], List<object>> fields = rows => rows.Select(r => (object)new Dictionary<string, object>
				{
					{ "key", r[0] }, { "type", r[1] }, { "default", r[2] }
				}).ToList();
				Func<IEnumerable<string>, List<object>> uniq = keys => keys.Distinct().Select(k => (object)k).ToList();
				object[][] args = { new object[] { "special", "int", 0 }, new object[] { "arg0", "int", 0 }, new object[] { "arg1", "int", 0 },
				                    new object[] { "arg2", "int", 0 }, new object[] { "arg3", "int", 0 }, new object[] { "arg4", "int", 0 } };
				return ToolResult.Json(new Dictionary<string, object>
				{
					{ "config", General.Map.ConfigSettings.Name },
					{ "note", "default null — поле пишется всегда (если есть); double — формат 0.0##############" },
					{ "vertex", new Dictionary<string, object> {
						{ "fields", fields(new[] { new object[] { "x", "double", null }, new object[] { "y", "double", null },
						                           new object[] { "zceiling", "double", "NaN" }, new object[] { "zfloor", "double", "NaN" } }) },
						{ "flags", new List<object>() } } },
					{ "linedef", new Dictionary<string, object> {
						{ "fields", fields(new[] { new object[] { "id", "int", 0 }, new object[] { "v1", "int", null }, new object[] { "v2", "int", null },
						                           new object[] { "moreids", "string", "" }, new object[] { "sidefront", "int", null },
						                           new object[] { "sideback", "int", -1 } }.Concat(args).ToArray()) },
						{ "flags", uniq(cfg.LinedefFlags.Keys.Concat(cfg.LinedefFlagsTranslation.SelectMany(f => f.Fields))
						                .Concat(cfg.LinedefActivates.Select(x => x.Key))) } } },
					{ "sidedef", new Dictionary<string, object> {
						{ "fields", fields(new[] { new object[] { "offsetx", "int", 0 }, new object[] { "offsety", "int", 0 },
						                           new object[] { "texturetop", "string", "-" }, new object[] { "texturebottom", "string", "-" },
						                           new object[] { "texturemiddle", "string", "-" }, new object[] { "sector", "int", null } }) },
						{ "flags", uniq(cfg.SidedefFlags.Keys) } } },
					{ "sector", new Dictionary<string, object> {
						{ "fields", fields(new[] { new object[] { "heightfloor", "int", null }, new object[] { "heightceiling", "int", null },
						                           new object[] { "texturefloor", "string", null }, new object[] { "textureceiling", "string", null },
						                           new object[] { "lightlevel", "int", null }, new object[] { "special", "int", 0 }, new object[] { "id", "int", 0 },
						                           new object[] { "moreids", "string", "" },
						                           new object[] { "floorplane_a", "double", null }, new object[] { "floorplane_b", "double", null },
						                           new object[] { "floorplane_c", "double", null }, new object[] { "floorplane_d", "double", null },
						                           new object[] { "ceilingplane_a", "double", null }, new object[] { "ceilingplane_b", "double", null },
						                           new object[] { "ceilingplane_c", "double", null }, new object[] { "ceilingplane_d", "double", null } }) },
						{ "flags", uniq(cfg.SectorFlags.Keys.Concat(cfg.CeilingPortalFlags.Keys).Concat(cfg.FloorPortalFlags.Keys)) } } },
					{ "thing", new Dictionary<string, object> {
						{ "fields", fields(new[] { new object[] { "id", "int", 0 }, new object[] { "x", "double", null }, new object[] { "y", "double", null },
						                           new object[] { "height", "double", 0 }, new object[] { "angle", "int", null },
						                           new object[] { "pitch", "int", 0 }, new object[] { "roll", "int", 0 },
						                           new object[] { "scalex", "double", 1 }, new object[] { "scaley", "double", 1 },
						                           new object[] { "type", "int", null } }.Concat(args).ToArray()) },
						{ "flags", uniq(cfg.ThingFlags.Keys.Concat(cfg.ThingFlagsTranslation.SelectMany(f => f.Fields))) } } }
				});
			});

			Add("ui_windows", "Открытые окна UDB: формы (кнопки, надписи, поля, списки со строками) и стандартные окна Windows " +
			    "(MessageBox). Главное окно — только заголовок.", Schema(), false, a => ToolResult.Json(UiAutomation.Windows()));

			Add("screenshot", "Картинка окна UDB (PNG): главного или по подстроке заголовка. save_to — ещё и сохранить в файл.",
			    Schema("window", "string", "подстрока заголовка; пусто — главное окно",
			           "save_to", "string", "путь PNG-файла"), false, a =>
			{
				byte[] png = UiAutomation.Capture(Str(a, "window"));
				ToolResult r = new ToolResult();
				string path = Str(a, "save_to");
				if(!string.IsNullOrEmpty(path)) { File.WriteAllBytes(SafeOut(path), png); r.Content.Add(ToolResult.TextItem("сохранено: " + path)); }
				r.Content.Add(new Dictionary<string, object> { { "type", "image" }, { "mimeType", "image/png" }, { "data", Convert.ToBase64String(png) } });
				return r;
			});

			Add("ui_click", "Нажать кнопку в окне UDB (сначала ищется в стандартных окнах Windows, затем в формах). " +
			    "Без window — в верхнем модальном окне.",
			    Schema("button!", "string", "текст кнопки (точно или подстрока)",
			           "window", "string", "подстрока заголовка окна"), true, a => ToolResult.Text(UiAutomation.Click(Str(a, "window"), Str(a, "button"))));

			Add("ui_select_rows", "Выделить строки списка в окне UDB (списки нумеруются по порядку в окне, как в ui_windows).",
			    Schema("rows!", "integer[]", "номера строк",
			           "list", "integer", "номер списка в окне, по умолчанию 0",
			           "window", "string", "подстрока заголовка окна"), true, a =>
			    ToolResult.Text(UiAutomation.SelectRows(Str(a, "window"), Int(a, "list", 0), Ints(a, "rows"))));

			Add("ui_set_text", "Ввести текст в поле окна UDB (поля нумеруются по порядку, как в ui_windows).",
			    Schema("text!", "string", "текст",
			           "textbox", "integer", "номер поля, по умолчанию 0",
			           "window", "string", "подстрока заголовка окна"), true, a =>
			    ToolResult.Text(UiAutomation.SetText(Str(a, "window"), Int(a, "textbox", 0), Str(a, "text"))));

			tools.Add(new Tool
			{
				Name = "run_action",
				Description = "Выполнить действие UDB или плагина по имени (как в настройках клавиш), например ztproject_ztget " +
				              "(«Получить»), ztproject_ztsend, ztproject_ztreloadmap, builder_undo. Ждёт до wait_ms; если действие " +
				              "открыло модальное окно, возвращает список окон — дальше ui_windows и ui_click.",
				Schema = Schema("action!", "string", "имя действия; без префикса ищется среди действий плагина ZT",
				                "wait_ms", "integer", "сколько ждать завершения, по умолчанию 5000"),
				Writes = true, OwnThread = true, Run = RunAction
			});

			Add("undo", "Отменить шаги (как Ctrl+Z).", Schema("levels", "integer", "сколько шагов, по умолчанию 1"), true, a =>
			{
				NeedMap();
				General.Map.UndoRedo.PerformUndo(Math.Max(1, Int(a, "levels", 1)));
				return ToolResult.Json(MapInfo());
			});

			Add("redo", "Повторить отменённые шаги (как Ctrl+Y).", Schema("levels", "integer", "сколько шагов, по умолчанию 1"), true, a =>
			{
				NeedMap();
				General.Map.UndoRedo.PerformRedo(Math.Max(1, Int(a, "levels", 1)));
				return ToolResult.Json(MapInfo());
			});

			Add("zt_set_project", "Плагин ZT: папка рабочей копии проекта (где лежит wad-src/wad_structure.json).",
			    Schema("dir!", "string", "путь к папке"), true, a => ToolResult.Text(plug.McpSetProject(Str(a, "dir"))));

			tools.Add(new Tool
			{
				Name = "zt_open_map", Writes = true, OwnThread = true,
				Description = "Плагин ZT: открыть карту проекта из wad-src/ без окна параметров (игровая конфигурация и ресурсы — из " +
				              ".dbs этой карты или той же карты другой рабочей копии). Карту с несохранёнными правками не закрывает. Если UDB " +
				              "после загрузки показал окно (Errors and Warnings), возвращает список окон — закрыть ui_click.",
				Schema = Schema("map!", "string", "имя карты, например MAP01",
				                "wait_ms", "integer", "сколько ждать, по умолчанию 120000"),
				Run = a => RunUiAsync("zt_open_map", () => plug.McpOpenMap(Str(a, "map")), Int(a, "wait_ms", 120000))
			});

			Add("zt_get_build", "Плагин ZT: «Получить сборку» — машина собирает WAD и pk3 своей ветки с сервера (в ~/ci, тот же коммит " +
			    "повторно не собирается), плагин скачивает изменившиеся файлы и перечитывает ресурсы UDB. Идёт в фоне: ход — " +
			    "map_info → zt.build.", Schema("dest", "string", "папка для файлов (по умолчанию — папка GZDoom из настроек)"), true,
			    a => ToolResult.Text(plug.GetBuild(Str(a, "dest"))));

			Add("zt_ci_status", "Плагин ZT: итог сборки и проверок на машине после «Отправить» / «Получить» (WAD, pk3, ZScript полным " +
			    "набором модов): «идёт: …», «ok <ветка> <коммит>», «fail …» с ошибками. start — запустить сейчас для текущей ветки.",
			    Schema("start", "boolean", "запустить сборку и проверки сейчас"), false, a =>
			{
				if(Bool(a, "start", false))
				{
					string dir = plug.ProjectDirForMcp;
					if(!ProjectMap.IsProjectDir(dir)) return ToolResult.Error("папка проекта не выбрана");
					plug.StartCi(dir, plug.CurrentBranchForMcp(dir));
				}
				return ToolResult.Text(plug.CiStateForMcp.Length > 0 ? plug.CiStateForMcp : "сборок после push в этом сеансе UDB не было");
			});

			tools.Add(new Tool
			{
				Name = "zt_reload_map", Writes = true, Timeout = 120000,
				Description = "Плагин ZT: перечитать открытую карту проекта из файлов wad-src/ на месте, одним шагом отмены (без вопроса " +
				              "о несохранённых правках — они заменяются версией из файлов).",
				Schema = Schema(),
				Run = a => ToolResult.Text(plug.McpReloadMap())
			});
		}

		// Действие — в поток интерфейса без ожидания: оно может открыть модальное окно и ждать человека (или ui_click)
		private ToolResult RunAction(Dictionary<string, object> a)
		{
			string name = Str(a, "action");
			int wait = Int(a, "wait_ms", 5000);
			bool timedout;
			string full = (string)McpServer.OnUi(() =>
			{
				if(General.Actions.Exists(name)) return name;
				string prefixed = "ztproject_" + name;
				return General.Actions.Exists(prefixed) ? prefixed : null;
			}, 10000, out timedout);
			if(timedout) return ToolResult.Error("UDB занят (открыто модальное окно?) — ui_windows");
			if(full == null) return ToolResult.Error("нет действия «" + name + "»");

			return RunUiAsync(full, () => { General.Actions.InvokeAction(full); return "выполнено"; }, wait);
		}

		// Работа в потоке интерфейса без ожидания дольше wait мс: она может открыть модальное окно и ждать человека (или ui_click).
		// Не дождались — ответ со списком окон; работа в UDB продолжается.
		private static ToolResult RunUiAsync(string what, Func<string> f, int wait)
		{
			System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false);
			Exception error = null;
			string result = null;
			General.MainWindow.BeginInvoke((MethodInvoker)delegate
			{
				try { result = f(); }
				catch(Exception ex) { error = ex; }
				finally { done.Set(); }
			});
			bool finished = done.WaitOne(Math.Max(0, wait));
			if(error != null) return ToolResult.Error(what + ": " + error.Message);
			bool timedout;
			object windows = McpServer.OnUi(() => UiAutomation.Windows(), 10000, out timedout);
			return ToolResult.Json(new Dictionary<string, object>
			{
				{ "action", what }, { "finished", finished },
				{ "result", finished ? result : "ещё выполняется — скорее всего, ждёт ответа в окне (ui_windows, ui_click)" },
				{ "windows", windows }
			});
		}

		private Dictionary<string, object> MapInfo()
		{
			Dictionary<string, object> d = new Dictionary<string, object> { { "revision", Revision } };
			d["zt"] = plug.McpStatus();
			if(General.Map == null) { d["open"] = false; return d; }
			MapSet m = General.Map.Map;
			d["open"] = true;
			d["map"] = General.Map.Options.CurrentName;
			d["file"] = General.Map.FilePathName;
			d["config"] = General.Map.ConfigSettings != null ? General.Map.ConfigSettings.Name : null;
			d["udmf"] = General.Map.UDMF;
			d["changed"] = General.Map.IsChanged;
			d["mode"] = General.Editing.Mode != null ? General.Editing.Mode.GetType().Name : null;
			d["counts"] = new Dictionary<string, object>
			{
				{ "vertex", m.Vertices.Count }, { "linedef", m.Linedefs.Count }, { "sidedef", m.Sidedefs.Count },
				{ "sector", m.Sectors.Count }, { "thing", m.Things.Count }
			};
			d["selected"] = new Dictionary<string, object>
			{
				{ "vertex", m.GetSelectedVertices(true).Count }, { "linedef", m.GetSelectedLinedefs(true).Count },
				{ "sector", m.GetSelectedSectors(true).Count }, { "thing", m.GetSelectedThings(true).Count }
			};
			d["resources"] = General.Map.ConfigSettings.Resources.Select(r => (object)("config: " + r.location))
				.Concat(General.Map.Options.Resources.Select(r => (object)("map: " + r.location))).ToList();
			d["undo"] = General.Map.UndoRedo.GetUndoList().Take(10).Select(u => (object)u.Description).ToList();
			d["redo"] = General.Map.UndoRedo.GetRedoList().Take(10).Select(u => (object)u.Description).ToList();
			return d;
		}

		#endregion
	}
}
