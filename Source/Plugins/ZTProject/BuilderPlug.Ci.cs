#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Сборка и проверки после «Отправить» (ZT/workbench-plan.md §2.2, п. 5): на машине в фоне, от имени пользователя, в его
	// ~/ci/ (рецепт Resources/ci.sh → ~/.zt/ci.sh, режим check: WAD, pk3, проверка ZScript полным набором модов). Рабочую
	// копию Claude сборка не трогает. Плагин следит за ~/ci/out/<ветка>/status и пишет итог в панель чата: «проверки
	// пройдены» или ошибки с файлом и строкой. Запускает сам плагин — хук post-receive голого репозитория может делать то же
	// (тогда собираются и push самого Claude); двойной запуск безвреден: сборки идут по очереди, тот же коммит не повторяется.
	public partial class BuilderPlug
	{
		#region ================== Variables

		private static string ciscript;
		private volatile string cistate = "";      // последний итог сборки после push (MCP zt_ci_status)

		#endregion

		#region ================== Recipe

		internal static string CiScript
		{
			get
			{
				if(ciscript == null)
					using(Stream s = typeof(BuilderPlug).Assembly.GetManifestResourceStream("CodeImp.DoomBuilder.ZTProject.Resources.ci.sh"))
					using(StreamReader r = new StreamReader(s, Encoding.UTF8))
						ciscript = r.ReadToEnd().Replace("\r\n", "\n");
				return ciscript;
			}
		}

		// bash: записать ~/.zt/ci.sh (через временный файл — запущенная сборка не увидит полузаписанный рецепт)
		internal static string CiInstall
		{
			get
			{
				return "mkdir -p ~/.zt && chmod 700 ~/.zt\ncat > ~/.zt/ci.sh.tmp <<'ZTCIEOF'\n" + CiScript + (CiScript.EndsWith("\n") ? "" : "\n") +
				       "ZTCIEOF\nchmod 700 ~/.zt/ci.sh.tmp && mv -f ~/.zt/ci.sh.tmp ~/.zt/ci.sh\n";
			}
		}

		// ssh рабочей копии, адрес машины и путь голого репозитория; false — сервер не машина по SSH
		private static bool RemoteOf(string dir, out List<string> ssh, out string host, out string bare)
		{
			ssh = null; host = null; bare = null;
			string url = Git.Run(dir, "config", "--get", "remote.origin.url").StdOut.Trim();
			int colon = url.IndexOf(':');
			if(colon <= 1 || url.Contains("://")) return false;
			host = url.Substring(0, colon);
			bare = url.Substring(colon + 1);
			ssh = SplitCommand(Git.Run(dir, "config", "--get", "core.sshCommand").StdOut.Trim());
			if(ssh.Count == 0) ssh.Add("ssh");
			return true;
		}

		#endregion

		#region ================== After push

		// Запустить сборку и проверки ветки на машине и следить за итогом (всё в фоне)
		internal void StartCi(string dir, string branch)
		{
			List<string> ssh;
			string host, bare;
			if(!RemoteOf(dir, out ssh, out host, out bare)) return;
			if(!Regex.IsMatch(branch, @"^[A-Za-z0-9._/-]+$") || !Regex.IsMatch(bare, @"^[A-Za-z0-9._/~-]+$")) return;
			string commit = Git.Run(dir, "rev-parse", "--short", "HEAD").StdOut.Trim();
			cistate = "идёт: " + branch + " " + commit;
			new Thread(() =>
			{
				long since;
				try
				{
					string o = Exec(ssh[0], ssh.Skip(1).Concat(new[] { host, "bash -s -- " + branch + " " + bare }),
						CiInstall + "date +%s\n(setsid nohup bash ~/.zt/ci.sh \"$1\" \"$2\" check < /dev/null > /dev/null 2>&1 &)\n", 60000);
					since = long.Parse(o.Trim().Split('\n').Last().Trim()) - 2;
				}
				catch(Exception ex)
				{
					cistate = "не запустилась: " + ex.Message.Split('\n')[0];
					Ui(() => chatpanel.Error("сборка на машине не запустилась: " + ex.Message.Split('\n')[0]));
					return;
				}
				SetBuildStatus("сборка и проверки " + branch + " (" + commit + ") идут на машине…", false);
				WatchCi(ssh, host, CiOut(bare, branch), branch, since);
			}) { IsBackground = true, Name = "ZT CI start" }.Start();
		}

		// Ждать итога в ~/ci/out/<ветка>/status, записанного после запуска (время машины)
		// Папка итога на машине — как в ci.sh: у проекта ~/ci/out/<ветка>, у другого голого репозитория ~/ci/out-<имя>/<ветка>
		private static string CiOut(string bare, string branch)
		{
			string name = bare.TrimEnd('/');
			name = name.Substring(name.LastIndexOf('/') + 1);
			if(name.EndsWith(".git")) name = name.Substring(0, name.Length - 4);
			return (name == "ZeroTolerance" ? "~/ci/out/" : "~/ci/out-" + name + "/") + branch;
		}

		private void WatchCi(List<string> ssh, string host, string outdir, string branch, long since)
		{
			// «; true»: без check.txt head вернул бы код 1, и Exec счёл бы опрос неудачным
			string cmd = "cat " + outdir + "/status 2>/dev/null; echo ZTSEP; head -50 " + outdir + "/check.txt 2>/dev/null; true";
			for(int i = 0; i < 180; i++)     // до 30 минут
			{
				Thread.Sleep(i == 0 ? 4000 : 10000);
				string o;
				try { o = Exec(ssh[0], ssh.Skip(1).Concat(new[] { "-n", host, cmd }), null, 60000).Replace("\r\n", "\n"); }
				catch(Exception) { continue; }
				int sep = o.IndexOf("ZTSEP", StringComparison.Ordinal);
				string[] st = (sep >= 0 ? o.Substring(0, sep) : o).Trim().Split(' ');
				string details = sep >= 0 ? o.Substring(sep + 5).Trim() : "";
				long time;
				if(st.Length < 3 || !long.TryParse(st[2], out time) || time < since) continue;
				if(st[0] != "ok" && st[0] != "fail") continue;
				string c = st[1].Length > 7 ? st[1].Substring(0, 7) : st[1];
				CiDone(branch, c, st[0] == "ok", details);
				return;
			}
			cistate = "нет итога за 30 минут: " + branch;
			SetBuildStatus("сборка " + branch + ": итога нет за 30 минут (~/ci/out/" + branch + "/status на машине)", true);
		}

		private void CiDone(string branch, string commit, bool ok, string details)
		{
			string head = "сборка " + branch + " " + commit + ": ";
			if(ok)
			{
				cistate = "ok " + branch + " " + commit;
				SetBuildStatus(head + "проверки пройдены, WAD и pk3 готовы на машине", true);
				Ui(() => chatpanel.Note(head + "проверки пройдены — WAD и pk3 готовы на машине, «Получить сборку» скачает их"));
				return;
			}
			// Ошибка ZScript в собранном pk3 — путь к файлу проекта: «ZeroTolerance.pk3:zscript/x.zs" line N» → src/zscript/x.zs:N
			string text = Regex.Replace(details, "\"ZeroTolerance\\.pk3:([^\"]+)\" line (\\d+)", m => "\"src/" + m.Groups[1].Value + "\" line " + m.Groups[2].Value);
			cistate = "fail " + branch + " " + commit + "\n" + text;
			SetBuildStatus(head + "ошибка — подробности в чате", true);
			Ui(() =>
			{
				chatpanel.Error(head + (text.Length > 0 ? text : "ошибка (подробности — ~/ci/out/" + branch + "/build.log и check.log на машине)"));
				General.Interface.SelectDocker(chatdocker);
			});
		}

		#endregion

		#region ================== For MCP

		internal string CiStateForMcp { get { return cistate; } }

		#endregion
	}
}
