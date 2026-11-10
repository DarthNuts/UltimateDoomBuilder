#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

#endregion

namespace ZTMapMerge.Udmf
{
	// Поле UDMF: имя и значение ровно в том виде, как оно записано в файле (12.0, "текст", true)
	public sealed class UdmfField
	{
		public string Key;
		public string Value;

		public UdmfField(string key, string value) { Key = key; Value = value; }
	}

	// Блок UDMF (thing, vertex, linedef, sidedef, sector или иной) с полями в исходном порядке
	public sealed class UdmfBlock
	{
		public string Type;
		public List<UdmfField> Fields = new List<UdmfField>();

		public UdmfBlock(string type) { Type = type; }

		public string Get(string key)
		{
			foreach(UdmfField f in Fields) if(f.Key == key) return f.Value;
			return null;
		}

		public void Set(string key, string value)
		{
			foreach(UdmfField f in Fields) if(f.Key == key) { f.Value = value; return; }
			Fields.Add(new UdmfField(key, value));
		}

		public void Remove(string key)
		{
			Fields.RemoveAll(f => f.Key == key);
		}

		public int GetInt(string key, int def)
		{
			string v = Get(key);
			int r;
			return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
		}

		public double GetDouble(string key, double def)
		{
			string v = Get(key);
			double r;
			return v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : def;
		}

		public UdmfBlock Clone()
		{
			UdmfBlock b = new UdmfBlock(Type);
			foreach(UdmfField f in Fields) b.Fields.Add(new UdmfField(f.Key, f.Value));
			return b;
		}
	}

	// Карта UDMF (TEXTMAP): глобальные поля и блоки по типам. Чтение — любого корректного UDMF;
	// запись — в формате Ultimate Doom Builder («thing // 0», поля по одному на строку, пустая строка
	// после блока), чтобы файл, сохранённый UDB, после чтения и записи совпадал байт в байт.
	public sealed class UdmfMap
	{
		public static readonly string[] KnownTypes = { "thing", "vertex", "linedef", "sidedef", "sector" };

		public List<UdmfField> Globals = new List<UdmfField>();
		public Dictionary<string, List<UdmfBlock>> Blocks = new Dictionary<string, List<UdmfBlock>>();
		public List<string> TypeOrder = new List<string>();     // порядок типов, как в исходном файле

		public List<UdmfBlock> Of(string type)
		{
			List<UdmfBlock> list;
			if(!Blocks.TryGetValue(type, out list))
			{
				list = new List<UdmfBlock>();
				Blocks[type] = list;
				if(!TypeOrder.Contains(type)) TypeOrder.Add(type);
			}
			return list;
		}

		#region ================== Reading

		public static UdmfMap Parse(string text)
		{
			UdmfMap map = new UdmfMap();
			Tokenizer t = new Tokenizer(text);
			while(true)
			{
				string name = t.Next();
				if(name == null) break;
				string op = t.Next();
				if(op == "=")
				{
					map.Globals.Add(new UdmfField(name, t.Value()));
					t.Expect(";");
				}
				else if(op == "{")
				{
					UdmfBlock b = new UdmfBlock(name.ToLowerInvariant());
					while(true)
					{
						string key = t.Next();
						if(key == null) throw new FormatException("UDMF: неожиданный конец файла в блоке " + name);
						if(key == "}") break;
						t.Expect("=");
						b.Fields.Add(new UdmfField(key, t.Value()));
						t.Expect(";");
					}
					map.Of(b.Type).Add(b);
				}
				else throw new FormatException("UDMF: ожидалось «=» или «{» после «" + name + "», строка " + t.Line);
			}
			return map;
		}

		private sealed class Tokenizer
		{
			private readonly string s;
			private int pos;
			public int Line = 1;

			public Tokenizer(string text) { s = text; }

			private void SkipSpace()
			{
				while(pos < s.Length)
				{
					char c = s[pos];
					if(c == '\n') { Line++; pos++; }
					else if(char.IsWhiteSpace(c)) pos++;
					else if(c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
					{
						while(pos < s.Length && s[pos] != '\n') pos++;
					}
					else if(c == '/' && pos + 1 < s.Length && s[pos + 1] == '*')
					{
						int end = s.IndexOf("*/", pos + 2, StringComparison.Ordinal);
						for(int i = pos; i < (end < 0 ? s.Length : end); i++) if(s[i] == '\n') Line++;
						pos = end < 0 ? s.Length : end + 2;
					}
					else break;
				}
			}

			// Следующий токен: имя, число, строка в кавычках (с кавычками) или знак = ; { }
			public string Next()
			{
				SkipSpace();
				if(pos >= s.Length) return null;
				char c = s[pos];
				if(c == '=' || c == ';' || c == '{' || c == '}') { pos++; return c.ToString(); }
				if(c == '"')
				{
					int start = pos++;
					while(pos < s.Length && s[pos] != '"')
					{
						if(s[pos] == '\\') pos++;
						else if(s[pos] == '\n') Line++;
						pos++;
					}
					pos++;
					return s.Substring(start, Math.Min(pos, s.Length) - start);
				}
				int st = pos;
				while(pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '=' && s[pos] != ';' && s[pos] != '{' && s[pos] != '}' && s[pos] != '/') pos++;
				if(pos == st) throw new FormatException("UDMF: неожиданный символ «" + c + "», строка " + Line);
				return s.Substring(st, pos - st);
			}

			public string Value()
			{
				string v = Next();
				if(v == null || v == ";" || v == "{" || v == "}" || v == "=") throw new FormatException("UDMF: ожидалось значение, строка " + Line);
				return v;
			}

			public void Expect(string tok)
			{
				string n = Next();
				if(n != tok) throw new FormatException("UDMF: ожидалось «" + tok + "», получено «" + n + "», строка " + Line);
			}
		}

		#endregion

		#region ================== Writing

		// Формат Ultimate Doom Builder: глобальные поля, затем блоки по типам с номером в комментарии
		public string Write()
		{
			StringBuilder sb = new StringBuilder(1 << 20);
			foreach(UdmfField g in Globals) sb.Append(g.Key).Append(" = ").Append(g.Value).Append(";\n");
			foreach(string type in WriteOrder())
			{
				List<UdmfBlock> list = Blocks[type];
				for(int i = 0; i < list.Count; i++)
				{
					sb.Append(type).Append(" // ").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\n{\n");
					foreach(UdmfField f in list[i].Fields) sb.Append(f.Key).Append(" = ").Append(f.Value).Append(";\n");
					sb.Append("}\n\n");
				}
			}
			return sb.ToString();
		}

		private IEnumerable<string> WriteOrder()
		{
			foreach(string t in TypeOrder) if(Blocks.ContainsKey(t) && Blocks[t].Count > 0) yield return t;
			foreach(string t in KnownTypes) if(!TypeOrder.Contains(t) && Blocks.ContainsKey(t) && Blocks[t].Count > 0) yield return t;
		}

		#endregion
	}
}
