#region ================== Namespaces

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

#endregion

namespace CodeImp.DoomBuilder.ZTProject.Mcp
{
	internal enum McpMode { Off = 0, ReadOnly = 1, ReadWrite = 2 }

	// MCP-сервер UDB (ZT/mcp-plan.md): Streamable HTTP на 127.0.0.1:<порт>/mcp, JSON-RPC 2.0 — initialize, ping,
	// tools/list, tools/call. HTTP — свой, поверх TcpListener: HttpListener требует резервирования адреса в Windows.
	// Доступ — по токену (заголовок Authorization: Bearer). Инструменты выполняются в потоке интерфейса UDB.
	internal sealed class McpServer
	{
		#region ================== Constants

		public const string PROTOCOL = "2025-06-18";
		private static readonly string[] Protocols = { "2025-06-18", "2025-03-26", "2024-11-05" };

		#endregion

		#region ================== Variables

		private readonly McpTools tools;
		private TcpListener listener;
		private Thread thread;
		private volatile bool running;

		#endregion

		#region ================== Properties

		public int Port { get; private set; }
		public string Token { get; private set; }
		public McpMode Mode { get; set; }
		public string Url { get { return "http://127.0.0.1:" + Port + "/mcp"; } }
		public bool Running { get { return running; } }

		// Последний вызов (для строки состояния): «инструмент — время»
		public event Action<string> Called;

		#endregion

		#region ================== Constructor

		public McpServer(McpTools tools, int port, string token, McpMode mode)
		{
			this.tools = tools;
			Port = port;
			Token = token;
			Mode = mode;
		}

		#endregion

		#region ================== Start / Stop

		public void Start()
		{
			listener = new TcpListener(IPAddress.Loopback, Port);
			listener.Start();
			running = true;
			thread = new Thread(AcceptLoop) { IsBackground = true, Name = "ZT MCP" };
			thread.Start();
		}

		public void Stop()
		{
			running = false;
			try { if(listener != null) listener.Stop(); } catch(Exception) { }
			listener = null;
		}

		private void AcceptLoop()
		{
			while(running)
			{
				TcpClient client;
				try { client = listener.AcceptTcpClient(); }
				catch(Exception) { break; }
				ThreadPool.QueueUserWorkItem(_ => Serve(client));
			}
		}

		#endregion

		#region ================== HTTP

		private sealed class Request
		{
			public string Method, Path;
			public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			public byte[] Body = new byte[0];

			public string Header(string name) { string v; return Headers.TryGetValue(name, out v) ? v : null; }
		}

		private void Serve(TcpClient client)
		{
			try
			{
				using(client)
				using(NetworkStream ns = client.GetStream())
				{
					ns.ReadTimeout = 300000;
					while(running)
					{
						Request req = ReadRequest(ns);
						if(req == null) break;
						string ctype = "application/json";
						byte[] body;
						int status = Handle(req, out body, ref ctype);
						bool close = string.Equals(req.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
						WriteResponse(ns, status, ctype, body, close);
						if(close) break;
					}
				}
			}
			catch(Exception)
			{
				// обрыв соединения клиентом
			}
		}

		private static Request ReadRequest(Stream s)
		{
			string line = ReadLine(s);
			if(string.IsNullOrEmpty(line)) return null;
			string[] parts = line.Split(' ');
			if(parts.Length < 2) return null;
			Request r = new Request { Method = parts[0].ToUpperInvariant(), Path = parts[1] };
			int q = r.Path.IndexOf('?');
			if(q >= 0) r.Path = r.Path.Substring(0, q);
			while(true)
			{
				string h = ReadLine(s);
				if(h == null) return null;
				if(h.Length == 0) break;
				int c = h.IndexOf(':');
				if(c > 0) r.Headers[h.Substring(0, c).Trim()] = h.Substring(c + 1).Trim();
			}
			string te = r.Header("Transfer-Encoding");
			if(te != null && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				MemoryStream ms = new MemoryStream();
				while(true)
				{
					string sz = ReadLine(s);
					if(sz == null) return null;
					int semi = sz.IndexOf(';');
					int n = int.Parse(semi >= 0 ? sz.Substring(0, semi) : sz, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
					if(n == 0) { while(!string.IsNullOrEmpty(ReadLine(s))) { } break; }
					byte[] chunk = ReadExactly(s, n);
					ms.Write(chunk, 0, chunk.Length);
					ReadLine(s);
				}
				r.Body = ms.ToArray();
			}
			else
			{
				int len;
				string cl = r.Header("Content-Length");
				if(cl != null && int.TryParse(cl, out len) && len > 0) r.Body = ReadExactly(s, len);
			}
			return r;
		}

		private static string ReadLine(Stream s)
		{
			List<byte> bytes = new List<byte>();
			while(true)
			{
				int b = s.ReadByte();
				if(b < 0) return bytes.Count > 0 ? Encoding.ASCII.GetString(bytes.ToArray()) : null;
				if(b == '\n') break;
				if(b != '\r') bytes.Add((byte)b);
				if(bytes.Count > 65536) throw new InvalidDataException("слишком длинная строка заголовка");
			}
			return Encoding.ASCII.GetString(bytes.ToArray());
		}

		private static byte[] ReadExactly(Stream s, int n)
		{
			byte[] buf = new byte[n];
			int got = 0;
			while(got < n)
			{
				int r = s.Read(buf, got, n - got);
				if(r <= 0) throw new EndOfStreamException();
				got += r;
			}
			return buf;
		}

		private static void WriteResponse(Stream s, int status, string ctype, byte[] body, bool close)
		{
			string reason = status == 200 ? "OK" : status == 202 ? "Accepted" : status == 401 ? "Unauthorized" : status == 403 ? "Forbidden" :
				status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed" : "Bad Request";
			StringBuilder h = new StringBuilder();
			h.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
			if(body.Length > 0) h.Append("Content-Type: ").Append(ctype).Append("\r\n");
			h.Append("Content-Length: ").Append(body.Length).Append("\r\n");
			if(status == 405) h.Append("Allow: POST\r\n");
			h.Append(close ? "Connection: close\r\n" : "Connection: keep-alive\r\n");
			h.Append("\r\n");
			byte[] head = Encoding.ASCII.GetBytes(h.ToString());
			s.Write(head, 0, head.Length);
			if(body.Length > 0) s.Write(body, 0, body.Length);
			s.Flush();
		}

		#endregion

		#region ================== JSON-RPC

		private static JavaScriptSerializer Json()
		{
			return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
		}

		private int Handle(Request req, out byte[] body, ref string ctype)
		{
			body = new byte[0];
			if(req.Path != "/mcp") return 404;

			// Защита от подмены DNS: запросы из браузера допускаются только со своих адресов
			string origin = req.Header("Origin");
			if(origin != null && !origin.StartsWith("http://127.0.0.1", StringComparison.Ordinal) &&
			   !origin.StartsWith("http://localhost", StringComparison.Ordinal)) return 403;
			if(req.Header("Authorization") != "Bearer " + Token)
			{
				body = Encoding.UTF8.GetBytes("{\"error\":\"нужен заголовок Authorization: Bearer <токен из меню ZT → MCP>\"}");
				return 401;
			}
			if(req.Method != "POST") return 405;

			object msg;
			try { msg = Json().DeserializeObject(Encoding.UTF8.GetString(req.Body)); }
			catch(Exception ex)
			{
				body = Encoding.UTF8.GetBytes(Json().Serialize(Error(null, -32700, "Parse error: " + ex.Message)));
				return 400;
			}

			// Пакет сообщений или одно сообщение
			List<object> replies = new List<object>();
			bool batch = msg is object[] || msg is ArrayList;
			IEnumerable items = batch ? (IEnumerable)msg : new[] { msg };
			foreach(object item in items)
			{
				Dictionary<string, object> m = item as Dictionary<string, object>;
				if(m == null) { replies.Add(Error(null, -32600, "Invalid Request")); continue; }
				object reply = Dispatch(m);
				if(reply != null) replies.Add(reply);
			}
			if(replies.Count == 0) return 202;
			body = Encoding.UTF8.GetBytes(Json().Serialize(batch ? (object)replies : replies[0]));
			return 200;
		}

		private object Dispatch(Dictionary<string, object> m)
		{
			object id;
			bool isrequest = m.TryGetValue("id", out id);
			object mo;
			string method = m.TryGetValue("method", out mo) ? mo as string : null;
			if(method == null) return null;    // ответ клиента на наш запрос — не используем
			object po;
			Dictionary<string, object> p = m.TryGetValue("params", out po) ? po as Dictionary<string, object> : null;
			if(p == null) p = new Dictionary<string, object>();

			try
			{
				switch(method)
				{
					case "initialize":
					{
						object pv;
						string asked = p.TryGetValue("protocolVersion", out pv) ? pv as string : null;
						string version = Array.IndexOf(Protocols, asked) >= 0 ? asked : PROTOCOL;
						return Reply(id, new Dictionary<string, object>
						{
							{ "protocolVersion", version },
							{ "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object> { { "listChanged", false } } } } },
							{ "serverInfo", new Dictionary<string, object> { { "name", "udb-zt" }, { "version", "0.1" } } },
							{ "instructions", McpTools.Instructions }
						});
					}
					case "ping":
						return isrequest ? Reply(id, new Dictionary<string, object>()) : null;
					case "tools/list":
						return Reply(id, new Dictionary<string, object> { { "tools", tools.List(Mode) } });
					case "tools/call":
					{
						object no, ao;
						string name = p.TryGetValue("name", out no) ? no as string : null;
						Dictionary<string, object> args = p.TryGetValue("arguments", out ao) ? ao as Dictionary<string, object> : null;
						ToolResult r = tools.Call(name, args ?? new Dictionary<string, object>(), Mode);
						Action<string> called = Called;
						if(called != null) called(name + " — " + DateTime.Now.ToString("HH:mm:ss"));
						return Reply(id, r.ToJson());
					}
					default:
						if(method.StartsWith("notifications/", StringComparison.Ordinal)) return null;
						return isrequest ? Error(id, -32601, "Method not found: " + method) : null;
				}
			}
			catch(Exception ex)
			{
				return isrequest ? Error(id, -32603, ex.GetType().Name + ": " + ex.Message) : null;
			}
		}

		private static Dictionary<string, object> Reply(object id, object result)
		{
			return new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } };
		}

		private static Dictionary<string, object> Error(object id, int code, string message)
		{
			return new Dictionary<string, object>
			{
				{ "jsonrpc", "2.0" }, { "id", id },
				{ "error", new Dictionary<string, object> { { "code", code }, { "message", message } } }
			};
		}

		#endregion

		#region ================== UI thread

		// Выполнить в потоке интерфейса UDB и дождаться результата. Во время модального окна (диалог, MessageBox)
		// вызов тоже выполняется: его вложенный цикл сообщений разбирает и наши. timedout — не дождались за timeoutms
		// (работа в потоке интерфейса при этом продолжается, например действие открыло модальное окно).
		public static object OnUi(Func<object> f, int timeoutms, out bool timedout)
		{
			timedout = false;
			Control ui = General.MainWindow;
			if(!ui.InvokeRequired) return f();
			object result = null;
			Exception error = null;
			ManualResetEvent done = new ManualResetEvent(false);
			ui.BeginInvoke((MethodInvoker)delegate
			{
				try { result = f(); }
				catch(Exception ex) { error = ex; }
				finally { done.Set(); }
			});
			if(!done.WaitOne(timeoutms)) { timedout = true; return null; }
			if(error != null) throw new TargetException(error);
			return result;
		}

		#endregion
	}

	// Исключение из потока интерфейса с исходным сообщением
	internal sealed class TargetException : Exception
	{
		public TargetException(Exception inner) : base(inner.Message, inner) { }
	}
}
