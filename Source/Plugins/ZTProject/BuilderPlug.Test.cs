#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Actions;
using CodeImp.DoomBuilder.Map;
using CodeImp.DoomBuilder.Windows;
using CodeImp.DoomBuilder.ZT;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Тест карты в игре по порядку загрузки мода (ZT/workbench-plan.md, этап 6): карта из UDB (со скомпилированными
	// скриптами) — во временный WAD, GZDoom — с аргументами ZeroTolerance.bat, где собранный pk3 заменён папкой src/
	// рабочей копии, тестовый WAD — последним (перекрывает карту из ZeroTolerance.wad). Правки src/ проверяются в игре без
	// сборки pk3. Встроенный Test Map UDB (F9) остаётся как был — с ресурсами игровой конфигурации.
	public partial class BuilderPlug
	{
		#region ================== Action

		[BeginAction("zttestmap")]
		private void TestMapAction()
		{
			string msg = TestMap();
			if(msg != null) General.Interface.DisplayStatus(StatusType.Warning, "ZT: " + msg);
		}

		// null — игра запущена; иначе — почему нет. log — куда GZDoom пишет лог (для проверки агентом).
		internal string TestMap() { string log; return TestMap(out log); }

		internal string TestMap(out string log)
		{
			log = null;
			if(General.Map == null || current == null) return "открыта не карта проекта";
			string gz = GzdoomDir();
			if(gz == null || !File.Exists(Path.Combine(gz, "gzdoom.exe"))) return "нет gzdoom.exe в папке GZDoom";
			List<string> modargs;
			try { modargs = LaunchArgs(gz, Path.Combine(current.ProjectDir, "src")); }
			catch(Exception ex) { return ex.Message; }

			// Карта — во временный WAD, как у Test Map UDB (скрипты компилируются; ошибки — в редакторе скриптов)
			string tmp = Path.Combine(Path.GetTempPath(), "ZTWorkbench-test");
			Directory.CreateDirectory(tmp);
			string wad = Path.Combine(tmp, current.MapName + ".wad");
			log = Path.Combine(tmp, "test.log");
			try { if(File.Exists(wad)) File.Delete(wad); }
			catch(IOException) { return "временный WAD занят — закрыта ли прошлая игра?"; }
			Cursor.Current = Cursors.WaitCursor;
			General.Plugins.OnMapSaveBegin(SavePurpose.Testing);
			bool saved = General.Map.SaveMap(wad, SavePurpose.Testing);
			General.Plugins.OnMapSaveEnd(SavePurpose.Testing);
			Cursor.Current = Cursors.Default;
			if(!saved) return "карта не сохранилась для теста";
			if(General.Map.Errors.Count > 0)
			{
				ZTMapTools.ShowScriptErrors(General.Map.Errors.ToList());
				return "ошибки в скриптах карты — тест не запущен";
			}

			List<string> args = new List<string>(modargs) { "-file", wad, "+map", current.MapName, "+logfile", log };
			ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(gz, "gzdoom.exe"), string.Join(" ", args.Select(QuoteArg)))
			{
				WorkingDirectory = gz, UseShellExecute = true
			};
			Process p = Process.Start(psi);
			General.WriteLogLine("ZT: тест карты " + current.MapName + ": gzdoom.exe " + psi.Arguments);
			General.Interface.DisplayStatus(StatusType.Info, "ZT: тест карты " + current.MapName + " — порядок мода, src/ вместо pk3");
			Task.Run(() =>
			{
				try { p.WaitForExit(); } catch(Exception) { }
				try { File.Delete(wad); } catch(Exception) { }
			});
			return null;
		}

		#endregion
	}
}
