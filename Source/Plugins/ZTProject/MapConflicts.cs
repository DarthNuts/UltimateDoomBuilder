#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Editing;
using CodeImp.DoomBuilder.Geometry;
using CodeImp.DoomBuilder.Map;
using CodeImp.DoomBuilder.ZT;
using ZTMapMerge.Udmf;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Конфликтная карта слияния: три версии из git, результат семантического слияния и выбор по каждому конфликту
	internal sealed class MapConflictFile
	{
		public string Path;          // путь в рабочей копии: wad-src/MAP01/TEXTMAP
		public string MapName;
		public UdmfMap Ours;
		public MergeResult Result;   // в конфликтных полях — «у нас», как у драйвера слияния
		public bool[] TakeTheirs;    // по Result.Conflicts
		public Dictionary<string, TypeCorrespondence> ToMemory;   // «у нас» -> карта, открытая в UDB (для «Показать»)
	}

	// Окно конфликтов карты (ZT/workbench-plan.md §8.2): «Получить» не отменяет слияние, если конфликтуют только карты.
	// Слияние повторяется здесь же тем же UdmfMerger, что у драйвера git; пользователь по каждому полю выбирает «у нас»
	// или «у них» и смотрит элемент на карте; выбор записывается в TEXTMAP, слияние завершается коммитом.
	internal static class MapConflicts
	{
		#region ================== Variables

		private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
		private static readonly Encoding Bytes = Encoding.GetEncoding(28591);

		#endregion

		#region ================== Merge

		// Конфликтный файл — TEXTMAP карты в wad-src/ (им назначен драйвер merge=udmf)
		public static bool IsMapTextmap(string path)
		{
			string[] parts = path.Split('/');
			return parts.Length == 3 && parts[0] == "wad-src" && parts[2] == "TEXTMAP";
		}

		// Три версии каждой конфликтной карты из индекса git (:1: база, :2: у нас, :3: у них) и их слияние.
		// null — не вышло (причина — в why): например, карту удалили с одной стороны.
		public static List<MapConflictFile> Load(string dir, List<string> paths, out string why)
		{
			why = null;
			List<MapConflictFile> files = new List<MapConflictFile>();
			foreach(string path in paths)
			{
				UdmfMap b = Stage(dir, 1, path), o = Stage(dir, 2, path), t = Stage(dir, 3, path);
				if(o == null || t == null) { why = path + ": карта удалена с одной стороны"; return null; }
				MergeResult r = UdmfMerger.Merge(b ?? new UdmfMap(), o, t);
				files.Add(new MapConflictFile { Path = path, MapName = path.Split('/')[1], Ours = o, Result = r, TakeTheirs = new bool[r.Conflicts.Count] });
			}
			return files;
		}

		private static UdmfMap Stage(string dir, int stage, string path)
		{
			GitResult r = Git.Run(dir, "show", ":" + stage + ":" + path);
			return r.Ok ? UdmfMap.Parse(r.StdOut) : null;
		}

		// Можно ли взять «у них». Ссылки на стороны (sidefront/sideback) не берутся: две линии получили бы одну сторону.
		public static bool CanTakeTheirs(MergeConflict c)
		{
			if(c.Key == null || c.Index < 0) return false;
			if(c.Key == "sidefront" || c.Key == "sideback") return false;
			if(c.Type == "linedef" && (c.Key == "v1" || c.Key == "v2") || c.Type == "sidedef" && c.Key == "sector")
			{
				int n;
				return c.Theirs != null && int.TryParse(c.Theirs, out n) && n >= 0;
			}
			return true;
		}

		// Записать выбор в TEXTMAP рабочей копии и отметить файл решённым (git add)
		public static string Apply(string dir, List<MapConflictFile> files)
		{
			foreach(MapConflictFile f in files)
			{
				for(int i = 0; i < f.Result.Conflicts.Count; i++)
				{
					if(!f.TakeTheirs[i]) continue;
					MergeConflict c = f.Result.Conflicts[i];
					UdmfBlock b = f.Result.Map.Of(c.Type)[c.Index];
					if(c.Theirs == null) b.Remove(c.Key); else b.Set(c.Key, c.Theirs);
				}
				File.WriteAllText(System.IO.Path.Combine(dir, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)), f.Result.Map.Write(), Utf8);
				GitResult add = Git.Run(dir, "add", "--", f.Path);
				if(!add.Ok) return f.Path + ": git add не удался\n" + add.Output;
			}
			return null;
		}

		// Для сообщения коммита слияния
		public static string Summary(List<MapConflictFile> files)
		{
			List<string> lines = new List<string>();
			int total = files.Sum(f => f.Result.Conflicts.Count), theirs = files.Sum(f => f.TakeTheirs.Count(x => x));
			lines.Add("Конфликты карт решены в UDB: " + total + " (взято «у них» — " + theirs + ", остальное — «у нас»).");
			foreach(MapConflictFile f in files)
				for(int i = 0; i < f.Result.Conflicts.Count && lines.Count <= 60; i++)
				{
					MergeConflict c = f.Result.Conflicts[i];
					lines.Add("  " + f.MapName + ": " + Element(c) + (c.Key != null ? "." + c.Key : "") + " — " +
					          (c.Key == null ? c.Note : (f.TakeTheirs[i] ? "у них " + (c.Theirs ?? "нет") : "у нас " + (c.Ours ?? "нет"))));
				}
			if(lines.Count > 60) lines.Add("  …");
			return string.Join("\n", lines);
		}

		private static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>
		{
			{ "vertex", "вершина" }, { "linedef", "линия" }, { "sidedef", "сторона" }, { "sector", "сектор" }, { "thing", "вещь" }
		};

		// Элемент — по номеру «у нас» (его видно в UDB до слияния), иначе — по номеру в результате
		public static string Element(MergeConflict c)
		{
			string name;
			if(!TypeNames.TryGetValue(c.Type, out name)) name = c.Type;
			if(c.OursIndex >= 0) return name + " " + c.OursIndex;
			return name + " (у них " + c.TheirsIndex + ")";
		}

		#endregion

		#region ================== Map

		// Показать элемент «у нас» на открытой карте: режим по типу элемента, выделение, вид на элемент
		public static bool Show(MapConflictFile f, MergeConflict c)
		{
			if(General.Map == null || c.OursIndex < 0) return false;
			if(f.ToMemory == null)
			{
				UdmfMap mem = UdmfMap.Parse(Bytes.GetString(ZTMapTools.WriteUdmf(General.Map.Map)));
				f.ToMemory = UdmfMerger.Correspond(f.Ours, mem);
			}
			TypeCorrespondence tc;
			if(!f.ToMemory.TryGetValue(c.Type, out tc) || c.OursIndex >= tc.AToB.Length) return false;
			int i = tc.AToB[c.OursIndex];
			if(i < 0) return false;

			return MapNav.Select(c.Type, new[] { i }, true) > 0;
		}

		#endregion

		#region ================== Window

		// Окно конфликтов. canshow — карта файла открыта в UDB. true — «Завершить слияние», false — «Отменить слияние».
		public static bool Ask(IWin32Window owner, List<MapConflictFile> files, Func<MapConflictFile, bool> canshow)
		{
			using(Form f = new Form
			{
				Text = "ZT: конфликты карты при получении правок",
				FormBorderStyle = FormBorderStyle.Sizable,
				StartPosition = FormStartPosition.CenterParent,
				ClientSize = new Size(980, 460),
				MinimizeBox = false,
				ShowInTaskbar = false
			})
			{
				Label hint = new Label
				{
					Dock = DockStyle.Top, Height = 52, Padding = new Padding(8, 6, 8, 0),
					Text = "С сервера пришли правки, которые меняют те же поля карты, что и ваши, по-разному. Сейчас везде взято «у нас». " +
					       "Выберите, что оставить, и завершите слияние — карта перечитается одним шагом отмены. " +
					       "«Отменить слияние» вернёт всё как до «Получить» (ваши правки остаются в локальном коммите)."
				};
				ListView lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, GridLines = true };
				lv.Columns.Add("Карта", 60);
				lv.Columns.Add("Элемент", 120);
				lv.Columns.Add("Поле", 170);
				lv.Columns.Add("База", 150);
				lv.Columns.Add("У нас", 150);
				lv.Columns.Add("У них", 150);
				lv.Columns.Add("Взять", 90);

				List<KeyValuePair<MapConflictFile, int>> rows = new List<KeyValuePair<MapConflictFile, int>>();
				foreach(MapConflictFile mf in files)
					for(int i = 0; i < mf.Result.Conflicts.Count; i++)
					{
						MergeConflict c = mf.Result.Conflicts[i];
						ListViewItem it = new ListViewItem(new[]
						{
							mf.MapName, Element(c), c.Key ?? c.Note,
							c.Key != null ? c.Base ?? "нет" : "", c.Key != null ? c.Ours ?? "нет" : "", c.Key != null ? c.Theirs ?? "нет" : "", ""
						});
						it.Tag = rows.Count;
						rows.Add(new KeyValuePair<MapConflictFile, int>(mf, i));
						lv.Items.Add(it);
					}

				Action refresh = () =>
				{
					foreach(ListViewItem it in lv.Items)
					{
						KeyValuePair<MapConflictFile, int> row = rows[(int)it.Tag];
						MergeConflict c = row.Key.Result.Conflicts[row.Value];
						it.SubItems[6].Text = c.Key == null ? "оставлен" : (row.Key.TakeTheirs[row.Value] ? "у них" : "у нас");
						it.ForeColor = row.Key.TakeTheirs[row.Value] ? Color.DarkBlue : SystemColors.WindowText;
					}
				};
				Action<bool, bool> take = (theirs, all) =>
				{
					IEnumerable<ListViewItem> items = all ? lv.Items.Cast<ListViewItem>() : lv.SelectedItems.Cast<ListViewItem>();
					foreach(ListViewItem it in items)
					{
						KeyValuePair<MapConflictFile, int> row = rows[(int)it.Tag];
						if(!theirs || CanTakeTheirs(row.Key.Result.Conflicts[row.Value])) row.Key.TakeTheirs[row.Value] = theirs;
					}
					refresh();
				};
				Label status = new Label { Dock = DockStyle.Bottom, Height = 20, Padding = new Padding(8, 2, 8, 0) };
				Action show = () =>
				{
					if(lv.SelectedItems.Count == 0) return;
					KeyValuePair<MapConflictFile, int> row = rows[(int)lv.SelectedItems[0].Tag];
					MergeConflict c = row.Key.Result.Conflicts[row.Value];
					if(!canshow(row.Key)) { status.Text = "Карта " + row.Key.MapName + " не открыта в UDB."; return; }
					Cursor.Current = Cursors.WaitCursor;
					bool ok = Show(row.Key, c);
					Cursor.Current = Cursors.Default;
					status.Text = ok ? "На карте выделено: " + Element(c) + "." : "Элемента " + Element(c) + " нет на открытой карте.";
				};

				FlowLayoutPanel tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 4, 6, 0) };
				Button bshow = new Button { Text = "Показать на карте", Width = 140 };
				Button bours = new Button { Text = "Взять «у нас»", Width = 110 };
				Button btheirs = new Button { Text = "Взять «у них»", Width = 110 };
				Button ballours = new Button { Text = "Всё «у нас»", Width = 100 };
				Button balltheirs = new Button { Text = "Всё «у них»", Width = 100 };
				bshow.Click += (s, e) => show();
				bours.Click += (s, e) => take(false, false);
				btheirs.Click += (s, e) => take(true, false);
				ballours.Click += (s, e) => take(false, true);
				balltheirs.Click += (s, e) => take(true, true);
				lv.DoubleClick += (s, e) => show();
				tools.Controls.AddRange(new Control[] { bshow, bours, btheirs, ballours, balltheirs });

				FlowLayoutPanel bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6) };
				Button cancel = new Button { Text = "Отменить слияние", DialogResult = DialogResult.Cancel, Width = 130 };
				Button ok2 = new Button { Text = "Завершить слияние", DialogResult = DialogResult.OK, Width = 140 };
				bottom.Controls.Add(cancel);
				bottom.Controls.Add(ok2);
				f.CancelButton = cancel;

				f.Controls.Add(lv);
				f.Controls.Add(tools);
				f.Controls.Add(hint);
				f.Controls.Add(status);
				f.Controls.Add(bottom);
				refresh();
				if(lv.Items.Count > 0) lv.Items[0].Selected = true;
				return f.ShowDialog(owner) == DialogResult.OK;
			}
		}

		#endregion
	}
}
