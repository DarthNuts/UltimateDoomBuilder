#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

#endregion

namespace CodeImp.DoomBuilder.ZTProject.Chat
{
	// Сеанс чата с Claude на ИИ-машине (ZT/workbench-plan.md §4): ssh.exe с ключом рабочей копии запускает немодифицированный
	// Claude Code пользователя (`claude -p`, поток JSON в обе стороны) в его рабочей копии на машине. Обратный порт ssh -R
	// отдаёт Claude MCP-сервер UDB (живая карта); запросы разрешений приходят в том же потоке (--permission-prompt-tool
	// stdio) и решаются кнопками панели. События разбираются в фоновом потоке и передаются наружу как есть — панель сама
	// переводит их в поток интерфейса.
	internal sealed class ChatSession : IDisposable
	{
		#region ================== Events

		public event Action<string> SessionStarted;                           // id сессии Claude
		public event Action<string> TextDelta;                                // кусок ответа по мере набора
		public event Action<string, string> ToolUse;                          // инструмент, кратко что делает
		public event Action<string, string, Dictionary<string, object>> PermissionRequest;   // id запроса, инструмент, ввод
		public event Action<string, bool, double> TurnFinished;               // итог хода, ошибка, стоимость
		public event Action<string> Info;                                     // служебное: stderr, состояние
		public event Action<int> Exited;                                      // процесс завершился (код)

		#endregion

		#region ================== Variables

		private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
		private Process proc;
		private readonly object writelock = new object();
		private int nextid;
		private volatile bool streamedtext;    // в этом ходе текст уже шёл кусками — целое сообщение не дублировать

		#endregion

		#region ================== Properties

		public string SessionId { get; private set; }
		public bool Running { get { return proc != null && !proc.HasExited; } }
		public bool Busy { get; private set; }

		#endregion

		#region ================== Start / Stop

		// ssh — исполняемый файл и опции из core.sshCommand рабочей копии; remotecmd — команда на машине
		public void Start(List<string> ssh, string host, int remoteport, int localport, string remotecmd)
		{
			List<string> args = ssh.Skip(1).ToList();
			args.AddRange(new[] { "-T", "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=30",
				"-R", "127.0.0.1:" + remoteport + ":127.0.0.1:" + localport, host, remotecmd });
			ProcessStartInfo psi = new ProcessStartInfo(ssh[0], string.Join(" ", args.Select(Quote)))
			{
				UseShellExecute = false, CreateNoWindow = true,
				RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
			};
			proc = Process.Start(psi);
			proc.StandardInput.AutoFlush = true;
			proc.EnableRaisingEvents = true;
			proc.Exited += (s, e) => { Busy = false; Raise(Exited, SafeExitCode()); };
			new Thread(ReadOut) { IsBackground = true, Name = "ZT chat out" }.Start();
			new Thread(ReadErr) { IsBackground = true, Name = "ZT chat err" }.Start();
			Control("initialize", null);
		}

		private int SafeExitCode() { try { return proc.ExitCode; } catch(Exception) { return -1; } }

		public void Dispose()
		{
			if(proc == null) return;
			try { if(!proc.HasExited) { proc.StandardInput.Close(); if(!proc.WaitForExit(3000)) proc.Kill(); } }
			catch(Exception) { }
			proc = null;
		}

		#endregion

		#region ================== Sending

		private void Write(object message)
		{
			string line = json.Serialize(message);
			lock(writelock)
			{
				if(!Running) throw new InvalidOperationException("сеанс чата не запущен");
				// Тот же UTF-8, что у потока вывода; StandardInput по умолчанию — кодовая страница консоли
				byte[] b = Encoding.UTF8.GetBytes(line + "\n");
				proc.StandardInput.BaseStream.Write(b, 0, b.Length);
				proc.StandardInput.BaseStream.Flush();
			}
		}

		public void Send(string text)
		{
			Busy = true;
			streamedtext = false;
			Write(new Dictionary<string, object>
			{
				{ "type", "user" },
				{ "message", new Dictionary<string, object> { { "role", "user" }, { "content", text } } }
			});
		}

		private void Control(string subtype, Dictionary<string, object> extra)
		{
			Dictionary<string, object> req = new Dictionary<string, object> { { "subtype", subtype } };
			if(extra != null) foreach(KeyValuePair<string, object> kv in extra) req[kv.Key] = kv.Value;
			Write(new Dictionary<string, object>
			{
				{ "type", "control_request" }, { "request_id", "udb" + Interlocked.Increment(ref nextid) }, { "request", req }
			});
		}

		// Остановить текущий ход (как Esc в терминале): сеанс остаётся, историю можно продолжать
		public void Interrupt() { Control("interrupt", null); }

		// Ответ на запрос разрешения
		public void Answer(string requestid, bool allow, Dictionary<string, object> input, string message)
		{
			Dictionary<string, object> response = allow
				? new Dictionary<string, object> { { "behavior", "allow" }, { "updatedInput", input ?? new Dictionary<string, object>() } }
				: new Dictionary<string, object> { { "behavior", "deny" }, { "message", message ?? "Пользователь отклонил в UDB" } };
			Write(new Dictionary<string, object>
			{
				{ "type", "control_response" },
				{ "response", new Dictionary<string, object> { { "subtype", "success" }, { "request_id", requestid }, { "response", response } } }
			});
		}

		#endregion

		#region ================== Reading

		private void ReadErr()
		{
			try
			{
				string line;
				while((line = proc.StandardError.ReadLine()) != null)
					if(line.Trim().Length > 0) Raise(Info, line.Trim());
			}
			catch(Exception) { }
		}

		private void ReadOut()
		{
			try
			{
				string line;
				while((line = proc.StandardOutput.ReadLine()) != null)
				{
					line = line.Trim();
					if(line.Length == 0) continue;
					Dictionary<string, object> m;
					try { m = json.DeserializeObject(line) as Dictionary<string, object>; }
					catch(Exception) { Raise(Info, line); continue; }
					if(m != null) Handle(m);
				}
			}
			catch(Exception ex) { Raise(Info, "чтение ответа прервалось: " + ex.Message); }
		}

		private static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as string : null; }
		private static Dictionary<string, object> D(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
		private static IEnumerable<object> L(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v is System.Collections.IEnumerable && !(v is string) ? ((System.Collections.IEnumerable)v).Cast<object>() : Enumerable.Empty<object>(); }

		private void Handle(Dictionary<string, object> m)
		{
			switch(S(m, "type"))
			{
				case "system":
					if(S(m, "subtype") == "init")
					{
						SessionId = S(m, "session_id");
						Raise(SessionStarted, SessionId);
						foreach(Dictionary<string, object> srv in L(m, "mcp_servers").OfType<Dictionary<string, object>>())
							Raise(Info, "MCP " + S(srv, "name") + ": " + S(srv, "status"));
					}
					break;

				case "stream_event":
				{
					Dictionary<string, object> ev = D(m, "event");
					Dictionary<string, object> delta = D(ev, "delta");
					if(S(ev, "type") == "content_block_delta" && S(delta, "type") == "text_delta")
					{
						streamedtext = true;
						Raise(TextDelta, S(delta, "text"));
					}
					break;
				}

				case "assistant":
					foreach(Dictionary<string, object> block in L(D(m, "message"), "content").OfType<Dictionary<string, object>>())
					{
						string bt = S(block, "type");
						if(bt == "text" && !streamedtext) Raise(TextDelta, S(block, "text"));
						else if(bt == "tool_use") Raise(ToolUse, S(block, "name"), Summary(S(block, "name"), D(block, "input")));
					}
					streamedtext = false;
					break;

				case "control_request":
				{
					Dictionary<string, object> req = D(m, "request");
					if(S(req, "subtype") == "can_use_tool")
						Raise(PermissionRequest, S(m, "request_id"), S(req, "tool_name"), D(req, "input") ?? new Dictionary<string, object>());
					break;
				}

				case "result":
				{
					Busy = false;
					object cost, err;
					double c = m.TryGetValue("total_cost_usd", out cost) && cost != null ? Convert.ToDouble(cost, System.Globalization.CultureInfo.InvariantCulture) : 0;
					bool e = m.TryGetValue("is_error", out err) && err is bool && (bool)err;
					Raise(TurnFinished, S(m, "result") ?? "", e, c);
					break;
				}
			}
		}

		// Кратко, что делает инструмент: путь файла, команда, шаблон поиска
		internal static string Summary(string tool, Dictionary<string, object> input)
		{
			if(input == null) return "";
			foreach(string k in new[] { "file_path", "command", "pattern", "path", "url", "description", "prompt" })
			{
				string v = S(input, k);
				if(!string.IsNullOrEmpty(v)) return v.Length > 160 ? v.Substring(0, 160) + "…" : v.Replace("\n", " ");
			}
			return string.Join(", ", input.Keys.Take(4));
		}

		private static void Raise<T>(Action<T> h, T a) { if(h != null) h(a); }
		private static void Raise<T1, T2>(Action<T1, T2> h, T1 a, T2 b) { if(h != null) h(a, b); }
		private static void Raise<T1, T2, T3>(Action<T1, T2, T3> h, T1 a, T2 b, T3 c) { if(h != null) h(a, b, c); }

		private static string Quote(string arg)
		{
			if(arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
			return "\"" + arg.Replace("\\\"", "\\\\\"").Replace("\"", "\\\"") + "\"";
		}

		#endregion
	}
}
