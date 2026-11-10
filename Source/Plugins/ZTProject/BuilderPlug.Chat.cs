#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Actions;
using CodeImp.DoomBuilder.Controls;
using CodeImp.DoomBuilder.Editing;
using CodeImp.DoomBuilder.Geometry;
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
		private List<string> chatssh;          // ssh рабочей копии и адрес машины — для ~/out
		private string chathost;
		private readonly Dictionary<string, string> outseen = new Dictionary<string, string>();   // ~/out: имя -> «время размер»

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
			chatpanel.MapImageRequested += () => AttachMapImage();
			chatpanel.ScreenshotRequested += () => AttachScreenshot();
			chatpanel.FileRequested += AttachFileDialog;
			chatpanel.OutFileClicked += name => DownloadOutFile(name, true);
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
			chat.PermissionRequest += (id, tool, input) => Ui(() =>
			{
				if(me != chat) return;
				General.Interface.SelectDocker(chatdocker);     // ждёт человека — панель выдвинуть
				// вопрос Claude — варианты ответа, а не «Разрешить / Отклонить»
				if(tool == "AskUserQuestion") chatpanel.AskQuestions(id, input);
				else chatpanel.AskPermission(id, tool, ChatSession.Summary(tool, input), input);
			});
			chat.Ready += x => Ui(() =>
			{
				// запрос мог уйти сразу после запуска («Попросить ИИ свести») — тогда Claude уже думает
				if(me == chat && chat.SessionId == null) chatpanel.SetStatus(chat.Busy ? "Claude думает…" : "готово — пишите запрос", true, chat.Busy);
			});
			chat.TurnFinished += (text, error, seconds) => Ui(() =>
			{
				if(me != chat) return;
				if(error) chatpanel.Error(text.Length > 0 ? text : "ход прерван");     // «Стоп»: error_during_execution без текста
				chatpanel.TurnEnd(seconds > 0 ? string.Format(CultureInfo.InvariantCulture, "— ход завершён за {0:0} с", seconds) : "— ход завершён");
				chatpanel.SetStatus("сессия " + chat.SessionId, true, false);
				ScanOut(true);
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
			chatpanel.ClearOutFiles();
			chatssh = ssh;
			chathost = host;
			ScanOut(false);     // что уже лежит в ~/out — не новое
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
			int n = chatpanel.ImageCount;
			string labels = string.Join(", ", chatpanel.ImageLabels);
			List<byte[]> images = chatpanel.TakeImages();
			chatpanel.User(text + (n > 0 ? "\n🖼 " + labels : ""));
			if(chatpanel.SendFirst)
			{
				string sent = QuietSend();
				if(sent != null) chatpanel.Note(sent);
			}
			try { chat.Send(text, images); chatpanel.SetStatus("Claude думает…", true, true); }
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
			                      "отправляй по правилам проекта — владелец заберёт их кнопкой «Получить». Отдельные файлы для владельца " +
			                      "(картинки, отчёты, логи — не файлы проекта) клади в папку ~/out под простыми латинскими именами: в панели " +
			                      "появится кнопка скачать и открыть. Отвечай по-русски.";
			return "set -e\numask 077\nmkdir -p ~/.zt ~/out\n" +
			       "cat > ~/.zt/udb-mcp.json <<'ZTEOF'\n" + mcpjson + "\nZTEOF\n" +
			       "cat > ~/.zt/chat.sh <<'ZTEOF'\n" +
			       "#!/usr/bin/env bash\n" +
			       "# ZT: чат UDB — немодифицированный Claude Code пользователя в его рабочей копии (файл пишет плагин ZTProject)\n" +
			       "cd \"${ZT_CHAT_DIR:-$HOME/projects/ZeroTolerance}\" || exit 1\n" +
			       "C=$(command -v claude || ls -1t \"$HOME\"/.local/bin/claude \"$HOME\"/.claude/remote/ccd-cli/* 2>/dev/null | head -1)\n" +
			       "[ -n \"$C\" ] || { echo '{\"type\":\"result\",\"is_error\":true,\"result\":\"на машине не найден Claude Code\"}'; exit 1; }\n" +
			       "exec \"$C\" -p --input-format stream-json --output-format stream-json --verbose --include-partial-messages \\\n" +
			       "  --permission-prompt-tool stdio --permission-mode acceptEdits --mcp-config \"$HOME/.zt/udb-mcp.json\" --strict-mcp-config \\\n" +
			       "  --allowedTools \"" + string.Join(",", mcptools.ReadOnlyNames().Select(n => "mcp__udb__" + n)) + "\" \\\n" +
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

		// «Картинка карты»: схема видимой области 2D-вида (в 3D — вокруг выделенного или вся карта), выделенное подсвечено
		// номерами; уходит картинкой в следующем сообщении, в поле запроса — подпись с координатами области
		internal string AttachMapImage()
		{
			if(General.Map == null) { chatpanel.Note("карта не открыта"); return null; }
			CodeImp.DoomBuilder.Map.MapSet m = General.Map.Map;
			Dictionary<string, List<int>> hl = new Dictionary<string, List<int>>();
			Action<string, IEnumerable<int>> add = (k, ids) => { List<int> l = ids.ToList(); if(l.Count > 0) hl[k] = l; };
			add("sector", m.GetSelectedSectors(true).Select(x => x.Index));
			add("linedef", m.GetSelectedLinedefs(true).Select(x => x.Index));
			add("thing", m.GetSelectedThings(true).Select(x => x.Index));
			add("vertex", m.GetSelectedVertices(true).Select(x => x.Index));
			double[] rect = null;
			if(General.Editing.Mode is ClassicMode && General.Interface.Display != null)
			{
				System.Drawing.Size s = General.Interface.Display.ClientSize;
				Vector2D lt = General.Map.Renderer2D.DisplayToMap(new Vector2D(0, 0));
				Vector2D rb = General.Map.Renderer2D.DisplayToMap(new Vector2D(s.Width, s.Height));
				rect = new[] { lt.x, rb.y, rb.x, lt.y };
			}
			byte[] png = MapRender.Render(hl, rect, 512, 1400, true, true);
			string name = General.Map.Options.CurrentName;
			chatpanel.AddImage("схема " + name, png);
			string caption = "[картинка: схема карты " + name + " из UDB" +
				(rect != null ? string.Format(CultureInfo.InvariantCulture, ", область x {0:0}…{1:0}, y {2:0}…{3:0}", rect[0], rect[2], rect[1], rect[3]) : "") +
				(hl.Count > 0 ? ", выделенное подсвечено номерами" : "") + "]";
			chatpanel.InsertText(caption);
			return caption;
		}

		// «Скриншот»: главное окно UDB как есть
		internal string AttachScreenshot()
		{
			byte[] png = UiAutomation.Capture("");
			chatpanel.AddImage("скриншот UDB", png);
			return "скриншот " + png.Length / 1024 + " КБ";
		}

		private void AttachFileDialog()
		{
			using(OpenFileDialog d = new OpenFileDialog { Title = "Файл для Claude", Filter = "Все файлы|*.*" })
			{
				if(ProjectMap.IsProjectDir(ProjectDir)) d.InitialDirectory = ProjectDir;
				if(d.ShowDialog(General.MainWindow) == DialogResult.OK) chatpanel.Note(AttachFile(d.FileName));
			}
		}

		// Файл проекта — путем в поле запроса (Claude читает его в своей рабочей копии); картинка вне проекта — вложением
		internal string AttachFile(string path)
		{
			string full = Path.GetFullPath(path);
			string dir = ProjectDir;
			if(ProjectMap.IsProjectDir(dir))
			{
				string root = Path.GetFullPath(dir).TrimEnd('\\', '/') + "\\";
				if(full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
				{
					string rel = full.Substring(root.Length).Replace('\\', '/');
					chatpanel.InsertText("[файл проекта: " + rel + "]");
					return "в запрос добавлен путь " + rel + (chatpanel.SendFirst ? "" : " — без «Сначала отправить мои правки» Claude увидит версию с сервера");
				}
			}
			string ext = Path.GetExtension(full).ToLowerInvariant();
			if(ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif")
			{
				chatpanel.AddImage(Path.GetFileName(full), ImageAsPng(full));
				return "картинка " + Path.GetFileName(full) + " добавлена к сообщению";
			}
			return "файл вне проекта — Claude на машине его не увидит; положите его в проект или пришлите картинкой";
		}

		// Картинка из файла — PNG не больше 1568 px по длинной стороне (больше API всё равно уменьшает)
		private static byte[] ImageAsPng(string path)
		{
			using(System.Drawing.Image src = System.Drawing.Image.FromFile(path))
			{
				double k = Math.Min(1.0, 1568.0 / Math.Max(src.Width, src.Height));
				int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
				using(System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(w, h))
				using(MemoryStream ms = new MemoryStream())
				{
					using(System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
					{
						g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
						g.DrawImage(src, 0, 0, w, h);
					}
					bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
					return ms.ToArray();
				}
			}
		}

		#endregion

		#region ================== AI merge

		// «Попросить ИИ свести» доступно, если сервер рабочей копии — машина по SSH (там Claude пользователя)
		private static bool CanAskAiMerge(string dir)
		{
			string url = Git.Run(dir, "config", "--get", "remote.origin.url").StdOut.Trim();
			int colon = url.IndexOf(':');
			return colon > 1 && !url.Contains("://");
		}

		// Конфликт при «Получить» → свести силами Claude (ZT/workbench-plan.md §2.3, п. 4): слияние отменяется, локальный коммит
		// уходит на сервер во временную ветку <ветка>-merge-<время> (у дизайнера — design-merge-…, её пропускает хук прав),
		// Claude сливает её в свою рабочую копию и отправляет ветку; человек потом жмёт «Получить» — это перемотка вперёд
		private void AskAiMerge(string dir, string branch, List<string> conflicts)
		{
			string local = Git.Run(dir, "rev-parse", "--short", "HEAD").StdOut.Trim();
			Git.Run(dir, "merge", "--abort");
			string temp = branch + "-merge-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			GitResult p = Git.Run(dir, "push", "origin", "HEAD:refs/heads/" + temp);
			if(!p.Ok)
			{
				Dialogs.ShowText(General.Interface, "ZT: временная ветка не отправлена",
					"Слияние отменено, ваши правки на месте (локальный коммит " + local + ").\r\n\r\n" + p.Output);
				return;
			}
			string msg = "Сведи мои правки из UDB с сервером. Ветка " + temp + " — мои правки (локальный коммит " + local + "); при «Получить» " +
			             "она конфликтует с " + branch + " с сервера в файлах:\n" + string.Join("\n", conflicts.Select(c => "  - " + c)) + "\n\n" +
			             "Что сделать: в своей рабочей копии обнови " + branch + " с сервера (git fetch), слей в неё origin/" + temp + ", разреши " +
			             "конфликты по смыслу, сохранив правки обеих сторон (TEXTMAP карт сливает драйвер merge=udmf); если правки " +
			             "противоречат друг другу — спроси меня. Прогони проверки по правилам проекта. Закоммить слияние и отправь " + branch +
			             " на сервер — на этот коммит и push я согласен. Потом удали временную ветку: git push origin --delete " + temp +
			             ". Результат я заберу кнопкой «Получить».";
			General.Interface.DisplayStatus(StatusType.Info, "ZT: правки во временной ветке " + temp + " — Claude сводит их в чате.");

			if(chat == null || !chat.Running)
			{
				string why = StartChat(null);
				if(why != null)
				{
					Dialogs.ShowText(General.Interface, "ZT: чат не запустился",
						"Ваши правки отправлены во временную ветку " + temp + ", слияние отменено. Чат не запустился (" + why + ") — " +
						"попросите Claude свести их сами, например так:\r\n\r\n" + msg);
					return;
				}
			}
			General.Interface.SelectDocker(chatdocker);
			if(chat.Busy)
			{
				chatpanel.InsertText(msg);
				chatpanel.Note("Claude сейчас занят — запрос на слияние в поле ввода, отправьте его, когда ход закончится");
				return;
			}
			chatpanel.User(msg);
			try { chat.Send(msg, null); chatpanel.SetStatus("Claude сводит правки…", true, true); }
			catch(Exception ex) { chatpanel.Error(ex.Message); chatpanel.InsertText(msg); }
		}

		#endregion

		#region ================== Files from Claude (~/out)

		// Сверка ~/out на машине (в фоне): новые и изменившиеся файлы — карточки в панели. announce=false — только запомнить
		private void ScanOut(bool announce)
		{
			List<string> ssh = chatssh;
			string host = chathost;
			if(ssh == null || host == null) return;
			new Thread(() =>
			{
				string listing;
				try { listing = Exec(ssh[0], ssh.Skip(1).Concat(new[] { "-n", host, "mkdir -p ~/out && cd ~/out && find . -type f ! -name '*.part' -printf '%T@ %s %P\\n'" }), null, 60000); }
				catch(Exception ex) { if(announce) Ui(() => chatpanel.Note("~/out не прочитана: " + ex.Message.Split('\n')[0])); return; }
				List<KeyValuePair<string, long>> fresh = new List<KeyValuePair<string, long>>();
				lock(outseen)
				{
					if(!announce) outseen.Clear();
					foreach(string line in listing.Split('\n'))
					{
						string[] f = line.Trim().Split(new[] { ' ' }, 3);
						if(f.Length < 3 || f[2].Length == 0) continue;
						string stamp = f[0] + " " + f[1], old;
						if(outseen.TryGetValue(f[2], out old) && old == stamp) continue;
						outseen[f[2]] = stamp;
						long size;
						if(announce && long.TryParse(f[1], out size)) fresh.Add(new KeyValuePair<string, long>(f[2], size));
					}
				}
				if(fresh.Count > 0) Ui(() => { foreach(KeyValuePair<string, long> kv in fresh) chatpanel.AddOutFile(kv.Key, kv.Value); });
			}) { IsBackground = true, Name = "ZT chat out scan" }.Start();
		}

		internal static string OutDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZTWorkbench", "out"); } }

		// Скачать файл из ~/out в %LOCALAPPDATA%\ZTWorkbench\out и открыть программой по умолчанию (в фоне)
		internal void DownloadOutFile(string name, bool open)
		{
			List<string> ssh = chatssh;
			string host = chathost;
			if(ssh == null || host == null) { chatpanel.Error("сеанс чата не запускался — машина неизвестна"); return; }
			if(name.Split('/').Any(x => x == ".." || x.Length == 0)) { chatpanel.Error("странное имя файла: " + name); return; }
			string local = Path.Combine(OutDir, name.Replace('/', '\\'));
			chatpanel.Note("качаю " + name + "…");
			new Thread(() =>
			{
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(local));
					string part = local + ".part";
					if(File.Exists(part)) File.Delete(part);
					string scp = ssh[0] == "ssh" ? "scp" : Path.Combine(Path.GetDirectoryName(ssh[0]) ?? "", "scp" + Path.GetExtension(ssh[0]));
					Exec(scp, ssh.Skip(1).Concat(new[] { "-q", host + ":out/" + name, part }), null, 3600000);
					if(File.Exists(local)) File.Delete(local);
					File.Move(part, local);
					Ui(() =>
					{
						chatpanel.Note("скачано: " + local);
						if(open)
						{
							try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(local) { UseShellExecute = true }); }
							catch(Exception ex) { chatpanel.Error("не открылся: " + ex.Message); }
						}
					});
				}
				catch(Exception ex) { Ui(() => chatpanel.Error("не скачан " + name + ": " + ex.Message.Split('\n')[0])); }
			}) { IsBackground = true, Name = "ZT chat out download" }.Start();
		}

		#endregion

		#region ================== Sending

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
		internal void ScanOutForMcp() { ScanOut(true); }
		internal ChatSession ChatForMcp { get { return chat; } }

		#endregion
	}
}
