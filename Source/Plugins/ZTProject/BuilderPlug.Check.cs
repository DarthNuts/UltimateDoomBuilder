#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Actions;
using CodeImp.DoomBuilder.Compilers;
using CodeImp.DoomBuilder.Windows;
using CodeImp.DoomBuilder.ZT;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Проверка скриптов мода в UDB (ZT/workbench-plan.md §6, «GZDoom как компилятор»): GZDoom из папки игры грузит мод в
	// порядке ZeroTolerance.bat, но вместо собранного pk3 — папку src/ рабочей копии, с -norun: без ошибок он выходит сам
	// (~7 с), при ошибке ZScript/DECORATE пишет «Script error, "<ресурс>/:<путь>" line N:» в лог и ждёт в окне Fatal Error —
	// плагин читает лог, закрывает GZDoom и показывает ошибки в редакторе скриптов (клик — строка в файле). Ini игры не
	// трогается: GZDoom получает временную копию.
	public partial class BuilderPlug
	{
		#region ================== Variables

		private const string LAUNCHER = "ZeroTolerance.bat";
		private static readonly Regex ScriptErrorRe = new Regex("^Script error, \"(?<full>[^\"]*)\" line (?<line>\\d+)");
		private volatile bool checking;

		#endregion

		#region ================== Action

		[BeginAction("ztcheckscripts")]
		private void CheckScriptsAction()
		{
			string msg = CheckScripts(null);
			if(msg != null) General.Interface.DisplayStatus(StatusType.Info, "ZT: " + msg);
		}

		internal sealed class CheckResult
		{
			public bool Ok;
			public string Summary;
			public List<CompilerError> Errors = new List<CompilerError>();
			public string Log = "";
		}

		// Запустить проверку в фоне; done — по завершении в потоке интерфейса (null — показать итог как обычно).
		// Возвращает строку для строки состояния или null, если проверка запущена.
		internal string CheckScripts(Action<CheckResult> done)
		{
			if(checking) return "проверка уже идёт";
			string dir = ProjectDir;
			if(!ProjectMap.IsProjectDir(dir)) return "папка проекта не выбрана";
			string gz = GzdoomDir();
			if(gz == null || !File.Exists(Path.Combine(gz, "gzdoom.exe"))) return "нет gzdoom.exe в папке GZDoom";
			List<string> args;
			try { args = LaunchArgs(gz, Path.Combine(dir, "src")); }
			catch(Exception ex) { return ex.Message; }

			checking = true;
			General.Interface.DisplayStatus(StatusType.Busy, "ZT: проверка скриптов мода в GZDoom…");
			Task.Run(() => RunCheck(gz, args, Path.Combine(dir, "src"))).ContinueWith(t =>
			{
				General.MainWindow.BeginInvoke((MethodInvoker)delegate
				{
					checking = false;
					CheckResult r = t.IsFaulted ? new CheckResult { Summary = "проверка не удалась: " + t.Exception.InnerException.Message } : t.Result;
					if(done != null) done(r);
					else ShowCheck(r);
				});
			});
			return null;
		}

		internal void ShowCheckPublic(CheckResult r) { ShowCheck(r); }

		private void ShowCheck(CheckResult r)
		{
			General.Interface.DisplayStatus(StatusType.Info, "ZT: " + r.Summary);
			if(r.Errors.Count == 0) { ZTMapTools.ShowScriptErrors(r.Errors); return; }
			if(General.Map != null)
			{
				foreach(string f in r.Errors.Select(e => e.filename).Where(File.Exists).Distinct().Take(5)) ZTMapTools.OpenScriptFile(f, true);
				ZTMapTools.ShowScriptErrors(r.Errors);
			}
			else
				Dialogs.ShowText(General.Interface, "ZT: ошибки в скриптах мода", string.Join("\r\n", r.Errors.Select(e => e.filename + ":" + (e.linenumber + 1) + ": " + e.description)));
		}

		#endregion

		#region ================== GZDoom

		// Аргументы запуска мода из ZeroTolerance.bat: всё после gzdoom.exe, собранный pk3 — заменить папкой src/
		private static List<string> LaunchArgs(string gz, string src)
		{
			string bat = Path.Combine(gz, LAUNCHER);
			if(!File.Exists(bat)) throw new InvalidOperationException("нет " + LAUNCHER + " в папке GZDoom — неизвестен порядок загрузки мода");
			string line = File.ReadAllLines(bat).FirstOrDefault(l => l.IndexOf("gzdoom.exe", StringComparison.OrdinalIgnoreCase) >= 0);
			if(line == null) throw new InvalidOperationException("в " + LAUNCHER + " нет строки запуска gzdoom.exe");
			List<string> tokens = SplitCommand(line.Substring(line.IndexOf("gzdoom.exe", StringComparison.OrdinalIgnoreCase) + "gzdoom.exe".Length));
			List<string> args = new List<string>();
			foreach(string tk in tokens)
			{
				if(tk.StartsWith("%")) continue;                                    // %* и прочие подстановки bat
				if(string.Equals(Path.GetFileName(tk), BUILD_PK3, StringComparison.OrdinalIgnoreCase)) args.Add(src);
				else args.Add(tk);
			}
			if(!args.Contains(src)) { args.Add("-file"); args.Add(src); }
			return args;
		}

		private CheckResult RunCheck(string gz, List<string> modargs, string src)
		{
			string tmp = Path.Combine(Path.GetTempPath(), "ZTWorkbench-check");
			Directory.CreateDirectory(tmp);
			string ini = Path.Combine(tmp, "check.ini"), log = Path.Combine(tmp, "check.log");
			string gameini = Path.Combine(gz, "GZDoom_portable.ini");
			if(File.Exists(gameini)) File.Copy(gameini, ini, true);
			if(File.Exists(log)) File.Delete(log);

			List<string> args = new List<string> { "-config", ini };
			args.AddRange(modargs);
			args.AddRange(new[] { "-norun", "-nosound", "+logfile", log });
			// Через скрытую консоль cmd: GZDoom без консоли у родителя открывает свою и после -norun ждёт «Press any key to
			// exit»; к консоли cmd он подключается и выходит сам. На экране ничего не появляется.
			string gzexe = Path.Combine(gz, "gzdoom.exe");
			ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + gzexe + "\" " + string.Join(" ", args.Select(QuoteArg)) + "\"")
			{
				WorkingDirectory = gz, UseShellExecute = false, CreateNoWindow = true
			};
			DateTime start = DateTime.Now;
			CheckResult r = new CheckResult();
			using(Process p = Process.Start(psi))
			{
				bool failed = false;
				while(!p.WaitForExit(250))
				{
					if(ReadLog(log).Contains("Script error") || DateTime.Now - start > TimeSpan.FromSeconds(180))
					{
						failed = true;
						Thread.Sleep(700);          // дописать лог до конца
						KillTree(p.Id);       // cmd и GZDoom под ним
						p.WaitForExit(5000);
						break;
					}
				}
				r.Log = ReadLog(log);
				r.Errors = ParseErrors(r.Log, src);
				int seconds = (int)(DateTime.Now - start).TotalSeconds;
				if(r.Errors.Count > 0) r.Summary = "скрипты мода: ошибок " + r.Errors.Count + " (" + seconds + " с)";
				else if(failed) r.Summary = "GZDoom не закончил загрузку за 3 минуты — см. " + log;
				else if(p.ExitCode != 0 && p.ExitCode != 1337) r.Summary = "GZDoom вышел с кодом " + p.ExitCode + " — см. " + log;
				else { r.Ok = true; r.Summary = "скрипты мода: ошибок нет (" + seconds + " с)"; }
			}
			return r;
		}

		private static void KillTree(int pid)
		{
			try
			{
				using(Process k = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + pid) { UseShellExecute = false, CreateNoWindow = true }))
					k.WaitForExit(10000);
			}
			catch(Exception) { }
		}

		private static string ReadLog(string path)
		{
			try
			{
				using(FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				using(StreamReader sr = new StreamReader(fs, Encoding.UTF8))
					return sr.ReadToEnd();
			}
			catch(IOException) { return ""; }
		}

		// «Script error, "<ресурс>/:<путь>" line N:» и текст до пустой строки. Файл из src/ — полным путём (для вкладки).
		internal static List<CompilerError> ParseErrors(string log, string src)
		{
			List<CompilerError> list = new List<CompilerError>();
			string[] lines = log.Replace("\r\n", "\n").Split('\n');
			string srcnorm = src.Replace('\\', '/').TrimEnd('/');
			for(int i = 0; i < lines.Length; i++)
			{
				Match m = ScriptErrorRe.Match(lines[i].Trim());
				if(!m.Success) continue;
				StringBuilder text = new StringBuilder();
				for(int j = i + 1; j < lines.Length && lines[j].Trim().Length > 0 && !lines[j].StartsWith("Script error"); j++)
					text.Append(text.Length > 0 ? " " : "").Append(lines[j].Trim());
				// «папка/:путь» у папки-ресурса, «архив.pk3:путь» у архива (двоеточие после буквы диска — не разделитель)
				string full = m.Groups["full"].Value.Replace('\\', '/');
				int sep = full.IndexOf("/:", StringComparison.Ordinal);
				if(sep < 0) sep = full.LastIndexOf(':') > 1 ? full.LastIndexOf(':') : -1;
				string res = sep >= 0 ? full.Substring(0, sep).TrimEnd('/') : "";
				string path = sep >= 0 ? full.Substring(sep + (full[sep] == '/' ? 2 : 1)) : full;
				string file = string.Equals(res, srcnorm, StringComparison.OrdinalIgnoreCase)
					? FindFile(src, path) : res + ":" + path;
				CompilerError e = new CompilerError(text.ToString(), file, int.Parse(m.Groups["line"].Value) - 1);
				if(!list.Any(x => x.filename == e.filename && x.linenumber == e.linenumber && x.description == e.description)) list.Add(e);
			}
			return list;
		}

		// Путь в папке без учёта регистра (GZDoom пишет имена лампов в нижнем регистре)
		private static string FindFile(string root, string rel)
		{
			string cur = root;
			foreach(string part in rel.Split('/').Where(s => s.Length > 0))
			{
				string next = Directory.Exists(cur) ? Directory.EnumerateFileSystemEntries(cur)
					.FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase)) : null;
				cur = next ?? Path.Combine(cur, part);
			}
			return cur;
		}

		#endregion
	}
}
