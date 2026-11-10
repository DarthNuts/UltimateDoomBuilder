#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

#endregion

namespace CodeImp.DoomBuilder.ZTProject.Mcp
{
	// Окна UDB для агента: формы WinForms (окна плагинов и UDB) и стандартные окна Windows (MessageBox). Прочитать
	// содержимое, нажать кнопку, выбрать строки списка, ввести текст, снять картинку окна. Всё — в потоке интерфейса.
	internal static class UiAutomation
	{
		#region ================== Win32

		private delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
		[DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lparam);
		[DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lparam);
		[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
		[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
		[DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
		[DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
		[DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

		[StructLayout(LayoutKind.Sequential)]
		private struct RECT { public int Left, Top, Right, Bottom; }

		private const uint BM_CLICK = 0x00F5;
		private const uint PW_RENDERFULLCONTENT = 2;

		private static string ClassOf(IntPtr h) { StringBuilder sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
		private static string TextOf(IntPtr h) { StringBuilder sb = new StringBuilder(2048); GetWindowText(h, sb, 2048); return sb.ToString(); }

		// Стандартные диалоги Windows (класс #32770) этого процесса: MessageBox и т. п.
		private static List<IntPtr> NativeDialogs()
		{
			List<IntPtr> list = new List<IntPtr>();
			uint me = (uint)Process.GetCurrentProcess().Id;
			EnumWindows((h, l) =>
			{
				uint pid;
				GetWindowThreadProcessId(h, out pid);
				if(pid == me && IsWindowVisible(h) && ClassOf(h) == "#32770") list.Add(h);
				return true;
			}, IntPtr.Zero);
			return list;
		}

		private static List<IntPtr> Children(IntPtr parent)
		{
			List<IntPtr> list = new List<IntPtr>();
			EnumChildWindows(parent, (h, l) => { list.Add(h); return true; }, IntPtr.Zero);
			return list;
		}

		#endregion

		#region ================== Reading

		private static string Clean(string s) { return (s ?? "").Replace("&", ""); }
		private static string Cut(string s, int n) { s = s ?? ""; return s.Length > n ? s.Substring(0, n) + "…" : s; }

		// Все видимые окна: формы и стандартные диалоги. Главное окно — только заголовок и строка состояния.
		public static List<object> Windows()
		{
			List<object> list = new List<object>();
			foreach(Form f in Application.OpenForms.Cast<Form>().ToList())
			{
				if(!f.Visible) continue;
				Dictionary<string, object> w = new Dictionary<string, object>
				{
					{ "kind", "form" }, { "title", f.Text }, { "modal", f.Modal }, { "active", Form.ActiveForm == f }
				};
				if(f == General.MainWindow) w["main"] = true;
				else w["controls"] = Describe(f);
				list.Add(w);
			}
			foreach(IntPtr h in NativeDialogs())
			{
				List<object> controls = new List<object>();
				foreach(IntPtr c in Children(h))
				{
					if(!IsWindowVisible(c)) continue;
					string cls = ClassOf(c), text = TextOf(c);
					if(cls == "Button") controls.Add(new Dictionary<string, object> { { "type", "button" }, { "text", Clean(text) } });
					else if(cls == "Static" && text.Length > 0) controls.Add(new Dictionary<string, object> { { "type", "label" }, { "text", Cut(text, 2000) } });
				}
				list.Add(new Dictionary<string, object> { { "kind", "dialog" }, { "title", TextOf(h) }, { "modal", true }, { "controls", controls } });
			}
			return list;
		}

		private static List<object> Describe(Control root)
		{
			List<object> list = new List<object>();
			int lists = 0, boxes = 0;
			Walk(root, list, ref lists, ref boxes);
			return list;
		}

		private static void Walk(Control parent, List<object> list, ref int lists, ref int boxes)
		{
			foreach(Control c in parent.Controls)
			{
				if(!c.Visible) continue;
				if(c is Button)
					list.Add(new Dictionary<string, object> { { "type", "button" }, { "text", Clean(c.Text) }, { "enabled", c.Enabled } });
				else if(c is CheckBox || c is RadioButton)
					list.Add(new Dictionary<string, object> { { "type", c is CheckBox ? "checkbox" : "radio" }, { "text", Clean(c.Text) },
						{ "checked", c is CheckBox ? ((CheckBox)c).Checked : ((RadioButton)c).Checked } });
				else if(c is Label)
				{
					if(c.Text.Length > 0) list.Add(new Dictionary<string, object> { { "type", "label" }, { "text", Cut(c.Text, 2000) } });
				}
				else if(c is TextBox)
					list.Add(new Dictionary<string, object> { { "type", "textbox" }, { "index", boxes++ }, { "text", Cut(c.Text, 4000) }, { "readonly", ((TextBox)c).ReadOnly } });
				else if(c is ListView)
				{
					ListView lv = (ListView)c;
					List<object> rows = new List<object>();
					foreach(ListViewItem it in lv.Items.Cast<ListViewItem>().Take(300))
						rows.Add(it.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => (object)s.Text).ToList());
					list.Add(new Dictionary<string, object>
					{
						{ "type", "listview" }, { "index", lists++ }, { "count", lv.Items.Count },
						{ "columns", lv.Columns.Cast<ColumnHeader>().Select(h => (object)h.Text).ToList() },
						{ "rows", rows }, { "selected", lv.SelectedIndices.Cast<int>().Select(i => (object)i).ToList() }
					});
				}
				else if(c is ListBox)
				{
					ListBox lb = (ListBox)c;
					list.Add(new Dictionary<string, object>
					{
						{ "type", "listbox" }, { "index", lists++ }, { "count", lb.Items.Count },
						{ "items", lb.Items.Cast<object>().Take(300).Select(o => (object)lb.GetItemText(o)).ToList() },
						{ "selected", lb.SelectedIndices.Cast<int>().Select(i => (object)i).ToList() }
					});
				}
				else if(c is ComboBox)
				{
					ComboBox cb = (ComboBox)c;
					list.Add(new Dictionary<string, object> { { "type", "combobox" }, { "text", cb.Text },
						{ "items", cb.Items.Cast<object>().Take(100).Select(o => (object)cb.GetItemText(o)).ToList() } });
				}
				if(!(c is ListView) && !(c is ListBox) && c.Controls.Count > 0) Walk(c, list, ref lists, ref boxes);
			}
		}

		#endregion

		#region ================== Finding

		// Форма по подстроке заголовка; без заголовка — активная модальная (иначе последняя открытая, кроме главного окна)
		private static Form FindForm(string title)
		{
			List<Form> forms = Application.OpenForms.Cast<Form>().Where(f => f.Visible).ToList();
			if(!string.IsNullOrEmpty(title))
				return forms.LastOrDefault(f => f.Text.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0);
			Form modal = forms.LastOrDefault(f => f.Modal);
			return modal ?? forms.LastOrDefault(f => f != General.MainWindow);
		}

		private static IntPtr FindNative(string title)
		{
			foreach(IntPtr h in NativeDialogs())
				if(string.IsNullOrEmpty(title) || TextOf(h).IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0) return h;
			return IntPtr.Zero;
		}

		private static IEnumerable<T> All<T>(Control parent) where T : Control
		{
			foreach(Control c in parent.Controls)
			{
				if(c is T) yield return (T)c;
				foreach(T x in All<T>(c)) yield return x;
			}
		}

		private static bool TextMatches(string text, string wanted, bool exact)
		{
			string a = Clean(text).Trim(), b = Clean(wanted).Trim();
			return exact ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase) : a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		#endregion

		#region ================== Actions

		// Нажать кнопку: сначала в стандартном диалоге (он модальнее всего), затем в форме
		public static string Click(string window, string button)
		{
			IntPtr nd = FindNative(window);
			if(nd != IntPtr.Zero)
			{
				foreach(bool exact in new[] { true, false })
					foreach(IntPtr c in Children(nd))
						if(ClassOf(c) == "Button" && IsWindowVisible(c) && TextMatches(TextOf(c), button, exact))
						{
							PostMessage(c, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
							return "нажата «" + Clean(TextOf(c)) + "» в окне «" + TextOf(nd) + "»";
						}
				if(!string.IsNullOrEmpty(window)) throw new InvalidOperationException("в окне «" + TextOf(nd) + "» нет кнопки «" + button + "»");
			}
			Form f = FindForm(window);
			if(f == null) throw new InvalidOperationException("окно «" + window + "» не найдено");
			foreach(bool exact in new[] { true, false })
				foreach(Button b in All<Button>(f))
					if(b.Visible && TextMatches(b.Text, button, exact))
					{
						if(!b.Enabled) throw new InvalidOperationException("кнопка «" + Clean(b.Text) + "» недоступна");
						b.PerformClick();
						return "нажата «" + Clean(b.Text) + "» в окне «" + f.Text + "»";
					}
			throw new InvalidOperationException("в окне «" + f.Text + "» нет кнопки «" + button + "»");
		}

		// Выделить строки списка (ListView или ListBox по порядку в окне)
		public static string SelectRows(string window, int list, IList<int> rows)
		{
			Form f = FindForm(window);
			if(f == null) throw new InvalidOperationException("окно «" + window + "» не найдено");
			List<Control> lists = All<Control>(f).Where(c => c.Visible && (c is ListView || c is ListBox)).ToList();
			if(list < 0 || list >= lists.Count) throw new InvalidOperationException("в окне «" + f.Text + "» нет списка " + list);
			ListView lv = lists[list] as ListView;
			if(lv != null)
			{
				lv.SelectedIndices.Clear();
				foreach(int r in rows) if(r >= 0 && r < lv.Items.Count) lv.Items[r].Selected = true;
				if(rows.Count > 0 && rows[0] < lv.Items.Count) { lv.Items[rows[0]].Focused = true; lv.EnsureVisible(rows[0]); }
				lv.Focus();
				return "выделено строк: " + lv.SelectedIndices.Count;
			}
			ListBox lb = (ListBox)lists[list];
			lb.ClearSelected();
			foreach(int r in rows) if(r >= 0 && r < lb.Items.Count) lb.SetSelected(r, true);
			return "выделено строк: " + lb.SelectedIndices.Count;
		}

		// Ввести текст в поле (TextBox по порядку в окне)
		public static string SetText(string window, int box, string text)
		{
			Form f = FindForm(window);
			if(f == null) throw new InvalidOperationException("окно «" + window + "» не найдено");
			List<TextBox> boxes = All<TextBox>(f).Where(c => c.Visible).ToList();
			if(box < 0 || box >= boxes.Count) throw new InvalidOperationException("в окне «" + f.Text + "» нет поля " + box);
			boxes[box].Text = text;
			return "текст введён";
		}

		// Картинка окна (PNG). PrintWindow с PW_RENDERFULLCONTENT снимает и окна под другими, и вывод видеокарты
		public static byte[] Capture(string window)
		{
			IntPtr h;
			if(string.IsNullOrEmpty(window)) h = General.MainWindow.Handle;
			else
			{
				h = FindNative(window);
				if(h == IntPtr.Zero)
				{
					Form f = FindForm(window);
					if(f == null) throw new InvalidOperationException("окно «" + window + "» не найдено");
					h = f.Handle;
				}
			}
			RECT r;
			GetWindowRect(h, out r);
			int w = Math.Max(1, r.Right - r.Left), hh = Math.Max(1, r.Bottom - r.Top);
			using(Bitmap bmp = new Bitmap(w, hh, PixelFormat.Format32bppArgb))
			{
				using(Graphics g = Graphics.FromImage(bmp))
				{
					IntPtr hdc = g.GetHdc();
					try { PrintWindow(h, hdc, PW_RENDERFULLCONTENT); }
					finally { g.ReleaseHdc(hdc); }
				}
				using(MemoryStream ms = new MemoryStream())
				{
					bmp.Save(ms, ImageFormat.Png);
					return ms.ToArray();
				}
			}
		}

		#endregion
	}
}
