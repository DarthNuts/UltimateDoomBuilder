#region ================== Namespaces

using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Actions;
using CodeImp.DoomBuilder.Data;
using CodeImp.DoomBuilder.Map;
using CodeImp.DoomBuilder.Windows;
using CodeImp.DoomBuilder.ZT;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Папка src/ рабочей копии — ресурсом карт проекта (ZT/workbench-plan.md §7, §9.2): текстуры, спрайты и акторы мода видны
	// в UDB прямо из исходников, без сборки и скачивания pk3. Ресурс — в параметрах карты (.dbs), после собранного pk3 из
	// игровой конфигурации, поэтому файлы src/ перекрывают его; в тест карты (запуск игры) папка не передаётся.
	// Файлы src/ поменяли вне UDB или пришли через «Получить» — ресурсы перечитываются (карта и история отмены остаются).
	public partial class BuilderPlug
	{
		#region ================== Variables

		private ToolStripMenuItem srcmenu;
		private FileSystemWatcher srcwatcher;
		private Timer srctimer;
		private bool srcdirty;

		private const string BUILD_PK3 = "ZeroTolerance.pk3";    // собранный из src/ pk3 — вместе с src/ не грузится

		private bool SrcResourceOn
		{
			get { return General.Settings.ReadPluginSetting("srcresource", true); }
			set { General.Settings.WritePluginSetting("srcresource", value); }
		}

		#endregion

		#region ================== Interface

		private void InitSrc()
		{
			srcmenu = new ToolStripMenuItem("Ресурсы из src/ (без сборки pk3)", null, (s, e) => ToggleSrc()) { Checked = SrcResourceOn };
			ToolStripMenuItem openfile = new ToolStripMenuItem("Открыть файл проекта…", null, (s, e) => OpenProjectFile());
			ToolStripMenuItem check = new ToolStripMenuItem("Проверить скрипты мода (GZDoom)", null, (s, e) => CheckScriptsAction());
			ToolStripMenuItem test = new ToolStripMenuItem("Тест карты в игре (порядок мода, src/)", null, (s, e) => TestMapAction());
			int i = 0;
			while(i < menu.DropDownItems.Count && menu.DropDownItems[i] != buildmenu) i++;
			menu.DropDownItems.Insert(Math.Min(i + 1, menu.DropDownItems.Count), srcmenu);
			menu.DropDownItems.Insert(2, openfile);     // после «Открыть карту проекта…» и «Папка проекта…»
			menu.DropDownItems.Insert(3, check);
			menu.DropDownItems.Insert(4, test);
			srctimer = new Timer { Interval = 2000 };       // git и редакторы пишут пачками — ждём тишины
			srctimer.Tick += (s, e) => SrcTick();
			ZTMapTools.ConfigResourceFilter = FilterForSrc;
		}

		// Ресурсы игровой конфигурации для открываемой карты: если ресурс карты — src/ рабочей копии проекта, собранный из неё
		// pk3 убирается (иначе классы, cvar и звуки определены дважды). Тест карты в игре по-прежнему получает pk3.
		private static DataLocationList FilterForSrc(DataLocationList list)
		{
			if(General.Map == null || General.Map.Options == null) return list;
			bool hassrc = General.Map.Options.Resources.Any(d => d.type == DataLocation.RESOURCE_DIRECTORY && d.location != null &&
				string.Equals(Path.GetFileName(d.location.TrimEnd('\\', '/')), "src", StringComparison.OrdinalIgnoreCase) &&
				ProjectMap.IsProjectDir(Path.GetDirectoryName(d.location.TrimEnd('\\', '/'))));
			if(!hassrc) return list;
			DataLocationList r = new DataLocationList();
			r.AddRange(list.Where(d => !string.Equals(Path.GetFileName(d.location ?? ""), BUILD_PK3, StringComparison.OrdinalIgnoreCase)));
			return r;
		}

		private void DisposeSrc()
		{
			ZTMapTools.ConfigResourceFilter = null;
			StopSrcWatch();
			if(srctimer != null) { srctimer.Dispose(); srctimer = null; }
		}

		private void ToggleSrc()
		{
			SrcResourceOn = !SrcResourceOn;
			srcmenu.Checked = SrcResourceOn;
			if(current != null && General.Map != null)
			{
				if(SrcResourceOn) EnsureSrcResource(true);
				else RemoveSrcResource();
			}
		}

		#endregion

		#region ================== Resource

		private static string SrcDir(ProjectMap pm) { return Path.Combine(pm.ProjectDir, "src"); }

		private static bool IsSrc(DataLocation d, string src)
		{
			return d.type == DataLocation.RESOURCE_DIRECTORY && d.location != null &&
			       string.Equals(Path.GetFullPath(d.location).TrimEnd('\\'), Path.GetFullPath(src).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
		}

		// Добавить src/ в ресурсы параметров карты (до её открытия — без перечитывания)
		internal static bool AddSrcTo(MapOptions options, ProjectMap pm)
		{
			string src = SrcDir(pm);
			if(!Directory.Exists(src) || options.Resources.Any(d => IsSrc(d, src))) return false;
			options.Resources.Add(new DataLocation(DataLocation.RESOURCE_DIRECTORY, src, false, false, true, null));
			return true;
		}

		// Открытой карте проекта — src/ в ресурсы; reload — сразу перечитать ресурсы (иначе — при следующем открытии)
		private void EnsureSrcResource(bool reload)
		{
			if(current == null || General.Map == null || !SrcResourceOn) return;
			if(AddSrcTo(General.Map.Options, current))
			{
				General.WriteLogLine("ZT: src/ рабочей копии добавлена в ресурсы карты " + current.MapName);
				if(reload) ReloadResources("ZT: ресурсы — из src/ рабочей копии");
			}
			WatchSrc();
		}

		private void RemoveSrcResource()
		{
			StopSrcWatch();
			string src = SrcDir(current);
			int n = General.Map.Options.Resources.RemoveAll(d => IsSrc(d, src));
			if(n > 0) ReloadResources("ZT: src/ убрана из ресурсов карты");
		}

		private void ReloadResources(string status)
		{
			if(General.Map == null) return;
			General.Actions.InvokeAction("builder_reloadresources");
			General.Interface.DisplayStatus(StatusType.Info, status);
		}

		// Файл рабочей копии (src/, wad-src/…) — во вкладке редактора скриптов: ZScript, DECORATE, MAPINFO и др. Хвостовые
		// пробелы при сохранении не срезаются; если файл поменяли снаружи, сохранение спросит. Сохранили файл src/ — ресурсы
		// UDB перечитываются слежением.
		[BeginAction("ztopenfile")]
		private void OpenProjectFile()
		{
			string dir = EnsureProjectDir();
			if(dir == null) return;
			if(General.Map == null)
			{
				General.ShowWarningMessage("ZT: файлы открываются в редакторе скриптов карты — сначала откройте карту проекта.", MessageBoxButtons.OK);
				return;
			}
			using(OpenFileDialog fd = new OpenFileDialog
			{
				Title = "ZT: файл проекта", InitialDirectory = Directory.Exists(Path.Combine(dir, "src")) ? Path.Combine(dir, "src") : dir,
				Filter = "Скрипты мода|*.zs;*.zsc;*.dec;*.acs;*.txt;ZSCRIPT*;DECORATE*;*MAPINFO*;SNDINFO*;MENUDEF*;CVARINFO*;KEYCONF*;GLDEFS*;TEXTURES*;ANIMDEFS*;SBARINFO*;LANGUAGE*|Все файлы|*.*",
				RestoreDirectory = true
			})
			{
				if(fd.ShowDialog(General.Interface) != DialogResult.OK) return;
				if(!ZTMapTools.OpenScriptFile(fd.FileName, true))
					General.ShowErrorMessage("ZT: не удалось открыть " + fd.FileName, MessageBoxButtons.OK);
			}
		}

		#endregion

		#region ================== Watching

		private void WatchSrc()
		{
			StopSrcWatch();
			if(current == null || !SrcResourceOn) return;
			string src = SrcDir(current);
			if(!Directory.Exists(src)) return;
			srcwatcher = new FileSystemWatcher(src)
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
				InternalBufferSize = 65536
			};
			FileSystemEventHandler h = (s, e) => PokeSrc();
			srcwatcher.Changed += h;
			srcwatcher.Created += h;
			srcwatcher.Deleted += h;
			srcwatcher.Renamed += (s, e) => PokeSrc();
			srcwatcher.Error += (s, e) => PokeSrc();      // переполнение буфера — значит, менялось много
			srcwatcher.EnableRaisingEvents = true;
		}

		private void StopSrcWatch()
		{
			if(srcwatcher != null) { srcwatcher.EnableRaisingEvents = false; srcwatcher.Dispose(); srcwatcher = null; }
			if(srctimer != null) srctimer.Stop();
			srcdirty = false;
		}

		private void PokeSrc()
		{
			try
			{
				General.MainWindow.BeginInvoke((MethodInvoker)delegate
				{
					srcdirty = true;
					if(srctimer == null) return;
					srctimer.Stop();
					srctimer.Start();
				});
			}
			catch(InvalidOperationException) { }
		}

		private void SrcTick()
		{
			srctimer.Stop();
			if(!srcdirty || current == null || General.Map == null) return;
			bool modal = !IsWindowEnabled(General.MainWindow.Handle) || Application.OpenForms.Cast<Form>().Any(f => f.Visible && f.Modal);
			if(busy > 0 || modal || General.Editing.Mode == null || General.Editing.Mode.Attributes.Volatile) { srctimer.Start(); return; }
			srcdirty = false;
			int tabs = ZTMapTools.ReloadScriptFiles();
			ReloadResources("ZT: файлы src/ изменились — ресурсы перечитаны" + (tabs > 0 ? ", вкладок перечитано: " + tabs : ""));
		}

		// После «Получить»: пришли файлы src/ — перечитать ресурсы (слежение в это время молчит)
		private void SrcChangedBy(System.Collections.Generic.IEnumerable<string> changed)
		{
			if(current == null || General.Map == null || !SrcResourceOn) return;
			if(!General.Map.Options.Resources.Any(d => IsSrc(d, SrcDir(current)))) return;
			if(changed.Any(p => p.StartsWith("src/", StringComparison.OrdinalIgnoreCase)))
			{
				srcdirty = false;
				ZTMapTools.ReloadScriptFiles();
				ReloadResources("ZT: пришли файлы src/ — ресурсы перечитаны");
			}
		}

		#endregion
	}
}
