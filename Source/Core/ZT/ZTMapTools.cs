#region ================== Namespaces

using System;
using System.IO;
using System.Linq;
using CodeImp.DoomBuilder.Config;
using CodeImp.DoomBuilder.Data;
using CodeImp.DoomBuilder.IO;
using CodeImp.DoomBuilder.Map;

#endregion

namespace CodeImp.DoomBuilder.ZT
{
	// ZT: служебные операции ядра для плагина ZTProject (ZT/workbench-plan.md §9.2 — перечитывание карты на месте после
	// «Получить»). Плагину нужно внутреннее: читатель и писатель UDMF, автоудаление элементов, флаг правок карты, вкладки
	// лампов в редакторе скриптов.
	public static class ZTMapTools
	{
		#region ================== UDMF

		// TEXTMAP -> отдельный MapSet, обработанный как при открытии карты (MapManager.InitializeOpenMap): без
		// неиспользуемых секторов, односторонние линии повёрнуты передней стороной, координаты округлены. Это не карта UDB —
		// история отмены его не записывает. Освобождать — Discard.
		public static MapSet ReadUdmf(byte[] textmap)
		{
			if(General.Map == null || !General.Map.UDMF) throw new InvalidOperationException("Карта не в формате UDMF");
			MapSet set = new MapSet();
			try
			{
				UniversalStreamReader reader = new UniversalStreamReader(General.Map.FormatInterface.UIFields);
				reader.SetKnownCustomTypes = false;
				set.BeginAddRemove();
				using(MemoryStream ms = new MemoryStream(textmap)) reader.Read(set, ms);
				set.EndAddRemove();

				set.RemoveUnusedSectors(false);
				// Linedef.FlipVertices выключает и включает автоудаление у General.Map.Map, а не у своего MapSet:
				// здесь автоудаление выключается самим, иначе вершина одиночной линии пропадёт при повороте
				bool mainauto = General.Map.Map.AutoRemove;
				set.AutoRemove = false;
				MapSet.FlipBackwardLinedefs(set.Linedefs);
				set.AutoRemove = true;
				General.Map.Map.AutoRemove = mainauto;
				set.UpdateConfiguration();
				set.SnapAllToAccuracy();
				return set;
			}
			catch
			{
				Discard(set);
				throw;
			}
		}

		// MapSet -> TEXTMAP тем же писателем, что при сохранении карты (типы пользовательских полей в .dbs не пишутся)
		public static byte[] WriteUdmf(MapSet map)
		{
			UniversalStreamWriter writer = new UniversalStreamWriter();
			writer.RememberCustomTypes = false;
			using(MemoryStream ms = new MemoryStream())
			{
				writer.Write(map, ms, General.Map.Config.EngineName);
				return ms.ToArray();
			}
		}

		// Освободить MapSet из ReadUdmf
		public static void Discard(MapSet set)
		{
			if(set != null && !set.IsDisposed && (General.Map == null || set != General.Map.Map)) set.Dispose();
		}

		#endregion

		#region ================== Changing the map

		// Автоудаление осиротевших элементов (вершина без линий, сектор без сторон, стороны удалённой линии). Пока карта
		// перестраивается по образцу, его надо выключить, иначе элементы пропадают посреди правки. Возвращает прежнее значение.
		public static bool SetAutoRemove(MapSet map, bool value)
		{
			bool old = map.AutoRemove;
			map.AutoRemove = value;
			return old;
		}

		// Свойства элемента из другого MapSet. Выделение и группы получателя сохраняются: их сначала получает источник,
		// иначе CopyPropertiesTo снял бы выделение
		public static void CopyProperties(Vertex src, Vertex dst) { src.Selected = dst.Selected; src.GroupsMask = dst.GroupsMask; src.CopyPropertiesTo(dst); }
		public static void CopyProperties(Linedef src, Linedef dst) { src.Selected = dst.Selected; src.GroupsMask = dst.GroupsMask; src.CopyPropertiesTo(dst); }
		public static void CopyProperties(Sector src, Sector dst) { src.Selected = dst.Selected; src.GroupsMask = dst.GroupsMask; src.CopyPropertiesTo(dst); }
		public static void CopyProperties(Thing src, Thing dst) { src.Selected = dst.Selected; src.GroupsMask = dst.GroupsMask; src.CopyPropertiesTo(dst); }
		public static void CopyProperties(Sidedef src, Sidedef dst) { src.CopyPropertiesTo(dst); }

		// Начало внешней правки карты одним шагом отмены
		public static void BeginExternalChange(string description)
		{
			General.Editing.CancelVolatileMode();
			General.Map.UndoRedo.CreateUndo(description);
			General.Map.Map.ClearAllMarks(false);
			General.Map.Map.BeginAddRemove();
		}

		// Конец внешней правки: карта, режим и интерфейс обновляются так же, как после отмены (UndoManager.PerformUndo).
		// Изменённые элементы должны быть помечены (Marked).
		public static void EndExternalChange()
		{
			MapSet map = General.Map.Map;
			map.EndAddRemove();
			map.Update();
			foreach(Thing t in map.Things) if(t.Marked) t.UpdateConfiguration();
			General.Map.ThingsFilter.Update();
			General.Map.Data.UpdateUsedTextures();
			General.MainWindow.RefreshInfo();
			General.Editing.Mode.OnUndoEnd();
			General.Editing.Mode.UpdateSelectionInfo();
			General.MainWindow.RedrawDisplay();
			General.MainWindow.UpdateInterface();
		}

		// Карта в памяти снова совпадает с файлами (перечитана из них): флаг несохранённых правок снимается — CreateUndo и
		// SetLumpData его ставят, а сохранять нечего
		public static void MarkSaved()
		{
			General.Map.ForceMapIsChangedFalse();
		}

		#endregion

		#region ================== Resources

		// Отбор ресурсов игровой конфигурации для открываемой карты (MapManager — при загрузке и перечитывании ресурсов).
		// Плагин ZTProject: если ресурс карты — папка src/ рабочей копии, собранный из неё pk3 не грузится (иначе всё дважды).
		public static Func<DataLocationList, DataLocationList> ConfigResourceFilter;

		internal static DataLocationList FilterConfigResources(DataLocationList list)
		{
			Func<DataLocationList, DataLocationList> f = ConfigResourceFilter;
			if(f == null) return list;
			try { return f(list) ?? list; }
			catch(Exception ex) { General.WriteLogLine("ZT: отбор ресурсов не удался: " + ex.Message); return list; }
		}

		#endregion

		#region ================== Script files

		// Тип скрипта по имени файла мода (ZSCRIPT.zs, *.zs, DECORATE, *.dec, *.acs, MAPINFO, SNDINFO…); иначе — простой текст
		public static ScriptType ScriptTypeOf(string path)
		{
			string name = Path.GetFileName(path).ToUpperInvariant(), ext = Path.GetExtension(path).ToLowerInvariant();
			string stem = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
			if(ext == ".zs" || ext == ".zsc" || stem == "ZSCRIPT") return ScriptType.ZSCRIPT;
			if(ext == ".dec" || stem == "DECORATE") return ScriptType.DECORATE;
			if(ext == ".acs" || stem == "SCRIPTS") return ScriptType.ACS;
			foreach(ScriptType t in Enum.GetValues(typeof(ScriptType)))
				if(t != ScriptType.UNKNOWN && (stem == t.ToString() || stem == "Z" + t.ToString())) return t;   // MAPINFO, ZMAPINFO, SNDINFO…
			return ScriptType.UNKNOWN;
		}

		// Открыть файл (например, из src/ рабочей копии) во вкладке редактора скриптов карты. keepws — не срезать хвостовые
		// пробелы при сохранении. false — карта не открыта или файл не прочитан.
		public static bool OpenScriptFile(string path, bool keepws)
		{
			if(General.Map == null || !File.Exists(path)) return false;
			ScriptConfiguration cfg = General.GetScriptConfiguration(ScriptTypeOf(path)) ?? General.GetScriptConfiguration(ScriptType.UNKNOWN);
			if(cfg == null) cfg = General.ScriptConfigs.Values.First();
			General.Map.ShowScriptEditor();
			return General.Map.ScriptEditor.Editor.OpenFileTab(path, cfg, keepws) != null;
		}

		// Вкладки редактора скриптов: заголовок, файл (для лампа — имя лампа), есть ли несохранённые правки
		public static System.Collections.Generic.List<Tuple<string, string, bool>> ScriptTabs()
		{
			var list = new System.Collections.Generic.List<Tuple<string, string, bool>>();
			if(General.Map == null || !General.Map.IsScriptsWindowOpen) return list;
			foreach(var t in General.Map.ScriptEditor.Editor.OpenTabs()) list.Add(Tuple.Create(t.Title, t.Filename, t.IsChanged));
			return list;
		}

		// Текст вкладки (по имени файла или лампа): прочитать; text != null — заменить (как правка в редакторе, с отменой);
		// save — сохранить вкладку. null — вкладки нет.
		public static string ScriptTabText(string file, string text, bool save)
		{
			if(General.Map == null || !General.Map.IsScriptsWindowOpen) return null;
			foreach(var t in General.Map.ScriptEditor.Editor.OpenTabs())
			{
				if(!string.Equals(t.Filename, file, StringComparison.OrdinalIgnoreCase) && !string.Equals(t.Title, file, StringComparison.OrdinalIgnoreCase)) continue;
				if(text != null) t.Editor.Scintilla.Text = text;
				if(save) t.Save();
				return t.Editor.Scintilla.Text;
			}
			return null;
		}

		// Показать ошибки (например, проверки ZScript в GZDoom) в списке ошибок редактора скриптов; клик открывает строку
		// в открытой вкладке файла. Пустой список — убрать прежние ошибки.
		public static void ShowScriptErrors(System.Collections.Generic.List<CodeImp.DoomBuilder.Compilers.CompilerError> errors)
		{
			if(General.Map == null) return;
			if(errors.Count == 0 && !General.Map.IsScriptsWindowOpen) return;
			General.Map.ShowScriptEditor();
			General.Map.ScriptEditor.Editor.ShowErrors(errors, false);
		}

		// Перечитать вкладки файлов без несохранённых правок, если файлы поменяли снаружи («Получить», git). Возвращает число
		public static int ReloadScriptFiles()
		{
			if(General.Map == null || !General.Map.IsScriptsWindowOpen) return 0;
			int n = 0;
			foreach(var t in General.Map.ScriptEditor.Editor.OpenTabs())
			{
				var ft = t as CodeImp.DoomBuilder.Controls.ScriptFileDocumentTab;
				if(ft != null && ft.ReloadIfUnchanged()) n++;
			}
			return n;
		}

		#endregion

		#region ================== Map lumps

		// Заменить ламп карты во временном WAD UDB (TEXTMAP, SCRIPTS, BEHAVIOR, DIALOGUE…) и перечитать его вкладку в
		// редакторе скриптов. false — такого лампа во временном WAD нет (новый ламп так не добавить: он встал бы за картой).
		public static bool ReplaceMapLump(string lumpname, byte[] data)
		{
			if(General.Map == null || !General.Map.LumpExists(lumpname)) return false;
			General.Map.SetLumpData(lumpname, new MemoryStream(data));
			if(General.Map.IsScriptsWindowOpen) General.Map.ScriptEditor.Editor.ReloadLumpTab(lumpname);
			if(string.Equals(lumpname, "SCRIPTS", StringComparison.OrdinalIgnoreCase)) General.Map.LoadACS();
			return true;
		}

		#endregion
	}
}
