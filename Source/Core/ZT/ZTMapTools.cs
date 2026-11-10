#region ================== Namespaces

using System;
using System.IO;
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
