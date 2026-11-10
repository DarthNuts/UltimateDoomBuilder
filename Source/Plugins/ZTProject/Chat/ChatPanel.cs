#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Drawing;
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
		private readonly Button send, stop, newchat, resume, attach, getbtn;
		private readonly CheckBox sendfirst;
		private readonly Label status;
		private readonly Panel permbar;
		private readonly Label permtext;
		private string permid;
		private Dictionary<string, object> perminput;
		private readonly StringBuilder transcript = new StringBuilder();     // для MCP chat_log

		public event Action<string> SendRequested;
		public event Action StopRequested, NewChatRequested, ResumeRequested, AttachRequested, GetRequested;
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

			FlowLayoutPanel opts = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(2), WrapContents = false };
			attach = new Button { Text = "Выделенное", Width = 85 };
			sendfirst = new CheckBox { Text = "Сначала отправить мои правки", Checked = true, AutoSize = true, Padding = new Padding(4, 4, 0, 0) };
			opts.Controls.AddRange(new Control[] { attach, sendfirst });
			Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(2) };
			input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
			send = new Button { Dock = DockStyle.Right, Text = "Отправить\n(Ctrl+Enter)", Width = 90, Enabled = false };
			bottom.Controls.Add(input);
			bottom.Controls.Add(send);

			Controls.Add(log);
			Controls.Add(permbar);
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

		public void CancelPermission() { permid = null; permbar.Visible = false; }

		#endregion

		#region ================== Input

		private void DoSend()
		{
			string text = input.Text.Trim();
			if(text.Length == 0 || !send.Enabled) return;
			input.Clear();
			if(SendRequested != null) SendRequested(text);
		}

		// Вставить текст в поле запроса (вложение «Выделенное»)
		public void InsertText(string text)
		{
			input.SelectedText = (input.TextLength > 0 && !input.Text.EndsWith(" ") ? " " : "") + text + " ";
			input.Focus();
		}

		// Отправить текст как с клавиатуры (MCP chat_send)
		public bool SendText(string text)
		{
			if(!send.Enabled) return false;
			if(SendRequested != null) SendRequested(text);
			return true;
		}

		#endregion
	}
}
