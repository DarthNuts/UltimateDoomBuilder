#region ================== Namespaces

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodeImp.DoomBuilder.Windows;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Слежение за файлами открытой карты проекта (ZT/workbench-plan.md §9.2): файлы wad-src/<КАРТА>/ поменяли вне UDB
	// (git в терминале, скрипт мода, правка руками) — карта перечитывается на месте одним шагом отмены. Если в UDB есть
	// несохранённые правки — вопрос: взять версию из файлов (правки вернёт Ctrl+Z) или оставить свои.
	// Свои записи плагина (сохранение, «Получить», «Отправить») не в счёт: сверяется отпечаток файлов карты.
	public partial class BuilderPlug
	{
		#region ================== Variables

		private FileSystemWatcher watcher;
		private Timer watchtimer;
		private string knownprint;      // отпечаток файлов карты на момент последней сверки с UDB
		private int busy;               // идёт своя операция плагина с файлами и git

		[DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);

		#endregion

		#region ================== Watching

		private void WatchMap()
		{
			StopWatch();
			if(current == null) return;
			string folder = Path.Combine(current.ProjectDir, "wad-src", current.MapName);
			if(!Directory.Exists(folder)) return;
			if(watchtimer == null)
			{
				watchtimer = new Timer { Interval = 1000 };       // события файлов приходят пачкой — ждём тишины
				watchtimer.Tick += (s, e) => WatchTick();
			}
			SyncWatch();
			watcher = new FileSystemWatcher(folder)
			{
				IncludeSubdirectories = false,
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
			};
			FileSystemEventHandler h = (s, e) => Poke();
			watcher.Changed += h;
			watcher.Created += h;
			watcher.Deleted += h;
			watcher.Renamed += (s, e) => Poke();
			watcher.EnableRaisingEvents = true;
		}

		private void StopWatch()
		{
			if(watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
			if(watchtimer != null) watchtimer.Stop();
			knownprint = null;
		}

		// Карта в UDB и файлы снова совпадают (открыли, сохранили, перечитали, получили)
		private void SyncWatch()
		{
			if(current == null) return;
			try { knownprint = current.Fingerprint(); }
			catch(IOException) { knownprint = null; }
		}

		// Событие файловой системы (из потока пула) — перезапустить отсчёт тишины в потоке интерфейса
		private void Poke()
		{
			try
			{
				General.MainWindow.BeginInvoke((MethodInvoker)delegate
				{
					if(watchtimer == null) return;
					watchtimer.Stop();
					watchtimer.Start();
				});
			}
			catch(InvalidOperationException) { }    // окно уже закрыто
		}

		private void WatchTick()
		{
			watchtimer.Stop();
			if(current == null || General.Map == null || watcher == null) return;

			// Не вмешиваться посреди своей операции, модального окна или действия в карте (перетаскивание и т. п.)
			bool modal = !IsWindowEnabled(General.MainWindow.Handle) || Application.OpenForms.Cast<Form>().Any(f => f.Visible && f.Modal);
			if(busy > 0 || modal || General.Editing.Mode == null || General.Editing.Mode.Attributes.Volatile) { watchtimer.Start(); return; }

			string print;
			try { print = current.Fingerprint(); }
			catch(IOException) { watchtimer.Start(); return; }       // файл ещё пишут
			if(print == knownprint) return;
			knownprint = print;

			string folder = current.MapFolder.Replace('/', '\\');
			if(General.Map.IsChanged)
			{
				DialogResult answer = General.ShowWarningMessage("ZT: файлы карты " + current.MapName + " (" + folder + ") изменены вне UDB, " +
					"а в UDB есть несохранённые правки.\n\nВзять версию из файлов? Ваши правки карты можно будет вернуть через Ctrl+Z " +
					"(несохранённый текст скриптов — нет).\n\n«Нет» — оставить правки UDB: при сохранении они перезапишут файлы.",
					MessageBoxButtons.YesNo);
				if(answer != DialogResult.Yes)
				{
					General.Interface.DisplayStatus(StatusType.Info, "ZT: оставлены правки UDB — файлы " + folder + " перезапишутся при сохранении.");
					return;
				}
			}
			busy++;
			try
			{
				string text = ReloadCurrentMap("ZT: файлы карты изменены вне UDB", General.Map.IsChanged);
				General.Interface.DisplayStatus(StatusType.Info, "ZT: файлы изменены вне UDB. " + text);
			}
			catch(Exception ex)
			{
				General.ShowErrorMessage("ZT: перечитать карту после изменения файлов не удалось.\n" + ex.Message, MessageBoxButtons.OK);
			}
			finally
			{
				busy--;
				Cursor.Current = Cursors.Default;
				SyncWatch();
			}
		}

		#endregion
	}
}
