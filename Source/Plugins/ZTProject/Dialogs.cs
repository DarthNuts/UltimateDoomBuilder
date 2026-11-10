#region ================== Namespaces

using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Простые окна плагина (без дизайнера форм)
	internal static class Dialogs
	{
		private static Form NewDialog(string title, int w, int h)
		{
			return new Form
			{
				Text = title,
				FormBorderStyle = FormBorderStyle.FixedDialog,
				StartPosition = FormStartPosition.CenterParent,
				ClientSize = new Size(w, h),
				MinimizeBox = false,
				MaximizeBox = false,
				ShowInTaskbar = false
			};
		}

		private static FlowLayoutPanel Buttons(Form f, string oktext)
		{
			FlowLayoutPanel p = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6) };
			Button cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 90 };
			Button ok = new Button { Text = oktext, DialogResult = DialogResult.OK, Width = 110 };
			p.Controls.Add(cancel);
			p.Controls.Add(ok);
			f.AcceptButton = ok;
			f.CancelButton = cancel;
			return p;
		}

		// Выбор карты проекта; null — отмена
		public static string ChooseMap(IWin32Window owner, IList<string> maps, string preselect)
		{
			using(Form f = NewDialog("ZT: карта проекта", 280, 360))
			{
				ListBox lb = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
				lb.Items.AddRange(maps.Cast<object>().ToArray());
				int i = preselect != null ? maps.IndexOf(preselect) : -1;
				lb.SelectedIndex = i >= 0 ? i : (maps.Count > 0 ? 0 : -1);
				lb.DoubleClick += (s, e) => { if(lb.SelectedItem != null) f.DialogResult = DialogResult.OK; };
				f.Controls.Add(lb);
				f.Controls.Add(Buttons(f, "Открыть"));
				return f.ShowDialog(owner) == DialogResult.OK ? lb.SelectedItem as string : null;
			}
		}

		// Необязательный комментарий к отправке; null — отмена, "" — без комментария
		public static string AskComment(IWin32Window owner, string summary)
		{
			using(Form f = NewDialog("ZT: отправить свежие правки", 520, 250))
			{
				Label what = new Label { Dock = DockStyle.Top, Height = 110, Text = summary, Padding = new Padding(8), AutoEllipsis = true };
				Label hint = new Label { Dock = DockStyle.Top, Height = 22, Text = "Комментарий (необязательно):", Padding = new Padding(8, 4, 8, 0) };
				TextBox tb = new TextBox { Dock = DockStyle.Top };
				f.Controls.Add(tb);
				f.Controls.Add(hint);
				f.Controls.Add(what);
				f.Controls.Add(Buttons(f, "Отправить"));
				f.Shown += (s, e) => tb.Focus();
				return f.ShowDialog(owner) == DialogResult.OK ? tb.Text.Trim() : null;
			}
		}

		// Подробности: вывод git и т. п.
		public static void ShowText(IWin32Window owner, string title, string text)
		{
			using(Form f = NewDialog(title, 720, 420))
			{
				f.FormBorderStyle = FormBorderStyle.Sizable;
				TextBox tb = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9f), Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n") };
				FlowLayoutPanel p = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6) };
				Button close = new Button { Text = "Закрыть", DialogResult = DialogResult.OK, Width = 90 };
				p.Controls.Add(close);
				f.AcceptButton = close;
				f.CancelButton = close;
				f.Controls.Add(tb);
				f.Controls.Add(p);
				f.ShowDialog(owner);
			}
		}
	}
}
