#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Actions;
using CodeImp.DoomBuilder.Controls;
using CodeImp.DoomBuilder.Windows;
using CodeImp.DoomBuilder.ZTProject.Chat;
using CodeImp.DoomBuilder.ZTProject.Mcp;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Чат с Claude в UDB (ZT/workbench-plan.md §4, этап 3): докер «Чат Claude», сеанс — ChatSession через ssh рабочей копии.
	// Перед запуском на машину пишутся ~/.zt/udb-mcp.json (MCP UDB через обратный порт, токен) и ~/.zt/chat.sh (запуск
	// Claude Code пользователя), права 600 — порт на localhost машины видят все её пользователи, поэтому нужен токен.
	public partial class BuilderPlug
	{
		#region ================== Variables

		private ChatPanel chatpanel;
		private Docker chatdocker;
		private ChatSession chat;

		private string ChatLastSession
		{
			get { return General.Settings.ReadPluginSetting("chatsession", ""); }
			set { General.Settings.WritePluginSetting("chatsession", value); }
		}

		// Порт на машине для обратного туннеля к MCP-серверу UDB (у каждого пользователя машины — свой)
		private int ChatRemotePort { get { return General.Settings.ReadPluginSetting("chatport", 47391); } }

		// Команда на машине вместо chat.sh (для проверки — учебный «claude»); пусто — настоящий Claude Code
		private string ChatCommand { get { return chatcommand ?? General.Settings.ReadPluginSetting("chatcommand", ""); } }
		internal string chatcommand;     // на один сеанс (MCP chat_start command — учебный «claude» для проверки)

		#endregion

		#region ================== Interface

		private void InitChat()
		{
			chatpanel = new ChatPanel();
			chatdocker = new Docker("ztchat", "Чат Claude", chatpanel);
			General.Interface.AddDocker(chatdocker);
			chatpanel.SendRequested += ChatSend;
			chatpanel.StopRequested += () => { if(chat != null && chat.Running) chat.Interrupt(); };
			chatpanel.NewChatRequested += () => StartChat(null);
			chatpanel.ResumeRequested += () => StartChat(ChatLastSession.Length > 0 ? ChatLastSession : null);
			chatpanel.AttachRequested += AttachSelection;
			chatpanel.GetRequested += () => Get();
			chatpanel.PermissionAnswered += (id, allow, input) => { if(chat != null && chat.Running) chat.Answer(id, allow, input, null); };
			menu.DropDownItems.Insert(0, new ToolStripMenuItem("Чат с Claude", null, (s, e) => ShowChat()));
			menu.DropDownItems.Insert(1, new ToolStripSeparator());
		}

		private void DisposeChat()
		{
			if(chat != null) { chat.Dispose(); chat = null; }
			if(chatdocker != null) { General.Interface.RemoveDocker(chatdocker); chatdocker = null; }
		}

		[BeginAction("ztchat")]
		private void ShowChat()
		{
			General.Interface.SelectDocker(chatdocker);
			if(chat == null || !chat.Running) chatpanel.SetStatus("не подключено — «Новый чат» или «Продолжить»", false, false);
		}

		private void Ui(System.Action a)
		{
			try { General.MainWindow.BeginInvoke((MethodInvoker)(() => a())); }
			catch(InvalidOperationException) { }
		}

		#endregion

		#region ================== Session

		// Запуск сеанса; resume — id прежней сессии Claude (null — новый чат). null — запущен, иначе — почему нет.
		internal string StartChat(string resume)
		{
			if(chat != null) { chat.Dispose(); chat = null; }
			chatpanel.CancelPermission();
			string dir = ProjectDir;
			if(!ProjectMap.IsProjectDir(dir)) return Fail("папка проекта не выбрана (ZT → «Папка проекта…»)");
			string url = Git.Run(dir, "config", "--get", "remote.origin.url").StdOut.Trim();
			int colon = url.IndexOf(':');
			if(colon <= 1 || url.Contains("://")) return Fail("сервер рабочей копии — не ИИ-машина по SSH (" + url + ")");
			string host = url.Substring(0, colon);
			List<string> ssh = SplitCommand(Git.Run(dir, "config", "--get", "core.sshCommand").StdOut.Trim());
			if(ssh.Count == 0) ssh.Add("ssh");

			// MCP-сервер UDB нужен Claude для живой карты: включить на время сеанса, если выключен
			if(mcp == null) SetMcpMode(McpMode.ReadWrite, false);
			if(mcp == null) return Fail("MCP-сервер UDB не запустился");

			try
			{
				Exec(ssh[0], ssh.Skip(1).Concat(new[] { host, "bash -s" }), RemoteSetup(mcp.Token), 60000);
			}
			catch(Exception ex) { return Fail("не удалось подготовить машину: " + ex.Message.Split('\n')[0]); }

			string cmd = ChatCommand.Length > 0 ? ChatCommand : "bash ~/.zt/chat.sh";
			if(resume != null) cmd += " --resume " + resume;
			chat = new ChatSession();
			ChatSession me = chat;
			chat.SessionStarted += id => Ui(() => { if(me != chat) return; ChatLastSession = id; chatpanel.SetStatus("сессия " + id, true, chat.Busy); });
			chat.TextDelta += t => Ui(() => { if(me == chat) chatpanel.Text(t); });
			chat.ToolUse += (n, w) => Ui(() => { if(me == chat) chatpanel.Tool(n, w); });
			chat.PermissionRequest += (id, tool, input) => Ui(() => { if(me == chat) chatpanel.AskPermission(id, tool, ChatSession.Summary(tool, input), input); });
			chat.TurnFinished += (text, error, cost) => Ui(() =>
			{
				if(me != chat) return;
				if(error) chatpanel.Error(text);
				chatpanel.TurnEnd(cost > 0 ? string.Format(CultureInfo.InvariantCulture, "— ход завершён (${0:0.####})", cost) : "— ход завершён");
				chatpanel.SetStatus("сессия " + chat.SessionId, true, false);
			});
			chat.Info += t => Ui(() => { if(me == chat) chatpanel.Note(t); });
			chat.Exited += code => Ui(() =>
			{
				if(me != chat) return;
				chatpanel.CancelPermission();
				chatpanel.Note("сеанс завершён (код " + code + ")");
				chatpanel.SetStatus("не подключено — «Новый чат» или «Продолжить»", false, false);
			});
			chatpanel.Clear();
			chatpanel.Note((resume != null ? "продолжение сессии " + resume : "новый чат") + " — " + host + ", MCP UDB через порт " + ChatRemotePort);
			try { chat.Start(ssh, host, ChatRemotePort, mcp.Port, cmd); }
			catch(Exception ex) { chat = null; return Fail("ssh не запустился: " + ex.Message); }
			chatpanel.SetStatus("подключение…", true, false);
			General.Interface.SelectDocker(chatdocker);
			return null;
		}

		private string Fail(string why)
		{
			chatpanel.Error(why);
			chatpanel.SetStatus("не подключено", false, false);
			return why;
		}

		private void ChatSend(string text)
		{
			if(chat == null || !chat.Running) { chatpanel.Error("сеанс не запущен"); return; }
			chatpanel.User(text);
			if(chatpanel.SendFirst)
			{
				string sent = QuietSend();
				if(sent != null) chatpanel.Note(sent);
			}
			try { chat.Send(text); chatpanel.SetStatus("Claude думает…", true, true); }
			catch(Exception ex) { chatpanel.Error(ex.Message); }
		}

		// Файлы на машине: MCP-конфиг (обратный порт + токен) и запуск Claude Code; рабочая копия — «доверенная»,
		// иначе Claude игнорирует разрешения из .claude/settings.json проекта
		private string RemoteSetup(string token)
		{
			string mcpjson = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
			{
				{ "mcpServers", new Dictionary<string, object>
					{
						{ "udb", new Dictionary<string, object>
							{
								{ "type", "http" }, { "url", "http://127.0.0.1:" + ChatRemotePort + "/mcp" },
								{ "headers", new Dictionary<string, object> { { "Authorization", "Bearer " + token } } }
							}
						}
					}
				}
			});
			const string prompt = "Ты в чате панели Ultimate Doom Builder владельца проекта (плагин ZT). MCP-сервер udb — его UDB на ПК: " +
			                      "открытая карта (map_info, get_elements, set_fields, render_2d…) и окна UDB. Правки файлов проекта коммить и " +
			                      "отправляй по правилам проекта — владелец заберёт их кнопкой «Получить». Отвечай по-русски.";
			return "set -e\numask 077\nmkdir -p ~/.zt\n" +
			       "cat > ~/.zt/udb-mcp.json <<'ZTEOF'\n" + mcpjson + "\nZTEOF\n" +
			       "cat > ~/.zt/chat.sh <<'ZTEOF'\n" +
			       "#!/usr/bin/env bash\n" +
			       "# ZT: чат UDB — немодифицированный Claude Code пользователя в его рабочей копии (файл пишет плагин ZTProject)\n" +
			       "cd \"${ZT_CHAT_DIR:-$HOME/projects/ZeroTolerance}\" || exit 1\n" +
			       "C=$(command -v claude || ls -1t \"$HOME\"/.local/bin/claude \"$HOME\"/.claude/remote/ccd-cli/* 2>/dev/null | head -1)\n" +
			       "[ -n \"$C\" ] || { echo '{\"type\":\"result\",\"is_error\":true,\"result\":\"на машине не найден Claude Code\"}'; exit 1; }\n" +
			       "exec \"$C\" -p --input-format stream-json --output-format stream-json --verbose --include-partial-messages \\\n" +
			       "  --permission-prompt-tool stdio --permission-mode acceptEdits --mcp-config \"$HOME/.zt/udb-mcp.json\" \\\n" +
			       "  --append-system-prompt \"" + prompt + "\" \"$@\"\n" +
			       "ZTEOF\n" +
			       "chmod 700 ~/.zt/chat.sh\n" +
			       "python3 - <<'ZTEOF'\n" +
			       "import json, os\n" +
			       "p = os.path.expanduser('~/.claude.json')\n" +
			       "d = json.load(open(p)) if os.path.exists(p) else {}\n" +
			       "w = os.path.expanduser(os.environ.get('ZT_CHAT_DIR', '~/projects/ZeroTolerance'))\n" +
			       "pr = d.setdefault('projects', {}).setdefault(w, {})\n" +
			       "if not pr.get('hasTrustDialogAccepted'):\n" +
			       "    pr['hasTrustDialogAccepted'] = True\n" +
			       "    json.dump(d, open(p, 'w'), indent=2)\n" +
			       "ZTEOF\n";
		}

		#endregion

		#region ================== Attachments and sending

		// «Выделенное»: номера выделенных элементов карты (как в TEXTMAP) — подробности Claude возьмёт через MCP
		private void AttachSelection()
		{
			if(General.Map == null) { chatpanel.Note("карта не открыта"); return; }
			CodeImp.DoomBuilder.Map.MapSet m = General.Map.Map;
			List<string> parts = new List<string>();
			Func<string, IEnumerable<int>, string> fmt = (name, ids) =>
			{
				List<int> l = ids.ToList();
				return l.Count == 0 ? null : name + " " + string.Join(", ", l.Take(60)) + (l.Count > 60 ? " … (всего " + l.Count + ")" : "");
			};
			foreach(string s in new[]
			{
				fmt("секторы", m.GetSelectedSectors(true).Select(x => x.Index)),
				fmt("линии", m.GetSelectedLinedefs(true).Select(x => x.Index)),
				fmt("вещи", m.GetSelectedThings(true).Select(x => x.Index)),
				fmt("вершины", m.GetSelectedVertices(true).Select(x => x.Index))
			}) if(s != null) parts.Add(s);
			if(parts.Count == 0) { chatpanel.Note("на карте ничего не выделено"); return; }
			chatpanel.InsertText("[в UDB выделено на " + General.Map.Options.CurrentName + " (revision " + McpTools.Revision + "): " + string.Join("; ", parts) + "]");
		}

		// Перед запросом: сохранить карту, закоммитить и отправить неотправленное (без окна комментария). null — нечего
		private string QuietSend()
		{
			string dir = ProjectDir;
			if(!ProjectMap.IsProjectDir(dir)) return null;
			if(!SaveProjectMap()) return "карта не сохранена — правки не отправлены";
			List<string> changes = Git.Lines(Git.Run(dir, "status", "--porcelain"));
			if(changes.Count == 0) return null;
			busy++;
			try
			{
				Cursor.Current = Cursors.WaitCursor;
				string branch = CurrentBranch(dir);
				Git.Run(dir, "add", "-A");
				GitResult c = Git.CommitWithMessage(dir, "UDB: " + SummaryLine(changes) + "\n\n" + Summarize(changes) +
					"\n\nОтправлено перед запросом в чат Claude.\n");
				if(!c.Ok) return "коммит не удался: " + c.Output.Split('\n')[0];
				Push(dir, branch);
				return "правки из UDB отправлены в " + branch + ": " + SummaryLine(changes);
			}
			finally
			{
				busy--;
				SyncWatch();
				Cursor.Current = Cursors.Default;
			}
		}

		#endregion

		#region ================== For MCP

		internal ChatPanel ChatPanelForMcp { get { return chatpanel; } }
		internal string AttachSelectionForMcp() { AttachSelection(); return chatpanel.InputText; }
		internal ChatSession ChatForMcp { get { return chat; } }

		#endregion
	}
}
