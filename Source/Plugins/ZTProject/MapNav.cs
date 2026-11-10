#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Drawing;
using CodeImp.DoomBuilder.Editing;
using CodeImp.DoomBuilder.Geometry;
using CodeImp.DoomBuilder.Map;

#endregion

namespace CodeImp.DoomBuilder.ZTProject
{
	// Показать элементы открытой карты: режим по типу элемента, выделение, вид на них (окно конфликтов, MCP select)
	internal static class MapNav
	{
		public static readonly string[] Types = { "vertex", "linedef", "sidedef", "sector", "thing" };

		// Выделить элементы type с номерами indices (сторона — своей линией) и, если focus, показать их.
		// Возвращает число выделенных.
		public static int Select(string type, IList<int> indices, bool focus)
		{
			MapSet map = General.Map.Map;
			List<SelectableElement> elements = new List<SelectableElement>();
			RectangleF area = RectangleF.Empty;
			Action<RectangleF> grow = r => area = area.IsEmpty ? r : RectangleF.Union(area, r);
			foreach(int i in indices)
			{
				switch(type)
				{
					case "vertex":
						Vertex v = map.GetVertexByIndex(i);
						if(v != null) { elements.Add(v); grow(Around(v.Position)); }
						break;
					case "linedef":
					case "sidedef":
						Linedef l = type == "linedef" ? map.GetLinedefByIndex(i) : (map.GetSidedefByIndex(i) != null ? map.GetSidedefByIndex(i).Line : null);
						if(l != null) { elements.Add(l); grow(Rect(l.Start.Position, l.End.Position)); }
						break;
					case "sector":
						Sector s = map.GetSectorByIndex(i);
						if(s != null) { elements.Add(s); grow(s.BBox); }
						break;
					case "thing":
						Thing t = map.GetThingByIndex(i);
						if(t != null) { elements.Add(t); grow(Around(t.Position)); }
						break;
					default:
						throw new ArgumentException("неизвестный тип элемента: " + type);
				}
			}

			string mode = type == "vertex" ? "VerticesMode" : type == "sector" ? "SectorsMode" : type == "thing" ? "ThingsMode" : "LinedefsMode";
			if(General.Editing.Mode == null || General.Editing.Mode.GetType().Name != mode) General.Editing.ChangeMode(mode);
			map.ClearAllSelected();
			foreach(SelectableElement e in elements) e.Selected = true;
			if(General.Editing.Mode != null) General.Editing.Mode.UpdateSelectionInfo();
			ClassicMode cm = General.Editing.Mode as ClassicMode;
			if(focus && cm != null && !area.IsEmpty) cm.CenterOnArea(area, 0.6f);
			General.Interface.RedrawDisplay();
			return elements.Count;
		}

		private static RectangleF Around(Vector2D p)
		{
			return new RectangleF((float)p.x - 256f, (float)p.y - 256f, 512f, 512f);
		}

		private static RectangleF Rect(Vector2D a, Vector2D b)
		{
			float x = (float)Math.Min(a.x, b.x), y = (float)Math.Min(a.y, b.y);
			float w = (float)Math.Abs(a.x - b.x), h = (float)Math.Abs(a.y - b.y);
			float pw = Math.Max(0f, 256f - w) / 2, ph = Math.Max(0f, 256f - h) / 2;
			return new RectangleF(x - pw, y - ph, w + 2 * pw, h + 2 * ph);
		}
	}
}
