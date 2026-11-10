#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

#endregion

namespace CodeImp.DoomBuilder.ZTProject.Chat
{
	// Панель чата с Claude (докер UDB, ZT/workbench-plan.md §4.2): ответ потоком, шаги ИИ строкой, запросы разрешений —
	// кнопками, «Стоп» прерывает ход. Всё обновление — в потоке интерфейса.
	internal sealed class ChatPanel : UserControl
	{
		#region ================== Variables

		private readonly RichTextBox log;
		private readonly TextBox input;
		private readonly Button send, stop, newchat, resume, attach, getbtn, mapimage, screenshot, projectfile;
		private readonly CheckBox sendfirst;
		private readonly FlowLayoutPanel atts;                               // ожидающие отправки картинки (клик — убрать)
		private readonly List<KeyValuePair<string, byte[]>> images = new List<KeyValuePair<string, byte[]>>();
		private readonly FlowLayoutPanel files;                              // файлы от Claude из ~/out (клик — скачать и открыть)
		private readonly Label status;
		private readonly Panel permbar;
		private readonly Label permtext;
		private string permid;
		private Dictionary<string, object> perminput;
		private readonly Panel askbar;                                       // вопросы Claude (AskUserQuestion): варианты кнопками
		private readonly FlowLayoutPanel askbody;
		private string askid;
		private Dictionary<string, object> askinput;
		private readonly List<Func<KeyValuePair<string, object>?>> askcollect = new List<Func<KeyValuePair<string, object>?>>();
		private readonly StringBuilder transcript = new StringBuilder();     // для MCP chat_log

		public event Action<string> SendRequested;
		public event Action StopRequested, NewChatRequested, ResumeRequested, AttachRequested, GetRequested;
		public event Action MapImageRequested, ScreenshotRequested, FileRequested;
		public event Action<string> OutFileClicked;                          // имя файла в ~/out
		public event Action<string, bool, Dictionary<string, object>> PermissionAnswered;

		public string Transcript { get { return transcript.ToString(); } }
		public bool PermissionPending { get { return permid != null; } }
		// Перед запросом отправить неотправленные правки из UDB (Claude видит ровно текущее состояние)
		public bool SendFirst { get { return sendfirst.Checked; } }
		public string InputText { get { return input.Text; } }

		#endregion

		#region ================== Constructor

		public ChatPanel()
		{
			Dock = DockStyle.Fill;
			Font = new Font("Segoe UI", 9f);

			FlowLayoutPanel top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(2), WrapContents = false };
			newchat = new Button { Text = "Новый чат", Width = 80 };
			resume = new Button { Text = "Продолжить", Width = 85 };
			stop = new Button { Text = "Стоп", Width = 50, Enabled = false };
			getbtn = new Button { Text = "Получить", Width = 75 };
			top.Controls.AddRange(new Control[] { newchat, resume, stop, getbtn });
			status = new Label { Dock = DockStyle.Top, Height = 18, Padding = new Padding(4, 2, 4, 0), ForeColor = SystemColors.GrayText, Text = "не подключено" };

			log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, DetectUrls = true, HideSelection = false };

			permbar = new Panel { Dock = DockStyle.Bottom, Height = 64, Visible = false, BackColor = Color.FromArgb(255, 246, 214), Padding = new Padding(4) };
			permtext = new Label { Dock = DockStyle.Fill, AutoEllipsis = true };
			FlowLayoutPanel pbtn = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 170, FlowDirection = FlowDirection.TopDown };
			Button allow = new Button { Text = "Разрешить", Width = 160 }, deny = new Button { Text = "Отклонить", Width = 160 };
			allow.Click += (s, e) => AnswerPermission(true);
			deny.Click += (s, e) => AnswerPermission(false);
			pbtn.Controls.AddRange(new Control[] { allow, deny });
			permbar.Controls.Add(permtext);
			permbar.Controls.Add(pbtn);

			askbar = new Panel { Dock = DockStyle.Bottom, Height = 240, Visible = false, BackColor = Color.FromArgb(228, 242, 228), Padding = new Padding(4) };
			askbody = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
			FlowLayoutPanel abtn = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, FlowDirection = FlowDirection.RightToLeft };
			Button answerq = new Button { Text = "Ответить", AutoSize = true }, skipq = new Button { Text = "Не отвечать", AutoSize = true };
			answerq.Click += (s, e) => AnswerQuestions(null);
			skipq.Click += (s, e) => SkipQuestions();
			abtn.Controls.AddRange(new Control[] { answerq, skipq });
			askbar.Controls.Add(askbody);
			askbar.Controls.Add(abtn);

			// Вложения: панели переносят кнопки на новую строку в узком докере
			FlowLayoutPanel opts = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(2), WrapContents = true };
			attach = new Button { Text = "Выделенное", AutoSize = true };
			mapimage = new Button { Text = "Картинка карты", AutoSize = true };
			screenshot = new Button { Text = "Скриншот", AutoSize = true };
			projectfile = new Button { Text = "Файл…", AutoSize = true };
			sendfirst = new CheckBox { Text = "Сначала отправить мои правки", Checked = true, AutoSize = true, Padding = new Padding(4, 4, 0, 0) };
			opts.Controls.AddRange(new Control[] { attach, mapimage, screenshot, projectfile, sendfirst });
			ToolTip tips = new ToolTip();
			tips.SetToolTip(attach, "Номера выделенных элементов карты — в поле запроса");
			tips.SetToolTip(mapimage, "Схема видимой области карты (выделенное подсвечено) — картинкой в сообщение");
			tips.SetToolTip(screenshot, "Картинка окна UDB — в сообщение");
			tips.SetToolTip(projectfile, "Путь файла проекта — в поле запроса (Claude прочитает его в своей рабочей копии)");
			atts = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(2), WrapContents = true, Visible = false };
			files = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(2), WrapContents = true, Visible = false, BackColor = Color.FromArgb(232, 242, 255) };
			Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(2) };
			input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
			send = new Button { Dock = DockStyle.Right, Text = "Отправить\n(Ctrl+Enter)", Width = 90, Enabled = false };
			bottom.Controls.Add(input);
			bottom.Controls.Add(send);

			Controls.Add(log);
			Controls.Add(files);
			Controls.Add(askbar);
			Controls.Add(permbar);
			Controls.Add(atts);
			Controls.Add(bottom);
			Controls.Add(opts);
			Controls.Add(status);
			Controls.Add(top);

			send.Click += (s, e) => DoSend();
			input.KeyDown += (s, e) => { if(e.Control && e.KeyCode == Keys.Enter) { DoSend(); e.SuppressKeyPress = true; } };
			stop.Click += (s, e) => { if(StopRequested != null) StopRequested(); };
			newchat.Click += (s, e) => { if(NewChatRequested != null) NewChatRequested(); };
			resume.Click += (s, e) => { if(ResumeRequested != null) ResumeRequested(); };
			attach.Click += (s, e) => { if(AttachRequested != null) AttachRequested(); };
			mapimage.Click += (s, e) => { if(MapImageRequested != null) MapImageRequested(); };
			screenshot.Click += (s, e) => { if(ScreenshotRequested != null) ScreenshotRequested(); };
			projectfile.Click += (s, e) => { if(FileRequested != null) FileRequested(); };
			getbtn.Click += (s, e) => { if(GetRequested != null) GetRequested(); };
		}

		#endregion

		#region ================== Output

		public void SetStatus(string text, bool connected, bool busy)
		{
			status.Text = text;
			send.Enabled = connected && !busy;
			stop.Enabled = connected && busy;
		}

		private void Append(string text, Color color, FontStyle style)
		{
			log.SelectionStart = log.TextLength;
			log.SelectionLength = 0;
			log.SelectionColor = color;
			log.SelectionFont = new Font(log.Font, style);
			log.AppendText(text);
			log.SelectionColor = log.ForeColor;
			log.ScrollToCaret();
			transcript.Append(text);
		}

		public void User(string text) { Append("\n▸ " + text.Trim() + "\n\n", Color.FromArgb(30, 90, 160), FontStyle.Bold); }
		public void Text(string text) { Append(text, SystemColors.WindowText, FontStyle.Regular); }
		public void Tool(string name, string what) { Append("\n  ⚙ " + name + (what.Length > 0 ? ": " + what : "") + "\n", SystemColors.GrayText, FontStyle.Regular); }
		public void Note(string text) { Append("\n· " + text + "\n", Color.FromArgb(120, 120, 120), FontStyle.Italic); }
		public void Error(string text) { Append("\n✖ " + text + "\n", Color.FromArgb(180, 40, 40), FontStyle.Regular); }
		public void TurnEnd(string footer) { Append("\n" + footer + "\n", Color.FromArgb(140, 140, 140), FontStyle.Italic); }

		public void Clear() { log.Clear(); transcript.Clear(); }

		#endregion

		#region ================== Permissions

		public void AskPermission(string id, string tool, string what, Dictionary<string, object> toolinput)
		{
			permid = id;
			perminput = toolinput;
			permtext.Text = "Claude просит: " + tool + (what.Length > 0 ? "\n" + what : "");
			permbar.Visible = true;
			Note("запрос разрешения: " + tool + (what.Length > 0 ? " — " + what : ""));
		}

		public void AnswerPermission(bool allow)
		{
			if(permid == null) return;
			string id = permid;
			permid = null;
			permbar.Visible = false;
			Note(allow ? "разрешено" : "отклонено");
			if(PermissionAnswered != null) PermissionAnswered(id, allow, perminput);
		}

		public void CancelPermission()
		{
			permid = null;
			permbar.Visible = false;
			askid = null;
			askbar.Visible = false;
		}

		#endregion

		#region ================== Questions (AskUserQuestion)

		public bool QuestionPending { get { return askid != null; } }
		public Dictionary<string, object> PendingQuestions { get { return askinput; } }

		private static IEnumerable<Dictionary<string, object>> Questions(Dictionary<string, object> input)
		{
			object q;
			if(input == null || !input.TryGetValue("questions", out q) || !(q is System.Collections.IEnumerable)) yield break;
			foreach(object o in (System.Collections.IEnumerable)q)
				if(o is Dictionary<string, object>) yield return (Dictionary<string, object>)o;
		}

		private static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as string ?? "" : ""; }

		// Вопросы Claude: по каждому — варианты (радио или галочки при multiSelect) и «Другое» со своим текстом
		public void AskQuestions(string id, Dictionary<string, object> input)
		{
			askid = id;
			askinput = input;
			askcollect.Clear();
			askbody.SuspendLayout();
			foreach(Control c in askbody.Controls) c.Dispose();
			askbody.Controls.Clear();
			int w = Math.Max(200, askbody.ClientSize.Width - 24);
			ToolTip tips = new ToolTip();
			List<string> lognote = new List<string>();
			foreach(Dictionary<string, object> q in Questions(input))
			{
				string question = Str(q, "question"), header = Str(q, "header");
				object ms;
				bool multi = q.TryGetValue("multiSelect", out ms) && ms is bool && (bool)ms;
				lognote.Add(question);
				askbody.Controls.Add(new Label
				{
					Text = (header.Length > 0 ? header + ": " : "") + question + (multi ? " (можно несколько)" : ""),
					AutoSize = true, MaximumSize = new Size(w, 0), Font = new Font(Font, FontStyle.Bold), Margin = new Padding(2, 8, 2, 2)
				});
				// Радиокнопки одного вопроса — в своей панели, иначе они переключали бы друг друга между вопросами
				FlowLayoutPanel group = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(8, 0, 2, 0) };
				List<ButtonBase> boxes = new List<ButtonBase>();
				object opts;
				if(q.TryGetValue("options", out opts) && opts is System.Collections.IEnumerable)
					foreach(Dictionary<string, object> o in ((System.Collections.IEnumerable)opts).OfType<Dictionary<string, object>>())
					{
						ButtonBase b = multi ? (ButtonBase)new CheckBox { Text = Str(o, "label"), AutoSize = true } : new RadioButton { Text = Str(o, "label"), AutoSize = true };
						group.Controls.Add(b);
						boxes.Add(b);
						string desc = Str(o, "description");
						if(desc.Length > 0)
						{
							tips.SetToolTip(b, desc);
							group.Controls.Add(new Label { Text = desc, AutoSize = true, MaximumSize = new Size(w - 30, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(20, 0, 2, 4) });
						}
					}
				ButtonBase other = multi ? (ButtonBase)new CheckBox { Text = "Другое:", AutoSize = true } : new RadioButton { Text = "Другое:", AutoSize = true };
				TextBox othertext = new TextBox { Width = Math.Max(120, w - 40), Margin = new Padding(20, 0, 2, 4) };
				othertext.TextChanged += (s, e) => { if(othertext.Text.Length > 0) SetChecked(other, true); };
				group.Controls.Add(other);
				group.Controls.Add(othertext);
				askbody.Controls.Add(group);

				askcollect.Add(() =>
				{
					List<string> picked = boxes.Where(IsChecked).Select(b => b.Text).ToList();
					if(IsChecked(other) && othertext.Text.Trim().Length > 0) picked.Add(othertext.Text.Trim());
					if(picked.Count == 0) return null;
					return new KeyValuePair<string, object>(question, string.Join(", ", picked));
				});
			}
			askbody.ResumeLayout();
			askbar.Visible = true;
			Note("Claude спрашивает: " + string.Join(" / ", lognote));
		}

		private static bool IsChecked(ButtonBase b) { return b is CheckBox ? ((CheckBox)b).Checked : ((RadioButton)b).Checked; }
		private static void SetChecked(ButtonBase b, bool v) { if(b is CheckBox) ((CheckBox)b).Checked = v; else ((RadioButton)b).Checked = v; }

		// Ответ: given — готовые ответы (MCP: вопрос -> текст), иначе — выбор в панели
		public string AnswerQuestions(Dictionary<string, object> given)
		{
			if(askid == null) return "вопроса нет";
			Dictionary<string, object> answers = new Dictionary<string, object>();
			if(given != null) foreach(KeyValuePair<string, object> kv in given) answers[kv.Key] = kv.Value;
			else foreach(Func<KeyValuePair<string, object>?> c in askcollect) { KeyValuePair<string, object>? a = c(); if(a.HasValue) answers[a.Value.Key] = a.Value.Value; }
			if(answers.Count == 0) { Note("выберите вариант ответа или «Не отвечать»"); return "ответ не выбран"; }
			object qs;
			askinput.TryGetValue("questions", out qs);
			Dictionary<string, object> updated = new Dictionary<string, object> { { "questions", qs }, { "answers", answers } };
			string id = askid;
			askid = null;
			askbar.Visible = false;
			Note("ответ: " + string.Join("; ", answers.Select(kv => kv.Key + " → " + kv.Value)));
			if(PermissionAnswered != null) PermissionAnswered(id, true, updated);
			return null;
		}

		public void SkipQuestions()
		{
			if(askid == null) return;
			string id = askid;
			askid = null;
			askbar.Visible = false;
			Note("без ответа");
			if(PermissionAnswered != null) PermissionAnswered(id, false, null);
		}

		#endregion

		#region ================== Input

		private void DoSend()
		{
			string text = input.Text.Trim();
			if(text.Length == 0 && images.Count > 0) text = "Посмотри на картинку.";
			if(text.Length == 0 || !send.Enabled) return;
			input.Clear();
			if(SendRequested != null) SendRequested(text);
		}

		// Картинка к следующему сообщению: кнопка в полосе вложений, клик по ней — убрать
		public void AddImage(string label, byte[] png)
		{
			KeyValuePair<string, byte[]> item = new KeyValuePair<string, byte[]>(label, png);
			images.Add(item);
			Button b = new Button { Text = "🖼 " + label + " ✕", AutoSize = true, FlatStyle = FlatStyle.Flat };
			b.Click += (s, e) => { images.Remove(item); atts.Controls.Remove(b); b.Dispose(); atts.Visible = images.Count > 0; };
			atts.Controls.Add(b);
			atts.Visible = true;
		}

		public int ImageCount { get { return images.Count; } }
		public IEnumerable<string> ImageLabels { get { foreach(KeyValuePair<string, byte[]> kv in images) yield return kv.Key; } }

		// Забрать картинки в отправляемое сообщение
		public List<byte[]> TakeImages()
		{
			List<byte[]> result = new List<byte[]>();
			foreach(KeyValuePair<string, byte[]> kv in images) result.Add(kv.Value);
			images.Clear();
			foreach(Control c in atts.Controls) c.Dispose();
			atts.Controls.Clear();
			atts.Visible = false;
			return result;
		}

		// Карточка файла из ~/out: имя и размер; повторное появление (файл перезаписан) — та же карточка вперёд
		public void AddOutFile(string name, long size)
		{
			string text = "📄 " + name + " (" + FormatSize(size) + ")";
			foreach(Control c in files.Controls)
				if(c.Tag as string == name) { c.Text = text; files.Controls.SetChildIndex(c, 0); files.Visible = true; return; }
			Button b = new Button { Text = text, Tag = name, AutoSize = true, FlatStyle = FlatStyle.Flat };
			b.Click += (s, e) => { if(OutFileClicked != null) OutFileClicked(name); };
			files.Controls.Add(b);
			files.Controls.SetChildIndex(b, 0);
			files.Visible = true;
			Note("Claude положил файл в ~/out: " + name + " (" + FormatSize(size) + ") — кнопка над полем запроса: скачать и открыть");
		}

		public IEnumerable<string> OutFiles { get { foreach(Control c in files.Controls) yield return c.Tag as string; } }

		public void ClearOutFiles()
		{
			foreach(Control c in files.Controls) c.Dispose();
			files.Controls.Clear();
			files.Visible = false;
		}

		private static string FormatSize(long n)
		{
			if(n < 1024) return n + " Б";
			if(n < 1024 * 1024) return (n / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " КБ";
			return (n / 1048576.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " МБ";
		}

		// Вставить текст в поле запроса (вложение «Выделенное»)
		public void InsertText(string text)
		{
			input.SelectedText = (input.TextLength > 0 && !input.Text.EndsWith(" ") ? " " : "") + text + " ";
			input.Focus();
		}

		// Отправить текст как с клавиатуры (MCP chat_send)
		// Пустой text — как кнопка «Отправить»: то, что в поле запроса (с подписями вложений)
		public bool SendText(string text)
		{
			if(!send.Enabled) return false;
			if(string.IsNullOrEmpty(text)) { DoSend(); return true; }
			if(SendRequested != null) SendRequested(text);
			return true;
		}

		#endregion
	}
}
