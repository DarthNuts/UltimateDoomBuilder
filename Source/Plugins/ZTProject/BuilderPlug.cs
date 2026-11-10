#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Actions;
using CodeImp.DoomBuilder.Controls;
using CodeImp.DoomBuilder.Editing;
using CodeImp.DoomBuilder.Geometry;
using CodeImp.DoomBuilder.Plugins;
using CodeImp.DoomBuilder.Windows;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Режим проекта Zero Tolerance (ZT/workbench-plan.md, этап 2):
	// - карта открывается прямо из wad-src/ рабочей копии git и после каждого сохранения раскладывается обратно в файлы;
	// - «Отправить свежие правки» — сохранить, закоммитить всё, отправить свою ветку;
	// - «Получить свежие правки» — сохранить, закоммитить локальное, слить свою ветку с сервера, перечитать карту.
	public class BuilderPlug : Plug
	{
		#region ================== Variables

		private static BuilderPlug me;

		private ToolStripMenuItem menu;
		private ToolStripSeparator separator;
		private ToolStripButton sendbutton;
		private ToolStripButton getbutton;
		private ToolStripStatusLabel branchlabel;
		private ToolStripSeparator branchseparator;
		private Timer branchtimer;

		// Временный WAD (полный путь) -> карта проекта
		private readonly Dictionary<string, ProjectMap> projectmaps = new Dictionary<string, ProjectMap>(StringComparer.OrdinalIgnoreCase);
		private ProjectMap current;

		#endregion

		#region ================== Properties

		public static BuilderPlug Me { get { return me; } }
		public override string Name { get { return "ZT Project"; } }

		private string ProjectDir
		{
			get { return General.Settings.ReadPluginSetting("projectdir", ""); }
			set { General.Settings.WritePluginSetting("projectdir", value); }
		}

		#endregion

		#region ================== Init / Dispose

		public override void OnInitialize()
		{
			base.OnInitialize();
			me = this;
			General.Actions.BindMethods(this);
			CreateInterface();
		}

		public override void Dispose()
		{
			if(branchtimer != null) { branchtimer.Stop(); branchtimer.Dispose(); branchtimer = null; }
			if(branchlabel != null) General.MainWindow.RemoveStatusItem(branchlabel);
			if(branchseparator != null) General.MainWindow.RemoveStatusItem(branchseparator);
			if(menu != null) General.Interface.RemoveMenu(menu);
			if(separator != null) General.Interface.RemoveButton(separator);
			if(sendbutton != null) General.Interface.RemoveButton(sendbutton);
			if(getbutton != null) General.Interface.RemoveButton(getbutton);
			General.Actions.UnbindMethods(this);
			base.Dispose();
		}

		private void CreateInterface()
		{
			menu = new ToolStripMenuItem("ZT");
			menu.DropDownItems.Add(new ToolStripMenuItem("Открыть карту проекта…", null, (s, e) => OpenProjectMap()));
			menu.DropDownItems.Add(new ToolStripMenuItem("Папка проекта…", null, (s, e) => ChooseProjectDir()));
			menu.DropDownItems.Add(new ToolStripSeparator());
			menu.DropDownItems.Add(new ToolStripMenuItem("Отправить свежие правки", null, (s, e) => Send()));
			menu.DropDownItems.Add(new ToolStripMenuItem("Получить свежие правки", null, (s, e) => Get()));
			General.Interface.AddMenu(menu);

			separator = new ToolStripSeparator();
			sendbutton = new ToolStripButton("Отправить") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "ZT: отправить свежие правки (сохранить, закоммитить, отправить свою ветку)" };
			getbutton = new ToolStripButton("Получить") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "ZT: получить свежие правки (слить свою ветку с сервера и перечитать карту)" };
			sendbutton.Click += (s, e) => Send();
			getbutton.Click += (s, e) => Get();
			General.Interface.AddButton(separator);
			General.Interface.AddButton(sendbutton);
			General.Interface.AddButton(getbutton);

			// Текущая ветка рабочей копии — в строке состояния, после названия игровой конфигурации
			branchlabel = new ToolStripStatusLabel { Visible = false, Margin = new Padding(4, 3, 4, 2) };
			branchseparator = new ToolStripSeparator { Visible = false };
			General.MainWindow.AddStatusItem(branchlabel);        // каждый следующий встаёт перед предыдущим:
			General.MainWindow.AddStatusItem(branchseparator);    // конфигурация | Ветка: dev | сетка …
			branchtimer = new Timer { Interval = 3000 };     // ветку могли переключить вне UDB
			branchtimer.Tick += (s, e) => UpdateBranchLabel();
			branchtimer.Start();
			UpdateBranchLabel();
		}

		// Ветка — по .git/HEAD рабочей копии (без запуска git: проверка идёт раз в 3 с)
		private void UpdateBranchLabel()
		{
			if(branchlabel == null) return;
			string dir = ProjectDir;
			string branch = ProjectMap.IsProjectDir(dir) ? ReadBranch(dir) : null;
			bool show = branch != null;
			string text = show ? "Ветка: " + branch : "";
			if(branchlabel.Text != text) branchlabel.Text = text;
			branchlabel.ToolTipText = show ? "ZT: рабочая копия " + dir : null;
			branchlabel.Visible = show;
			branchseparator.Visible = show;
		}

		private static string ReadBranch(string dir)
		{
			try
			{
				string head = File.ReadAllText(Path.Combine(dir, ".git", "HEAD")).Trim();
				const string prefix = "ref: refs/heads/";
				if(head.StartsWith(prefix, StringComparison.Ordinal)) return head.Substring(prefix.Length);
				return head.Length >= 7 ? "(отдельный коммит " + head.Substring(0, 7) + ")" : null;
			}
			catch(Exception)
			{
				return null;
			}
		}

		#endregion

		#region ================== Map events

		public override void OnMapOpenEnd()
		{
			base.OnMapOpenEnd();
			ProjectMap pm;
			current = (General.Map != null && !string.IsNullOrEmpty(General.Map.FilePathName) &&
			           projectmaps.TryGetValue(Path.GetFullPath(General.Map.FilePathName), out pm)) ? pm : null;
			if(current != null)
				General.Interface.DisplayStatus(StatusType.Info, "ZT: карта " + current.MapName + " из " + current.ProjectDir + "\\" + current.MapFolder.Replace('/', '\\'));
		}

		public override void OnMapCloseEnd()
		{
			base.OnMapCloseEnd();
			current = null;
		}

		// После обычного сохранения — разложить лампы карты обратно в файлы wad-src/
		public override void OnMapSaveEnd(SavePurpose purpose)
		{
			base.OnMapSaveEnd(purpose);
			if(purpose != SavePurpose.Normal || current == null || General.Map == null) return;
			if(!string.Equals(Path.GetFullPath(General.Map.FilePathName), current.WadPath, StringComparison.OrdinalIgnoreCase)) return;
			try
			{
				List<string> changed = current.ExportFromWad();
				General.WriteLogLine("ZT: карта " + current.MapName + " записана в wad-src: " + (changed.Count > 0 ? string.Join(", ", changed) : "без изменений"));
			}
			catch(Exception ex)
			{
				General.ShowErrorMessage("ZT: не удалось записать карту в wad-src.\n" + ex.Message, MessageBoxButtons.OK);
			}
		}

		#endregion

		#region ================== Actions

		[BeginAction("ztopenprojectmap")]
		private void OpenProjectMap()
		{
			string dir = EnsureProjectDir();
			if(dir == null) return;

			List<string> maps;
			try { maps = ProjectMap.ListMaps(dir); }
			catch(Exception ex) { General.ShowErrorMessage("ZT: не прочитан " + ProjectMap.MANIFEST + ".\n" + ex.Message, MessageBoxButtons.OK); return; }

			string preselect = current != null ? current.MapName : General.Settings.ReadPluginSetting("lastmap", "MAP01");
			string mapname = Dialogs.ChooseMap(General.Interface, maps, preselect);
			if(mapname == null) return;

			// Сначала закрыть текущую карту (UDB спросит про сохранение): временный WAD пересобирается из файлов
			if(General.Map != null && !General.CloseMap()) return;

			try
			{
				ProjectMap pm = ProjectMap.Load(dir, mapname);
				pm.BuildWad();
				projectmaps[pm.WadPath] = pm;
				General.Settings.WritePluginSetting("lastmap", mapname);
				// Окно параметров карты UDB: игровая конфигурация и ресурсы; запоминаются в .dbs рядом с временным WAD
				General.OpenMapFile(pm.WadPath, null);
			}
			catch(Exception ex)
			{
				General.ShowErrorMessage("ZT: не удалось открыть карту проекта.\n" + ex.Message, MessageBoxButtons.OK);
			}
		}

		[BeginAction("ztsend")]
		private void Send()
		{
			string dir = EnsureProjectDir();
			if(dir == null || !SaveProjectMap()) return;
			try
			{
				Cursor.Current = Cursors.WaitCursor;
				string branch = CurrentBranch(dir);
				List<string> changes = Git.Lines(Git.Run(dir, "status", "--porcelain"));
				if(changes.Count == 0)
				{
					GitResult ahead = Git.Run(dir, "rev-list", "--count", "@{u}..HEAD");
					int n;
					if(ahead.Ok && int.TryParse(ahead.StdOut.Trim(), out n) && n > 0) Push(dir, branch);
					else General.Interface.DisplayStatus(StatusType.Info, "ZT: нечего отправлять — всё уже на сервере.");
					return;
				}

				string summary = Summarize(changes);
				Cursor.Current = Cursors.Default;
				string comment = Dialogs.AskComment(General.Interface, "Ветка " + branch + ". Будет отправлено:\n" + summary);
				if(comment == null) return;
				Cursor.Current = Cursors.WaitCursor;

				GitResult add = Git.Run(dir, "add", "-A");
				if(!add.Ok) { Dialogs.ShowText(General.Interface, "ZT: git add не удался", add.Output); return; }
				StringBuilder msg = new StringBuilder("UDB: " + SummaryLine(changes));
				if(comment.Length > 0) msg.Append("\n\n").Append(comment);
				msg.Append("\n\n").Append(summary).Append("\n\nОтправлено кнопкой «Отправить свежие правки» из UDB.\n");
				GitResult commit = Git.CommitWithMessage(dir, msg.ToString());
				if(!commit.Ok) { Dialogs.ShowText(General.Interface, "ZT: коммит не удался", commit.Output); return; }
				Push(dir, branch);
			}
			catch(Exception ex)
			{
				General.ShowErrorMessage("ZT: отправка не удалась.\n" + ex.Message, MessageBoxButtons.OK);
			}
			finally
			{
				Cursor.Current = Cursors.Default;
			}
		}

		[BeginAction("ztget")]
		private void Get()
		{
			string dir = EnsureProjectDir();
			if(dir == null || !SaveProjectMap()) return;
			try
			{
				Cursor.Current = Cursors.WaitCursor;
				Pull(dir, true);
			}
			catch(Exception ex)
			{
				General.ShowErrorMessage("ZT: получение не удалось.\n" + ex.Message, MessageBoxButtons.OK);
			}
			finally
			{
				Cursor.Current = Cursors.Default;
			}
		}

		#endregion

		#region ================== Git flow

		private void Push(string dir, string branch)
		{
			GitResult r = Git.Run(dir, "push", "origin", "HEAD:" + branch);
			if(!r.Ok && (r.Output.Contains("rejected") || r.Output.Contains("fetch first") || r.Output.Contains("non-fast-forward")))
			{
				// На сервере есть новые коммиты: сначала получить, потом отправить
				if(!Pull(dir, false)) return;
				r = Git.Run(dir, "push", "origin", "HEAD:" + branch);
			}
			if(r.Ok) General.Interface.DisplayStatus(StatusType.Info, "ZT: отправлено в " + branch + ".");
			else Dialogs.ShowText(General.Interface, "ZT: отправка не удалась", r.Output);
		}

		// Слить свою ветку с сервера. Неотправленные правки сначала коммитятся локально — они не теряются.
		private bool Pull(string dir, bool report)
		{
			string branch = CurrentBranch(dir);
			List<string> dirty = Git.Lines(Git.Run(dir, "status", "--porcelain"));
			if(dirty.Count > 0)
			{
				Git.Run(dir, "add", "-A");
				GitResult c = Git.CommitWithMessage(dir, "UDB: локальные правки перед получением\n\n" + Summarize(dirty) + "\n");
				if(!c.Ok) { Dialogs.ShowText(General.Interface, "ZT: локальный коммит не удался", c.Output); return false; }
			}

			string before = Git.Run(dir, "rev-parse", "HEAD").StdOut.Trim();
			GitResult pull = Git.Run(dir, "pull", "--no-rebase", "--no-edit", "origin", branch);
			if(!pull.Ok)
			{
				List<string> conflicts = Git.Lines(Git.Run(dir, "diff", "--name-only", "--diff-filter=U"));
				if(conflicts.Count > 0)
				{
					Git.Run(dir, "merge", "--abort");
					Dialogs.ShowText(General.Interface, "ZT: получить не удалось — конфликт",
						"Слияние отменено, ваши правки на месте (локальный коммит).\r\nКонфликт в файлах:\r\n  " +
						string.Join("\r\n  ", conflicts) + "\r\n\r\nСемантическое слияние карт — следующий шаг этапа 2.\r\n\r\n" + pull.Output);
				}
				else
				{
					Dialogs.ShowText(General.Interface, "ZT: получить не удалось", pull.Output);
				}
				return false;
			}

			string after = Git.Run(dir, "rev-parse", "HEAD").StdOut.Trim();
			if(before == after)
			{
				if(report) General.Interface.DisplayStatus(StatusType.Info, "ZT: всё уже свежее.");
				return true;
			}

			List<string> changed = Git.Lines(Git.Run(dir, "diff", "--name-only", before, after));
			List<string> commits = Git.Lines(Git.Run(dir, "log", "--no-merges", "--format=%h %s", before + ".." + after));
			bool reloaded = false;
			if(current != null && current.IsTouchedBy(changed)) reloaded = ReloadCurrentMap();

			string text = "Получено коммитов: " + commits.Count + (reloaded ? ". Карта " + current.MapName + " перечитана." : ".");
			General.Interface.DisplayStatus(StatusType.Info, "ZT: " + text);
			if(report)
				Dialogs.ShowText(General.Interface, "ZT: получены свежие правки", text + "\r\n\r\n" + string.Join("\r\n", commits) +
					"\r\n\r\nИзменённые файлы:\r\n  " + string.Join("\r\n  ", changed));
			return true;
		}

		// Перечитать открытую карту проекта после получения правок: та же игровая конфигурация и ресурсы, тот же вид.
		// Пробная версия: карта открывается заново, история отмены начинается с нуля.
		private bool ReloadCurrentMap()
		{
			ProjectMap pm = current;
			var options = General.Map.Options;
			float ox = General.Map.Renderer2D.OffsetX, oy = General.Map.Renderer2D.OffsetY, scale = General.Map.Renderer2D.Scale;
			bool classic = General.Editing.Mode is ClassicMode;
			pm.BuildWad();
			General.OpenMapFileWithOptions(pm.WadPath, options);
			ClassicMode mode = General.Editing.Mode as ClassicMode;
			if(classic && mode != null) mode.CenterOnCoordinates(new Vector2D(ox, oy), scale);
			return General.Map != null;
		}

		// Сохранить открытую карту проекта, если в ней есть правки (сохранение само раскладывает лампы в wad-src/)
		private bool SaveProjectMap()
		{
			if(General.Map == null || current == null || !General.Map.IsChanged) return true;
			return General.SaveMap();
		}

		private static string CurrentBranch(string dir)
		{
			return Git.Run(dir, "rev-parse", "--abbrev-ref", "HEAD").StdOut.Trim();
		}

		// Пути из вывода git status --porcelain
		private static List<string> Paths(IEnumerable<string> porcelain)
		{
			List<string> result = new List<string>();
			foreach(string line in porcelain)
			{
				if(line.Length < 4) continue;
				string p = line.Substring(3);
				int arrow = p.IndexOf(" -> ", StringComparison.Ordinal);
				if(arrow >= 0) p = p.Substring(arrow + 4);
				result.Add(p.Trim('"'));
			}
			return result;
		}

		// Сводка изменений: карты по лампам, остальное — по папкам верхнего уровня
		private static string Summarize(List<string> porcelain)
		{
			List<string> paths = Paths(porcelain);
			List<string> lines = new List<string>();
			foreach(IGrouping<string, string> g in paths.Where(p => p.StartsWith("wad-src/")).GroupBy(p => p.Split('/').Length > 2 ? p.Split('/')[1] : "wad-src"))
				lines.Add("  " + g.Key + ": " + string.Join(", ", g.Select(p => p.Substring(p.LastIndexOf('/') + 1))));
			foreach(IGrouping<string, string> g in paths.Where(p => !p.StartsWith("wad-src/")).GroupBy(p => p.Contains('/') ? p.Substring(0, p.IndexOf('/') + 1) : p))
				lines.Add("  " + g.Key + (g.Count() > 1 || g.Key.EndsWith("/") ? " — файлов: " + g.Count() : ""));
			return string.Join("\n", lines);
		}

		private static string SummaryLine(List<string> porcelain)
		{
			List<string> paths = Paths(porcelain);
			List<string> parts = paths.Where(p => p.StartsWith("wad-src/") && p.Split('/').Length > 2).Select(p => p.Split('/')[1]).Distinct().ToList();
			int other = paths.Count(p => !p.StartsWith("wad-src/"));
			if(other > 0) parts.Add("файлов вне карт: " + other);
			return parts.Count > 0 ? string.Join(", ", parts) : "правки";
		}

		#endregion

		#region ================== Project folder

		private string EnsureProjectDir()
		{
			string dir = ProjectDir;
			if(ProjectMap.IsProjectDir(dir)) return dir;
			return ChooseProjectDir();
		}

		// Выбор папки в окне проводника, как у открытия файла (FolderSelectDialog ядра UDB), а не в дереве
		private string ChooseProjectDir()
		{
			FolderSelectDialog fd = new FolderSelectDialog();
			fd.Title = "ZT: рабочая копия проекта Zero Tolerance (папка, где лежит wad-src\\wad_structure.json)";
			if(Directory.Exists(ProjectDir)) fd.InitialDirectory = ProjectDir;
			if(!fd.ShowDialog(General.Interface.Handle) || string.IsNullOrEmpty(fd.FileName)) return null;
			if(!ProjectMap.IsProjectDir(fd.FileName))
			{
				General.ShowWarningMessage("ZT: в папке нет " + ProjectMap.MANIFEST + " — это не рабочая копия проекта.", MessageBoxButtons.OK);
				return null;
			}
			ProjectDir = fd.FileName;
			return fd.FileName;
		}

		#endregion
	}
}
