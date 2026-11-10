#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Actions;
using CodeImp.DoomBuilder.Controls;
using CodeImp.DoomBuilder.Data;
using CodeImp.DoomBuilder.Windows;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// «Получить сборку» (ZT/workbench-plan.md §2, этап 2): машина собирает WAD и pk3 из своей ветки на сервере — в отдельной
	// копии ~/ci/ZeroTolerance, не трогая рабочую копию, где работает Claude, — и плагин скачивает изменившиеся файлы в папку
	// GZDoom (сверка sha256, запись через .part). Потом UDB перечитывает ресурсы. Всё идёт в фоне, UDB не замирает.
	public partial class BuilderPlug
	{
		#region ================== Variables

		// Рецепт сборки и проверок — Resources/ci.sh (ставится на машину в ~/.zt/ci.sh: его зовут «Получить сборку» и сборка
		// после «Отправить»). Вывод для плагина: «ZTBUILD <sha256> <размер> <путь>» по файлу, «ZTCOMMIT <коммит> <тема>».

		private ToolStripMenuItem buildmenu;
		private volatile bool building;
		private string buildstatus = "";          // последнее состояние сборки (для строки состояния и MCP)

		#endregion

		#region ================== Interface

		private void InitBuild()
		{
			buildmenu = new ToolStripMenuItem("Получить сборку (WAD и pk3 с машины)", null, (s, e) => GetBuild(null));
			// после «Получить свежие правки»
			int i = 0;
			while(i < menu.DropDownItems.Count && menu.DropDownItems[i].Text != "Получить свежие правки") i++;
			menu.DropDownItems.Insert(Math.Min(i + 1, menu.DropDownItems.Count), buildmenu);
		}

		internal string BuildStatus { get { return (building ? "идёт: " : "") + buildstatus; } }

		private void SetBuildStatus(string text, bool final)
		{
			buildstatus = text;
			try
			{
				General.MainWindow.BeginInvoke((MethodInvoker)delegate
				{
					General.Interface.DisplayStatus(final ? StatusType.Info : StatusType.Busy, "ZT: " + text);
				});
			}
			catch(InvalidOperationException) { }
		}

		#endregion

		#region ================== Action

		[BeginAction("ztgetbuild")]
		private void GetBuildAction() { GetBuild(null); }

		// dest — папка GZDoom (null — из настроек или ресурсов игровой конфигурации). Возвращает сразу, сборка — в фоне.
		internal string GetBuild(string dest)
		{
			if(building) return "сборка уже идёт";
			string dir = dest == null ? EnsureProjectDir() : ProjectDir;
			if(dir == null || !ProjectMap.IsProjectDir(dir)) return "папка проекта не выбрана";
			string gz = dest ?? GzdoomDir();
			if(gz == null) return "папка GZDoom не выбрана";

			string branch = CurrentBranch(dir);
			string url = Git.Run(dir, "config", "--get", "remote.origin.url").StdOut.Trim();
			int colon = url.IndexOf(':');
			if(colon <= 1 || url.Contains("://"))     // нет «хост:путь» или это путь с буквой диска
			{
				General.ShowWarningMessage("ZT: сервер рабочей копии — не ИИ-машина по SSH (" + url + ").", MessageBoxButtons.OK);
				return "сервер не по SSH";
			}
			string host = url.Substring(0, colon), bare = url.Substring(colon + 1);
			List<string> ssh = SplitCommand(Git.Run(dir, "config", "--get", "core.sshCommand").StdOut.Trim());
			if(ssh.Count == 0) ssh.Add("ssh");

			building = true;
			buildmenu.Enabled = false;
			SetBuildStatus("сборка ветки " + branch + " на машине…", false);
			Task.Run(() => RunBuild(ssh, host, bare, branch, gz)).ContinueWith(t =>
			{
				General.MainWindow.BeginInvoke((MethodInvoker)delegate { BuildDone(t.IsFaulted ? null : t.Result, t.Exception, gz); });
			});
			return "сборка ветки " + branch + " запущена, файлы — в " + gz;
		}

		private sealed class BuildResult
		{
			public string Commit = "";
			public List<string> Updated = new List<string>(), Same = new List<string>();
			public TimeSpan Time;
		}

		private BuildResult RunBuild(List<string> ssh, string host, string bare, string branch, string gz)
		{
			DateTime start = DateTime.Now;
			BuildResult r = new BuildResult();
			string output = Exec(ssh[0], ssh.Skip(1).Concat(new[] { host, "bash -s -- " + branch + " " + bare }),
				CiInstall + "exec bash ~/.zt/ci.sh \"$1\" \"$2\"\n", 1200000);
			List<string[]> files = new List<string[]>();
			foreach(string line in output.Split('\n').Select(l => l.Trim()))
			{
				if(line.StartsWith("ZTBUILD ", StringComparison.Ordinal)) files.Add(line.Substring(8).Split(new[] { ' ' }, 3));
				else if(line.StartsWith("ZTCOMMIT ", StringComparison.Ordinal)) r.Commit = line.Substring(9);
			}
			if(files.Count == 0) throw new InvalidOperationException("машина не вернула сборку:\n" + output);

			string scp = ssh[0] == "ssh" ? "scp" : Path.Combine(Path.GetDirectoryName(ssh[0]) ?? "", "scp" + Path.GetExtension(ssh[0]));
			foreach(string[] f in files)
			{
				string sha = f[0], remote = f[2], name = remote.Substring(remote.LastIndexOf('/') + 1);
				long size = long.Parse(f[1]);
				string local = Path.Combine(gz, name);
				if(File.Exists(local) && Sha256(local) == sha) { r.Same.Add(name); continue; }

				string part = local + ".part";
				if(File.Exists(part)) File.Delete(part);
				SetBuildStatus("качаю " + name + " (" + size / 1048576 + " МБ)…", false);
				using(System.Threading.Timer progress = new System.Threading.Timer(_ =>
				{
					try { if(File.Exists(part)) SetBuildStatus("качаю " + name + ": " + new FileInfo(part).Length / 1048576 + " из " + size / 1048576 + " МБ…", false); }
					catch(IOException) { }
				}, null, 2000, 2000))
				{
					Exec(scp, ssh.Skip(1).Concat(new[] { "-q", host + ":" + remote, part }), null, 3600000);
				}
				if(Sha256(part) != sha) { File.Delete(part); throw new InvalidOperationException(name + ": хеш скачанного файла не совпал"); }
				try
				{
					if(File.Exists(local)) File.Delete(local);
					File.Move(part, local);
				}
				catch(IOException ex)
				{
					throw new InvalidOperationException(name + " не заменён (файл занят — закрыта ли игра?): " + ex.Message + "\nСкачанный файл — " + part);
				}
				r.Updated.Add(name);
			}
			r.Time = DateTime.Now - start;
			return r;
		}

		private void BuildDone(BuildResult r, AggregateException error, string gz)
		{
			building = false;
			if(buildmenu != null) buildmenu.Enabled = true;
			if(r == null)
			{
				string msg = error != null ? error.InnerException.Message : "неизвестная ошибка";
				SetBuildStatus("сборка не получена: " + msg.Split('\n')[0], true);
				Dialogs.ShowText(General.Interface, "ZT: сборка не получена", msg);
				return;
			}
			string text = "сборка " + r.Commit + " за " + (int)r.Time.TotalSeconds + " с: " +
			              (r.Updated.Count > 0 ? "обновлены " + string.Join(", ", r.Updated) : "файлы уже свежие") +
			              (r.Same.Count > 0 && r.Updated.Count > 0 ? "; без изменений " + string.Join(", ", r.Same) : "") + " (" + gz + ")";
			// Новые ресурсы — UDB перечитывает их (карта и история отмены остаются)
			if(r.Updated.Count > 0 && General.Map != null && General.Editing.Mode != null && !General.Editing.Mode.Attributes.Volatile)
			{
				General.Actions.InvokeAction("builder_reloadresources");
				text += "; ресурсы UDB перечитаны";
			}
			SetBuildStatus(text, true);
			General.WriteLogLine("ZT: " + text);
		}

		#endregion

		#region ================== Helpers

		// Папка GZDoom: из настроек плагина, иначе — папка ZeroTolerance.pk3 из ресурсов игровой конфигурации, иначе — выбор
		private string GzdoomDir()
		{
			string dir = General.Settings.ReadPluginSetting("gzdoomdir", "");
			if(Directory.Exists(dir)) return dir;
			IEnumerable<DataLocation> res = General.Map != null && General.Map.ConfigSettings != null
				? General.Map.ConfigSettings.Resources : General.Configs.SelectMany(c => c.Resources);
			DataLocation pk3 = res.FirstOrDefault(d => d.location != null &&
				string.Equals(Path.GetFileName(d.location), "ZeroTolerance.pk3", StringComparison.OrdinalIgnoreCase));
			if(pk3.location != null) dir = Path.GetDirectoryName(pk3.location);   // DataLocation — структура
			else
			{
				FolderSelectDialog fd = new FolderSelectDialog { Title = "ZT: папка GZDoom (куда класть ZeroTolerance.wad и ZeroTolerance.pk3)" };
				if(!fd.ShowDialog(General.Interface.Handle) || string.IsNullOrEmpty(fd.FileName)) return null;
				dir = fd.FileName;
			}
			General.Settings.WritePluginSetting("gzdoomdir", dir);
			return dir;
		}

		private static string Sha256(string path)
		{
			using(SHA256 sha = SHA256.Create())
			using(FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
		}

		// Команда core.sshCommand -> исполняемый файл и аргументы (кавычки "…" держат пробелы)
		private static List<string> SplitCommand(string cmd)
		{
			List<string> parts = new List<string>();
			StringBuilder cur = new StringBuilder();
			bool quoted = false, any = false;
			foreach(char c in cmd)
			{
				if(c == '"') { quoted = !quoted; any = true; }
				else if(char.IsWhiteSpace(c) && !quoted) { if(any) { parts.Add(cur.ToString()); cur.Clear(); any = false; } }
				else { cur.Append(c); any = true; }
			}
			if(any) parts.Add(cur.ToString());
			return parts;
		}

		// Запуск процесса; stdin — текст на вход (null — без входа). Ошибка — исключение с выводом.
		private static string Exec(string exe, IEnumerable<string> args, string stdin, int timeoutms)
		{
			ProcessStartInfo psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(QuoteArg)))
			{
				UseShellExecute = false, CreateNoWindow = true,
				RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null,
				StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
			};
			using(Process p = Process.Start(psi))
			{
				Task<string> err = p.StandardError.ReadToEndAsync();
				Task<string> outp = p.StandardOutput.ReadToEndAsync();
				if(stdin != null)
				{
					// UTF-8 байтами: StandardInput .NET Framework пишет в кодовой странице консоли и портит кириллицу
					byte[] b = Encoding.UTF8.GetBytes(stdin.Replace("\r\n", "\n"));
					p.StandardInput.BaseStream.Write(b, 0, b.Length);
					p.StandardInput.Close();
				}
				if(!p.WaitForExit(timeoutms))
				{
					try { p.Kill(); } catch(Exception) { }
					throw new TimeoutException(Path.GetFileName(exe) + " не завершился за " + timeoutms / 60000 + " мин");
				}
				p.WaitForExit();
				string o = outp.Result, e = err.Result;
				if(p.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(exe) + " — код " + p.ExitCode + "\n" + (o + "\n" + e).Trim());
				return o;
			}
		}

		private static string QuoteArg(string arg)
		{
			if(arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
			return "\"" + arg.Replace("\\\"", "\\\\\"").Replace("\"", "\\\"") + "\"";
		}

		#endregion
	}
}
