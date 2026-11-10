#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	internal sealed class GitResult
	{
		public int ExitCode;
		public string Output;     // stdout + stderr
		public string StdOut;
		public bool Ok { get { return ExitCode == 0; } }
	}

	// Вызов git рабочей копии проекта. Авторизация — настройками самой рабочей копии (core.sshCommand с ключом машины);
	// запросы пароля в консоли отключены, чтобы git не повис без окна.
	internal static class Git
	{
		public static GitResult Run(string dir, params string[] args)
		{
			ProcessStartInfo psi = new ProcessStartInfo("git", string.Join(" ", args.Select(Quote)))
			{
				WorkingDirectory = dir,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8
			};
			psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
			psi.EnvironmentVariables["LANG"] = "C";       // сообщения git — по-английски: их разбирает плагин
			psi.EnvironmentVariables["LC_ALL"] = "C";

			using(Process p = Process.Start(psi))
			{
				Task<string> err = p.StandardError.ReadToEndAsync();
				string outp = p.StandardOutput.ReadToEnd();
				if(!p.WaitForExit(180000))
				{
					try { p.Kill(); } catch { }
					return new GitResult { ExitCode = -1, StdOut = outp, Output = outp + "\ngit не ответил за 3 минуты" };
				}
				string e = err.Result;
				return new GitResult { ExitCode = p.ExitCode, StdOut = outp, Output = (outp + e).Trim() };
			}
		}

		// Список строк вывода без пустых
		public static List<string> Lines(GitResult r)
		{
			return (r.StdOut ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
		}

		// Сообщение коммита — через файл: так кириллица доходит до git без искажений командной строки
		public static GitResult CommitWithMessage(string dir, string message)
		{
			string tmp = Path.GetTempFileName();
			try
			{
				File.WriteAllText(tmp, message, new UTF8Encoding(false));
				return Run(dir, "commit", "-q", "-F", tmp);
			}
			finally
			{
				try { File.Delete(tmp); } catch { }
			}
		}

		// Экранирование аргумента по правилам CommandLineToArgvW
		private static string Quote(string arg)
		{
			if(arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
			StringBuilder sb = new StringBuilder("\"");
			int backslashes = 0;
			foreach(char c in arg)
			{
				if(c == '\\') { backslashes++; continue; }
				if(c == '"') sb.Append('\\', backslashes * 2 + 1);
				else sb.Append('\\', backslashes);
				backslashes = 0;
				sb.Append(c);
			}
			sb.Append('\\', backslashes * 2);
			sb.Append('"');
			return sb.ToString();
		}
	}
}
