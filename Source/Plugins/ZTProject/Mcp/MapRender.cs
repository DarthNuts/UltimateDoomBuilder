#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using CodeImp.DoomBuilder.Geometry;
using CodeImp.DoomBuilder.Map;

#endregion

namespace CodeImp.DoomBuilder.ZTProject.Mcp
{
	// Схема области карты в PNG (MCP render_2d, ZT/mcp-plan.md §3.2): линии, вещи, подсвеченные элементы с номерами —
	// для отчётов и приёмки без запуска игры. Рисуется из карты в памяти; вид пользователя в UDB не меняется.
	internal static class MapRender
	{
		private static readonly Color Back = Color.FromArgb(18, 18, 22);
		private static readonly Color OneSided = Color.FromArgb(230, 230, 230);
		private static readonly Color TwoSided = Color.FromArgb(95, 95, 105);
		private static readonly Color Special = Color.FromArgb(80, 170, 255);
		private static readonly Color ThingColor = Color.FromArgb(90, 200, 90);
		private static readonly Color Mark = Color.FromArgb(255, 200, 40);
		private static readonly Color MarkFill = Color.FromArgb(90, 255, 120, 40);

		// highlight: тип -> номера; rect — {x1, y1, x2, y2} или null (вокруг подсвеченного с отступом margin, иначе вся карта)
		public static byte[] Render(Dictionary<string, List<int>> highlight, double[] rect, double margin, int width, bool labels, bool things)
		{
			MapSet map = General.Map.Map;
			List<Sector> hs = Get(highlight, "sector", i => map.GetSectorByIndex(i));
			List<Linedef> hl = Get(highlight, "linedef", i => map.GetLinedefByIndex(i));
			hl.AddRange(Get(highlight, "sidedef", i => map.GetSidedefByIndex(i) != null ? map.GetSidedefByIndex(i).Line : null));
			List<Thing> ht = Get(highlight, "thing", i => map.GetThingByIndex(i));
			List<Vertex> hv = Get(highlight, "vertex", i => map.GetVertexByIndex(i));

			// Область
			RectangleF area;
			if(rect != null && rect.Length == 4)
				area = RectangleF.FromLTRB((float)Math.Min(rect[0], rect[2]), (float)Math.Min(rect[1], rect[3]), (float)Math.Max(rect[0], rect[2]), (float)Math.Max(rect[1], rect[3]));
			else
			{
				List<Vector2D> pts = hs.SelectMany(s => new[] { new Vector2D(s.BBox.Left, s.BBox.Top), new Vector2D(s.BBox.Right, s.BBox.Bottom) })
					.Concat(hl.SelectMany(l => new[] { l.Start.Position, l.End.Position }))
					.Concat(ht.Select(t => new Vector2D(t.Position.x, t.Position.y))).Concat(hv.Select(v => v.Position)).ToList();
				if(pts.Count == 0) pts = map.Vertices.Select(v => v.Position).ToList();
				float x1 = (float)pts.Min(p => p.x), y1 = (float)pts.Min(p => p.y), x2 = (float)pts.Max(p => p.x), y2 = (float)pts.Max(p => p.y);
				float m = pts.Count > 0 && (hs.Count + hl.Count + ht.Count + hv.Count) > 0 ? (float)margin : 64f;
				area = RectangleF.FromLTRB(x1 - m, y1 - m, x2 + m, y2 + m);
			}
			if(area.Width < 64) area.Inflate(32 - area.Width / 2, 0);
			if(area.Height < 64) area.Inflate(0, 32 - area.Height / 2);

			width = Math.Max(200, Math.Min(4000, width));
			float scale = width / area.Width;
			int height = Math.Max(150, Math.Min(4000, (int)(area.Height * scale)));
			scale = Math.Min(scale, height / area.Height);
			Func<double, double, PointF> P = (x, y) => new PointF((float)((x - area.Left) * scale), (float)((area.Bottom - y) * scale));

			using(Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
			using(Graphics g = Graphics.FromImage(bmp))
			{
				g.SmoothingMode = SmoothingMode.AntiAlias;
				g.Clear(Back);
				RectangleF view = area;
				view.Inflate(area.Width * 0.05f, area.Height * 0.05f);

				// Подсвеченные секторы — заливка по триангуляции
				using(Brush fill = new SolidBrush(MarkFill))
					foreach(Sector s in hs)
					{
						IList<Vector2D> tri = s.Triangles.Vertices;
						for(int i = 0; i + 2 < tri.Count; i += 3)
							g.FillPolygon(fill, new[] { P(tri[i].x, tri[i].y), P(tri[i + 1].x, tri[i + 1].y), P(tri[i + 2].x, tri[i + 2].y) });
					}

				// Линии в области
				HashSet<Linedef> marked = new HashSet<Linedef>(hl);
				using(Pen one = new Pen(OneSided, 1.2f), two = new Pen(TwoSided, 1f), spec = new Pen(Special, 1.4f), mark = new Pen(Mark, 3f))
					foreach(Linedef l in map.Linedefs)
					{
						Vector2D a = l.Start.Position, b = l.End.Position;
						if(Math.Max(a.x, b.x) < view.Left || Math.Min(a.x, b.x) > view.Right || Math.Max(a.y, b.y) < view.Top || Math.Min(a.y, b.y) > view.Bottom) continue;
						Pen pen = marked.Contains(l) ? mark : l.Action != 0 ? spec : l.Back == null ? one : two;
						g.DrawLine(pen, P(a.x, a.y), P(b.x, b.y));
					}

				// Вещи
				if(things)
					using(Brush tb = new SolidBrush(ThingColor))
						foreach(Thing t in map.Things)
						{
							if(!view.Contains((float)t.Position.x, (float)t.Position.y)) continue;
							PointF c = P(t.Position.x, t.Position.y);
							g.FillEllipse(tb, c.X - 2.5f, c.Y - 2.5f, 5, 5);
						}
				using(Brush mb = new SolidBrush(Mark))
				{
					foreach(Thing t in ht) { PointF c = P(t.Position.x, t.Position.y); g.FillEllipse(mb, c.X - 5, c.Y - 5, 10, 10); }
					foreach(Vertex v in hv) { PointF c = P(v.Position.x, v.Position.y); g.FillRectangle(mb, c.X - 4, c.Y - 4, 8, 8); }
				}

				// Подписи номеров подсвеченного
				if(labels)
					using(Font f = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold))
					{
						foreach(Sector s in hs) Label(g, f, "s" + s.Index, P(s.BBox.Left + s.BBox.Width / 2, s.BBox.Top + s.BBox.Height / 2));
						foreach(Linedef l in hl) Label(g, f, "l" + l.Index, P((l.Start.Position.x + l.End.Position.x) / 2, (l.Start.Position.y + l.End.Position.y) / 2));
						foreach(Thing t in ht) Label(g, f, "t" + t.Index, P(t.Position.x + 12, t.Position.y + 12));
						foreach(Vertex v in hv) Label(g, f, "v" + v.Index, P(v.Position.x + 10, v.Position.y + 10));
					}

				// Масштаб и область — в углу
				using(Font f = new Font(FontFamily.GenericMonospace, 9f))
				using(Brush b = new SolidBrush(Color.FromArgb(170, 170, 170)))
					g.DrawString(string.Format("{0} [{1:0},{2:0} — {3:0},{4:0}]  1 px = {5:0.##} ед.", General.Map.Options.CurrentName,
						area.Left, area.Top, area.Right, area.Bottom, 1 / scale), f, b, 4, height - 16);

				using(MemoryStream ms = new MemoryStream())
				{
					bmp.Save(ms, ImageFormat.Png);
					return ms.ToArray();
				}
			}
		}

		private static List<T> Get<T>(Dictionary<string, List<int>> h, string type, Func<int, T> get) where T : class
		{
			List<int> idx;
			return h != null && h.TryGetValue(type, out idx) ? idx.Select(get).Where(x => x != null).ToList() : new List<T>();
		}

		private static void Label(Graphics g, Font f, string text, PointF at)
		{
			SizeF sz = g.MeasureString(text, f);
			RectangleF r = new RectangleF(at.X - sz.Width / 2 - 2, at.Y - sz.Height / 2, sz.Width + 4, sz.Height);
			using(Brush bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.FillRectangle(bg, r);
			using(Brush fg = new SolidBrush(Color.White)) g.DrawString(text, f, fg, r.X + 2, r.Y);
		}
	}
}
