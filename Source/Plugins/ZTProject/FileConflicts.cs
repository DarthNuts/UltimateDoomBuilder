#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	internal enum FileConflictAction { Cancel, Apply, AskAi }

	// Конфликт в обычном файле (скрипт, src/, картинка, звук) при «Получить»: что сделали стороны
	internal sealed class FileConflict
	{
		public string Path;
		public string Code;          // XY из git status: UU, AA, DU, UD, AU, UA, DD
		public bool TakeTheirs;      // взять версию с сервера (иначе — свою)

		public bool OursDeleted { get { return Code == "DU" || Code == "DD"; } }
		public bool TheirsDeleted { get { return Code == "UD" || Code == "DD"; } }

		public string What
		{
			get
			{
				switch(Code)
				{
					case "DU": return "у нас удалён, у них изменён";
					case "UD": return "у нас изменён, у них удалён";
					case "AA": return "добавлен обеими сторонами";
					case "DD": return "удалён обеими сторонами";
					default: return "изменён обеими сторонами";
				}
			}
		}

		public string Choice { get { return TakeTheirs ? (TheirsDeleted ? "с сервера (удалить)" : "с сервера") : (OursDeleted ? "у нас (удалить)" : "у нас"); } }
	}

	// Окно конфликтов в обычных файлах (ZT/workbench-plan.md §2.3, п. 4): по каждому файлу — «у нас» или «с сервера»,
	// либо «Попросить ИИ свести» (Claude на машине сливает в своей рабочей копии), либо «Отменить слияние».
	// Конфликты карт решаются потом своим окном (MapConflicts).
	internal static class FileConflicts
	{
		// Конфликтные файлы, кроме TEXTMAP карт
		public static List<FileConflict> Load(string dir)
		{
			List<FileConflict> list = new List<FileConflict>();
			foreach(string line in Git.Lines(Git.Run(dir, "status", "--porcelain", "--untracked-files=no")))
			{
				if(line.Length < 4) continue;
				string code = line.Substring(0, 2);
				if(!(code == "UU" || code == "AA" || code == "DU" || code == "UD" || code == "AU" || code == "UA" || code == "DD")) continue;
				string path = line.Substring(3).Trim().Trim('"');
				if(MapConflicts.IsMapTextmap(path)) continue;
				list.Add(new FileConflict { Path = path, Code = code });
			}
			return list;
		}

		public static FileConflictAction Ask(IWin32Window owner, string dir, List<FileConflict> files, int mapconflicts, bool canai)
		{
			using(Form f = new Form
			{
				Text = "ZT: конфликты в файлах при получении правок",
				FormBorderStyle = FormBorderStyle.Sizable,
				StartPosition = FormStartPosition.CenterParent,
				ClientSize = new Size(820, 380),
				MinimizeBox = false,
				ShowInTaskbar = false
			})
			{
				Label hint = new Label
				{
					Dock = DockStyle.Top, Height = 66, Padding = new Padding(8, 6, 8, 0),
					Text = "С сервера пришли правки тех же файлов, что и ваши, и git не смог их совместить. Выберите по каждому файлу, " +
					       "какую версию оставить, — или «Попросить ИИ свести»: ваши правки уйдут на сервер во временную ветку, Claude " +
					       "сведёт их в своей рабочей копии и отправит, а вы потом нажмёте «Получить». «Отменить слияние» вернёт всё как " +
					       "до «Получить» (ваши правки — в локальном коммите)." +
					       (mapconflicts > 0 ? " Конфликты карт (" + mapconflicts + ") — следующим окном." : "")
				};
				ListView lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, GridLines = true };
				lv.Columns.Add("Файл", 380);
				lv.Columns.Add("Что", 210);
				lv.Columns.Add("Взять", 150);
				foreach(FileConflict c in files)
					lv.Items.Add(new ListViewItem(new[] { c.Path, c.What, c.Choice }) { Tag = c });
				if(lv.Items.Count > 0) lv.Items[0].Selected = true;

				Action<bool> take = theirs =>
				{
					foreach(ListViewItem it in lv.SelectedItems)
					{
						FileConflict c = (FileConflict)it.Tag;
						c.TakeTheirs = theirs;
						it.SubItems[2].Text = c.Choice;
					}
				};
				FlowLayoutPanel side = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 150, FlowDirection = FlowDirection.TopDown, Padding = new Padding(4) };
				Button ours = new Button { Text = "Взять у нас", Width = 138 };
				Button theirsb = new Button { Text = "Взять с сервера", Width = 138 };
				Button diff = new Button { Text = "Показать разницу", Width = 138 };
				ours.Click += (s, e) => take(false);
				theirsb.Click += (s, e) => take(true);
				diff.Click += (s, e) =>
				{
					if(lv.SelectedItems.Count == 0) return;
					FileConflict c = (FileConflict)lv.SelectedItems[0].Tag;
					GitResult d = Git.Run(dir, "diff", "--", c.Path);
					Dialogs.ShowText(f, "ZT: " + c.Path + " — у нас и у них", c.What + "\n\n" + (d.StdOut.Trim().Length > 0 ? d.StdOut : d.Output));
				};
				lv.DoubleClick += (s, e) => diff.PerformClick();
				side.Controls.AddRange(new Control[] { ours, theirsb, diff });

				FlowLayoutPanel bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42, Padding = new Padding(6) };
				Button cancel = new Button { Text = "Отменить слияние", DialogResult = DialogResult.Cancel, Width = 130 };
				Button apply = new Button { Text = "Завершить слияние", DialogResult = DialogResult.OK, Width = 140 };
				Button ai = new Button { Text = "Попросить ИИ свести", DialogResult = DialogResult.Yes, Width = 150, Enabled = canai };
				bottom.Controls.AddRange(new Control[] { cancel, apply, ai });
				f.CancelButton = cancel;

				f.Controls.Add(lv);
				f.Controls.Add(side);
				f.Controls.Add(hint);
				f.Controls.Add(bottom);
				DialogResult r = f.ShowDialog(owner);
				return r == DialogResult.OK ? FileConflictAction.Apply : r == DialogResult.Yes ? FileConflictAction.AskAi : FileConflictAction.Cancel;
			}
		}

		// Записать выбор: версия стороны в рабочую копию и индекс; сторона удалила файл — удалить. null — успех, иначе ошибка
		public static string Apply(string dir, List<FileConflict> files)
		{
			foreach(FileConflict c in files)
			{
				bool deleted = c.TakeTheirs ? c.TheirsDeleted : c.OursDeleted;
				GitResult r = deleted
					? Git.Run(dir, "rm", "-q", "-f", "--", c.Path)
					: Git.Run(dir, "checkout", c.TakeTheirs ? "--theirs" : "--ours", "--", c.Path);
				if(r.Ok && !deleted) r = Git.Run(dir, "add", "--", c.Path);
				if(!r.Ok) return c.Path + ": " + r.Output.Trim();
			}
			return null;
		}

		public static string Summary(List<FileConflict> files)
		{
			return "Конфликты в файлах решены в UDB:\n" + string.Join("\n", files.Select(c => "  " + c.Path + " — взято " + c.Choice));
		}
	}
}
